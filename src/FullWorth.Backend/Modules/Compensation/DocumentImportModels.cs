using System.Text.Json;
using FullWorth.Backend.Modules.Paperless;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Wo ein Fund aus Paperless steht - siehe die Migration CompensationDocumentImports.</summary>
public static class DocumentImportStatuses
{
    public const string Pending = "pending";
    public const string NeedsReview = "needs_review";
    public const string Confirmed = "confirmed";
    public const string Dismissed = "dismissed";
    public const string Failed = "failed";
}

/// <summary>Was ein Fund werden kann. Dieselben Namen wie die Presets (<c>PaperlessPresetKinds</c>), damit ein Fund seiner Art folgt.</summary>
public static class DocumentKinds
{
    public const string Payslip = PaperlessPresetKinds.Payslip;
    public const string Contract = PaperlessPresetKinds.Contract;
}

/// <summary>
/// Liest ein heruntergeladenes Dokument einer Art in ihre Felder. Das Ergebnis wird als JSON am Fund
/// abgelegt und dem Benutzer zur Pruefung gezeigt; was die Felder bedeuten, weiss nur die Art.
/// </summary>
public interface IDocumentReader
{
    string Kind { get; }

    Task<object> ReadAsync(Guid userId, Stream content, string fileName, CancellationToken ct);
}

public sealed class CompensationStorageOptions
{
    public const string SectionName = "CompensationStorage";

    // Unter dem Pension-Volume und nicht auf einem eigenen: es ist dasselbe - verschluesselte
    // persoenliche Dokumente, die diese Installation nie verlassen -, und ein weiteres Volume hiesse
    // eine Aenderung an jedem Compose-File, bevor die erste Abrechnung ankommt.
    public string RootPath { get; set; } = "/data/pension/compensation";

    // So gross wie der Download aus Paperless (PaperlessClient) - eine Datei, die dort durchgeht, passt hier.
    public long MaxDocumentBytes { get; set; } = 25L * 1024 * 1024;
}

/// <summary>Ein offener Fund: gefunden, wartet auf das Auslesen, oder ausgelesen und wartet auf die Bestaetigung.</summary>
public sealed record DocumentImportView(
    Guid Id,
    string Title,
    string Status,
    string? Error,
    string PaperlessUrl,
    JsonElement? Extraction,
    DateTimeOffset CreatedAt);

/// <summary>Das abgelegte Original einer bestaetigten Abrechnung, der Verweis auf das Paperless-Dokument und wie sicher das Auslesen war, bevor der Benutzer korrigiert hat.</summary>
public sealed record DocumentRef(Guid ImportId, string PaperlessUrl, int PaperlessDocumentId, string? FileName, decimal? ReadConfidencePercent);

public sealed record DocumentContent(byte[] Content, string ContentType, string FileName);

/// <summary>Ein Fund, dessen PDF noch geholt und ausgelesen werden muss.</summary>
public sealed record PendingDocumentImport(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid UserId,
    string Kind,
    string PaperlessSource,
    int PaperlessDocumentId,
    string Title);
