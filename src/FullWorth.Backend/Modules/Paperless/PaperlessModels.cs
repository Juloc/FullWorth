namespace FullWorth.Backend.Modules.Paperless;

/// <summary>
/// Tuning of the Paperless client. Bound to the section the receipt import always used
/// (<c>ReceiptImports</c>), so no existing setting changes its name; <c>MaxBatchItems</c> is read by
/// both classes on purpose - how many documents one listing returns is a Paperless limit here and a
/// batch limit for receipts.
/// </summary>
public sealed class PaperlessOptions
{
    public const string SectionName = "ReceiptImports";
    public int MaxBatchItems { get; set; } = 500;

    /// <summary>
    /// Wie viele Anfragen gleichzeitig an EINE Paperless-Instanz gehen duerfen (#127). Zwei, weil die
    /// typische Instanz ein kleiner Container neben der Anwendung ist und ein Import von 115 Dokumenten
    /// sie sonst fuer die Dauer des Imports unbenutzbar macht.
    /// </summary>
    public int PaperlessMaxConcurrentRequests { get; set; } = 2;

    /// <summary>Wie oft eine abgewiesene Anfrage hoechstens wiederholt wird, bevor sie als Fehler gilt.</summary>
    public int PaperlessMaxRetries { get; set; } = 3;
    public int PaperlessPageSize { get; set; } = 100;
    public int PaperlessTimeoutSeconds { get; set; } = 60;
    public int PaperlessAutoImportIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Wofuer ein Preset Dokumente holt. Die Art bestimmt, wer sie importiert
/// (<see cref="IPaperlessPresetImporter"/>); eine weitere Art ist eine weitere Konstante und ein
/// weiterer Importer.
/// </summary>
public static class PaperlessPresetKinds
{
    public const string Receipt = "receipt";
    public const string Payslip = "payslip";
    public const string Contract = "contract";

    public static bool IsKnown(string kind) => kind is Receipt or Payslip or Contract;

    /// <summary>
    /// Ob ein neues automatisches Preset nur holt, was nach dem Speichern erscheint. Belege ja: wer ein
    /// Preset anlegt, will nicht seine ganze Ablage als Einkaeufe. Lohnabrechnungen nein: der Sinn ist
    /// gerade, dass die vorhandenen mitkommen - sie landen ohnehin nur zur Pruefung.
    /// </summary>
    public static bool StartsFromNow(string kind) => kind == Receipt;
}

/// <summary>Verbindung samt entschluesseltem Token - nur im Speicher, nie in einer Antwort.</summary>
public sealed record PaperlessConnection(string BaseUrl, string Token, string? DefaultQuery);

/// <summary>Was ein Importer aus einem Preset-Lauf meldet; der Hintergrunddienst traegt es am Preset ein.</summary>
public sealed record PaperlessImportOutcome(int? MaxSeenDocumentId, bool Imported, string? Error);

/// <summary>
/// Importiert die neuen Dokumente eines automatischen Presets einer Art. Ein erwarteter Fehler
/// (Paperless antwortet nicht, ein Dokument laesst sich nicht lesen) kommt als <c>Error</c> zurueck und
/// steht danach am Preset - er wirft nicht, sonst braeche er den Lauf der anderen Presets ab.
/// </summary>
public interface IPaperlessPresetImporter
{
    /// <summary>Die Arten, die dieser Importer holt; das Preset nennt, welche gerade dran ist.</summary>
    IReadOnlyCollection<string> Kinds { get; }

    Task<PaperlessImportOutcome> ImportAsync(PaperlessImportPresetView preset, PaperlessConnection connection, CancellationToken ct);
}

public sealed class PaperlessException(string message) : InvalidOperationException(message);

public sealed record PaperlessStoredConnection(
    Guid FullWorthSpaceId,
    Guid UserId,
    string BaseUrl,
    string ApiTokenProtected,
    string? DefaultQuery,
    bool IsEnabled,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record PaperlessConnectionWrite(
    string BaseUrl,
    string ApiToken,
    string? DefaultQuery = null,
    bool IsEnabled = true);

public sealed record PaperlessConnectionView(
    Guid FullWorthSpaceId,
    string BaseUrl,
    bool Configured,
    string? DefaultQuery,
    bool IsEnabled,
    DateTimeOffset? LastSyncAt,
    DateTimeOffset UpdatedAt);

public sealed record PaperlessFilterOption(int Id, string Name);

public sealed record PaperlessFilterOptionsView(
    IReadOnlyList<PaperlessFilterOption> Tags,
    IReadOnlyList<PaperlessFilterOption> DocumentTypes,
    IReadOnlyList<PaperlessFilterOption> Correspondents,
    IReadOnlyList<PaperlessFilterOption> StoragePaths,
    IReadOnlyList<PaperlessFilterOption> CustomFields);

public sealed record PaperlessImportPresetWrite(
    string Name,
    string? Query = null,
    string? EditorJson = null,
    bool AutoImport = false,
    bool AnalyzeAutomatically = true,
    string? Currency = null,
    string Kind = PaperlessPresetKinds.Receipt);

public sealed record PaperlessImportPresetView(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid UserId,
    string Name,
    string? Query,
    string? EditorJson,
    bool AutoImport,
    bool AnalyzeAutomatically,
    string Currency,
    int? LastSeenDocumentId,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastImportedAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Kind);

public sealed record PaperlessPreviewRequest(
    string? Query = null,
    int? DocumentTypeId = null,
    int? CorrespondentId = null,
    IReadOnlyList<int>? TagIds = null,
    DateOnly? CreatedFrom = null,
    DateOnly? CreatedTo = null,
    int? Limit = null);


public sealed record PaperlessDocumentSummary(
    int Id,
    string Title,
    DateOnly? Created,
    int? DocumentType,
    int? Correspondent,
    IReadOnlyList<int> Tags,
    string? OriginalFileName = null,
    bool Imported = false,
    string? MimeType = null,
    string? Content = null,
    DateTimeOffset? Modified = null,
    // MD5 der Originaldatei, so wie Paperless sie fuehrt. Daran erkennt FullWorth ein Dokument wieder,
    // das es selbst abgelegt hat (siehe PaperlessUploads), und holt es nicht ein zweites Mal als Kauf.
    string? Checksum = null);

public sealed record PaperlessPreviewResult(
    int Count,
    IReadOnlyList<PaperlessDocumentSummary> Documents,
    bool Truncated);

/// <summary>Was an Paperless geht: die Datei und die Angaben, die FullWorth aus der Analyse kennt.</summary>
public sealed record PaperlessUploadRequest(
    byte[] Content,
    string FileName,
    string ContentType,
    string Title,
    DateOnly? Created,
    int? CorrespondentId);

/// <summary>Antwort von Paperless auf eine Ablage: die Nummer des Verarbeitungsauftrags, oder warum es nicht ging.</summary>
public sealed record PaperlessUploadOutcome(bool Success, string? TaskId, string? Error);

