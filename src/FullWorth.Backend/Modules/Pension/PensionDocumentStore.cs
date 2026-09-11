using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// The bAV document path: upload, extraction, review, commit (step 2 of docs/PENSION.md). It follows
/// <see cref="PensionStore"/> in shape and in access order — reads need space membership, writes
/// additionally need the owner role, and the ordering is not-found → forbidden → conflict → invalid so
/// a non-member never learns whether a document exists.
///
/// The one rule the whole type exists to enforce: <b>no extracted value is stored as a contract value
/// without a review</b>. Extraction writes a draft into <c>BavDocument.ExtractionDraftJson</c> and
/// nothing else; only <see cref="CommitAsync"/> turns that draft into rows, and it does so exclusively
/// through <see cref="PensionStore"/>'s public writes so the domain rules (asset creation and link,
/// <c>IsCurrent</c> by newest effective date, the projection and tax-effect constraints, the snapshot
/// identity conflict) are enforced in exactly one place.
///
/// Nothing here writes a policy number, a file name, an amount or a line of document content to a log
/// or to an audit record.
/// </summary>
public sealed class PensionDocumentStore(
    FullWorthDbContext db,
    PensionStore contracts,
    IBavDocumentBlobStore blobs,
    IBavDocumentTextSource textSource,
    IBavDocumentParser parser,
    IBavDocumentAiStructurer ai,
    AuditService? auditService = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);

    /// <summary>
    /// The hard upload cap. Above this nothing is stored at all: a pension statement is a handful of
    /// pages, and a larger file is either not one or an attempt to fill the volume.
    /// </summary>
    public const long MaxDocumentBytes = 12L * 1024 * 1024;

    /// <summary>
    /// Above this the file is stored and kept reviewable but is not put through OCR, which would hold
    /// a rasterised page per core in memory. The document survives with <c>too_large</c> so the user
    /// can still type the values in, which beats refusing the upload of a real statement.
    /// </summary>
    private const long MaxExtractionBytes = 8L * 1024 * 1024;

    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.Ordinal) { ".pdf", ".jpg", ".jpeg", ".png" };

    // Only the draft travels through here, and only between this process and its own database column.
    private static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web);

    // ---- authorization ----

    private Task<bool> IsMemberAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == spaceId && member.UserId == userId, ct);

    private Task<bool> IsOwnerAsync(Guid userId, Guid spaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == spaceId
                                && member.UserId == userId
                                && member.Role == FullWorthSpaceRoles.Owner, ct);

    // ---- upload ----

    /// <summary>
    /// Stores the file, then extracts. The two are deliberately separate outcomes: a failed extraction
    /// keeps the row and the blob so the document can still be reviewed by hand, because losing a
    /// statement because Tesseract is missing would be the worse failure.
    /// </summary>
    public async Task<BavDocumentOutcome> UploadAsync(
        Guid userId, Guid spaceId, byte[] content, string? fileName, string? kind, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        if (content.Length == 0) return new(BavMutationResult.Invalid, Error: "An empty file carries no document.");
        if (content.Length > MaxDocumentBytes)
            return new(BavMutationResult.Invalid, Error: $"A document may not exceed {MaxDocumentBytes / (1024 * 1024)} MB.");

        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            return new(BavMutationResult.Invalid, Error: "A pension document is a PDF, a JPEG or a PNG.");

        var documentKind = Trim(kind) ?? BavDocumentKinds.AnnualStatement;
        if (!BavDocumentKinds.Allowed.Contains(documentKind))
            return new(BavMutationResult.Invalid, Error: $"Unknown document kind '{documentKind}'.");

        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        // Re-uploading the same document creates nothing (docs/PENSION.md). The caller is pointed at
        // the document it already has, so the review it is looking for is the one already in progress.
        var existing = await db.BavDocuments.AsNoTracking()
            .Where(row => row.FullWorthSpaceId == spaceId && row.Sha256 == sha)
            .Select(row => row.Id)
            .FirstOrDefaultAsync(ct);
        if (existing != Guid.Empty)
            return new(BavMutationResult.Conflict,
                await DetailAsync(userId, spaceId, existing, ct),
                "This document has already been uploaded.",
                existing);

        var documentId = Guid.NewGuid();
        var blob = await blobs.WriteAsync(spaceId, documentId, extension, content, ct);

        var document = new BavDocument
        {
            Id = documentId,
            FullWorthSpaceId = spaceId,
            Sha256 = sha,
            Kind = documentKind,
            OriginalFileName = SafeFileName(fileName),
            MediaType = MediaType(extension),
            ByteSize = content.Length,
            StoragePath = blob.StoragePath,
            EncryptionScheme = blob.EncryptionScheme,
            ExtractionStatus = BavExtractionStatuses.Pending,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.BavDocuments.Add(document);
        audit.Record(spaceId, userId, "pension.document.uploaded", "BavDocument", documentId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // The row is the only index of the blob. Without it the bytes are unreachable and would sit
            // there encrypted forever, so they go with the failed insert.
            db.Entry(document).State = EntityState.Detached;
            await SafeDeleteBlobAsync(blob.StoragePath, ct);
            throw;
        }

        await ExtractAsync(userId, document, content, extension, ct);
        await db.SaveChangesAsync(ct);

        return new(BavMutationResult.Success, await DetailAsync(userId, spaceId, documentId, ct));
    }

    /// <summary>
    /// Text layer → deterministic parser → optional AI enrichment, and the result lands in the draft
    /// column and nowhere else. Every failure is recorded as one of four categories: this string is
    /// returned to the browser, so a tool's own message — which can quote the document — must never
    /// become its value.
    /// </summary>
    private async Task ExtractAsync(Guid userId, BavDocument document, byte[] content, string extension, CancellationToken ct)
    {
        document.ExtractedAt = DateTimeOffset.UtcNow;

        if (content.Length > MaxExtractionBytes)
        {
            Fail(document, BavExtractionErrors.TooLarge);
            return;
        }

        BavDocumentText text;
        try
        {
            text = await textSource.ReadAsync(content, extension, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fail(document, Categorize(error));
            return;
        }

        document.PageCount = text.PageCount;
        document.TextLayerUsed = text.TextLayerUsed;

        if (text.IsEmpty)
        {
            // A scan with no recognisable text is not an error in the file; it is a document that has
            // to be typed in. Saying which of the four it was is what lets the UI say what to do next.
            Fail(document, BavExtractionErrors.NoText);
            return;
        }

        BavDocumentDraft draft;
        try
        {
            draft = parser.Parse(text);
            // The AI pass is optional by construction: with no bridge the deterministic draft is what
            // the review screen gets, only with more fields left to fill in.
            if (ai.IsEnabled) draft = await ai.EnrichAsync(userId, draft, text, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Fail(document, Categorize(error));
            return;
        }

        document.ExtractionStatus = BavExtractionStatuses.Parsed;
        document.ExtractionSource = draft.Source;
        document.ExtractionConfidence = Clamp(draft.Confidence);
        document.PageCount = draft.PageCount > 0 ? draft.PageCount : text.PageCount;
        document.TextLayerUsed = draft.TextLayerUsed;
        document.ExtractionError = null;
        document.ExtractionDraftJson = JsonSerializer.Serialize(draft, DraftJson);
    }

    private static void Fail(BavDocument document, string category)
    {
        document.ExtractionStatus = BavExtractionStatuses.Failed;
        document.ExtractionError = category;
        document.ExtractionDraftJson = null;
        document.ExtractionConfidence = null;
        document.ExtractionSource = null;
    }

    /// <summary>
    /// A missing external tool is the one failure a self-hoster can act on, so it gets its own
    /// category. Everything else collapses into <c>unsupported</c> rather than into a message whose
    /// text this code does not control.
    /// </summary>
    private static string Categorize(Exception error) => error switch
    {
        OutOfMemoryException => BavExtractionErrors.TooLarge,
        FileNotFoundException or DllNotFoundException or System.ComponentModel.Win32Exception
            => BavExtractionErrors.ToolMissing,
        _ => BavExtractionErrors.Unsupported
    };

    // ---- reads ----

    public async Task<IReadOnlyList<BavDocumentView>?> ListAsync(Guid userId, Guid spaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return null;

        var rows = await db.BavDocuments.AsNoTracking()
            .Where(row => row.FullWorthSpaceId == spaceId)
            .OrderByDescending(row => row.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    public Task<BavDocumentDetailView?> GetAsync(Guid userId, Guid spaceId, Guid documentId, CancellationToken ct) =>
        DetailAsync(userId, spaceId, documentId, ct);

    private async Task<BavDocumentDetailView?> DetailAsync(Guid userId, Guid spaceId, Guid documentId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return null;

        var document = await db.BavDocuments.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == documentId && row.FullWorthSpaceId == spaceId, ct);
        if (document is null) return null;

        var draft = Deserialize(document.ExtractionDraftJson);

        // Only an unattached document asks the match rules: once a document belongs to a contract, the
        // question is answered and re-asking it could suggest a different one.
        BavContractMatchResult? match = null;
        if (document.BavContractId is null && draft is not null)
            match = await contracts.MatchAsync(userId, spaceId, new BavContractMatchRequest(
                draft.Contract.PolicyNumber,
                draft.Contract.ProviderName,
                draft.Contract.TariffName,
                draft.Contract.EmployerName,
                draft.Contract.RetirementDate), ct);

        return new BavDocumentDetailView(ToView(document), draft, match, Warnings(document, draft));
    }

    /// <summary>
    /// What the numbers do not say about themselves. Every one of these is shown and none is fixed: a
    /// document that does not add up is a fact about the document, and correcting it silently would
    /// hide the one thing the reviewer has to look at.
    /// </summary>
    private static IReadOnlyList<string> Warnings(BavDocument document, BavDocumentDraft? draft)
    {
        var warnings = new List<string>();

        if (document.TextLayerUsed == false)
            warnings.Add("Der Text wurde per Texterkennung (OCR) gelesen: die Zahlen sind erkannt, nicht ausgelesen. Bitte jeden Betrag prüfen.");

        if (draft is null) return warnings;

        if (draft.Snapshot.EffectiveDate is null)
            warnings.Add("Das Dokument nennt keinen Stichtag. Ohne Stichtag ist nicht klar, für welchen Zeitpunkt die Werte gelten.");

        var hasProjection = draft.Snapshot.ProjectedCapitalAtRetirement is not null
                            || draft.Snapshot.ProjectedMonthlyAnnuity is not null;
        if (hasProjection && draft.Snapshot.ProjectionReturnPercent is null)
            warnings.Add("Eine prognostizierte Leistung ohne Renditeannahme lässt sich nicht von einer Garantie unterscheiden. Bitte die Annahme ergänzen.");

        if (draft.Contribution is { } contribution && contribution.StatedTotalAmount is { } stated)
        {
            var parts = (contribution.EmployeeAmount ?? 0m)
                        + (contribution.EmployerSubsidyAmount ?? 0m)
                        + (contribution.EmployerAmount ?? 0m);
            if (Math.Abs(stated - parts) > 0.01m)
                warnings.Add("Die ausgewiesene Gesamtsumme stimmt nicht mit den Einzelanteilen überein. Der gedruckte Wert bleibt erhalten und wird nicht angepasst.");
        }

        if (draft.Snapshot.Balance is { } balance
            && (draft.Snapshot.SecurityAssetsAmount is not null || draft.Snapshot.FundAssetsAmount is not null))
        {
            var split = (draft.Snapshot.SecurityAssetsAmount ?? 0m) + (draft.Snapshot.FundAssetsAmount ?? 0m);
            if (Math.Abs(split - balance) > 0.01m)
                warnings.Add("Sicherungsvermögen und Fondsvermögen ergeben zusammen nicht das Guthaben. Bitte die Aufteilung prüfen.");
        }

        if (draft.Costs.Any(cost => cost.IsEstimated))
            warnings.Add("Mindestens eine Kostenposition ist eine Schätzung und wird als Schätzung gespeichert, nicht als Vertragswert.");

        if (draft.Unresolved.Count > 0)
            warnings.Add($"{draft.Unresolved.Count} Feld(er) konnten nicht gelesen werden und müssen von Hand ergänzt werden.");

        return warnings;
    }

    /// <summary>
    /// What the content endpoint needs to hand the original back. The bytes come out of the blob store
    /// already decrypted, so there is no path on disk the response could stream instead.
    /// </summary>
    public async Task<BavDocumentContent?> ContentAsync(Guid userId, Guid spaceId, Guid documentId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return null;

        var document = await db.BavDocuments.AsNoTracking()
            .Where(row => row.Id == documentId && row.FullWorthSpaceId == spaceId)
            .Select(row => new { row.StoragePath, row.EncryptionScheme, row.MediaType, row.OriginalFileName })
            .SingleOrDefaultAsync(ct);
        if (document?.StoragePath is null) return null;

        var content = await blobs.ReadAsync(document.StoragePath, document.EncryptionScheme, ct);
        if (content is null) return null;

        return new BavDocumentContent(
            content,
            document.MediaType ?? "application/octet-stream",
            SafeFileName(document.OriginalFileName) ?? $"dokument-{documentId:N}");
    }

    // ---- review ----

    /// <summary>
    /// Replaces the draft with the one a person edited. The edited draft is validated exactly as a
    /// commit would validate it, because storing an uncommittable draft only moves the failure to the
    /// button the user presses next.
    /// </summary>
    public async Task<BavDocumentOutcome> ReviewAsync(
        Guid userId, Guid spaceId, Guid documentId, BavDocumentReviewRequest request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);

        var document = await db.BavDocuments.SingleOrDefaultAsync(
            row => row.Id == documentId && row.FullWorthSpaceId == spaceId, ct);
        if (document is null) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        if (document.ExtractionStatus == BavExtractionStatuses.Committed)
            return new(BavMutationResult.Conflict,
                Error: "This document is committed: its values are contract rows now and are corrected there.");

        if (request.Draft is null) return new(BavMutationResult.Invalid, Error: "A review needs a draft.");

        var invalid = ValidateDraft(request.Draft);
        if (invalid is not null) return new(BavMutationResult.Invalid, Error: invalid);

        if (Trim(request.Kind) is { } kind)
        {
            if (!BavDocumentKinds.Allowed.Contains(kind))
                return new(BavMutationResult.Invalid, Error: $"Unknown document kind '{kind}'.");
            document.Kind = kind;
        }

        // The draft is now a person's statement about the document, not an extractor's, and both the
        // draft and the document say so — the review screen reads the source off the document.
        var reviewed = request.Draft with { Source = BavExtractionSources.Manual };
        document.ExtractionDraftJson = JsonSerializer.Serialize(reviewed, DraftJson);
        document.ExtractionStatus = BavExtractionStatuses.Reviewed;
        document.ExtractionSource = BavExtractionSources.Manual;
        document.ExtractionError = null;
        document.ReviewedByUserId = userId;
        document.ReviewedAt = DateTimeOffset.UtcNow;

        audit.Record(spaceId, userId, "pension.document.reviewed", "BavDocument", documentId);
        await db.SaveChangesAsync(ct);

        return new(BavMutationResult.Success, await DetailAsync(userId, spaceId, documentId, ct));
    }

    // ---- commit ----

    /// <summary>
    /// Turns the reviewed draft into rows. Every write goes through <see cref="PensionStore"/>, so the
    /// asset link, the <c>IsCurrent</c> rule, the projection constraint and the snapshot identity
    /// conflict are enforced once rather than twice.
    ///
    /// The whole commit runs in one transaction. The store methods each call <c>SaveChangesAsync</c>,
    /// but an explicit transaction on the shared context makes them enlist rather than commit on their
    /// own, so a contract is never left half-written.
    ///
    /// A snapshot date the contract already carries is skipped and named, not an error: the rest of
    /// the document still belongs in the database, and a silent success would hide that the figures
    /// were already there.
    /// </summary>
    public async Task<BavDocumentOutcome> CommitAsync(
        Guid userId, Guid spaceId, Guid documentId, BavDocumentCommitRequest request, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);

        var document = await db.BavDocuments.SingleOrDefaultAsync(
            row => row.Id == documentId && row.FullWorthSpaceId == spaceId, ct);
        if (document is null) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        if (request.Draft is null) return new(BavMutationResult.Invalid, Error: "A commit needs a draft.");
        if ((request.ContractId is not null) == request.CreateContract)
            return new(BavMutationResult.Invalid,
                Error: "A commit either names the contract this document belongs to or asks for a new one, never both and never neither.");

        var draft = request.Draft;
        var invalid = ValidateDraft(draft);
        if (invalid is not null) return new(BavMutationResult.Invalid, Error: invalid);

        if (Trim(request.Kind) is { } kind)
        {
            if (!BavDocumentKinds.Allowed.Contains(kind))
                return new(BavMutationResult.Invalid, Error: $"Unknown document kind '{kind}'.");
            document.Kind = kind;
        }

        var applied = new List<string>();
        var skipped = new List<string>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        Guid contractId;
        var contractCreated = false;
        if (request.CreateContract)
        {
            var provider = Trim(draft.Contract.ProviderName);
            if (provider is null)
                return new(BavMutationResult.Invalid, Error: "A new contract needs the provider the document names.");

            var created = await contracts.CreateAsync(userId, spaceId, new BavContractWrite(
                ProviderName: provider,
                TariffName: draft.Contract.TariffName,
                PolicyNumber: draft.Contract.PolicyNumber,
                ImplementationRoute: draft.Contract.ImplementationRoute,
                Status: draft.Contract.Status,
                EmployerName: draft.Contract.EmployerName,
                PolicyHolderName: draft.Contract.PolicyHolderName,
                InsuredPersonName: draft.Contract.InsuredPersonName,
                StartDate: draft.Contract.StartDate,
                RetirementDate: draft.Contract.RetirementDate,
                ContractEndDate: null,
                Currency: draft.Contract.Currency ?? draft.Snapshot.Currency,
                GuaranteeQuotaPercent: draft.Contract.GuaranteeQuotaPercent,
                GuaranteedAnnuityFactor: draft.Contract.GuaranteedAnnuityFactor,
                FundSelectionChangeable: false,
                IncludeInNetWorth: request.IncludeInNetWorth,
                Notes: null), ct);

            if (created.Result != BavMutationResult.Success || created.Contract is null)
            {
                await transaction.RollbackAsync(ct);
                // A policy-number clash means the document belongs to a contract that already exists.
                // The caller is told which one so it re-commits against it instead of forking history.
                return created.ExistingContractId is { } clash
                    ? new(BavMutationResult.Conflict, new BavDocumentContractConflict(clash), created.Error)
                    : new(created.Result, Error: created.Error);
            }

            contractId = created.Contract.Id;
            contractCreated = true;
            applied.Add("contract");
        }
        else
        {
            contractId = request.ContractId!.Value;
            if (!await db.BavContracts.AsNoTracking()
                    .AnyAsync(row => row.Id == contractId && row.FullWorthSpaceId == spaceId, ct))
            {
                await transaction.RollbackAsync(ct);
                return new(BavMutationResult.NotFound);
            }
        }

        var confidence = document.ExtractionConfidence ?? Clamp(draft.Confidence);

        Guid? snapshotId = null;
        var valueDate = draft.Snapshot.EffectiveDate ?? draft.Contribution?.ValidFrom;

        if (!HasSnapshotFigure(draft.Snapshot))
            skipped.Add("snapshot: the draft carries no dated figure");
        else if (draft.Snapshot.EffectiveDate is not { } effectiveDate)
            skipped.Add("snapshot: the draft has no effective date");
        else if (await db.BavSnapshots.AsNoTracking()
                     .AnyAsync(row => row.BavContractId == contractId && row.EffectiveDate == effectiveDate, ct))
        {
            // A date the contract already holds is skipped even when the file is a different one. Two
            // statements for the same effective date describe the same dated state, and a second row
            // would double the history rather than extend it — PensionStore's own identity rule is
            // (contract, date, hash) and would let a different file through. The rest of the document
            // still commits; saying what was skipped beats a silent success.
            skipped.Add($"snapshot:{effectiveDate:yyyy-MM-dd} already recorded");
        }
        else
        {
            // Source, document id, hash and confidence together are the snapshot's identity. That
            // triple is what makes a re-commit of the same document a no-op instead of a duplicate.
            var snapshot = await contracts.AddSnapshotAsync(userId, spaceId, contractId, new BavSnapshotWrite(
                EffectiveDate: effectiveDate,
                Currency: draft.Snapshot.Currency,
                Balance: draft.Snapshot.Balance,
                GuaranteedBalance: draft.Snapshot.GuaranteedBalance,
                SurrenderValue: draft.Snapshot.SurrenderValue,
                SecurityAssetsAmount: draft.Snapshot.SecurityAssetsAmount,
                FundAssetsAmount: draft.Snapshot.FundAssetsAmount,
                GuaranteedCapitalAtRetirement: draft.Snapshot.GuaranteedCapitalAtRetirement,
                GuaranteedMonthlyAnnuity: draft.Snapshot.GuaranteedMonthlyAnnuity,
                ProjectedCapitalAtRetirement: draft.Snapshot.ProjectedCapitalAtRetirement,
                ProjectedMonthlyAnnuity: draft.Snapshot.ProjectedMonthlyAnnuity,
                ProjectionReturnPercent: draft.Snapshot.ProjectionReturnPercent,
                ProjectionBasis: draft.Snapshot.ProjectionBasis,
                Source: BavValueSources.Document,
                BavDocumentId: document.Id,
                DocumentSha256: document.Sha256,
                ExtractionConfidence: confidence,
                Note: null), ct);

            switch (snapshot.Result)
            {
                case BavMutationResult.Success:
                    snapshotId = snapshot.Snapshot!.Id;
                    applied.Add($"snapshot:{effectiveDate:yyyy-MM-dd}");
                    break;
                case BavMutationResult.Conflict:
                    // Not an error: the contract already holds this dated state, and no field of an
                    // existing snapshot is ever overwritten.
                    skipped.Add($"snapshot:{effectiveDate:yyyy-MM-dd} already recorded");
                    break;
                default:
                    await transaction.RollbackAsync(ct);
                    return new(snapshot.Result, Error: snapshot.Error);
            }
        }

        if (draft.Contribution is { } contributionDraft && HasContributionFigure(contributionDraft))
        {
            var validFrom = contributionDraft.ValidFrom ?? draft.Snapshot.EffectiveDate;
            if (validFrom is not { } from) skipped.Add("contribution: the draft has no date it is valid from");
            else
            {
                var contribution = await contracts.AddContributionAsync(userId, spaceId, contractId, new BavContributionWrite(
                    ValidFrom: from,
                    ValidUntil: null,
                    EndReason: null,
                    Cycle: contributionDraft.Cycle,
                    Currency: contributionDraft.Currency ?? draft.Snapshot.Currency,
                    EmployeeAmount: contributionDraft.EmployeeAmount ?? 0m,
                    EmployerSubsidyAmount: contributionDraft.EmployerSubsidyAmount ?? 0m,
                    EmployerAmount: contributionDraft.EmployerAmount ?? 0m,
                    StatedTotalAmount: contributionDraft.StatedTotalAmount,
                    Source: BavValueSources.Document,
                    // A tax or social-insurance effect is never extracted: a statement almost never
                    // states one, and inventing it here is what BavContribution's source rule forbids.
                    TaxSavingAmount: null,
                    SocialSecuritySavingAmount: null,
                    NetEffortAmount: null,
                    TaxEffectSource: null,
                    TaxEffectSourceReference: null,
                    BavDocumentId: document.Id,
                    Note: null), ct);

                if (contribution.Result != BavMutationResult.Success)
                {
                    await transaction.RollbackAsync(ct);
                    return new(contribution.Result, Error: contribution.Error);
                }
                applied.Add($"contribution:{from:yyyy-MM-dd}");
            }
        }

        if (draft.Allocations.Count > 0)
        {
            if (valueDate is not { } allocationDate) skipped.Add("allocations: the draft has no effective date");
            else
                foreach (var allocationDraft in draft.Allocations)
                {
                    var allocation = await contracts.AddAllocationAsync(userId, spaceId, contractId, new BavAllocationWrite(
                        EffectiveDate: allocationDate,
                        FundName: allocationDraft.FundName,
                        Isin: allocationDraft.Isin,
                        WeightPercent: allocationDraft.WeightPercent,
                        Amount: allocationDraft.Amount,
                        Currency: allocationDraft.Currency ?? draft.Snapshot.Currency,
                        OngoingChargesPercent: allocationDraft.OngoingChargesPercent,
                        OngoingChargesEstimated: allocationDraft.OngoingChargesEstimated,
                        AssetClass: allocationDraft.AssetClass,
                        Source: BavValueSources.Document,
                        BavSnapshotId: snapshotId,
                        Note: null), ct);

                    if (allocation.Result != BavMutationResult.Success)
                    {
                        await transaction.RollbackAsync(ct);
                        return new(allocation.Result, Error: allocation.Error);
                    }
                    applied.Add("allocation");
                }
        }

        if (draft.Costs.Count > 0)
        {
            if (valueDate is not { } costDate) skipped.Add("costs: the draft has no effective date");
            else
                foreach (var costDraft in draft.Costs)
                {
                    var cost = await contracts.AddCostAsync(userId, spaceId, contractId, new BavCostWrite(
                        EffectiveDate: costDate,
                        Kind: costDraft.Kind,
                        Basis: costDraft.Basis,
                        AppliesUntilDate: null,
                        Amount: costDraft.Amount,
                        Currency: draft.Snapshot.Currency,
                        Percent: costDraft.Percent,
                        Timing: costDraft.Timing,
                        // An estimate is committed as an estimate, with what it rests on, so it is
                        // never displayed as a contract value.
                        IsEstimated: costDraft.IsEstimated,
                        EstimateBasis: costDraft.EstimateBasis,
                        ContinuesWhenPaidUp: costDraft.ContinuesWhenPaidUp,
                        Source: BavValueSources.Document,
                        BavDocumentId: document.Id,
                        BavSnapshotId: snapshotId,
                        Note: null), ct);

                    if (cost.Result != BavMutationResult.Success)
                    {
                        await transaction.RollbackAsync(ct);
                        return new(cost.Result, Error: cost.Error);
                    }
                    applied.Add($"cost:{costDraft.Kind}");
                }
        }

        document.BavContractId = contractId;
        document.ExtractionStatus = BavExtractionStatuses.Committed;
        // The rows are the truth from here on. A second copy of the same figures — the policy number
        // among them — would only be another place to leak from, so the draft goes.
        document.ExtractionDraftJson = null;
        document.ReviewedByUserId ??= userId;
        document.ReviewedAt ??= DateTimeOffset.UtcNow;

        audit.Record(spaceId, userId, "pension.document.committed", "BavDocument", documentId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new(BavMutationResult.Success, new BavDocumentCommitResult(
            documentId, contractId, snapshotId, contractCreated, applied, skipped));
    }

    // ---- delete ----

    /// <summary>
    /// Removes the row and the blob. Refused while a committed value points at the document: the
    /// snapshot's foreign key is <c>SetNull</c>, so deleting the document would leave a stored figure
    /// claiming to come from a document nobody can produce any more — a value without its provenance.
    /// </summary>
    public async Task<BavDocumentOutcome> DeleteAsync(Guid userId, Guid spaceId, Guid documentId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, spaceId, ct)) return new(BavMutationResult.NotFound);

        var document = await db.BavDocuments.SingleOrDefaultAsync(
            row => row.Id == documentId && row.FullWorthSpaceId == spaceId, ct);
        if (document is null) return new(BavMutationResult.NotFound);
        if (!await IsOwnerAsync(userId, spaceId, ct)) return new(BavMutationResult.Forbidden);

        if (await db.BavSnapshots.AsNoTracking().AnyAsync(
                row => row.BavDocumentId == documentId || row.DocumentSha256 == document.Sha256, ct))
            return new(BavMutationResult.Conflict,
                Error: "A snapshot was taken from this document. Delete the snapshot's contract first, or keep the document.");
        if (await db.BavContributions.AsNoTracking().AnyAsync(row => row.BavDocumentId == documentId, ct)
            || await db.BavCosts.AsNoTracking().AnyAsync(row => row.BavDocumentId == documentId, ct))
            return new(BavMutationResult.Conflict,
                Error: "A contribution or a cost was taken from this document and would lose its source.");

        var storagePath = document.StoragePath;
        db.BavDocuments.Remove(document);
        audit.Record(spaceId, userId, "pension.document.deleted", "BavDocument", documentId);
        // The row goes first: it is the only index of the blob, so an orphaned blob is recoverable
        // while a row pointing at bytes that are gone is not.
        await db.SaveChangesAsync(ct);
        if (storagePath is not null) await SafeDeleteBlobAsync(storagePath, ct);

        return new(BavMutationResult.Success);
    }

    private async Task SafeDeleteBlobAsync(string storagePath, CancellationToken ct)
    {
        try
        {
            await blobs.DeleteAsync(storagePath, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The row is already gone, so the bytes are unreachable either way. Failing the request
            // here would only tell the user their delete did not work when it did.
        }
    }

    // ---- validation ----

    /// <summary>
    /// What a commit will insist on, checked before a draft is stored. The domain rules themselves
    /// stay in <see cref="PensionStore"/>; this is the same set of questions asked early so a review
    /// cannot save a draft whose commit is already impossible.
    /// </summary>
    private static string? ValidateDraft(BavDocumentDraft draft)
    {
        if (draft.Confidence is < 0 or > 1) return "The extraction confidence is a value between 0 and 1.";
        if (draft.PageCount < 0) return "A page count cannot be negative.";
        if (Trim(draft.Source) is { } source && !BavExtractionSources.Allowed.Contains(source))
            return $"Unknown extraction source '{source}'.";

        var snapshot = draft.Snapshot;
        foreach (var (label, amount) in new (string, decimal?)[]
                 {
                     ("balance", snapshot.Balance),
                     ("guaranteed balance", snapshot.GuaranteedBalance),
                     ("surrender value", snapshot.SurrenderValue),
                     ("security assets", snapshot.SecurityAssetsAmount),
                     ("fund assets", snapshot.FundAssetsAmount),
                     ("guaranteed capital", snapshot.GuaranteedCapitalAtRetirement),
                     ("guaranteed annuity", snapshot.GuaranteedMonthlyAnnuity),
                     ("projected capital", snapshot.ProjectedCapitalAtRetirement),
                     ("projected annuity", snapshot.ProjectedMonthlyAnnuity)
                 })
            if (amount is < 0) return $"The {label} cannot be negative.";

        if (snapshot.GuaranteedBalance is { } guaranteed && snapshot.Balance is { } balance && guaranteed > balance)
            return "The guaranteed part cannot exceed the balance.";

        // The rule the whole snapshot model exists for: a projected figure without its return
        // assumption is indistinguishable from a guarantee, and refusing it here is what keeps a
        // forecast from ever being read as a promise.
        var hasProjection = snapshot.ProjectedCapitalAtRetirement is not null || snapshot.ProjectedMonthlyAnnuity is not null;
        var basis = Trim(snapshot.ProjectionBasis);
        if (basis is not null && !BavProjectionBases.Allowed.Contains(basis))
            return $"Unknown projection basis '{basis}'.";
        if (hasProjection && (basis is null || snapshot.ProjectionReturnPercent is null))
            return "A projected figure needs its basis and the return assumption behind it, or it cannot be told apart from a guarantee.";
        if (!hasProjection && (basis is not null || snapshot.ProjectionReturnPercent is not null))
            return "A projection basis belongs to a projected figure.";
        if (snapshot.ProjectionReturnPercent is < -100 or > 100)
            return "The assumed return must be between -100 and 100 percent.";

        if (snapshot.EffectiveDate is { } effective && effective > DateOnly.FromDateTime(DateTime.UtcNow))
            return "A snapshot date cannot be in the future: it would be a value nobody has seen yet.";

        if (Trim(draft.Contract.ImplementationRoute) is { } route && !BavImplementationRoutes.Allowed.Contains(route))
            return $"Unknown implementation route '{route}'.";
        if (Trim(draft.Contract.Status) is { } status && !BavContractStatuses.Allowed.Contains(status))
            return $"Unknown contract status '{status}'.";
        if (draft.Contract.GuaranteeQuotaPercent is < 0 or > 100)
            return "The guarantee quota must be between 0 and 100 percent.";
        if (draft.Contract.GuaranteedAnnuityFactor is < 0)
            return "The guaranteed annuity factor cannot be negative.";

        if (draft.Contribution is { } contribution)
        {
            if (Trim(contribution.Cycle) is { } cycle && !BavContributionCycles.Allowed.Contains(cycle))
                return $"Unknown contribution cycle '{cycle}'.";
            if (contribution.EmployeeAmount is < 0 || contribution.EmployerSubsidyAmount is < 0
                || contribution.EmployerAmount is < 0 || contribution.StatedTotalAmount is < 0)
                return "A contribution share cannot be negative.";
        }

        foreach (var allocation in draft.Allocations)
        {
            if (Trim(allocation.FundName) is null) return "A fund needs a name.";
            var isin = PensionIdentity.NormalizeIsin(allocation.Isin);
            if (isin is not null && !PensionIdentity.IsValidIsin(isin))
                return "An ISIN is two letters followed by ten alphanumeric characters.";
            if (Trim(allocation.AssetClass) is { } assetClass && !BavAssetClasses.Allowed.Contains(assetClass))
                return $"Unknown asset class '{assetClass}'.";
            if (allocation.WeightPercent is < 0 or > 100) return "A weight must be between 0 and 100 percent.";
            if (allocation.Amount is < 0) return "A fund value cannot be negative.";
            if (allocation.OngoingChargesPercent is < 0 or > 100)
                return "Ongoing charges must be between 0 and 100 percent.";
            if (allocation.OngoingChargesEstimated && allocation.OngoingChargesPercent is null)
                return "An estimated charge needs a figure to be an estimate of.";
        }

        foreach (var cost in draft.Costs)
        {
            if (!BavCostKinds.Allowed.Contains(cost.Kind)) return $"Unknown cost kind '{cost.Kind}'.";
            if (!BavCostBases.Allowed.Contains(cost.Basis)) return $"Unknown cost basis '{cost.Basis}'.";
            if (Trim(cost.Timing) is { } timing && !BavCostTimings.Allowed.Contains(timing))
                return $"Unknown cost timing '{timing}'.";
            if (cost.Basis == BavCostBases.FixedAmount)
            {
                if (cost.Amount is null) return "A fixed cost needs an amount.";
                if (cost.Percent is not null) return "A fixed cost carries no percentage.";
                if (cost.Amount < 0) return "A cost amount cannot be negative.";
            }
            else
            {
                if (cost.Percent is null) return "A percentage cost needs a percentage.";
                if (cost.Amount is not null) return "A percentage cost carries no amount.";
                if (cost.Percent is < 0 or > 100) return "A cost percentage must be between 0 and 100.";
            }
            if (cost.IsEstimated && Trim(cost.EstimateBasis) is null)
                return "An estimated cost has to say what the estimate is based on.";
            if (!cost.IsEstimated && Trim(cost.EstimateBasis) is not null)
                return "An estimate basis belongs to an estimated cost.";
        }

        return null;
    }

    private static bool HasSnapshotFigure(BavSnapshotDraft snapshot) =>
        snapshot.Balance is not null
        || snapshot.GuaranteedBalance is not null
        || snapshot.SurrenderValue is not null
        || snapshot.SecurityAssetsAmount is not null
        || snapshot.FundAssetsAmount is not null
        || snapshot.GuaranteedCapitalAtRetirement is not null
        || snapshot.GuaranteedMonthlyAnnuity is not null
        || snapshot.ProjectedCapitalAtRetirement is not null
        || snapshot.ProjectedMonthlyAnnuity is not null;

    private static bool HasContributionFigure(BavContributionDraft contribution) =>
        contribution.EmployeeAmount is not null
        || contribution.EmployerSubsidyAmount is not null
        || contribution.EmployerAmount is not null
        || contribution.StatedTotalAmount is not null;

    // ---- projection and helpers ----

    private static BavDocumentView ToView(BavDocument row) => new(
        row.Id,
        row.BavContractId,
        row.Kind,
        row.OriginalFileName,
        row.MediaType,
        row.ByteSize,
        row.PageCount,
        row.ExtractionStatus,
        row.ExtractionConfidence,
        row.ExtractionSource,
        row.TextLayerUsed,
        row.ExtractionError,
        row.ExtractedAt,
        row.ReviewedByUserId,
        row.ReviewedAt,
        row.CreatedAt);

    /// <summary>
    /// A draft that cannot be read back is treated as absent rather than as an error: the document and
    /// its file are still there to review, which is the point of storing them.
    /// </summary>
    private static BavDocumentDraft? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<BavDocumentDraft>(json, DraftJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MediaType(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        _ => "image/jpeg"
    };

    /// <summary>
    /// A file name comes from the user's disk and is echoed in a Content-Disposition header, so it is
    /// reduced to a leaf name of harmless characters before it is ever stored.
    /// </summary>
    private static string? SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var leaf = fileName.AsSpan()[(fileName.LastIndexOfAny(['/', '\\']) + 1)..].ToString();
        var safe = new string(leaf.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or ' ' ? character : '_').ToArray()).Trim();
        return safe.Length == 0 ? null : safe.Length <= 200 ? safe : safe[^200..];
    }

    private static decimal? Clamp(decimal confidence) => confidence switch
    {
        < 0m => 0m,
        > 1m => 1m,
        _ => confidence
    };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The decrypted original, ready to be handed back. Never cached, never shared.</summary>
public sealed record BavDocumentContent(byte[] Content, string MediaType, string FileName);

/// <summary>
/// The commit found that the document's contract already exists. Reported instead of silently using
/// it, because merging two contracts is a decision a person makes.
/// </summary>
public sealed record BavDocumentContractConflict(Guid ExistingContractId);

/// <summary>
/// The four categories <c>BavDocument.ExtractionError</c> may hold. A category and nothing else: the
/// column is returned to the browser and may reach a log, and a tool's own output can quote the
/// document it just read.
/// </summary>
public static class BavExtractionErrors
{
    public const string NoText = "no_text";
    public const string ToolMissing = "tool_missing";
    public const string Unsupported = "unsupported";
    public const string TooLarge = "too_large";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    { NoText, ToolMissing, Unsupported, TooLarge };
}
