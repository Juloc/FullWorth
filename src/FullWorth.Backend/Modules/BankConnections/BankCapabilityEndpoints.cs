using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Was die angebundenen Institute koennen - Salden, Buchungen, vorgemerkte Posten, Fremdwaehrung,
/// wie weit die Historie reicht - plus die Institute, die geplant und noch nicht geprueft sind.
///
/// Lag in Modules/Parity/BankingExperienceParityModule.cs zusammen mit drei Routen zur Darstellung
/// von Konten. Vier Routen, zwei Fachbereiche, eine Datei: die drei anderen sind nach Accounts
/// gezogen, diese eine hierher.
/// </summary>
public static class BankCapabilityEndpoints
{
    private static readonly object[] PlannedInstitutions =
    [
        new { institutionKey = "dkb", provider = "enable-banking", displayName = "DKB", country = "DE", iconAssetKey = "dkb", validated = false },
        new { institutionKey = "ing", provider = "enable-banking", displayName = "ING", country = "DE", iconAssetKey = "ing", validated = false },
        new { institutionKey = "paypal", provider = "enable-banking", displayName = "PayPal", country = "DE", iconAssetKey = "paypal", validated = false },
        new { institutionKey = "c24", provider = "enable-banking", displayName = "C24 Bank", country = "DE", iconAssetKey = "c24", validated = false },
        new { institutionKey = "revolut", provider = "enable-banking", displayName = "Revolut", country = "LT", iconAssetKey = "revolut", validated = false }
    ];

    public static IEndpointRouteBuilder MapBankCapabilityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/bank-capabilities", GetCapabilities).WithTags("Banking");
        return app;
    }

    private static async Task<IResult> GetCapabilities(
        Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT "InstitutionKey","Provider","DisplayName","Country","IconAssetKey","BalancesTested","TransactionsTested","PendingTested","MultiCurrencyTested","HistoryDepthDays","LastValidatedAt","LastValidatedVersion","KnownLimitations"
FROM "BankValidationRecords" ORDER BY "DisplayName"
""");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new
            {
                institutionKey = RawSql.String(reader, "InstitutionKey"),
                provider = RawSql.String(reader, "Provider"),
                displayName = RawSql.String(reader, "DisplayName"),
                country = RawSql.String(reader, "Country"),
                iconAssetKey = RawSql.NullableString(reader, "IconAssetKey"),
                balancesTested = RawSql.Bool(reader, "BalancesTested"),
                transactionsTested = RawSql.Bool(reader, "TransactionsTested"),
                pendingTested = RawSql.Bool(reader, "PendingTested"),
                multiCurrencyTested = RawSql.Bool(reader, "MultiCurrencyTested"),
                historyDepthDays = reader.IsDBNull(reader.GetOrdinal("HistoryDepthDays")) ? (int?)null : RawSql.Int(reader, "HistoryDepthDays"),
                lastValidatedAt = RawSql.NullableTimestamp(reader, "LastValidatedAt"),
                lastValidatedVersion = RawSql.NullableString(reader, "LastValidatedVersion"),
                knownLimitations = RawSql.NullableString(reader, "KnownLimitations"),
                validated = RawSql.Bool(reader, "BalancesTested") && RawSql.Bool(reader, "TransactionsTested")
            });
        }
        return Results.Ok(rows.Count == 0 ? PlannedInstitutions : rows);
    }

}
