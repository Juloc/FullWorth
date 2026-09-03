using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class IntelligenceSuggestionConcurrencyTests
{
    [Fact]
    public async Task Database_allows_only_one_pending_semantic_suggestion_per_fullworth_space()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var spaceId = Guid.NewGuid();

        db.IntelligenceSuggestions.Add(Create(spaceId));
        await db.SaveChangesAsync();
        db.IntelligenceSuggestions.Add(Create(spaceId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Reviewed_suggestion_does_not_block_a_new_pending_proposal()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        await using var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var spaceId = Guid.NewGuid();
        var first = Create(spaceId);
        db.IntelligenceSuggestions.Add(first);
        await db.SaveChangesAsync();
        first.Status = IntelligenceSuggestionStatuses.Rejected;
        first.ReviewedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        db.IntelligenceSuggestions.Add(Create(spaceId));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.IntelligenceSuggestions.CountAsync());
        Assert.Single(await db.IntelligenceSuggestions.Where(x => x.Status == IntelligenceSuggestionStatuses.Pending).ToListAsync());
    }

    private static IntelligenceSuggestion Create(Guid spaceId) => new()
    {
        FullWorthSpaceId = spaceId,
        Type = "merchant-category",
        SubjectType = "merchant",
        SubjectId = "REWE",
        SemanticKey = "merchant-category:expense",
        ProposedPayloadJson = "{\"categoryKey\":\"food.groceries\"}",
        EvidenceJson = "{}",
        Provider = "fake",
        Model = "fake",
        Confidence = 0.9m
    };
}
