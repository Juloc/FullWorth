namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Wo ein Lohnabrechnungs-Fund aus Paperless steht - siehe die Migration PayslipImports.</summary>
public static class PayslipImportStatuses
{
    public const string Pending = "pending";
    public const string NeedsReview = "needs_review";
    public const string Confirmed = "confirmed";
    public const string Dismissed = "dismissed";
    public const string Failed = "failed";
}

public sealed class PayslipStorageOptions
{
    public const string SectionName = "PayslipStorage";

    // Unter dem Pension-Volume und nicht auf einem eigenen: es ist dasselbe - verschluesselte
    // persoenliche Dokumente, die diese Installation nie verlassen -, und ein weiteres Volume hiesse
    // eine Aenderung an jedem Compose-File, bevor die erste Abrechnung ankommt.
    public string RootPath { get; set; } = "/data/pension/payslips";

    // So gross wie der Download aus Paperless (PaperlessClient) - eine Datei, die dort durchgeht, passt hier.
    public long MaxDocumentBytes { get; set; } = 25L * 1024 * 1024;
}

/// <summary>Ein offener Fund: gefunden, wartet auf das Auslesen, oder ausgelesen und wartet auf die Bestaetigung.</summary>
public sealed record PayslipImportView(
    Guid Id,
    string Title,
    string Status,
    string? Error,
    string PaperlessUrl,
    PayslipExtractionResult? Extraction,
    DateTimeOffset CreatedAt);

/// <summary>Das abgelegte Original einer bestaetigten Abrechnung, der Verweis auf das Paperless-Dokument und wie sicher das Auslesen war, bevor der Benutzer korrigiert hat.</summary>
public sealed record PayslipDocumentRef(Guid ImportId, string PaperlessUrl, string? FileName, decimal? ReadConfidencePercent);

public sealed record PayslipDocumentContent(byte[] Content, string ContentType, string FileName);

/// <summary>Ein Fund, dessen PDF noch geholt und ausgelesen werden muss.</summary>
public sealed record PendingPayslipImport(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid UserId,
    string PaperlessSource,
    int PaperlessDocumentId,
    string Title);
