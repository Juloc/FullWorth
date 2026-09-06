using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class CloudOperationalRegistryResolverTests
{
    [Fact]
    public async Task Resolves_provider_from_reviewed_signature()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.OfficialContractProviders.Add(new OfficialContractProvider
        {
            ProviderKey = "provider.telekom",
            CanonicalName = "Deutsche Telekom",
            ProviderCategory = "telecom",
            Country = "DE",
            Version = 1
        });
        fixture.Db.OfficialContractSignatures.Add(new OfficialContractSignature
        {
            ProviderKey = "provider.telekom",
            MerchantFingerprint = "DEUTSCHE TELEKOM",
            ExpectedRecurrence = "monthly",
            Confidence = 0.98m
        });
        await fixture.Db.SaveChangesAsync();

        var resolved = await new CloudOperationalRegistryResolver(fixture.Db)
            .ResolveProviderAsync("Deutsche Telekom", "DE", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("provider.telekom", resolved!.ProviderKey);
        Assert.Equal("Deutsche Telekom", resolved.CanonicalName);
        Assert.Equal(0.98m, resolved.Confidence);
    }

    [Fact]
    public async Task Resolves_product_from_gtin_and_follows_product_redirect()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.OfficialProducts.Add(new OfficialProduct
        {
            ProductKey = "product.coke-zero",
            CanonicalName = "Coca-Cola Zero",
            Country = "DE",
            Version = 2
        });
        fixture.Db.OfficialProductGtins.Add(new OfficialProductGtin
        {
            ProductKey = "product.coke-zero-old",
            Gtin = "4006381333931"
        });
        fixture.Db.OfficialOntologyRedirects.Add(new OfficialOntologyRedirect
        {
            EntityType = "product",
            FromCanonicalKey = "product.coke-zero-old",
            ToCanonicalKey = "product.coke-zero",
            Version = 2
        });
        await fixture.Db.SaveChangesAsync();

        var resolved = await new CloudOperationalRegistryResolver(fixture.Db)
            .ResolveProductByGtinAsync("4006381333931", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("product.coke-zero", resolved!.ProductKey);
        Assert.Equal("Coca-Cola Zero", resolved.CanonicalName);
        Assert.Equal(1m, resolved.Confidence);
    }

    private sealed class Fixture(SqliteConnection connection, IntelligenceDbContext db) : IAsyncDisposable
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
