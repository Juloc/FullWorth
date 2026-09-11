namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// The shapes and the seams of the bAV document pipeline (step 2 of docs/PENSION.md):
///
/// <code>
/// upload → store encrypted → text layer, OCR only if there is none
///        → deterministic parser → optional Codex structuring for what it could not fill
///        → review screen (every field editable, each with its page and confidence)
///        → commit: contract (new or matched) + snapshot + contributions + allocation + costs
/// </code>
///
/// Three rules from the brief are enforced by these types, not by discipline:
///
/// <list type="bullet">
///   <item>Nothing extracted is stored without review: the pipeline only ever produces a
///         <see cref="BavDocumentDraft"/>, and only a commit turns a draft into rows.</item>
///   <item><see cref="BavContributionDraft"/> carries <b>no tax or social-security effect</b>. A statement
///         almost never states one, and <c>BavContribution</c> refuses an amount without a source — so
///         leaving the fields out of the draft is what makes it impossible for an extractor (least of all
///         an AI one) to invent a tax effect.</item>
///   <item>An extracted cost is a draft cost with <see cref="BavCostDraft.IsEstimated"/> set where the
///         document only implied it, and an estimate is committed as an estimate.</item>
/// </list>
///
/// A draft never carries raw document text. <see cref="BavFieldProvenance.MatchedLabel"/> is the form
/// label the extractor recognised ("Vertragsguthaben"), which is the document's vocabulary, not its
/// content — the same distinction the payslip extractor already makes with its detected labels. No
/// amount, no name and no policy number reaches a log line through any of this.
/// </summary>
public sealed record BavDocumentDraft(
    BavContractDraft Contract,
    BavSnapshotDraft Snapshot,
    BavContributionDraft? Contribution,
    IReadOnlyList<BavAllocationDraft> Allocations,
    IReadOnlyList<BavCostDraft> Costs,
    /// <summary>Where each filled field came from, so the review screen can show page and confidence.</summary>
    IReadOnlyList<BavFieldProvenance> Provenance,
    /// <summary>0..1 over the fields that matter for a snapshot. Never a promise that the values are right.</summary>
    decimal Confidence,
    /// <summary>See <see cref="BavExtractionSources"/>. <c>manual</c> once a person has edited the draft.</summary>
    string Source,
    int PageCount,
    /// <summary>False means the text came from OCR, which is the weaker source and worth saying so.</summary>
    bool TextLayerUsed,
    /// <summary>Field names nothing could fill. The review screen asks for these rather than guessing.</summary>
    IReadOnlyList<string> Unresolved)
{
    public static BavDocumentDraft Empty(int pageCount, bool textLayerUsed, string source) => new(
        new BavContractDraft(), new BavSnapshotDraft(), null, [], [], [], 0m, source, pageCount, textLayerUsed, []);
}

/// <summary>
/// One field's origin. <see cref="Field"/> is a dotted path into the draft ("snapshot.balance") so the
/// review screen can attach it to the input the user is looking at.
/// </summary>
public sealed record BavFieldProvenance(
    string Field,
    int? Page,
    decimal Confidence,
    /// <summary>See <see cref="BavExtractionSources"/>: which pass produced this one field.</summary>
    string Source,
    string? MatchedLabel = null);

/// <summary>
/// The contract facts a statement can state. All nullable: a draft is allowed to be incomplete, and a
/// missing field is asked for rather than defaulted.
/// </summary>
public sealed record BavContractDraft(
    string? ProviderName = null,
    string? TariffName = null,
    /// <summary>Read for matching and for the commit. Never returned by the API, never logged.</summary>
    string? PolicyNumber = null,
    string? ImplementationRoute = null,
    string? EmployerName = null,
    string? PolicyHolderName = null,
    string? InsuredPersonName = null,
    DateOnly? StartDate = null,
    DateOnly? RetirementDate = null,
    string? Currency = null,
    decimal? GuaranteeQuotaPercent = null,
    decimal? GuaranteedAnnuityFactor = null,
    string? Status = null);

/// <summary>
/// The dated state. The guaranteed and the projected figures stay in separate fields all the way from
/// the document to the database, because a projection rendered where a guarantee belongs is the single
/// most expensive mistake this feature could make.
/// </summary>
public sealed record BavSnapshotDraft(
    DateOnly? EffectiveDate = null,
    string? Currency = null,
    decimal? Balance = null,
    decimal? GuaranteedBalance = null,
    decimal? SurrenderValue = null,
    decimal? SecurityAssetsAmount = null,
    decimal? FundAssetsAmount = null,
    decimal? GuaranteedCapitalAtRetirement = null,
    decimal? GuaranteedMonthlyAnnuity = null,
    decimal? ProjectedCapitalAtRetirement = null,
    decimal? ProjectedMonthlyAnnuity = null,
    /// <summary>Mandatory with a projected figure: the store and CK_BavSnapshots_Projection both refuse it otherwise.</summary>
    decimal? ProjectionReturnPercent = null,
    /// <summary>See <see cref="BavProjectionBases"/>. A document's own forecast is <c>document_forecast</c>.</summary>
    string? ProjectionBasis = null);

/// <summary>
/// The contribution arrangement. Deliberately without <c>TaxSavingAmount</c>,
/// <c>SocialSecuritySavingAmount</c> and <c>NetEffortAmount</c> — see the type-level rule on
/// <see cref="BavDocumentDraft"/>. The employee/employer split is kept apart because the employee share
/// is an outflow and the employer share never is.
/// </summary>
public sealed record BavContributionDraft(
    DateOnly? ValidFrom = null,
    string? Cycle = null,
    string? Currency = null,
    decimal? EmployeeAmount = null,
    decimal? EmployerSubsidyAmount = null,
    decimal? EmployerAmount = null,
    /// <summary>The total as the document printed it. Kept so a mismatch with the shares can be shown, not fixed.</summary>
    decimal? StatedTotalAmount = null);

public sealed record BavAllocationDraft(
    string FundName,
    string? Isin = null,
    decimal? WeightPercent = null,
    decimal? Amount = null,
    string? Currency = null,
    decimal? OngoingChargesPercent = null,
    bool OngoingChargesEstimated = false,
    string? AssetClass = null);

public sealed record BavCostDraft(
    string Kind,
    string Basis,
    decimal? Amount = null,
    decimal? Percent = null,
    string? Timing = null,
    /// <summary>True when the document did not state this cost outright. Committed as an estimate.</summary>
    bool IsEstimated = false,
    /// <summary>Mandatory when <see cref="IsEstimated"/> is true — the store refuses an unexplained estimate.</summary>
    string? EstimateBasis = null,
    bool ContinuesWhenPaidUp = true);

/// <summary>
/// The document's text, one entry per page. A pension statement is multi-page — which is the first of
/// the two reasons the payslip pipeline could not be reused as-is (it OCRs page one only).
/// </summary>
public sealed record BavDocumentText(IReadOnlyList<string> Pages, bool TextLayerUsed)
{
    public int PageCount => Pages.Count;

    /// <summary>All pages joined, for a parser that does not care where a value sat.</summary>
    public string All => string.Join("\n", Pages);

    public bool IsEmpty => Pages.All(string.IsNullOrWhiteSpace);
}

/// <summary>
/// Text layer first, OCR only when there is none: a digitally generated statement has exact text, and
/// running Tesseract over it would replace correct numbers with recognised ones.
/// </summary>
public interface IBavDocumentTextSource
{
    Task<BavDocumentText> ReadAsync(byte[] content, string extension, CancellationToken ct);
}

/// <summary>The deterministic parser. Pure: same text in, same draft out, on every host and every locale.</summary>
public interface IBavDocumentParser
{
    BavDocumentDraft Parse(BavDocumentText text);
}

/// <summary>
/// The optional AI pass. It may only fill fields the deterministic parser left empty, never overwrite
/// one it filled, and it must be a no-op when the Codex bridge is absent — a self-hosted installation
/// without a bridge has to reach the same review screen, only with more fields left to type.
/// </summary>
public interface IBavDocumentAiStructurer
{
    bool IsEnabled { get; }

    Task<BavDocumentDraft> EnrichAsync(Guid userId, BavDocumentDraft draft, BavDocumentText text, CancellationToken ct);
}

/// <summary>
/// Where the uploaded file itself lives. The second reason the payslip pipeline did not fit: a payslip
/// is never persisted, while a pension document is a contract document and has to be kept — so it is
/// kept encrypted, and it never leaves the installation.
/// </summary>
public interface IBavDocumentBlobStore
{
    Task<BavStoredBlob> WriteAsync(Guid fullWorthSpaceId, Guid documentId, string extension, byte[] content, CancellationToken ct);

    Task<byte[]?> ReadAsync(string storagePath, string? encryptionScheme, CancellationToken ct);

    Task DeleteAsync(string storagePath, CancellationToken ct);
}

/// <summary><see cref="EncryptionScheme"/> is stored so a future re-key knows what it has to re-wrap.</summary>
public sealed record BavStoredBlob(string StoragePath, string EncryptionScheme);

public static class BavDocumentEncryptionSchemes
{
    public const string AesGcmV1 = "aesgcm-v1";

    /// <summary>Dev/test without a configured key, mirroring <c>FieldCipher.Null</c>. Never in Production.</summary>
    public const string None = "none";
}

/// <summary>A document as the API returns it. No draft, no file name guessing, no policy number.</summary>
public sealed record BavDocumentView(
    Guid Id,
    Guid? BavContractId,
    string Kind,
    string? OriginalFileName,
    string? MediaType,
    long ByteSize,
    int? PageCount,
    string ExtractionStatus,
    decimal? ExtractionConfidence,
    string? ExtractionSource,
    bool? TextLayerUsed,
    /// <summary>A category ("no_text", "tool_missing", "unsupported"), never document content.</summary>
    string? ExtractionError,
    DateTimeOffset? ExtractedAt,
    Guid? ReviewedByUserId,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// The review payload: the document, the draft as it currently stands, and — for an unmatched document
/// — what the match rules think. A weak match is reported with the rule that fired so a person confirms
/// it; only an unmatched document offers to create a contract.
/// </summary>
public sealed record BavDocumentDetailView(
    BavDocumentView Document,
    BavDocumentDraft? Draft,
    BavContractMatchResult? Match,
    /// <summary>Stated doubts ("shares do not add up to the printed total"), shown and never auto-fixed.</summary>
    IReadOnlyList<string> Warnings);

public sealed record BavDocumentReviewRequest(BavDocumentDraft Draft, string? Kind = null);

/// <summary>
/// The commit. Either <see cref="ContractId"/> names the contract this statement belongs to, or
/// <see cref="CreateContract"/> asks for a new one — never both, and never neither.
/// </summary>
public sealed record BavDocumentCommitRequest(
    BavDocumentDraft Draft,
    Guid? ContractId = null,
    bool CreateContract = false,
    string? Kind = null,
    bool IncludeInNetWorth = true);

/// <summary>
/// What the commit actually wrote. <see cref="Skipped"/> names what it refused and why — a re-uploaded
/// document whose snapshot date already exists adds nothing, and saying so beats a silent success.
/// </summary>
public sealed record BavDocumentCommitResult(
    Guid DocumentId,
    Guid ContractId,
    Guid? SnapshotId,
    bool ContractCreated,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped);

public sealed record BavDocumentOutcome(
    BavMutationResult Result,
    object? Value = null,
    string? Error = null,
    /// <summary>Set on a conflict from an upload: the same file is already here, with its draft.</summary>
    Guid? ExistingDocumentId = null);
