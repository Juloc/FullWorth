using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Import;

/// <summary>
/// Import-Staging raeumt sich selbst auf (#142): weder Abbrechen noch Ruecknahme loeschten bisher eine
/// Zeile aus "ImportCandidates" - die Empfaenger und Verwendungszweck im Klartext haelt - und nichts
/// nullte je den OCR-Volltext eines abgeschlossenen Beleg-Importstapels.
/// </summary>
public sealed class ImportStagingCleanupServiceTests
{
    private const int RetentionDays = 30;

    [Fact]
    public async Task AnOldRolledBackJobLosesItsCandidatesButKeepsTheJobRow()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedSpaceAsync(factory);
        var jobId = await SeedJobAsync(factory, userId, spaceId, "rolled_back", DaysAgo(RetentionDays + 1));
        await SeedCandidateAsync(factory, jobId);

        var purged = await PurgeAsync(factory);

        Assert.Equal(1, purged);
        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await CandidateCountAsync(db, jobId));
            Assert.Equal(1, await JobCountAsync(db, jobId));
        });
    }

    [Fact]
    public async Task ARolledBackJobYoungerThanTheWindowIsUntouched()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedSpaceAsync(factory);
        var jobId = await SeedJobAsync(factory, userId, spaceId, "rolled_back", DaysAgo(RetentionDays - 1));
        await SeedCandidateAsync(factory, jobId);

        var purged = await PurgeAsync(factory);

        Assert.Equal(0, purged);
        await factory.SeedAsync(async db => Assert.Equal(1, await CandidateCountAsync(db, jobId)));
    }

    /// <summary>
    /// Ein abgeschlossener Auftrag mit noch verknuepften Buchungen ist ueber "rollbackAvailable" noch
    /// rueckgaengig machbar (siehe ImportJobStore.JobRow) - seine Kandidatenzeilen speisen die
    /// Detailansicht, in der der Nutzer nachsieht, was darin steckt, und bleiben deshalb stehen, egal
    /// wie alt der Auftrag ist.
    /// </summary>
    [Fact]
    public async Task ACompletedJobWithLiveLinksIsUntouchedEvenWhenOld()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedSpaceAsync(factory);
        var jobId = await SeedJobAsync(factory, userId, spaceId, "completed", DaysAgo(RetentionDays + 10));
        await SeedCandidateAsync(factory, jobId);
        await SeedLinkedTransactionAsync(factory, spaceId, jobId);

        var purged = await PurgeAsync(factory);

        Assert.Equal(0, purged);
        await factory.SeedAsync(async db => Assert.Equal(1, await CandidateCountAsync(db, jobId)));
    }

    [Fact]
    public async Task ACompletedJobWithNoLinksIsPurgedOnceOld()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedSpaceAsync(factory);
        var jobId = await SeedJobAsync(factory, userId, spaceId, "completed", DaysAgo(RetentionDays + 1));
        await SeedCandidateAsync(factory, jobId);

        var purged = await PurgeAsync(factory);

        Assert.Equal(1, purged);
        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await CandidateCountAsync(db, jobId));
            Assert.Equal(1, await JobCountAsync(db, jobId));
        });
    }

    /// <summary>
    /// Ein Zwischenstand, den nie jemand festgeschrieben hat (#131, Schritt 4). Er steht auf
    /// 'ready' und wuerde ohne diese Regel ewig liegen bleiben - mitsamt der gelesenen Datei, die
    /// der Finanzguru-Weg bis zum Festschreiben am Auftrag aufbewahrt.
    /// </summary>
    [Fact]
    public async Task AnAbandonedStagedJobLosesItsRowsAndItsStoredFile()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedSpaceAsync(factory);
        var jobId = await SeedJobAsync(factory, userId, spaceId, "ready", DaysAgo(RetentionDays + 1));
        await SeedCandidateAsync(factory, jobId);
        await SeedStoredPayloadAsync(factory, jobId);

        var purged = await PurgeAsync(factory);

        Assert.Equal(1, purged);
        await factory.SeedAsync(async db =>
        {
            Assert.Equal(0, await CandidateCountAsync(db, jobId));
            Assert.Equal(1, await JobCountAsync(db, jobId));
            Assert.Null(await StoredPayloadAsync(db, jobId));
            // Nicht mehr 'ready': ein Festschreiben haette nichts mehr zu schreiben.
            Assert.Equal("cancelled", await StatusAsync(db, jobId));
        });
    }

    [Fact]
    public async Task AStagedJobYoungerThanTheWindowKeepsEverything()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedSpaceAsync(factory);
        var jobId = await SeedJobAsync(factory, userId, spaceId, "ready", DaysAgo(RetentionDays - 1));
        await SeedCandidateAsync(factory, jobId);
        await SeedStoredPayloadAsync(factory, jobId);

        var purged = await PurgeAsync(factory);

        Assert.Equal(0, purged);
        await factory.SeedAsync(async db =>
        {
            Assert.Equal(1, await CandidateCountAsync(db, jobId));
            Assert.NotNull(await StoredPayloadAsync(db, jobId));
            Assert.Equal("ready", await StatusAsync(db, jobId));
        });
    }

    [Fact]
    public async Task AnOldCompletedReceiptBatchHasItsSourceTextNulledButKeepsTheItemRow()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedSpaceAsync(factory);
        var (_, itemId) = await SeedReceiptBatchAsync(factory, userId, spaceId, "completed", DaysAgo(RetentionDays + 1));

        int scrubbed;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ImportStagingCleanupService>();
            scrubbed = await service.ScrubStaleReceiptSourceTextAsync(RetentionDays, CancellationToken.None);
        }

        Assert.Equal(1, scrubbed);
        await factory.SeedAsync(async db =>
        {
            var row = await db.Database.SqlQuery<ReceiptItemProbe>(
                $"""SELECT "SourceText", "ExternalKey", "ContentFingerprint", "PurchaseId" FROM "ReceiptImportItems" WHERE "Id" = {itemId}""")
                .SingleAsync();
            Assert.Null(row.SourceText);
            Assert.Equal("doc-1", row.ExternalKey);
            Assert.Equal("fp-1", row.ContentFingerprint);
            Assert.NotNull(row.PurchaseId);
        });
    }

    [Fact]
    public async Task AYoungCompletedReceiptBatchIsUntouched()
    {
        using var factory = new BackendWebApplicationFactory();
        var (userId, spaceId) = await SeedSpaceAsync(factory);
        var (_, itemId) = await SeedReceiptBatchAsync(factory, userId, spaceId, "completed", DaysAgo(RetentionDays - 1));

        int scrubbed;
        using (var scope = factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ImportStagingCleanupService>();
            scrubbed = await service.ScrubStaleReceiptSourceTextAsync(RetentionDays, CancellationToken.None);
        }

        Assert.Equal(0, scrubbed);
        await factory.SeedAsync(async db =>
        {
            var sourceText = await db.Database.SqlQuery<string>(
                $"""SELECT "SourceText" AS "Value" FROM "ReceiptImportItems" WHERE "Id" = {itemId}""").SingleAsync();
            Assert.NotNull(sourceText);
        });
    }

    private sealed record ReceiptItemProbe(string? SourceText, string ExternalKey, string? ContentFingerprint, Guid? PurchaseId);

    private static DateTimeOffset DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    private static async Task<int> PurgeAsync(BackendWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ImportStagingCleanupService>();
        return await service.PurgeStaleCandidatesAsync(RetentionDays, CancellationToken.None);
    }

    private static Task<int> CandidateCountAsync(FullWorthDbContext db, Guid jobId) =>
        db.Database.SqlQuery<int>(
            $"""SELECT count(*)::int AS "Value" FROM "ImportCandidates" WHERE "ImportJobId" = {jobId}""").SingleAsync();

    private static Task SeedStoredPayloadAsync(BackendWebApplicationFactory factory, Guid jobId) =>
        factory.SeedAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "ImportJobs" SET "SourcePayloadEncrypted" = 'verschluesselt' WHERE "Id" = {jobId}"""));

    private static async Task<string?> StoredPayloadAsync(FullWorthDbContext db, Guid jobId) =>
        (await db.Database.SqlQuery<string?>(
            $"""SELECT "SourcePayloadEncrypted" AS "Value" FROM "ImportJobs" WHERE "Id" = {jobId}""").ToListAsync()).Single();

    private static Task<string> StatusAsync(FullWorthDbContext db, Guid jobId) =>
        db.Database.SqlQuery<string>(
            $"""SELECT "Status" AS "Value" FROM "ImportJobs" WHERE "Id" = {jobId}""").SingleAsync();

    private static Task<int> JobCountAsync(FullWorthDbContext db, Guid jobId) =>
        db.Database.SqlQuery<int>(
            $"""SELECT count(*)::int AS "Value" FROM "ImportJobs" WHERE "Id" = {jobId}""").SingleAsync();

    private static async Task<(Guid UserId, Guid SpaceId)> SeedSpaceAsync(BackendWebApplicationFactory factory)
    {
        var userId = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Users.Add(new FullWorthUser
            {
                Id = userId,
                EmailNormalized = $"{userId:N}@LOCAL.TEST",
                DisplayName = "Test",
                IsActive = true
            });
            db.FullWorthSpaces.Add(new FullWorthSpace { Id = spaceId, Name = "Test", BaseCurrency = "EUR" });
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember
            {
                FullWorthSpaceId = spaceId,
                UserId = userId,
                Role = FullWorthSpaceRoles.Owner
            });
            await db.SaveChangesAsync();
        });
        return (userId, spaceId);
    }

    private static async Task<Guid> SeedJobAsync(
        BackendWebApplicationFactory factory, Guid userId, Guid spaceId, string status, DateTimeOffset updatedAt)
    {
        var jobId = Guid.NewGuid();
        await factory.SeedAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ImportJobs"
                ("Id","FullWorthSpaceId","UserId","FileName","FileSha256","AdapterKey","Status",
                 "SourceRowCount","ReadyCount","DuplicateCount","ImportedCount","ErrorCount","CreatedAt","UpdatedAt")
            VALUES
                ({jobId},{spaceId},{userId},'test.csv','sha','generic',{status},1,1,0,1,0,{updatedAt},{updatedAt})
            """));
        return jobId;
    }

    private static Task SeedCandidateAsync(BackendWebApplicationFactory factory, Guid jobId) =>
        factory.SeedAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ImportCandidates"
                ("Id","ImportJobId","Amount","Currency","RowFingerprint","DuplicateStatus","ValidationStatus")
            VALUES
                ({Guid.NewGuid()},{jobId},-10.00,'EUR',{Guid.NewGuid().ToString("N")},'imported','ready')
            """));

    private static async Task SeedLinkedTransactionAsync(BackendWebApplicationFactory factory, Guid spaceId, Guid jobId)
    {
        var accountId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        await factory.SeedAsync(async db =>
        {
            db.Accounts.Add(new FinanceAccount
            {
                Id = accountId,
                FullWorthSpaceId = spaceId,
                Provider = "import",
                IdentificationHash = $"import|{accountId:N}",
                ProviderAccountId = $"import:{accountId:N}",
                InstitutionName = "Import",
                DisplayName = "Import",
                Currency = "EUR",
                IsActive = true
            });
            db.Transactions.Add(new FinanceTransaction
            {
                Id = transactionId,
                AccountId = accountId,
                ExternalKey = $"import:{transactionId:N}",
                BookingDate = new DateOnly(2026, 1, 1),
                ValueDate = new DateOnly(2026, 1, 1),
                Amount = -10m,
                Currency = "EUR"
            });
            await db.SaveChangesAsync();
        });
        await factory.SeedAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ImportTransactionLinks" ("ImportJobId","TransactionId","CreatedAt")
            VALUES ({jobId},{transactionId},{DateTimeOffset.UtcNow})
            """));
    }

    private static async Task<(Guid BatchId, Guid ItemId)> SeedReceiptBatchAsync(
        BackendWebApplicationFactory factory, Guid userId, Guid spaceId, string status, DateTimeOffset updatedAt)
    {
        var batchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        // "ReceiptImportItems.PurchaseId" has no foreign key today (#141 gives it real provenance) -
        // a bare, unrelated id is exactly what the running code sees, so that is what is seeded here.
        var purchaseId = Guid.NewGuid();
        await factory.SeedAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReceiptImportBatches"
                ("Id","FullWorthSpaceId","UserId","SourceType","SourceName","Currency","Status","AutoStart","CreatedAt","UpdatedAt")
            VALUES
                ({batchId},{spaceId},{userId},'upload','test.pdf','EUR',{status},false,{updatedAt},{updatedAt})
            """));
        await factory.SeedAsync(db => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "ReceiptImportItems"
                ("Id","BatchId","FullWorthSpaceId","SourceType","ExternalKey","DisplayName","ContentFingerprint","PurchaseId","Status","CreatedAt","UpdatedAt","SourceText")
            VALUES
                ({itemId},{batchId},{spaceId},'upload','doc-1','Beleg','fp-1',{purchaseId},'done',{updatedAt},{updatedAt},'ocr text')
            """));
        return (batchId, itemId);
    }
}
