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
            SpaceAccess space,
            MerchantSpendStore spend,
            CloudRequestContextStore cloudContext,
            CloudOperationalRegistryResolver registryResolver,
            CloudIntelligenceStateService cloudState,
            CloudCredentialAcquisition acquisition,
            IFullWorthCloudClient cloud,
            CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!await space.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

            var merchant = await spend.FindMerchantAsync(fullWorthSpaceId, merchantId, ct);
            if (merchant is null) return Results.NotFound();

            var aliases = await spend.AliasesAsync(fullWorthSpaceId, merchantId, ct);
            aliases.Add(merchant.NormalizedName);
            var aliasSet = aliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            if (aliasSet.Count == 0)
                return Results.Ok(new { available = false });

            var country = await cloudContext.SpaceCountryAsync(fullWorthSpaceId, ct);
            var identities = new List<CloudMerchantIdentity>();
            foreach (var alias in aliasSet)
            {
                var resolved = await registryResolver.ResolveMerchantAsync(alias, country, "expense", ct);
                if (resolved is not null)
                    identities.Add(resolved);
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

            var expenses = await spend.ExpensesAsync(fullWorthSpaceId, aliasSet, monthStart, monthEnd, ct);
            var allMerchantExpenseIds = await spend.AllExpenseIdsAsync(fullWorthSpaceId, aliasSet, ct);
            var refunds = await spend.RefundsAsync(fullWorthSpaceId, allMerchantExpenseIds, monthStart, monthEnd, ct);

            var spendByCurrency = expenses
                .Select(x => new { x.Currency, Value = -x.Amount })
                .Concat(refunds.Select(x => new { x.Currency, Value = -x.Amount }))
                .GroupBy(x => CloudRequestContextStore.NormalizeCurrency(x.Currency))
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

            // A page load must never wait out the Cloud client's HTTP timeout, so the attempt is
            // budgeted and a failure puts the next request straight into this branch.
            var (secret, _) = await acquisition.TryGetAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                return Results.Ok(new { available = false, merchantName = identity.CanonicalName, observedMonth });
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



}
