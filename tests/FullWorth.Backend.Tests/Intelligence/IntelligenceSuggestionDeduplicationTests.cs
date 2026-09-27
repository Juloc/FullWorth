using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Ein abgelehnter Vorschlag wird nicht erneut gestellt. Ein Mensch hat schon geantwortet, und
/// "nein" ist eine Antwort so gut wie jede andere - der naechste Lauf darf sie nicht einfach
/// uebergehen, nur weil das Pruef-Fenster inzwischen zu ist.
/// </summary>
public sealed class IntelligenceSuggestionDeduplicationTests
{
    private static IntelligenceSuggestion Suggestion(string status = IntelligenceSuggestionStatuses.Pending) => new()
    {
        FullWorthSpaceId = Guid.NewGuid(),
        Type = "merchant-category",
        SubjectType = "merchant",
        SubjectId = "REWE",
        SemanticKey = "merchant-category:expense",
        ProposedPayloadJson = "{}",
        EvidenceJson = "{}",
        Provider = "fake",
        Model = "fake-model",
        Confidence = 0.9m,
        Status = status
    };

    [Fact]
    public async Task A_pending_suggestion_for_the_same_subject_is_not_duplicated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new IntelligenceDbContext(new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new IntelligenceStore(db, FieldCipher.Null, new IntelligenceProviderRegistry([]));
        var first = Suggestion();
        first.FullWorthSpaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Suggestion();
        second.FullWorthSpaceId = first.FullWorthSpaceId;

        Assert.NotNull(await store.TryAddSuggestionAsync(first, CancellationToken.None));
        Assert.Null(await store.TryAddSuggestionAsync(second, CancellationToken.None));

        Assert.Single(await db.IntelligenceSuggestions.ToListAsync());
    }

    /// <summary>Der Kern dieses Tests: die Ablehnung sperrt dieselbe Frage dauerhaft, nicht nur bis zum naechsten Lauf.</summary>
    [Fact]
    public async Task A_rejected_suggestion_for_the_same_subject_is_not_re_proposed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new IntelligenceDbContext(new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new IntelligenceStore(db, FieldCipher.Null, new IntelligenceProviderRegistry([]));
        var spaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var rejected = Suggestion(IntelligenceSuggestionStatuses.Rejected);
        rejected.FullWorthSpaceId = spaceId;
        db.IntelligenceSuggestions.Add(rejected);
        await db.SaveChangesAsync();

        var reproposed = Suggestion();
        reproposed.FullWorthSpaceId = spaceId;

        Assert.Null(await store.TryAddSuggestionAsync(reproposed, CancellationToken.None));
        Assert.Single(await db.IntelligenceSuggestions.ToListAsync());
    }

    /// <summary>Ein anderer Haushalt auf derselben Instanz ist eine andere Frage - die Sperre ist pro Space.</summary>
    [Fact]
    public async Task A_rejection_in_one_space_does_not_block_another_space()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new IntelligenceDbContext(new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var store = new IntelligenceStore(db, FieldCipher.Null, new IntelligenceProviderRegistry([]));

        var rejected = Suggestion(IntelligenceSuggestionStatuses.Rejected);
        db.IntelligenceSuggestions.Add(rejected);
        await db.SaveChangesAsync();

        var forAnotherSpace = Suggestion();

        Assert.NotNull(await store.TryAddSuggestionAsync(forAnotherSpace, CancellationToken.None));
    }
}
