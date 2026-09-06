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
    public int MaxParallelImports { get; set; } = 2;
    public int PaperlessPageSize { get; set; } = 100;
    public int PaperlessTimeoutSeconds { get; set; } = 60;
    public int PaperlessAutoImportIntervalMinutes { get; set; } = 60;
    public string? InboxPath { get; set; }
    public bool FolderEnabled { get; set; }
    public bool FolderRecursive { get; set; } = true;
    public int FolderScanIntervalSeconds { get; set; } = 60;
    public int FolderStableAgeSeconds { get; set; } = 10;
    public bool AutoStart { get; set; } = true;
    public Guid? FolderFullWorthSpaceId { get; set; }
    public Guid? FolderUserId { get; set; }
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
    DateTimeOffset? CompletedAt);

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
    string? ReviewState = null);

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
    bool Imported = false);

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
