using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// Step 2 of the bAV work (docs/PENSION.md): a pension document is uploaded, stored encrypted, read,
/// parsed — and then <b>waits for a person</b>. That waiting state needs somewhere to live.
///
/// <c>BavDocuments</c> was created empty in <c>20260910233000_OccupationalPension</c> with room for the
/// file's identity (hash, kind, pages, storage path, extraction status/confidence/source) but no room
/// for the candidate values themselves. So this migration adds:
///
/// <list type="bullet">
/// <item>"ExtractionDraftJson" — the parsed draft the review screen edits. Nothing extracted may be
/// stored as a contract value without review, so the draft has to survive between upload and commit,
/// and it is cleared on commit: by then the real rows carry the numbers, and a leftover copy of the
/// same figures (policy number included) would only be a second place to leak from.</item>
/// <item>"ExtractionError" — a short category (<c>no_text</c>, <c>tool_missing</c>, <c>unsupported</c>),
/// deliberately capped at 64 characters so a tool's raw output cannot be smuggled in. No document
/// content is ever written here: this value is returned to the browser and may reach a log.</item>
/// <item>"ExtractedAt" — when the pipeline last ran, so a re-read is distinguishable from the original.</item>
/// <item>"TextLayerUsed" — whether the text came from the PDF's own text layer or from OCR. A digitally
/// generated statement has exact text; an OCR'd number is a <i>recognised</i> number, and the review
/// screen has to be able to say which of the two the user is confirming.</item>
/// </list>
///
/// Additive only. No existing column is touched, nothing is dropped, and every document already in the
/// table stays exactly as it was — with no draft, which is the correct state for a file that predates
/// the pipeline.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260911010000_PensionDocumentExtraction")]
public sealed class PensionDocumentExtraction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "BavDocuments"
  ADD COLUMN IF NOT EXISTS "ExtractionDraftJson" text NULL,
  ADD COLUMN IF NOT EXISTS "ExtractionError" character varying(64) NULL,
  ADD COLUMN IF NOT EXISTS "ExtractedAt" timestamp with time zone NULL,
  ADD COLUMN IF NOT EXISTS "TextLayerUsed" boolean NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "BavDocuments"
  DROP COLUMN IF EXISTS "ExtractionDraftJson",
  DROP COLUMN IF EXISTS "ExtractionError",
  DROP COLUMN IF EXISTS "ExtractedAt",
  DROP COLUMN IF EXISTS "TextLayerUsed";
""");
    }
}
