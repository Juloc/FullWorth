using FullWorth.Backend.Data;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Was in <c>BankValidationRecords</c> steht: je Institut, was davon nachweislich funktioniert.
///
/// Die Abfrage stand bis 2026-09-14 im Handler selbst. Sie ist dort nicht falsch gewesen, aber sie
/// hatte keinen Namen - und eine Abfrage ohne Namen wird beim naechsten Handler kopiert.
/// </summary>
public sealed record BankCapabilityRow(
    string InstitutionKey,
    string Provider,
    string DisplayName,
    string Country,
    string? IconAssetKey,
    bool BalancesTested,
    bool TransactionsTested,
    bool PendingTested,
    bool MultiCurrencyTested,
    int? HistoryDepthDays,
    DateTimeOffset? LastValidatedAt,
    string? LastValidatedVersion,
    string? KnownLimitations)
{
    /// <summary>Als geprueft gilt ein Institut, wenn Salden UND Buchungen belegt sind.</summary>
    public bool Validated => BalancesTested && TransactionsTested;
}

public sealed class BankCapabilityStore(FullWorthDbContext db)
{
    public async Task<IReadOnlyList<BankCapabilityRow>> ListAsync(CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT "InstitutionKey","Provider","DisplayName","Country","IconAssetKey","BalancesTested","TransactionsTested","PendingTested","MultiCurrencyTested","HistoryDepthDays","LastValidatedAt","LastValidatedVersion","KnownLimitations"
FROM "BankValidationRecords" ORDER BY "DisplayName"
""");
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<BankCapabilityRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new BankCapabilityRow(
                RawSql.String(reader, "InstitutionKey"),
                RawSql.String(reader, "Provider"),
                RawSql.String(reader, "DisplayName"),
                RawSql.String(reader, "Country"),
                RawSql.NullableString(reader, "IconAssetKey"),
                RawSql.Bool(reader, "BalancesTested"),
                RawSql.Bool(reader, "TransactionsTested"),
                RawSql.Bool(reader, "PendingTested"),
                RawSql.Bool(reader, "MultiCurrencyTested"),
                reader.IsDBNull(reader.GetOrdinal("HistoryDepthDays")) ? null : RawSql.Int(reader, "HistoryDepthDays"),
                RawSql.NullableTimestamp(reader, "LastValidatedAt"),
                RawSql.NullableString(reader, "LastValidatedVersion"),
                RawSql.NullableString(reader, "KnownLimitations")));

        return rows;
    }
}
