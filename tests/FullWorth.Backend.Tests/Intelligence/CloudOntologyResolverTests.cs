using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudOntologyResolverTests
{
    [Fact]
    public async Task Unique_active_alias_maps_custom_local_name_to_canonical_key()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.OfficialOntologyEntities.Add(new OfficialOntologyEntity
        {
            EntityType = "category",
            CanonicalKey = "housing.electricity",
            DisplayName = "Electricity",
            Status = "active",
            Version = 1
        });
        fixture.Db.OfficialOntologyAliases.Add(new OfficialOntologyAlias
        {
            EntityType = "category",
            CanonicalKey = "housing.electricity",
            Alias = "Strom",
            NormalizedAlias = "STROM",
            Locale = "de",
            Country = "DE",
            Confidence = 0.95m,
            DistinctInstances = 25,
            Version = 1
        });
        await fixture.Db.SaveChangesAsync();

        var localId = Guid.NewGuid();
        var result = await new CloudOntologyResolver(fixture.Db).ExpandCategoryMapAsync(
            [new LocalCategorySemanticCandidate(localId, "custom.power", "Strom")],
            "DE",
            CancellationToken.None);

        Assert.Equal(localId, result["custom.power"]);
        Assert.Equal(localId, result["housing.electricity"]);
    }

    [Fact]
    public async Task Ambiguous_alias_is_not_mapped_automatically()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var key in new[] { "housing.electricity", "housing.gas" })
        {
            fixture.Db.OfficialOntologyEntities.Add(new OfficialOntologyEntity
            {
                EntityType = "category",
                CanonicalKey = key,
                DisplayName = key,
                Status = "active",
                Version = 1
            });
            fixture.Db.OfficialOntologyAliases.Add(new OfficialOntologyAlias
            {
                EntityType = "category",
                CanonicalKey = key,
                Alias = "Energie",
                NormalizedAlias = "ENERGIE",
                Locale = "de",
                Country = "DE",
                Confidence = 0.95m,
                DistinctInstances = 25,
                Version = 1
            });
        }
        await fixture.Db.SaveChangesAsync();

        var localId = Guid.NewGuid();
        var result = await new CloudOntologyResolver(fixture.Db).ExpandCategoryMapAsync(
            [new LocalCategorySemanticCandidate(localId, "custom.energy", "Energie")],
            "DE",
            CancellationToken.None);

        Assert.Equal(localId, result["custom.energy"]);
        Assert.False(result.ContainsKey("housing.electricity"));
        Assert.False(result.ContainsKey("housing.gas"));
    }

    [Fact]
    public async Task Approved_redirect_preserves_old_local_category_key()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.OfficialOntologyRedirects.Add(new OfficialOntologyRedirect
        {
            EntityType = "category",
            FromCanonicalKey = "dynamic.category.strom.1234567890",
            ToCanonicalKey = "housing.electricity",
            Version = 2
        });
        await fixture.Db.SaveChangesAsync();

        var localId = Guid.NewGuid();
        var result = await new CloudOntologyResolver(fixture.Db).ExpandCategoryMapAsync(
            [new LocalCategorySemanticCandidate(
                localId,
                "dynamic.category.strom.1234567890",
                "Strom")],
            "DE",
            CancellationToken.None);

        Assert.Equal(localId, result["housing.electricity"]);
    }

    private sealed class Fixture(
        SqliteConnection connection,
        IntelligenceDbContext db) : IAsyncDisposable
    {
        public IntelligenceDbContext Db { get; } = db;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<IntelligenceDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new IntelligenceDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
