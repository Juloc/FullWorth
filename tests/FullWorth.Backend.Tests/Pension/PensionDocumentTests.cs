using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Pension;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Pension;

/// <summary>
/// The document path of the Altersvorsorge area (step 2 of docs/PENSION.md). The rule every one of
/// these tests circles is the same: <b>an extracted value is a draft until a person commits it</b>.
/// Upload stores a file and a draft and nothing else; only a commit writes a contract value, and it
/// writes it through <c>PensionStore</c> so the asset link, the projection constraint and the snapshot
/// identity are enforced once.
///
/// Text source, parser and AI pass are faked here on purpose: what is under test is the store, not
/// OCR. The fakes are also what proves the no-bridge path — a disabled AI structurer must change
/// nothing, because a self-hosted installation has no Codex bridge.
/// </summary>
public sealed class PensionDocumentTests
{
    /// <summary>
    /// Upload keeps what the extraction learned about the document itself — how many pages it had and
    /// whether the numbers were read or recognised — because both change how much the draft is worth.
    /// </summary>
    [Fact]
    public async Task An_upload_parses_into_a_draft_and_keeps_how_the_text_was_won()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);

        harness.Text.Result = new BavDocumentText(["Seite 1", "Seite 2", "Seite 3"], TextLayerUsed: false);
        harness.Parser.Result = Draft() with { PageCount = 3, TextLayerUsed = false, Confidence = 0.82m };

        var outcome = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("statement-2026"));
        Assert.Equal(BavMutationResult.Success, outcome.Result);

        var detail = Assert.IsType<BavDocumentDetailView>(outcome.Value);
        Assert.Equal(BavExtractionStatuses.Parsed, detail.Document.ExtractionStatus);
        Assert.Equal(BavExtractionSources.Deterministic, detail.Document.ExtractionSource);
        Assert.Equal(3, detail.Document.PageCount);
        Assert.False(detail.Document.TextLayerUsed);
        Assert.Equal(0.82m, detail.Document.ExtractionConfidence);
        Assert.NotNull(detail.Document.ExtractedAt);
        Assert.Null(detail.Document.ExtractionError);

        Assert.NotNull(detail.Draft);
        Assert.Equal(12_500.55m, detail.Draft!.Snapshot.Balance);

        // OCR'd numbers are recognised, not read, and the review screen has to say so.
        Assert.Contains(detail.Warnings, warning => warning.Contains("OCR", StringComparison.Ordinal));

        // The draft lives in the document row, not in a contract table.
        await harness.Factory.SeedAsync(async db =>
        {
            var row = await db.BavDocuments.AsNoTracking().SingleAsync(x => x.FullWorthSpaceId == scenario.Space);
            Assert.False(string.IsNullOrWhiteSpace(row.ExtractionDraftJson));
            Assert.Equal(3, row.PageCount);
            Assert.False(row.TextLayerUsed);
            Assert.True(harness.Blobs.Exists(row.StoragePath!));
        });

        Assert.False(harness.Ai.WasCalled);
    }

    /// <summary>
    /// "Re-uploading the same document creates nothing" (docs/PENSION.md). The caller is pointed at
    /// the document it already has so it lands on the review already in progress.
    /// </summary>
    [Fact]
    public async Task The_same_file_twice_is_a_conflict_that_names_the_first_document()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = Draft();

        var bytes = Pdf("same-file");
        var first = await harness.UploadAsync(scenario.Owner, scenario.Space, bytes);
        Assert.Equal(BavMutationResult.Success, first.Result);
        var firstId = ((BavDocumentDetailView)first.Value!).Document.Id;

        var second = await harness.UploadAsync(scenario.Owner, scenario.Space, bytes);
        Assert.Equal(BavMutationResult.Conflict, second.Result);
        Assert.Equal(firstId, second.ExistingDocumentId);

        await harness.Factory.SeedAsync(async db =>
            Assert.Equal(1, await db.BavDocuments.CountAsync(x => x.FullWorthSpaceId == scenario.Space)));
    }

    /// <summary>
    /// A failed extraction must not lose the document: the file and its row survive so the values can
    /// still be typed in. What is stored about the failure is a category and only a category — the
    /// column is returned to the browser and a tool's own message can quote the document it read.
    /// </summary>
    [Fact]
    public async Task A_failing_text_source_keeps_the_document_and_records_only_a_category()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);

        harness.Text.Failure = new InvalidOperationException(
            "pdftotext: Syntaxfehler auf Seite 2: Vertragsguthaben 12.500,55 EUR, Police BAV-2018/4711");

        var outcome = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("broken"));
        Assert.Equal(BavMutationResult.Success, outcome.Result);

        var detail = (BavDocumentDetailView)outcome.Value!;
        Assert.Equal(BavExtractionStatuses.Failed, detail.Document.ExtractionStatus);
        var category = detail.Document.ExtractionError;
        Assert.Equal(BavExtractionErrors.Unsupported, category);
        Assert.True(category!.Length <= 24, "an extraction error is a category, not a sentence");
        Assert.DoesNotContain("Vertragsguthaben", category, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pdftotext", category, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BAV", category, StringComparison.OrdinalIgnoreCase);
        Assert.Null(detail.Draft);

        // A missing external tool is the one failure a self-hoster can act on, so it is its own category.
        harness.Text.Failure = new FileNotFoundException("tesseract not found at /usr/bin/tesseract");
        var missing = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("no-tool"));
        Assert.Equal(BavExtractionErrors.ToolMissing, ((BavDocumentDetailView)missing.Value!).Document.ExtractionError);

        // And the bytes are still there, which is the whole point of storing them before extracting.
        await harness.Factory.SeedAsync(async db =>
        {
            var rows = await db.BavDocuments.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == scenario.Space).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, row =>
            {
                Assert.NotNull(row.StoragePath);
                Assert.True(harness.Blobs.Exists(row.StoragePath!));
                Assert.Null(row.ExtractionDraftJson);
            });
        });
    }

    /// <summary>
    /// The hardest rule of the brief, asserted directly: after an upload that parsed a full statement
    /// — balance, contribution, fund, cost — not one contract value exists. Everything is still a
    /// draft, and it stays a draft until somebody commits it.
    /// </summary>
    [Fact]
    public async Task Nothing_is_stored_as_a_contract_value_before_a_commit()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = FullDraft();

        var outcome = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("full"));
        Assert.Equal(BavMutationResult.Success, outcome.Result);
        Assert.Equal(BavExtractionStatuses.Parsed, ((BavDocumentDetailView)outcome.Value!).Document.ExtractionStatus);

        await harness.Factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await db.BavContracts.CountAsync(x => x.FullWorthSpaceId == scenario.Space));
            Assert.Equal(0, await db.BavSnapshots.CountAsync(x => x.FullWorthSpaceId == scenario.Space));
            Assert.Equal(0, await db.BavContributions.CountAsync(x => x.FullWorthSpaceId == scenario.Space));
            Assert.Equal(0, await db.BavCosts.CountAsync(x => x.FullWorthSpaceId == scenario.Space));
            Assert.Equal(0, await db.BavInvestmentAllocations.CountAsync(x => x.FullWorthSpaceId == scenario.Space));
            Assert.Equal(0, await db.Assets.CountAsync(
                x => x.FullWorthSpaceId == scenario.Space && x.Kind == AssetKinds.InsurancePension));
        });
    }

    /// <summary>A reviewed draft is a person's statement about the document, and it says whose.</summary>
    [Fact]
    public async Task A_review_stores_the_edited_draft_and_names_the_reviewer()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = Draft() with { Confidence = 0.4m };

        var uploaded = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("review"));
        var documentId = ((BavDocumentDetailView)uploaded.Value!).Document.Id;

        var edited = Draft() with { Snapshot = Draft().Snapshot with { Balance = 13_000m, GuaranteedBalance = 9_500m } };
        var reviewed = await harness.ReviewAsync(scenario.Owner, scenario.Space, documentId,
            new BavDocumentReviewRequest(edited));
        Assert.Equal(BavMutationResult.Success, reviewed.Result);

        var detail = (BavDocumentDetailView)reviewed.Value!;
        Assert.Equal(BavExtractionStatuses.Reviewed, detail.Document.ExtractionStatus);
        Assert.Equal(BavExtractionSources.Manual, detail.Document.ExtractionSource);
        Assert.Equal(scenario.Owner, detail.Document.ReviewedByUserId);
        Assert.NotNull(detail.Document.ReviewedAt);
        Assert.Equal(13_000m, detail.Draft!.Snapshot.Balance);
        Assert.Equal(BavExtractionSources.Manual, detail.Draft.Source);
    }

    /// <summary>
    /// The commit: one contract, one asset that carries the balance into net worth, and one snapshot
    /// that says which document and which hash it came from. The draft is gone afterwards, because
    /// the rows are the truth and a second copy of the same figures is only a second place to leak.
    /// </summary>
    [Fact]
    public async Task A_commit_creates_the_contract_the_asset_link_and_a_snapshot_that_names_its_document()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = FullDraft() with { Confidence = 0.9m };

        var uploaded = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("commit"));
        var documentId = ((BavDocumentDetailView)uploaded.Value!).Document.Id;

        var committed = await harness.CommitAsync(scenario.Owner, scenario.Space, documentId,
            new BavDocumentCommitRequest(FullDraft(), CreateContract: true));
        Assert.Equal(BavMutationResult.Success, committed.Result);

        var result = Assert.IsType<BavDocumentCommitResult>(committed.Value);
        Assert.True(result.ContractCreated);
        Assert.NotNull(result.SnapshotId);
        Assert.Empty(result.Skipped);
        Assert.Contains("contract", result.Applied);
        Assert.Contains(result.Applied, entry => entry.StartsWith("snapshot:", StringComparison.Ordinal));

        await harness.Factory.SeedAsync(async db =>
        {
            var contract = await db.BavContracts.AsNoTracking().SingleAsync(x => x.FullWorthSpaceId == scenario.Space);
            Assert.NotNull(contract.AssetId);

            var asset = await db.Assets.AsNoTracking().SingleAsync(x => x.Id == contract.AssetId);
            Assert.Equal(AssetKinds.InsurancePension, asset.Kind);
            Assert.Equal(12_500.55m, asset.CurrentValue);
            Assert.Equal(new DateOnly(2026, 1, 1), asset.ValuedAt);

            var snapshot = await db.BavSnapshots.AsNoTracking().SingleAsync(x => x.BavContractId == contract.Id);
            Assert.Equal(BavValueSources.Document, snapshot.Source);
            Assert.Equal(documentId, snapshot.BavDocumentId);
            Assert.Equal(0.9m, snapshot.ExtractionConfidence);
            Assert.True(snapshot.IsCurrent);

            var document = await db.BavDocuments.AsNoTracking().SingleAsync(x => x.Id == documentId);
            Assert.Equal(contract.Id, document.BavContractId);
            Assert.Equal(BavExtractionStatuses.Committed, document.ExtractionStatus);
            Assert.Null(document.ExtractionDraftJson);
            // The hash is the third part of the snapshot's identity, so a re-read of the file adds nothing.
            Assert.Equal(document.Sha256, snapshot.DocumentSha256);

            var contribution = await db.BavContributions.AsNoTracking().SingleAsync(x => x.BavContractId == contract.Id);
            Assert.Equal(BavValueSources.Document, contribution.Source);
            Assert.Equal(documentId, contribution.BavDocumentId);
            // A statement does not state a tax effect, so none was invented from one.
            Assert.Null(contribution.TaxEffectSource);
            Assert.Null(contribution.TaxSavingAmount);
        });
    }

    /// <summary>
    /// The second statement of a known contract belongs to that contract. The match rules say which
    /// one and why, and committing against it adds a snapshot rather than a second contract.
    /// </summary>
    [Fact]
    public async Task A_commit_against_a_matched_contract_adds_no_second_contract()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = Draft();

        var first = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("year-one"));
        var firstId = ((BavDocumentDetailView)first.Value!).Document.Id;
        var created = await harness.CommitAsync(scenario.Owner, scenario.Space, firstId,
            new BavDocumentCommitRequest(Draft(), CreateContract: true));
        var contractId = ((BavDocumentCommitResult)created.Value!).ContractId;

        // A second statement of the same contract, one year on.
        var nextYear = Draft() with { Snapshot = Draft().Snapshot with { EffectiveDate = new DateOnly(2026, 6, 1), Balance = 15_200.10m } };
        harness.Parser.Result = nextYear;
        var second = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("year-two"));
        var secondDetail = (BavDocumentDetailView)second.Value!;

        Assert.NotNull(secondDetail.Match);
        Assert.True(secondDetail.Match!.Matched);
        Assert.Equal(contractId, secondDetail.Match.ContractId);
        Assert.Equal("policy_number", secondDetail.Match.MatchedOn);

        var onto = await harness.CommitAsync(scenario.Owner, scenario.Space, secondDetail.Document.Id,
            new BavDocumentCommitRequest(nextYear, ContractId: contractId));
        Assert.Equal(BavMutationResult.Success, onto.Result);
        Assert.False(((BavDocumentCommitResult)onto.Value!).ContractCreated);

        await harness.Factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await db.BavContracts.CountAsync(x => x.FullWorthSpaceId == scenario.Space));
            Assert.Equal(2, await db.BavSnapshots.CountAsync(x => x.BavContractId == contractId));
            // The older snapshot is untouched and the newer one is current: "current" is the newest
            // effective date, not the newest insert.
            var older = await db.BavSnapshots.AsNoTracking()
                .SingleAsync(x => x.BavContractId == contractId && x.EffectiveDate == new DateOnly(2026, 1, 1));
            Assert.Equal(12_500.55m, older.Balance);
            Assert.False(older.IsCurrent);
        });
    }

    /// <summary>
    /// A date the contract already holds is skipped and named, not written twice and not an error that
    /// loses the rest of the commit. No field of the snapshot that is already there moves.
    /// </summary>
    [Fact]
    public async Task A_second_document_for_a_date_that_exists_is_skipped_and_changes_nothing()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = Draft();

        var first = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("original"));
        var firstId = ((BavDocumentDetailView)first.Value!).Document.Id;
        var created = await harness.CommitAsync(scenario.Owner, scenario.Space, firstId,
            new BavDocumentCommitRequest(Draft(), CreateContract: true));
        var contractId = ((BavDocumentCommitResult)created.Value!).ContractId;
        var snapshotId = ((BavDocumentCommitResult)created.Value!).SnapshotId;

        // Re-committing the same document is a no-op, even with a different figure in the draft.
        var recommit = await harness.CommitAsync(scenario.Owner, scenario.Space, firstId,
            new BavDocumentCommitRequest(Draft() with { Snapshot = Draft().Snapshot with { Balance = 99_999m } },
                ContractId: contractId));
        Assert.Equal(BavMutationResult.Success, recommit.Result);
        Assert.Null(((BavDocumentCommitResult)recommit.Value!).SnapshotId);
        Assert.Contains(((BavDocumentCommitResult)recommit.Value!).Skipped,
            entry => entry.Contains("2026-01-01", StringComparison.Ordinal));

        // And so is a different file that reports the same effective date.
        var duplicateDate = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("copy-of-the-same-year"));
        var duplicateId = ((BavDocumentDetailView)duplicateDate.Value!).Document.Id;
        var onto = await harness.CommitAsync(scenario.Owner, scenario.Space, duplicateId,
            new BavDocumentCommitRequest(Draft() with { Snapshot = Draft().Snapshot with { Balance = 11_111m } },
                ContractId: contractId));
        Assert.Equal(BavMutationResult.Success, onto.Result);
        Assert.Contains(((BavDocumentCommitResult)onto.Value!).Skipped,
            entry => entry.Contains("2026-01-01", StringComparison.Ordinal));

        await harness.Factory.SeedAsync(async db =>
        {
            var snapshots = await db.BavSnapshots.AsNoTracking().Where(x => x.BavContractId == contractId).ToListAsync();
            var only = Assert.Single(snapshots);
            Assert.Equal(snapshotId, only.Id);
            Assert.Equal(12_500.55m, only.Balance);
            Assert.Equal(firstId, only.BavDocumentId);

            var asset = await db.Assets.AsNoTracking().SingleAsync(
                x => x.FullWorthSpaceId == scenario.Space && x.Kind == AssetKinds.InsurancePension);
            Assert.Equal(12_500.55m, asset.CurrentValue);
        });
    }

    /// <summary>
    /// A projected figure without the return assumption behind it is indistinguishable from a
    /// guarantee, so the commit refuses it — and refuses it before anything is written.
    /// </summary>
    [Fact]
    public async Task A_projection_without_its_return_assumption_is_refused_and_writes_nothing()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = Draft();

        var uploaded = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("forecast"));
        var documentId = ((BavDocumentDetailView)uploaded.Value!).Document.Id;

        var bare = Draft() with
        {
            Snapshot = Draft().Snapshot with { ProjectedCapitalAtRetirement = 180_000m, ProjectionReturnPercent = null }
        };

        var refused = await harness.CommitAsync(scenario.Owner, scenario.Space, documentId,
            new BavDocumentCommitRequest(bare, CreateContract: true));
        Assert.Equal(BavMutationResult.Invalid, refused.Result);
        Assert.Contains("guarantee", refused.Error!, StringComparison.OrdinalIgnoreCase);

        // A review may not park it either: an uncommittable draft only moves the failure later.
        var review = await harness.ReviewAsync(scenario.Owner, scenario.Space, documentId,
            new BavDocumentReviewRequest(bare));
        Assert.Equal(BavMutationResult.Invalid, review.Result);

        await harness.Factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await db.BavContracts.CountAsync(x => x.FullWorthSpaceId == scenario.Space));
            Assert.Equal(0, await db.BavSnapshots.CountAsync(x => x.FullWorthSpaceId == scenario.Space));
        });
    }

    /// <summary>
    /// An estimated cost is committed as an estimate with what it rests on, so it is never displayed
    /// as a contract value.
    /// </summary>
    [Fact]
    public async Task An_estimated_cost_commits_as_an_estimate_with_its_basis()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = FullDraft();

        var uploaded = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("costs"));
        var documentId = ((BavDocumentDetailView)uploaded.Value!).Document.Id;
        Assert.Contains(((BavDocumentDetailView)uploaded.Value!).Warnings,
            warning => warning.Contains("Schätzung", StringComparison.Ordinal));

        var committed = await harness.CommitAsync(scenario.Owner, scenario.Space, documentId,
            new BavDocumentCommitRequest(FullDraft(), CreateContract: true));
        Assert.Equal(BavMutationResult.Success, committed.Result);

        await harness.Factory.SeedAsync(async db =>
        {
            var cost = await db.BavCosts.AsNoTracking().SingleAsync(x => x.FullWorthSpaceId == scenario.Space);
            Assert.True(cost.IsEstimated);
            Assert.Equal("Fondsprospekt, laufende Kosten p.a.", cost.EstimateBasis);
            Assert.Equal(BavCostKinds.Fund, cost.Kind);
            Assert.Equal(1.15m, cost.Percent);
            Assert.Equal(BavValueSources.Document, cost.Source);
            Assert.Equal(documentId, cost.BavDocumentId);
            Assert.True(cost.ContinuesWhenPaidUp);

            var allocation = await db.BavInvestmentAllocations.AsNoTracking()
                .SingleAsync(x => x.FullWorthSpaceId == scenario.Space);
            Assert.Equal("DE0009848119", allocation.Isin);
            Assert.Equal(BavValueSources.Document, allocation.Source);
        });
    }

    /// <summary>
    /// The no-bridge path. A self-hosted installation without Codex reaches the same review screen and
    /// the same commit; the AI structurer is never asked, and calling it here would throw.
    /// </summary>
    [Fact]
    public async Task A_disabled_ai_structurer_is_never_asked_and_the_path_still_completes()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Ai.Enabled = false;
        harness.Ai.ThrowIfCalled = true;
        harness.Parser.Result = FullDraft();

        var uploaded = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("no-bridge"));
        Assert.Equal(BavMutationResult.Success, uploaded.Result);
        var detail = (BavDocumentDetailView)uploaded.Value!;
        Assert.Equal(BavExtractionStatuses.Parsed, detail.Document.ExtractionStatus);
        Assert.Equal(BavExtractionSources.Deterministic, detail.Document.ExtractionSource);
        Assert.False(harness.Ai.WasCalled);

        var reviewed = await harness.ReviewAsync(scenario.Owner, scenario.Space, detail.Document.Id,
            new BavDocumentReviewRequest(FullDraft()));
        Assert.Equal(BavMutationResult.Success, reviewed.Result);

        var committed = await harness.CommitAsync(scenario.Owner, scenario.Space, detail.Document.Id,
            new BavDocumentCommitRequest(FullDraft(), CreateContract: true));
        Assert.Equal(BavMutationResult.Success, committed.Result);
        Assert.False(harness.Ai.WasCalled);
    }

    /// <summary>
    /// A non-member gets not-found everywhere, so the existence of a pension document does not leak,
    /// and a member without the owner role can read but not write.
    /// </summary>
    [Fact]
    public async Task An_outsider_gets_nothing_and_a_member_may_read_but_not_write()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = Draft();

        var uploaded = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("acl"));
        var documentId = ((BavDocumentDetailView)uploaded.Value!).Document.Id;

        Assert.Null(await harness.RunAsync(store => store.ListAsync(scenario.Outside, scenario.Space, default)));
        Assert.Null(await harness.RunAsync(store => store.GetAsync(scenario.Outside, scenario.Space, documentId, default)));
        Assert.Null(await harness.RunAsync(store => store.ContentAsync(scenario.Outside, scenario.Space, documentId, default)));
        Assert.Equal(BavMutationResult.NotFound,
            (await harness.UploadAsync(scenario.Outside, scenario.Space, Pdf("outsider"))).Result);
        Assert.Equal(BavMutationResult.NotFound,
            (await harness.ReviewAsync(scenario.Outside, scenario.Space, documentId, new BavDocumentReviewRequest(Draft()))).Result);
        Assert.Equal(BavMutationResult.NotFound,
            (await harness.CommitAsync(scenario.Outside, scenario.Space, documentId,
                new BavDocumentCommitRequest(Draft(), CreateContract: true))).Result);
        Assert.Equal(BavMutationResult.NotFound,
            (await harness.DeleteAsync(scenario.Outside, scenario.Space, documentId)).Result);

        Assert.NotNull(await harness.RunAsync(store => store.ListAsync(scenario.Member, scenario.Space, default)));
        Assert.NotNull(await harness.RunAsync(store => store.GetAsync(scenario.Member, scenario.Space, documentId, default)));
        Assert.Equal(BavMutationResult.Forbidden,
            (await harness.UploadAsync(scenario.Member, scenario.Space, Pdf("member"))).Result);
        Assert.Equal(BavMutationResult.Forbidden,
            (await harness.ReviewAsync(scenario.Member, scenario.Space, documentId, new BavDocumentReviewRequest(Draft()))).Result);
        Assert.Equal(BavMutationResult.Forbidden,
            (await harness.CommitAsync(scenario.Member, scenario.Space, documentId,
                new BavDocumentCommitRequest(Draft(), CreateContract: true))).Result);
        Assert.Equal(BavMutationResult.Forbidden,
            (await harness.DeleteAsync(scenario.Member, scenario.Space, documentId)).Result);
    }

    /// <summary>
    /// Delete takes the blob with the row. It is refused while a snapshot points at the document: the
    /// foreign key is <c>SetNull</c>, so the delete would leave a committed figure claiming a source
    /// nobody can produce any more.
    /// </summary>
    [Fact]
    public async Task Delete_removes_blob_and_row_but_never_a_committed_value_s_provenance()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = Draft();

        var committedUpload = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("committed"));
        var committedId = ((BavDocumentDetailView)committedUpload.Value!).Document.Id;
        Assert.Equal(BavMutationResult.Success, (await harness.CommitAsync(scenario.Owner, scenario.Space, committedId,
            new BavDocumentCommitRequest(Draft(), CreateContract: true))).Result);

        var refused = await harness.DeleteAsync(scenario.Owner, scenario.Space, committedId);
        Assert.Equal(BavMutationResult.Conflict, refused.Result);

        var pendingUpload = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("uncommitted"));
        var pendingDetail = (BavDocumentDetailView)pendingUpload.Value!;
        var pendingId = pendingDetail.Document.Id;

        string pendingPath = null!;
        await harness.Factory.SeedAsync(async db =>
            pendingPath = (await db.BavDocuments.AsNoTracking().SingleAsync(x => x.Id == pendingId)).StoragePath!);
        Assert.True(harness.Blobs.Exists(pendingPath));

        Assert.Equal(BavMutationResult.Success, (await harness.DeleteAsync(scenario.Owner, scenario.Space, pendingId)).Result);
        Assert.False(harness.Blobs.Exists(pendingPath));

        await harness.Factory.SeedAsync(async db =>
        {
            Assert.False(await db.BavDocuments.AsNoTracking().AnyAsync(x => x.Id == pendingId));
            Assert.True(await db.BavDocuments.AsNoTracking().AnyAsync(x => x.Id == committedId));
        });
    }

    /// <summary>
    /// The policy number is personal data, and an API response is the one place it would leak into a
    /// browser cache, a screenshot or any log that records a body - which is why BavContractView only
    /// ever exposes the last four characters. The review draft is such a response, so the number is not
    /// in it either.
    ///
    /// The consequence has to be handled rather than accepted: the browser was never given the number,
    /// so a null coming back means "unchanged", not "delete". Without that, committing a reviewed draft
    /// would create a contract with no policy number even though the document stated one - and the next
    /// statement for that contract would no longer match it, which is the whole point of storing it.
    /// </summary>
    [Fact]
    public async Task The_policy_number_never_reaches_the_browser_and_survives_the_round_trip()
    {
        using var harness = Harness.Create();
        var scenario = await PensionContractIntegrationTests.SeedAsync(harness.Factory);
        harness.Parser.Result = Draft();

        var uploaded = await harness.UploadAsync(scenario.Owner, scenario.Space, Pdf("policy"));
        var detail = (BavDocumentDetailView)uploaded.Value!;
        var documentId = detail.Document.Id;

        // Not in the upload response, and not in the detail the review screen loads.
        Assert.Null(detail.Draft!.Contract.PolicyNumber);
        var loaded = await harness.GetAsync(scenario.Owner, scenario.Space, documentId);
        Assert.Null(loaded!.Draft!.Contract.PolicyNumber);
        // But the screen can still say a number WAS found, because the match rule reports it fired.
        Assert.NotNull(loaded.Match);

        // A review that sends the redacted draft back must not erase it.
        var reviewed = await harness.ReviewAsync(scenario.Owner, scenario.Space, documentId,
            new BavDocumentReviewRequest(loaded.Draft!));
        Assert.Equal(BavMutationResult.Success, reviewed.Result);
        Assert.Null(((BavDocumentDetailView)reviewed.Value!).Draft!.Contract.PolicyNumber);

        var committed = await harness.CommitAsync(scenario.Owner, scenario.Space, documentId,
            new BavDocumentCommitRequest(loaded.Draft!, CreateContract: true));
        Assert.Equal(BavMutationResult.Success, committed.Result);

        // The contract carries the number the document stated, encrypted, with only its tail readable.
        await harness.Factory.SeedAsync(async db =>
        {
            var contract = await db.BavContracts.AsNoTracking()
                .SingleAsync(x => x.FullWorthSpaceId == scenario.Space && x.ProviderName == "Allianz Lebensversicherungs-AG");
            Assert.False(string.IsNullOrWhiteSpace(contract.PolicyNumberEncrypted));
            Assert.False(string.IsNullOrWhiteSpace(contract.PolicyNumberLookup));
            Assert.Equal("4711", contract.PolicyNumberLast4);
        });
    }

    // ---- drafts ----

    /// <summary>The minimum a Standmitteilung states: provider, policy number, a date and a balance.</summary>
    private static BavDocumentDraft Draft() => new(
        new BavContractDraft(
            ProviderName: "Allianz Lebensversicherungs-AG",
            TariffName: "PensionInvest Flex",
            PolicyNumber: "BAV-2018 / 4711",
            ImplementationRoute: BavImplementationRoutes.DirectInsurance,
            EmployerName: "Muster GmbH",
            RetirementDate: new DateOnly(2050, 6, 1),
            Currency: "EUR"),
        new BavSnapshotDraft(
            EffectiveDate: new DateOnly(2026, 1, 1),
            Currency: "EUR",
            Balance: 12_500.55m,
            GuaranteedBalance: 9_000m),
        null,
        [],
        [],
        [new BavFieldProvenance("snapshot.balance", 1, 0.95m, BavExtractionSources.Deterministic, "Vertragsguthaben")],
        0.75m,
        BavExtractionSources.Deterministic,
        2,
        true,
        []);

    /// <summary>
    /// A full statement: the split contribution, a fund and an estimated fund cost. The employer share
    /// is kept apart from the employee share because only the employee share is an outflow.
    /// </summary>
    private static BavDocumentDraft FullDraft() => Draft() with
    {
        Contribution = new BavContributionDraft(
            ValidFrom: new DateOnly(2026, 1, 1),
            Cycle: BavContributionCycles.Monthly,
            Currency: "EUR",
            EmployeeAmount: 169m,
            EmployerSubsidyAmount: 25.35m,
            EmployerAmount: 143.65m,
            StatedTotalAmount: 338m),
        Allocations =
        [
            new BavAllocationDraft(
                FundName: "DWS Vermögensbildungsfonds I",
                Isin: "DE0009848119",
                WeightPercent: 100m,
                AssetClass: BavAssetClasses.Equity)
        ],
        Costs =
        [
            new BavCostDraft(
                Kind: BavCostKinds.Fund,
                Basis: BavCostBases.PercentOfCapital,
                Percent: 1.15m,
                Timing: BavCostTimings.Ongoing,
                IsEstimated: true,
                EstimateBasis: "Fondsprospekt, laufende Kosten p.a.")
        ]
    };

    private static byte[] Pdf(string marker) =>
        [.. "%PDF-1.7\n"u8.ToArray(), .. System.Text.Encoding.UTF8.GetBytes(marker)];

    // ---- harness ----

    /// <summary>
    /// The store under test with the three pipeline seams faked. They are registered as singletons so
    /// a test can steer the extraction and then look at what the store did with the result.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public required BackendWebApplicationFactory Factory { get; init; }
        public required FakeBlobStore Blobs { get; init; }
        public required FakeTextSource Text { get; init; }
        public required FakeParser Parser { get; init; }
        public required FakeAi Ai { get; init; }

        public static Harness Create()
        {
            var blobs = new FakeBlobStore();
            var text = new FakeTextSource();
            var parser = new FakeParser();
            var ai = new FakeAi();
            var factory = new BackendWebApplicationFactory(
                new Dictionary<string, string?>(),
                services =>
                {
                    services.AddSingleton<IBavDocumentBlobStore>(blobs);
                    services.AddSingleton<IBavDocumentTextSource>(text);
                    services.AddSingleton<IBavDocumentParser>(parser);
                    services.AddSingleton<IBavDocumentAiStructurer>(ai);
                    services.AddScoped<PensionDocumentStore>();
                });
            return new Harness { Factory = factory, Blobs = blobs, Text = text, Parser = parser, Ai = ai };
        }

        // A fresh scope per call, so no operation sees another one's tracked entities - the endpoints
        // get a fresh request scope too.
        public async Task<T> RunAsync<T>(Func<PensionDocumentStore, Task<T>> work)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await work(scope.ServiceProvider.GetRequiredService<PensionDocumentStore>());
        }

        public Task<BavDocumentOutcome> UploadAsync(Guid userId, Guid spaceId, byte[] content) =>
            RunAsync(store => store.UploadAsync(userId, spaceId, content, "standmitteilung.pdf",
                BavDocumentKinds.AnnualStatement, default));

        public Task<BavDocumentOutcome> ReviewAsync(Guid userId, Guid spaceId, Guid documentId, BavDocumentReviewRequest request) =>
            RunAsync(store => store.ReviewAsync(userId, spaceId, documentId, request, default));

        public Task<BavDocumentOutcome> CommitAsync(Guid userId, Guid spaceId, Guid documentId, BavDocumentCommitRequest request) =>
            RunAsync(store => store.CommitAsync(userId, spaceId, documentId, request, default));

        public Task<BavDocumentOutcome> DeleteAsync(Guid userId, Guid spaceId, Guid documentId) =>
            RunAsync(store => store.DeleteAsync(userId, spaceId, documentId, default));

        public async Task<BavDocumentDetailView?> GetAsync(Guid userId, Guid spaceId, Guid documentId)
        {
            await using var scope = Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<PensionDocumentStore>()
                .GetAsync(userId, spaceId, documentId, default);
        }

        public void Dispose() => Factory.Dispose();
    }

    private sealed class FakeBlobStore : IBavDocumentBlobStore
    {
        private readonly Dictionary<string, byte[]> stored = new(StringComparer.Ordinal);

        public bool Exists(string storagePath) => stored.ContainsKey(storagePath);

        public Task<BavStoredBlob> WriteAsync(
            Guid fullWorthSpaceId, Guid documentId, string extension, byte[] content, CancellationToken ct)
        {
            var path = $"{fullWorthSpaceId:N}/{documentId:N}{extension}";
            stored[path] = content;
            return Task.FromResult(new BavStoredBlob(path, BavDocumentEncryptionSchemes.AesGcmV1));
        }

        public Task<byte[]?> ReadAsync(string storagePath, string? encryptionScheme, CancellationToken ct) =>
            Task.FromResult(stored.TryGetValue(storagePath, out var content) ? content : null);

        public Task DeleteAsync(string storagePath, CancellationToken ct)
        {
            stored.Remove(storagePath);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTextSource : IBavDocumentTextSource
    {
        public BavDocumentText Result { get; set; } = new(["Vertragsguthaben", "Beitrag"], TextLayerUsed: true);
        public Exception? Failure { get; set; }

        public Task<BavDocumentText> ReadAsync(byte[] content, string extension, CancellationToken ct) =>
            Failure is null ? Task.FromResult(Result) : throw Failure;
    }

    private sealed class FakeParser : IBavDocumentParser
    {
        public BavDocumentDraft Result { get; set; } = Draft();

        public BavDocumentDraft Parse(BavDocumentText text) => Result;
    }

    private sealed class FakeAi : IBavDocumentAiStructurer
    {
        public bool Enabled { get; set; }
        public bool ThrowIfCalled { get; set; }
        public bool WasCalled { get; private set; }

        public bool IsEnabled => Enabled;

        public Task<BavDocumentDraft> EnrichAsync(
            Guid userId, BavDocumentDraft draft, BavDocumentText text, CancellationToken ct)
        {
            WasCalled = true;
            if (ThrowIfCalled) throw new InvalidOperationException("No Codex bridge is configured.");
            return Task.FromResult(draft);
        }
    }
}
