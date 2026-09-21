using System.Text.Json.Serialization;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

public static class ReceiptImportSourceTypes
{
    public const string Upload = "upload";
    public const string Paperless = "paperless";
    public const string Folder = "folder";
}

public static class ReceiptImportStatuses
{
    public const string Importing = "importing";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";
    public const string Failed = "failed";
    public const string RolledBack = "rolled_back";
}

public static class ReceiptImportItemStatuses
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string NeedsReview = "needs_review";
    public const string SkippedDuplicate = "skipped_duplicate";
    public const string Failed = "failed";
}

public sealed class ReceiptImportOptions
{
    public const string SectionName = "ReceiptImports";
    public int MaxBatchItems { get; set; } = 500;
    public long MaxUploadBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>
    /// Wie viele Anfragen gleichzeitig an EINE Paperless-Instanz gehen duerfen (#127). Zwei, weil die
    /// typische Instanz ein kleiner Container neben der Anwendung ist und ein Import von 115 Dokumenten
    /// sie sonst fuer die Dauer des Imports unbenutzbar macht.
    /// </summary>
    public int PaperlessMaxConcurrentRequests { get; set; } = 2;

    /// <summary>Wie viele wartende Dokumente ein Durchgang des Hintergrunddienstes holt.</summary>
    public int PaperlessFetchBatchSize { get; set; } = 5;

    /// <summary>Wie oft der Hintergrunddienst nach wartenden Dokumenten sieht.</summary>
    public int PaperlessFetchIntervalSeconds { get; set; } = 10;

    /// <summary>Wie oft eine abgewiesene Anfrage hoechstens wiederholt wird, bevor sie als Fehler gilt.</summary>
    public int PaperlessMaxRetries { get; set; } = 3;
    public int PaperlessPageSize { get; set; } = 100;
    public int PaperlessTimeoutSeconds { get; set; } = 60;
    public int PaperlessAutoImportIntervalMinutes { get; set; } = 60;
    public string? InboxPath { get; set; }
    public bool FolderEnabled { get; set; }
    public bool FolderRecursive { get; set; } = true;
    public int FolderStableAgeSeconds { get; set; } = 10;
    public bool AutoStart { get; set; } = true;
    public string DefaultCurrency { get; set; } = "EUR";
}

public sealed record ReceiptImportBatchRow(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid UserId,
    string SourceType,
    string SourceName,
    string Currency,
    string Status,
    bool AutoStart,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    // Trailing and nullable: a batch that was never paused is indistinguishable from one from
    // before the column existed, which is what a backfill-free migration has to mean.
    DateTimeOffset? PausedAt = null,
    // Same reasoning as PausedAt (#141): a batch from before rollback tracking existed simply has none.
    DateTimeOffset? RolledBackAt = null)
{
    public bool IsPaused => PausedAt.HasValue;
    public bool IsRolledBack => RolledBackAt.HasValue;
}

/// <summary>The result of rolling back one receipt import batch - see ReceiptImportService.RollbackBatchAsync.</summary>
public sealed record ReceiptImportRollbackResult(int Removed, int Kept);

/// <summary>
/// Was die Quelle ueber einen Beleg weiss (#128). Ein Datensatz fuer alle Quellen - Paperless, Datei,
/// Kamera - damit nicht jede ihren eigenen Speicherweg bekommt. Fehlt ein Feld bei einer Quelle, steht
/// dort nichts; erfunden wird keines.
/// </summary>
public sealed record ReceiptSourceMetadata(
    DateOnly? DocumentDate = null,
    string? MimeType = null,
    string? Correspondent = null,
    IReadOnlyList<string>? Tags = null,
    string? Text = null,
    DateTimeOffset? ModifiedAt = null)
{
    public static readonly ReceiptSourceMetadata None = new();
}

/// <summary>Ein Beleg, der auf seinen Download aus Paperless wartet (#127).</summary>
public sealed record PendingPaperlessItem(
    Guid ItemId,
    Guid BatchId,
    Guid FullWorthSpaceId,
    string? SourceReference,
    Guid UserId,
    string Currency,
    bool AutoStart,
    DateTimeOffset? PausedAt);

public sealed record ReceiptImportItemRow(
    Guid Id,
    Guid BatchId,
    Guid FullWorthSpaceId,
    string SourceType,
    string ExternalKey,
    string DisplayName,
    string? SourceReference,
    string? ContentFingerprint,
    Guid? ReceiptScanJobId,
    Guid? PurchaseId,
    string Status,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? JobStatus = null,
    string? ReviewState = null,
    // #129: welcher Schritt gerade laeuft und womit verarbeitet wird. Beide kommen aus dem ohnehin
    // gejointen Scan-Job und sind der Unterschied zwischen "wird verarbeitet" und "OCR laeuft".
    string? JobStage = null,
    string? JobEngine = null);

public sealed record ReceiptImportBatchView(
    ReceiptImportBatchRow Batch,
    int Total,
    int Queued,
    int Processing,
    int Completed,
    int NeedsReview,
    int SkippedDuplicates,
    int Failed,
    IReadOnlyList<ReceiptImportItemRow> Items);

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
    string? Currency = null);

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
    DateTimeOffset UpdatedAt);

public sealed record PaperlessAutoImportTarget(
    Guid Id,
    Guid FullWorthSpaceId,
    Guid UserId,
    string Name,
    string? Query,
    bool AnalyzeAutomatically,
    string Currency,
    int? LastSeenDocumentId);

public sealed record PaperlessPreviewRequest(
    string? Query = null,
    int? DocumentTypeId = null,
    int? CorrespondentId = null,
    IReadOnlyList<int>? TagIds = null,
    DateOnly? CreatedFrom = null,
    DateOnly? CreatedTo = null,
    int? Limit = null);

public sealed record PaperlessImportRequest(
    PaperlessPreviewRequest Filter,
    IReadOnlyList<int>? DocumentIds = null,
    string? Currency = null,
    bool? AutoStart = null);

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
    DateTimeOffset? Modified = null);

public sealed record PaperlessPreviewResult(
    int Count,
    IReadOnlyList<PaperlessDocumentSummary> Documents,
    bool Truncated);

public sealed record FolderReceiptFile(
    string RelativePath,
    string FullPath,
    string FileName,
    long SizeBytes,
    DateTimeOffset LastWriteAt,
    string Fingerprint);

public sealed record FolderPreviewResult(
    bool Configured,
    [property: JsonIgnore] string? Root,
    int Count,
    long TotalBytes,
    IReadOnlyList<string> Files,
    bool Truncated);
