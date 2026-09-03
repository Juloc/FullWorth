using System.Globalization;
using System.Text;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;

namespace FullWorth.Backend.Modules.Tax;

public sealed class TaxExportService(FullWorthDbContext db, TaxStore store, AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);

    public async Task<TaxYearExport?> BuildAsync(Guid userId, Guid fullWorthSpaceId, int taxYear, CancellationToken ct)
    {
        var candidates = await new TaxCandidateViewStore(db, store)
            .ListAsync(userId, fullWorthSpaceId, taxYear, null, ct);
        if (candidates is null) return null;

        var rows = candidates
            .OrderBy(x => x.SourceDate)
            .ThenBy(x => x.SourceTitle)
            .ThenBy(x => x.Id)
            .Select(x => new TaxExportRow(
                x.SourceDate,
                x.SourceTitle,
                x.Explanation,
                x.TaxCategoryName ?? x.TaxCategoryCode ?? string.Empty,
                x.GrossAmount,
                x.EligibleAmount,
                x.EligiblePercentage,
                x.Currency,
                x.HasDocument,
                x.SourceType ?? string.Empty,
                x.Status))
            .ToList();

        audit.Record(fullWorthSpaceId, userId, "tax.export.generated", "TaxYear", null);
        await db.SaveChangesAsync(ct);
        return new TaxYearExport(taxYear, DateTimeOffset.UtcNow, rows);
    }

    public static byte[] ToCsv(TaxYearExport export)
    {
        var sb = new StringBuilder();
        sb.AppendLine("date,source,description,tax_category,gross_amount,eligible_amount,eligible_percentage,currency,document_available,source_type,status");
        foreach (var row in export.Rows)
        {
            Append(sb, row.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Append(sb, row.Source);
            Append(sb, row.Description);
            Append(sb, row.TaxCategory);
            Append(sb, row.GrossAmount.ToString(CultureInfo.InvariantCulture));
            Append(sb, row.EligibleAmount.ToString(CultureInfo.InvariantCulture));
            Append(sb, row.EligiblePercentage.ToString(CultureInfo.InvariantCulture));
            Append(sb, row.Currency);
            Append(sb, row.HasDocument ? "true" : "false");
            Append(sb, row.SourceType);
            Append(sb, row.Status, last: true);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
    }

    private static void Append(StringBuilder sb, string? value, bool last = false)
    {
        var escaped = (value ?? string.Empty).Replace("\"", "\"\"");
        sb.Append('"').Append(escaped).Append('"');
        sb.Append(last ? '\n' : ',');
    }
}
