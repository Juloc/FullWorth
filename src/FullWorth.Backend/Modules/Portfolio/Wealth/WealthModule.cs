using System.Data;
using System.Data.Common;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Parity;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Portfolio;

public sealed record WealthNativeAmount(string Currency, decimal Amount);

public sealed record WealthComponentView(
    decimal Amount,
    string Currency,
    bool IsComplete,
    IReadOnlyList<WealthNativeAmount> OriginalAmounts);

public sealed record EmergencyFundView(
    bool Enabled,
    decimal TargetAmount,
    decimal CurrentAmount,
    string Currency,
    Guid? AccountId,
    Guid? AccountGroupId,
    bool IsComplete);

public sealed record WealthOverviewView(
    DateOnly Date,
    string Currency,
    WealthComponentView Accounts,
    WealthComponentView ManualAssets,
    WealthComponentView Investments,
    WealthComponentView Loans,
    WealthComponentView OtherLiabilities,
    decimal TotalAssets,
    decimal TotalLiabilities,
    decimal NetWorth,
    bool IsComplete,
    bool InvestmentDataIncomplete,
    IReadOnlyList<string> MissingCurrencies,
    EmergencyFundView? EmergencyFund = null);

public sealed record WealthHistoryPoint(
    DateOnly Date,
    string Currency,
    decimal? Accounts,
    decimal? ManualAssets,
    decimal? Investments,
    decimal? Loans,
    decimal? OtherLiabilities,
    decimal? TotalAssets,
    decimal? TotalLiabilities,
    decimal NetWorth,
    bool IsComplete,
    IReadOnlyList<string> MissingCurrencies);

public enum WealthRequestStatus
{
    Success,
    NotFound,
    Invalid
}

public sealed record WealthOverviewOutcome(
    WealthRequestStatus Status,
    WealthOverviewView? Overview = null,
    string? Error = null);

public sealed record WealthHistoryOutcome(
    WealthRequestStatus Status,
    IReadOnlyList<WealthHistoryPoint>? History = null,
    string? Error = null);

public sealed class WealthOverviewService(
    FullWorthDbContext db,
    CurrencyConverter currencyConverter,
    InvestmentNetWorthService investments)
{
    public async Task<WealthOverviewOutcome> GetOverviewForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        string? requestedCurrency,
        CancellationToken ct)
    {
        var space = await db.FullWorthSpaces.AsNoTracking()
            .Where(item => item.Id == fullWorthSpaceId &&
                           db.FullWorthSpaceMembers.Any(member =>
                               member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Select(item => new { item.BaseCurrency })
            .SingleOrDefaultAsync(ct);
        if (space is null) return new(WealthRequestStatus.NotFound);

        var targetCurrency = NormalizeRequestedCurrency(requestedCurrency, space.BaseCurrency);
        if (targetCurrency is null)
            return new(WealthRequestStatus.Invalid, Error: "Currency must be a three-letter code.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var investment = await investments.CalculateAsync(fullWorthSpaceId, userId, today, ct);
        var excludedInvestmentAccounts = investment.ExcludedLinkedAccountIds;

        var accounts = await db.Accounts.AsNoTracking()
            .Where(account =>
                account.FullWorthSpaceId == fullWorthSpaceId &&
                account.IsActive &&
                account.IncludeInNetWorth &&
                account.Owners.Any(owner => owner.UserId == userId) &&
                !excludedInvestmentAccounts.Contains(account.Id))
            .Select(account => new { account.Id, account.Currency, account.GroupId })
            .ToListAsync(ct);
        var accountIds = accounts.Select(account => account.Id).ToArray();
        var balanceRows = await db.BalanceSnapshots.AsNoTracking()
            .Where(balance => accountIds.Contains(balance.AccountId))
            .Select(balance => new
            {
                balance.AccountId,
                balance.Amount,
                balance.Currency,
                balance.BalanceType,
                balance.CapturedAt
            })
            .ToListAsync(ct);
        var latestBalanceByAccount = balanceRows
            .GroupBy(balance => balance.AccountId)
            .Select(group => group
                .OrderByDescending(balance => balance.CapturedAt)
                .ThenBy(balance => BalanceRank(balance.BalanceType))
                .ThenBy(balance => balance.BalanceType, StringComparer.Ordinal)
                .First())
            .ToDictionary(
                balance => balance.AccountId,
                balance => new NativeValue(balance.Amount, balance.Currency));
        var latestBalances = latestBalanceByAccount.Values.ToList();

        var manualAssets = await db.Assets.AsNoTracking()
            .Where(asset => asset.FullWorthSpaceId == fullWorthSpaceId && asset.IncludeInNetWorth)
            .Select(asset => new NativeValue(asset.CurrentValue, asset.Currency))
            .ToListAsync(ct);

        var loanRows = await db.Loans.AsNoTracking()
            .Where(loan => loan.FullWorthSpaceId == fullWorthSpaceId && loan.IsActive)
            .Select(loan => new NativeValue(loan.CurrentBalance, loan.Currency))
            .ToListAsync(ct);

        var otherLiabilities = await db.Liabilities.AsNoTracking()
            .Where(liability => liability.FullWorthSpaceId == fullWorthSpaceId && liability.IncludeInNetWorth)
            .Select(liability => new NativeValue(liability.CurrentBalance, liability.Currency))
            .ToListAsync(ct);

        var fx = await currencyConverter.PrepareLatestAsync(targetCurrency, today, ct);
        var missingCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accountsView = ConvertComponent(latestBalances, targetCurrency, today, fx, missingCurrencies);
        var manualAssetsView = ConvertComponent(manualAssets, targetCurrency, today, fx, missingCurrencies);
        var loansView = ConvertComponent(loanRows, targetCurrency, today, fx, missingCurrencies);
        var otherLiabilitiesView = ConvertComponent(otherLiabilities, targetCurrency, today, fx, missingCurrencies);

        EmergencyFundView? emergencyFund = null;
        var emergencyJson = await db.UserPreferences.AsNoTracking()
            .Where(preference =>
                preference.FinanceUserId == userId &&
                preference.FullWorthSpaceId == fullWorthSpaceId &&
                preference.Key == "wealth.emergencyFund")
            .Select(preference => preference.ValueJson)
            .SingleOrDefaultAsync(ct);
        var emergencyPreference = ParseEmergencyFundPreference(emergencyJson);
        if (emergencyPreference is { Enabled: true, TargetAmount: > 0m })
        {
            var selectedAccounts = accounts.Where(account =>
                (!emergencyPreference.AccountId.HasValue || account.Id == emergencyPreference.AccountId.Value) &&
                (!emergencyPreference.AccountGroupId.HasValue || account.GroupId == emergencyPreference.AccountGroupId.Value));
            var selectedBalances = selectedAccounts
                .Where(account => latestBalanceByAccount.ContainsKey(account.Id))
                .Select(account => latestBalanceByAccount[account.Id])
                .ToList();
            var emergencyMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var emergencyComponent = ConvertComponent(selectedBalances, targetCurrency, today, fx, emergencyMissing);
            emergencyFund = new EmergencyFundView(
                true,
                emergencyPreference.TargetAmount,
                emergencyComponent.Amount,
                targetCurrency,
                emergencyPreference.AccountId,
                emergencyPreference.AccountGroupId,
                emergencyComponent.IsComplete);
        }

        var convertedInvestment = fx.ToBaseOn(investment.Amount, investment.BaseCurrency, today);
        var investmentConversionComplete = convertedInvestment.HasValue;
        if (!investmentConversionComplete)
            missingCurrencies.Add(FxSnapshot.Normalize(investment.BaseCurrency));
        var investmentsView = new WealthComponentView(
            convertedInvestment ?? 0m,
            targetCurrency,
            investmentConversionComplete && !investment.Incomplete,
            [new WealthNativeAmount(FxSnapshot.Normalize(investment.BaseCurrency), investment.Amount)]);

        var totalAssets = manualAssetsView.Amount + investmentsView.Amount;
        var totalLiabilities = loansView.Amount + otherLiabilitiesView.Amount;
        var netWorth = accountsView.Amount + totalAssets - totalLiabilities;
        var complete = accountsView.IsComplete &&
                       manualAssetsView.IsComplete &&
                       investmentsView.IsComplete &&
                       loansView.IsComplete &&
                       otherLiabilitiesView.IsComplete;

        return new(
            WealthRequestStatus.Success,
            new WealthOverviewView(
                today,
                targetCurrency,
                accountsView,
                manualAssetsView,
                investmentsView,
                loansView,
                otherLiabilitiesView,
                totalAssets,
                totalLiabilities,
                netWorth,
                complete,
                investment.Incomplete,
                missingCurrencies.Order(StringComparer.Ordinal).Select(x => x.ToUpperInvariant()).ToArray(),
                emergencyFund));
    }

    public async Task<WealthHistoryOutcome> GetHistoryForUserAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly? from,
        DateOnly? to,
        string? requestedCurrency,
        CancellationToken ct)
    {
        var space = await db.FullWorthSpaces.AsNoTracking()
            .Where(item => item.Id == fullWorthSpaceId &&
                           db.FullWorthSpaceMembers.Any(member =>
                               member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId))
            .Select(item => new { item.BaseCurrency })
            .SingleOrDefaultAsync(ct);
        if (space is null) return new(WealthRequestStatus.NotFound);

        var targetCurrency = NormalizeRequestedCurrency(requestedCurrency, space.BaseCurrency);
        if (targetCurrency is null)
            return new(WealthRequestStatus.Invalid, Error: "Currency must be a three-letter code.");
        if (from.HasValue && to.HasValue && from.Value > to.Value)
            return new(WealthRequestStatus.Invalid, Error: "From date cannot be after to date.");

        var rows = await ReadHistoryRowsAsync(fullWorthSpaceId, userId, from, to, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var minDate = rows.Count == 0 ? today : rows.Min(row => row.Date);
        var maxDate = rows.Count == 0 ? today : rows.Max(row => row.Date);
        var fx = await currencyConverter.PrepareAsync(targetCurrency, minDate, maxDate, ct);

        var points = new List<WealthHistoryPoint>();
        foreach (var dateRowsEnumerable in rows.GroupBy(row => row.Date).OrderBy(group => group.Key))
        {
            var dateRows = dateRowsEnumerable.ToList();
            var explicitRows = dateRows.Where(row => row.HasExplicitComponents).ToList();
            points.Add(explicitRows.Count > 0
                ? BuildExplicitHistoryPoint(dateRowsEnumerable.Key, dateRows, explicitRows, targetCurrency, fx)
                : BuildLegacyHistoryPoint(dateRowsEnumerable.Key, dateRows, targetCurrency, fx));
        }

        var includesToday = (!from.HasValue || from.Value <= today) && (!to.HasValue || to.Value >= today);
        if (includesToday)
        {
            var current = await GetOverviewForUserAsync(userId, fullWorthSpaceId, targetCurrency, ct);
            if (current.Status == WealthRequestStatus.Success && current.Overview is { } overview)
            {
                points.RemoveAll(point => point.Date == today);
                points.Add(ToHistoryPoint(overview));
            }
        }

        return new(
            WealthRequestStatus.Success,
            points.OrderBy(point => point.Date).ToArray());
    }

    /// <summary>
    /// Stores the V2 decomposition on exactly one of today's existing legacy snapshot rows. The legacy
    /// Accounts/Assets/Liabilities/NetWorth columns remain untouched and therefore keep their original
    /// native-currency semantics for the compatibility endpoint. ComponentCurrency records the currency
    /// used by the V2 decomposition independently of the carrier row's native Currency.
    /// </summary>
    public async Task PersistTodaySnapshotComponentsAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        CancellationToken ct)
    {
        var outcome = await GetOverviewForUserAsync(userId, fullWorthSpaceId, null, ct);
        if (outcome.Status != WealthRequestStatus.Success || outcome.Overview is not { } overview) return;

        var candidates = await db.NetWorthSnapshots.AsNoTracking()
            .Where(item => item.FullWorthSpaceId == fullWorthSpaceId && item.UserId == userId && item.Date == overview.Date)
            .OrderBy(item => item.Currency == overview.Currency ? 0 : 1)
            .ThenBy(item => item.Currency)
            .Select(item => new { item.Id })
            .ToListAsync(ct);
        var carrier = candidates.FirstOrDefault();
        if (carrier is null) return;

        var missingJson = JsonSerializer.Serialize(overview.MissingCurrencies);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "NetWorthSnapshots"
            SET "ManualAssets" = {overview.ManualAssets.Amount},
                "Investments" = {overview.Investments.Amount},
                "Loans" = {overview.Loans.Amount},
                "OtherLiabilities" = {overview.OtherLiabilities.Amount},
                "ComponentCurrency" = {overview.Currency},
                "IsComplete" = {overview.IsComplete},
                "MissingCurrenciesJson" = CAST({missingJson} AS jsonb)
            WHERE "Id" = {carrier.Id};
            """, ct);
    }

    private static WealthComponentView ConvertComponent(
        IReadOnlyCollection<NativeValue> values,
        string targetCurrency,
        DateOnly date,
        FxSnapshot fx,
        ISet<string> missingCurrencies)
    {
        decimal total = 0m;
        var complete = true;
        foreach (var value in values)
        {
            var converted = fx.ToBaseOn(value.Amount, value.Currency, date);
            if (!converted.HasValue)
            {
                complete = false;
                missingCurrencies.Add(FxSnapshot.Normalize(value.Currency));
                continue;
            }
            total += converted.Value;
        }

        var originals = values
            .GroupBy(value => FxSnapshot.Normalize(value.Currency), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new WealthNativeAmount(group.Key, group.Sum(value => value.Amount)))
            .ToArray();
        return new(total, targetCurrency, complete, originals);
    }

    private static WealthHistoryPoint BuildExplicitHistoryPoint(
        DateOnly date,
        IReadOnlyCollection<HistoryRow> allRows,
        IReadOnlyCollection<HistoryRow> explicitRows,
        string targetCurrency,
        FxSnapshot fx)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        decimal accounts = 0m;
        decimal manualAssets = 0m;
        decimal investments = 0m;
        decimal loans = 0m;
        decimal otherLiabilities = 0m;
        var complete = true;

        // Accounts remain native-currency legacy values, so aggregate them from every row for the day.
        foreach (var row in allRows)
        {
            var multiplier = fx.ToBaseOn(1m, row.Currency, date);
            if (!multiplier.HasValue)
            {
                missing.Add(FxSnapshot.Normalize(row.Currency));
                complete = false;
                continue;
            }
            accounts += row.Accounts * multiplier.Value;
        }

        // V2 components are stored once, with their own ComponentCurrency, and must not be interpreted
        // as the carrier row's native currency.
        foreach (var row in explicitRows)
        {
            var componentCurrency = row.ComponentCurrency ?? row.Currency;
            var multiplier = fx.ToBaseOn(1m, componentCurrency, date);
            if (!multiplier.HasValue)
            {
                missing.Add(FxSnapshot.Normalize(componentCurrency));
                complete = false;
                continue;
            }
            manualAssets += row.ManualAssets!.Value * multiplier.Value;
            investments += row.Investments!.Value * multiplier.Value;
            loans += row.Loans!.Value * multiplier.Value;
            otherLiabilities += row.OtherLiabilities!.Value * multiplier.Value;
            if (row.IsComplete != true) complete = false;
            foreach (var currency in row.MissingCurrencies) missing.Add(currency);
        }

        return new(
            date,
            targetCurrency,
            accounts,
            manualAssets,
            investments,
            loans,
            otherLiabilities,
            manualAssets + investments,
            loans + otherLiabilities,
            accounts + manualAssets + investments - loans - otherLiabilities,
            complete && missing.Count == 0,
            missing.Order(StringComparer.Ordinal).Select(x => x.ToUpperInvariant()).ToArray());
    }

    private static WealthHistoryPoint BuildLegacyHistoryPoint(
        DateOnly date,
        IReadOnlyCollection<HistoryRow> rows,
        string targetCurrency,
        FxSnapshot fx)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        decimal netWorth = 0m;
        decimal accounts = 0m;
        var accountComplete = true;
        foreach (var row in rows)
        {
            var multiplier = fx.ToBaseOn(1m, row.Currency, date);
            if (!multiplier.HasValue)
            {
                missing.Add(FxSnapshot.Normalize(row.Currency));
                accountComplete = false;
                continue;
            }
            netWorth += row.NetWorth * multiplier.Value;
            accounts += row.Accounts * multiplier.Value;
        }

        return new(
            date,
            targetCurrency,
            accountComplete ? accounts : null,
            null,
            null,
            null,
            null,
            null,
            null,
            netWorth,
            false,
            missing.Order(StringComparer.Ordinal).Select(x => x.ToUpperInvariant()).ToArray());
    }

    private static WealthHistoryPoint ToHistoryPoint(WealthOverviewView overview) => new(
        overview.Date,
        overview.Currency,
        overview.Accounts.Amount,
        overview.ManualAssets.Amount,
        overview.Investments.Amount,
        overview.Loans.Amount,
        overview.OtherLiabilities.Amount,
        overview.TotalAssets,
        overview.TotalLiabilities,
        overview.NetWorth,
        overview.IsComplete,
        overview.MissingCurrencies);

    private async Task<List<HistoryRow>> ReadHistoryRowsAsync(
        Guid fullWorthSpaceId,
        Guid userId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            var predicates = new List<string>
            {
                "\"FullWorthSpaceId\"=@space",
                "\"UserId\"=@user"
            };
            AddParameter(command, "@space", fullWorthSpaceId);
            AddParameter(command, "@user", userId);
            if (from.HasValue)
            {
                predicates.Add("\"Date\">=@from");
                AddParameter(command, "@from", from.Value);
            }
            if (to.HasValue)
            {
                predicates.Add("\"Date\"<=@to");
                AddParameter(command, "@to", to.Value);
            }

            command.CommandText = $"""
                SELECT "Date", "Currency", "Accounts", "Assets", "Liabilities", "NetWorth",
                       "ManualAssets", "Investments", "Loans", "OtherLiabilities", "ComponentCurrency",
                       "IsComplete", "MissingCurrenciesJson"
                FROM "NetWorthSnapshots"
                WHERE {string.Join(" AND ", predicates)}
                ORDER BY "Date", "Currency";
                """;

            var rows = new List<HistoryRow>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new HistoryRow(
                    reader.GetFieldValue<DateOnly>(0),
                    reader.GetString(1),
                    reader.GetDecimal(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                    reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetBoolean(11),
                    reader.IsDBNull(12) ? [] : ParseMissingCurrencies(reader.GetString(12))));
            }
            return rows;
        }
        finally
        {
            if (closeWhenDone) await connection.CloseAsync();
        }
    }

    private static IReadOnlyList<string> ParseMissingCurrencies(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record EmergencyFundPreference(
        bool Enabled,
        decimal TargetAmount,
        Guid? AccountId,
        Guid? AccountGroupId);

    private static EmergencyFundPreference? ParseEmergencyFundPreference(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<EmergencyFundPreference>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeRequestedCurrency(string? requested, string fallback)
    {
        var normalized = FxSnapshot.Normalize(string.IsNullOrWhiteSpace(requested) ? fallback : requested);
        return normalized.Length == 3 && normalized.All(character => character is >= 'A' and <= 'Z')
            ? normalized
            : null;
    }

    private static int BalanceRank(string? type) => type switch
    {
        "interimAvailable" => 0,
        "closingAvailable" => 1,
        "closingBooked" => 2,
        "interimBooked" => 3,
        "expected" => 4,
        _ => 5
    };

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record NativeValue(decimal Amount, string Currency);

    private sealed record HistoryRow(
        DateOnly Date,
        string Currency,
        decimal Accounts,
        decimal LegacyAssets,
        decimal LegacyLiabilities,
        decimal NetWorth,
        decimal? ManualAssets,
        decimal? Investments,
        decimal? Loans,
        decimal? OtherLiabilities,
        string? ComponentCurrency,
        bool? IsComplete,
        IReadOnlyList<string> MissingCurrencies)
    {
        public bool HasExplicitComponents =>
            ManualAssets.HasValue && Investments.HasValue && Loans.HasValue && OtherLiabilities.HasValue;
    }
}

public static class WealthEndpoints
{
    public static IEndpointRouteBuilder MapWealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wealth").WithTags("Wealth");

        group.MapGet("/overview", async (
            Guid fullWorthSpaceId,
            string? currency,
            CurrentUserContext currentUser,
            WealthOverviewService service,
            CancellationToken ct) =>
            ToResult(await service.GetOverviewForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, currency, ct)));

        group.MapGet("/history", async (
            Guid fullWorthSpaceId,
            DateOnly? from,
            DateOnly? to,
            string? currency,
            CurrentUserContext currentUser,
            WealthOverviewService service,
            CancellationToken ct) =>
            ToResult(await service.GetHistoryForUserAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, from, to, currency, ct)));

        return app;
    }

    private static IResult ToResult(WealthOverviewOutcome outcome) => outcome.Status switch
    {
        WealthRequestStatus.Success => Results.Ok(outcome.Overview),
        WealthRequestStatus.NotFound => Results.NotFound(),
        WealthRequestStatus.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid wealth request." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };

    private static IResult ToResult(WealthHistoryOutcome outcome) => outcome.Status switch
    {
        WealthRequestStatus.Success => Results.Ok(outcome.History),
        WealthRequestStatus.NotFound => Results.NotFound(),
        WealthRequestStatus.Invalid => Results.BadRequest(new { error = outcome.Error ?? "Invalid wealth history request." }),
        _ => Results.StatusCode(StatusCodes.Status409Conflict)
    };
}
