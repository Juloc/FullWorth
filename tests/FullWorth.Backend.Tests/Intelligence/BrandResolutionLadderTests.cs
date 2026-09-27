using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Brands;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Die Leiter: mitgelieferter Katalog, dann Ableitung, dann - eine Ebene hoeher - bezahlte Wege.
///
/// Was hier geprueft wird, ist nicht "findet es ein Logo", sondern **was es dafuer ausgibt**. Ein
/// Haendlername, der sich ausrechnen laesst, darf keinen einzigen Aufruf nach draussen ausloesen.
/// Der Dienst bekommt in diesen Tests weder Zugang noch Anbieter noch Netz: was die Ableitung
/// schafft, schafft sie ohne alles davon, und wo sie nichts schafft, faellt der Aufruf in "kein
/// Zugang".
///
/// Die Spiegel-Sprosse ist hier **abgeschaltet** - sie hat ihre eigene Datei
/// (<see cref="BrandCdnRungTests"/>). Ohne das wuerde jeder Fall, den die Ableitung nicht loest,
/// hier an einem Abruf scheitern statt an dem, was er pruefen will.
/// </summary>
public sealed class BrandResolutionLadderTests
{
    private static async Task<IntelligenceDbContext> BundledAsync(SqliteConnection connection, params string[] brandKeys)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.AiInstanceSettings.Add(new AiInstanceSettings { BrandCdnLookupEnabled = false });

        foreach (var key in brandKeys)
        {
            var svg = Encoding.UTF8.GetBytes(
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 4\"><title>{key}</title></svg>");
            var hash = Convert.ToHexString(SHA256.HashData(svg)).ToLowerInvariant();
            db.BrandAssetBlobs.Add(new BrandAssetBlob
            {
                ContentSha256 = hash, MediaType = "image/svg+xml", ByteLength = svg.Length, Content = svg
            });
            db.OfficialBrandAssets.Add(new OfficialBrandAsset
            {
                BrandKey = key, CanonicalName = key, LogoKey = key,
                ContentSha256 = hash, ByteLength = svg.Length
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    /// <summary>
    /// Der Fall, um den es geht. Auf der Buchung steht die Gesellschaft mit Region und Rechtsform,
    /// im Katalog steht die Marke - und dazwischen liegt nur Rechnen.
    /// </summary>
    [Fact]
    public async Task A_derivable_merchant_costs_no_call_at_all()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await BundledAsync(connection, "vodafone");

        var outcome = await Service(db).ResearchAsync("Vodafone West GmbH", null, CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeDerived, outcome);

        var alias = await db.ResearchedBrandAliases.SingleAsync();
        Assert.Equal("VODAFONE WEST GMBH", alias.AliasKey);
        Assert.Equal("vodafone", alias.BrandKey);
        Assert.Equal("derivation", alias.Source);
        Assert.Equal(0.90m, alias.Confidence);

        // Kein Bild geschrieben: das Logo liegt schon da, die Zeile zeigt nur darauf.
        Assert.Empty(await db.ResearchedBrandAssets.ToListAsync());
        // Und kein Vermerk "schon versucht" - es wurde nichts versucht, es wurde gerechnet.
        Assert.Empty(await db.BrandLogoResearchAttempts.ToListAsync());
    }

    /// <summary>
    /// Enthaelt der Name eine bekannte Marke an einer Wortgrenze, reicht das - solange es genau
    /// eine ist.
    /// </summary>
    [Fact]
    public async Task A_contained_brand_name_is_enough()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await BundledAsync(connection, "edeka");

        Assert.Equal(
            BrandLogoResearchService.OutcomeDerived,
            await Service(db).ResearchAsync("EDEKA MARKT 4711 BERLIN", null, CancellationToken.None));
        Assert.Equal("edeka", (await db.ResearchedBrandAliases.SingleAsync()).BrandKey);
    }

    /// <summary>
    /// Zwei Marken im selben Namen geben keine Antwort. Ein geratenes Logo ist schlechter als
    /// keines: es sieht richtig aus, und niemand findet spaeter, woher es kam.
    /// </summary>
    [Fact]
    public async Task Two_matching_brands_yield_nothing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await BundledAsync(connection, "amazon", "paypal");

        // Ohne KI-Zugang endet der Aufruf danach in "kein Zugang" - entscheidend ist, dass die
        // Ableitung NICHTS geschrieben hat.
        var outcome = await Service(db).ResearchAsync("AMAZON PAYPAL ZAHLUNG", null, CancellationToken.None);

        Assert.NotEqual(BrandLogoResearchService.OutcomeDerived, outcome);
        Assert.Empty(await db.ResearchedBrandAliases.ToListAsync());
    }

    /// <summary>Ein Name, der zu keiner mitgelieferten Marke passt, faellt weiter nach unten.</summary>
    [Fact]
    public async Task An_unknown_merchant_falls_through_to_the_paid_rungs()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await BundledAsync(connection, "vodafone");

        var outcome = await Service(db).ResearchAsync("Apotheke am Markt", null, CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeNoAccess, outcome);
        Assert.Empty(await db.ResearchedBrandAliases.ToListAsync());
    }

    /// <summary>
    /// Zweimal derselbe Haendler: beim zweiten Mal greift die oberste Sprosse, und auch die
    /// Ableitung laeuft nicht mehr.
    /// </summary>
    [Fact]
    public async Task The_second_occurrence_is_already_covered()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await BundledAsync(connection, "vodafone");
        var service = Service(db);

        Assert.Equal(
            BrandLogoResearchService.OutcomeDerived,
            await service.ResearchAsync("Vodafone West GmbH", null, CancellationToken.None));
        Assert.Equal(
            BrandLogoResearchService.OutcomeAlreadyKnown,
            await service.ResearchAsync("Vodafone West GmbH", null, CancellationToken.None));

        Assert.Single(await db.ResearchedBrandAliases.ToListAsync());
    }

    /// <summary>
    /// Ohne mitgelieferten Katalog kann die Ableitung nichts treffen - und darf trotzdem nicht
    /// scheitern. Eine frische Instanz, deren Installateur noch nicht gelaufen ist, faellt einfach
    /// auf die naechste Sprosse.
    /// </summary>
    [Fact]
    public async Task An_empty_catalogue_is_not_an_error()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await BundledAsync(connection);

        Assert.Equal(
            BrandLogoResearchService.OutcomeNoAccess,
            await Service(db).ResearchAsync("Vodafone West GmbH", null, CancellationToken.None));
    }

    private static BrandLogoResearchService Service(IntelligenceDbContext db)
    {
        var registry = new IntelligenceProviderRegistry([]);
        var store = new IntelligenceStore(db, FullWorth.Backend.Security.FieldCipher.Null, registry);
        return new BrandLogoResearchService(
            db,
            store,
            new AiAccessResolver(db, store, registry),
            new AiBudgetGuard(db),
            new AiCostEstimator(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            new BrandLogoFetcher(new HttpClient(new OfflineHandler())),
            new SimpleIconsCdnFetcher(new HttpClient(new OfflineHandler())),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrandLogoResearchService>.Instance);
    }
}
