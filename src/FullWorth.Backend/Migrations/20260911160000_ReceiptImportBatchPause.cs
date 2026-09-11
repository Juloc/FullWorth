using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FullWorth.Backend.Migrations;

/// <summary>
/// An import batch can be paused.
///
/// Until now a started batch ran to the end. There was <c>start-pending</c> and <c>retry-failed</c> and
/// nothing else — no cancel, no pause, no way back from <c>queued</c>. A hundred receipts uploaded by
/// mistake, or an extraction provider that turns out to cost money per call, could only be waited out.
///
/// The pause itself needs no new job state: the worker claims <c>Status = 'queued'</c> only, and
/// <c>draft</c> already means "created, not started". Pausing therefore pushes this batch's queued jobs
/// back to draft, which is exactly the state they were in before somebody pressed start. Resuming is
/// the same code path as starting. A job already in <c>processing</c> is left alone and runs to its end:
/// interrupting an extraction mid-flight would leave a purchase half-written, and "stop taking new work"
/// is what pause means everywhere else too.
///
/// So the only thing that has to be remembered is <b>that</b> the batch is paused — otherwise the next
/// upload into it, or the Paperless auto-import, would quietly start it again.
/// </summary>
[DbContext(typeof(FullWorthDbContext))]
[Migration("20260911160000_ReceiptImportBatchPause")]
public sealed class ReceiptImportBatchPause : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ReceiptImportBatches"
  ADD COLUMN IF NOT EXISTS "PausedAt" timestamp with time zone NULL;
""");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
ALTER TABLE "ReceiptImportBatches"
  DROP COLUMN IF EXISTS "PausedAt";
""");
    }
}
