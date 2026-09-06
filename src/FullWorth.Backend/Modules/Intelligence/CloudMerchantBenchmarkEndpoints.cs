using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public static class CloudMerchantBenchmarkEndpoints
{
    public static IEndpointRouteBuilder MapCloudMerchantBenchmarkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/intelligence/benchmarks/merchants/{merchantId:guid}", async (
            Guid merchantId,
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FullWorthDbContext financeDb,
            CloudOperationalRegistryResolver registryResolver,
            CloudIntelligenceStateService cloudState,
            CloudInstanceCredentialStore credentials,
            IFullWorthCloudClient cloud,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            var isMember = await financeDb.FullWorthSpaceMembers.AsNoTracking().AnyAsync(x =>
                x.FullWorthSpaceId == fullWorthSpaceId && x.UserId == userId, ct);
            if (!isMember) return Results.NotFound();

            var merchant = await financeDb.Set<Merchant>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == merchantId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
            if (merchant is null) return Results.NotFound();

            var aliases = await financeDb.Set<MerchantAlias>().AsNoTracking()
                .Where(x => x.MerchantId == merchantId && x.FullWorthSpaceId == fullWorthSpaceId)
                .Select(x => x.NormalizedAlias)
                .ToListAsync(ct);
            aliases.Add(merchant.NormalizedName);
            var aliasSet = aliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            if (aliasSet.Count == 0)
                return Results.Ok(new { available = false });

            var country = await SpaceCountryAsync(financeDb, fullWorthSpaceId, ct);
            var identities = new List<CloudMerchantIdentity>();
            foreach (var alias in aliasSet)
            {
                var identity = await registryResolver.ResolveMerchantAsync(alias, country, "expense", ct);
                if (identity is not null)
                    identities.Add(identity);
            }

            var identityGroups = identities
                .GroupBy(x => x.MerchantKey, StringComparer.Ordinal)
                .ToList();
            if (identityGroups.Count != 1)
                return Results.Ok(new { available = false, reason = "merchant_identity_unavailable" });

            var identity = identityGroups[0].OrderByDescending(x => x.Confidence).First();
            var entityKey = CloudBenchmarkEntityKeys.ForMerchant(identity.MerchantKey);

            var currentMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            var monthStart = currentMonth.AddMonths(-1);
            var monthEnd = currentMonth;
            var observedMonth = monthStart.ToString("yyyy-MM");

            var expenses = await financeDb.Transactions.AsNoTracking()
                .Join(
                    financeDb.Accounts.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId),
                    tx => tx.AccountId,
                    account => account.Id,
                    (tx, account) => tx)
                .Where(tx =>
                    (tx.BookingDate ?? tx.ValueDate) >= monthStart &&
                    (tx.BookingDate ?? tx.ValueDate) < monthEnd &&
                    tx.Amount < 0m &&
                    !tx.IsIgnored &&
                    !tx.IsTransfer &&
                    tx.NormalizedCounterparty != null &&
                    aliasSet.Contains(tx.NormalizedCounterparty))
                .Select(tx => new { tx.Id, tx.Amount, tx.Currency })
                .ToListAsync(ct);

            var allMerchantExpenseIds = await financeDb.Transactions.AsNoTracking()
                .Join(
                    financeDb.Accounts.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId),
                    tx => tx.AccountId,
                    account => account.Id,
                    (tx, account) => tx)
                .Where(tx =>
                    tx.Amount < 0m &&
                    tx.NormalizedCounterparty != null &&
                    aliasSet.Contains(tx.NormalizedCounterparty))
                .Select(tx => tx.Id)
                .ToListAsync(ct);

            var refunds = allMerchantExpenseIds.Count == 0
                ? []
                : await financeDb.Transactions.AsNoTracking()
                    .Join(
                        financeDb.Accounts.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId),
                        tx => tx.AccountId,
                        account => account.Id,
                        (tx, account) => tx)
                    .Where(tx =>
                        (tx.BookingDate ?? tx.ValueDate) >= monthStart &&
                        (tx.BookingDate ?? tx.ValueDate) < monthEnd &&
                        tx.Amount > 0m &&
                        tx.RefundOfTransactionId != null &&
                        allMerchantExpenseIds.Contains(tx.RefundOfTransactionId.Value) &&
                        !tx.IsIgnored &&
                        !tx.IsTransfer)
                    .Select(tx => new { tx.Amount, tx.Currency })
                    .ToListAsync(ct);

            var spendByCurrency = expenses
                .Select(x => new { x.Currency, Value = -x.Amount })
                .Concat(refunds.Select(x => new { x.Currency, Value = -x.Amount }))
                .GroupBy(x => NormalizeCurrency(x.Currency))
                .Where(g => g.Key is not null)
                .Select(g => new
                {
                    Currency = g.Key!,
                    Value = Math.Round(g.Sum(x => x.Value), 2)
                })
                .Where(x => x.Value > 0m)
                .ToList();

            if (spendByCurrency.Count == 0)
                return Results.Ok(new
                {
                    available = false,
                    merchantName = identity.CanonicalName,
                    observedMonth
                });

            if (!await cloudState.HasCurrentActiveConsentAsync(ct))
                return Results.Ok(new
                {
                    available = false,
                    merchantName = identity.CanonicalName,
                    observedMonth,
                    local = spendByCurrency
                });

            var state = await cloudState.GetEnabledStateAsync(ct);
            if (state is null)
                return Results.Ok(new { available = false, merchantName = identity.CanonicalName, observedMonth });

            var secret = await credentials.GetSecretAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                try
                {
                    var registration = await cloud.RegisterAsync(
                        state.InstanceId,
                        CloudIntelligencePolicy.CurrentVersion,
                        typeof(CloudMerchantBenchmarkEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
                        ct);
                    await credentials.SaveAsync(registration, ct);
                    secret = registration.Credential;
                }
                catch (FullWorthCloudException)
                {
                    return Results.Ok(new { available = false, merchantName = identity.CanonicalName, observedMonth });
                }
            }

            var items = new List<object>();
            foreach (var local in spendByCurrency)
            {
                try
                {
                    var aggregate = await cloud.GetEntityBenchmarkAsync(
                        secret,
                        CloudMerchantBenchmarkContributionService.MetricKey,
                        entityKey,
                        local.Currency,
                        country,
                        null,
                        null,
                        null,
                        null,
                        observedMonth,
                        ct);
                    if (aggregate is null) continue;

                    items.Add(new
                    {
                        local.Currency,
                        localSpend = local.Value,
                        aggregate.ObservationCount,
                        aggregate.DistinctInstanceCount,
                        aggregate.Median,
                        aggregate.Mean,
                        aggregate.P25,
                        aggregate.P75
                    });
                }
                catch (Exception ex) when (ex is FullWorthCloudException or NotSupportedException)
                {
                    // Merchant comparison is optional and must never break merchant management.
                }
            }

            return Results.Ok(new
            {
                available = items.Count > 0,
                merchantName = identity.CanonicalName,
                observedMonth,
                items
            });
        }).WithTags("Intelligence Benchmarks");

        return app;
    }

    private static async Task<string?> SpaceCountryAsync(
        FullWorthDbContext db,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        var countries = (await db.BankConnections.AsNoTracking()
                .Where(x =>
                    x.FullWorthSpaceId == fullWorthSpaceId &&
                    x.Country != null &&
                    x.Country != "")
                .Select(x => x.Country)
                .Distinct()
                .Take(3)
                .ToListAsync(ct))
            .Select(NormalizeCountry)
            .Where(x => x is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return countries.Count == 1 ? countries[0] : null;
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(char.IsAsciiLetter)
            ? normalized
            : null;
    }

    private static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter)
            ? normalized
            : null;
    }
}
