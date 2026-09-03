namespace FullWorth.Backend.Modules.Purchases;

public static class ReceiptScanJobStatuses
{
    public const string Draft = "draft";
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Error = "error";
}

public sealed class ReceiptScanJobRow
{
    public Guid Id { get; set; }
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid PurchaseId { get; set; }
    // Compatibility summary of the first source. Canonical source metadata lives in ReceiptScanSources.
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Status { get; set; } = ReceiptScanJobStatuses.Draft;
    public string Stage { get; set; } = "draft";
    public string? Engine { get; set; }
    public string? Error { get; set; }
    public string? WarningsJson { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// Kept flat rather than inheriting from ReceiptScanJobRow because EF Core's unmapped SqlQuery<T>
// path is intentionally simple and should not depend on inheritance mapping behavior.
public sealed class ReceiptScanJobView
{
    public Guid Id { get; set; }
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public Guid PurchaseId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Status { get; set; } = ReceiptScanJobStatuses.Draft;
    public string Stage { get; set; } = "draft";
    public string? Engine { get; set; }
    public string? Error { get; set; }
    public string? WarningsJson { get; set; }
    public int Attempts { get; set; }
    public int SourceCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? Merchant { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? Currency { get; set; }
}

public sealed class ReceiptScanSourceRow
{
    public Guid Id { get; set; }
    public Guid ReceiptScanJobId { get; set; }
    public Guid? PurchaseDocumentId { get; set; }
    public int SortOrder { get; set; }
    public string SourceType { get; set; } = "image";
    public string OriginalFileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public int? PageNumber { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record ReceiptScanOrderRequest(IReadOnlyList<Guid> SourceIds);
public sealed record ReceiptScanEnqueueOutcome(bool Success, ReceiptScanJobView? Job = null, string? Error = null);
public sealed record ReceiptScanSourcesOutcome(bool Success, IReadOnlyList<ReceiptScanSourceRow>? Sources = null, string? Error = null);
public sealed record ReceiptScanMutationOutcome(bool Success, ReceiptScanJobView? Job = null, string? Error = null);
