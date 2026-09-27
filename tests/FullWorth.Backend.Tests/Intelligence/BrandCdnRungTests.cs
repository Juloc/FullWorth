using System.Net;
using System.Text;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Brands;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Die dritte Sprosse: der selbst ausgerechnete Kurzname wird beim Icon-Spiegel nachgeschlagen.
///
/// Sie ist die einzige, die etwas hinausgibt, ohne dass ein Mensch eine Marke genannt hat -
/// deshalb steht hier nicht nur "findet sie ein Logo", sondern auch **welche Adresse genau**
/// gerufen wird, **wann gar nicht** gerufen wird, und dass ein Spiegel, der abwinkt, den Lauf
/// beendet statt vierundzwanzig weitere Abrufe auszuloesen.
/// </summary>
public sealed class BrandCdnRungTests
{
    private const string Icon =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><title>Rewe</title><path d=\"M0 0h24v24H0z\"/></svg>";

    /// <summary>
    /// Der Fall, fuer den es die Sprosse gibt: eine Marke, die der mitgelieferte Katalog nicht
    /// fuehrt, deren Kurzname sich aber ausrechnen laesst.
    /// </summary>
    [Fact]
    public async Task A_brand_the_image_does_not_carry_is_fetched_once_and_then_owned()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var handler = Mirror("rewe");
        var service = Service(db, handler);

        Assert.Equal(
            BrandLogoResearchService.OutcomeCdn,
            await service.ResearchAsync("REWE MARKT GMBH", null, CancellationToken.None));

        // Zwei Abrufe, nicht drei: "markt" und "gmbh" sind beide Rauschen am Ende, also gibt es
        // zwischen dem ganzen Namen und der Marke keinen dritten Kandidaten. Die Zahl steht hier,
        // weil sie das Mass ist - jeder unbekannte Haendler kostet genau so viele fremde Abrufe.
        Assert.Equal(2, handler.Urls.Count);
        Assert.EndsWith("/rewemarktgmbh.svg", handler.Urls[0]);
        Assert.EndsWith("/rewe.svg", handler.Urls[^1]);

        // Genau die gepinnte Fassung, genau ein Pfadabschnitt variabel - und der ist der Kurzname,
        // nicht der Haendlername.
        Assert.StartsWith(
            $"https://cdn.jsdelivr.net/npm/simple-icons@{BundledBrandCatalog.SimpleIconsVersion}/icons/",
            handler.Urls[^1]);

        // Das Bild gehoert jetzt der Instanz: Marke unter dem Kurznamen, Schreibweise mit Herkunft.
        var asset = await db.ResearchedBrandAssets.SingleAsync();
        Assert.Equal("rewe", asset.BrandKey);
        var alias = await db.ResearchedBrandAliases.SingleAsync();
        Assert.Equal("REWE MARKT GMBH", alias.AliasKey);
        Assert.Equal("rewe", alias.BrandKey);
        Assert.Equal("cdn", alias.Source);
        Assert.Equal(0.80m, alias.Confidence);

        // Und der zweite Durchlauf fragt niemanden mehr.
        var before = handler.Urls.Count;
        Assert.Equal(
            BrandLogoResearchService.OutcomeAlreadyKnown,
            await service.ResearchAsync("REWE MARKT GMBH", null, CancellationToken.None));
        Assert.Equal(before, handler.Urls.Count);
    }

    /// <summary>
    /// Die naechste Filiale derselben Kette leitet denselben Kurznamen ab - und findet das Bild
    /// schon da. Das ist der Grund, warum die Marke unter dem Kurznamen abgelegt wird und nicht
    /// unter dem Haendlernamen: sonst holte jede Filiale dasselbe Icon noch einmal.
    /// </summary>
    [Fact]
    public async Task A_second_branch_of_the_same_chain_costs_no_further_call()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var handler = Mirror("rewe");
        var service = Service(db, handler);

        await service.ResearchAsync("REWE MARKT GMBH", null, CancellationToken.None);

        Assert.Equal(
            BrandLogoResearchService.OutcomeCdn,
            await service.ResearchAsync("REWE CITY HAMBURG", null, CancellationToken.None));

        // Weitere Abrufe, weil der Haendlername ein anderer ist - aber nur EIN Bild und EINE
        // Marke, weil beide auf denselben Kurznamen zeigen.
        Assert.Single(await db.ResearchedBrandAssets.ToListAsync());
        Assert.Equal(2, await db.ResearchedBrandAliases.CountAsync());
    }

    /// <summary>
    /// Was der mitgelieferte Katalog schon kann, wird nicht nachgeschlagen. Das ist die ganze
    /// Reihenfolge der Leiter in einem Test.
    /// </summary>
    [Fact]
    public async Task What_the_image_already_carries_is_never_looked_up()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection, "vodafone");
        var handler = Mirror();

        Assert.Equal(
            BrandLogoResearchService.OutcomeDerived,
            await Service(db, handler).ResearchAsync("Vodafone West GmbH", null, CancellationToken.None));
        Assert.Empty(handler.Urls);
    }

    /// <summary>Abgeschaltet heisst abgeschaltet: kein Abruf, kein Vermerk.</summary>
    [Fact]
    public async Task The_switch_actually_stops_the_call()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var settings = await db.AiInstanceSettings.SingleAsync();
        settings.BrandCdnLookupEnabled = false;
        await db.SaveChangesAsync();

        var handler = Mirror("rewe");

        Assert.Equal(
            BrandLogoResearchService.OutcomeNoAccess,
            await Service(db, handler).ResearchAsync("REWE MARKT GMBH", null, CancellationToken.None));
        Assert.Empty(handler.Urls);
        Assert.Empty(await db.BrandLogoResearchAttempts.ToListAsync());
    }

    /// <summary>
    /// Der Spiegel winkt ab. Das sagt nichts ueber diesen Haendler - also wird nichts vermerkt -
    /// und der Aufrufer bekommt ein Ergebnis, an dem er den Lauf beendet.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task A_mirror_that_declines_stops_the_run_without_a_note(HttpStatusCode status)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var handler = new RecordingHandler(_ => Reply(status, "nope"));

        Assert.Equal(
            BrandLogoResearchService.OutcomeThrottled,
            await Service(db, handler).ResearchAsync("REWE MARKT GMBH", null, CancellationToken.None));

        // Genau ein Versuch - nicht drei Kurznamen hintereinander bei einem Dienst, der gerade
        // ausdruecklich nein sagt.
        Assert.Single(handler.Urls);
        Assert.Empty(await db.BrandLogoResearchAttempts.ToListAsync());
    }

    /// <summary>
    /// "Kennt der Spiegel nicht" ist eine Antwort ueber den Haendler und wird vermerkt - aber
    /// getrennt von der KI-Sprosse. Ohne die Spalte Rung haette dieser Vermerk den einen
    /// KI-Versuch dreissig Tage lang mitverbraucht.
    /// </summary>
    [Fact]
    public async Task A_miss_is_remembered_for_the_mirror_alone()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var handler = Mirror();
        var service = Service(db, handler);

        // Ohne KI-Zugang endet der Aufruf in "kein Zugang" - die KI-Sprosse wurde also erreicht.
        Assert.Equal(
            BrandLogoResearchService.OutcomeNoAccess,
            await service.ResearchAsync("Apotheke am Markt", null, CancellationToken.None));

        var attempt = await db.BrandLogoResearchAttempts.SingleAsync();
        Assert.Equal(BrandLogoResearchAttempt.RungCdn, attempt.Rung);
        Assert.Equal(BrandLogoResearchService.OutcomeCdnMiss, attempt.Outcome);

        // Beim zweiten Mal fragt niemand den Spiegel noch einmal.
        var before = handler.Urls.Count;
        await service.ResearchAsync("Apotheke am Markt", null, CancellationToken.None);
        Assert.Equal(before, handler.Urls.Count);
    }

    /// <summary>
    /// Der Spiegel wird nicht geglaubt, weil er der Spiegel ist. Kommt etwas, das FullWorth nicht
    /// ausliefern wuerde, wird es verworfen - und zwar bevor es in der Ablage steht.
    /// </summary>
    [Fact]
    public async Task Something_that_is_not_a_safe_svg_is_refused()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var handler = new RecordingHandler(_ => Reply(
            HttpStatusCode.OK,
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>fetch('//x')</script></svg>"));

        await Service(db, handler).ResearchAsync("REWE MARKT GMBH", null, CancellationToken.None);

        Assert.Empty(await db.ResearchedBrandAssets.ToListAsync());
        Assert.Empty(await db.BrandAssetBlobs.ToListAsync());
        var attempt = await db.BrandLogoResearchAttempts.SingleAsync();
        Assert.Equal(BrandLogoResearchService.OutcomeUnsafeAsset, attempt.Outcome);
    }

    /// <summary>
    /// Ein Kurzname, der die Form nicht erfuellt, verlaesst die Maschine nicht. Das ist keine
    /// Sauberkeitsregel, sondern die Grenze dessen, was ueberhaupt hinausgeht.
    /// </summary>
    [Theory]
    [InlineData("rewe", true)]
    [InlineData("1and1", true)]
    [InlineData("a", false)]
    [InlineData("mueller fliesen", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("REWE", false)]
    public void Only_a_well_formed_slug_becomes_a_url(string slug, bool allowed) =>
        Assert.Equal(allowed, SimpleIconsCdnFetcher.UrlFor(slug) is not null);

    private static async Task<IntelligenceDbContext> FreshAsync(
        SqliteConnection connection, params string[] bundledBrandKeys)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AiInstanceSettings.Add(new AiInstanceSettings());

        foreach (var key in bundledBrandKeys)
        {
            var svg = Encoding.UTF8.GetBytes(
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 4 4\"><title>{key}</title></svg>");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(svg)).ToLowerInvariant();
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
    /// Ein Spiegel, der genau die angegebenen Kurznamen fuehrt und auf alles andere mit 404
    /// antwortet - so wie der echte. Ein Fake, der auf JEDE Adresse ein Bild zurueckgibt, wuerde
    /// die Reihenfolge der Kandidaten unpruefbar machen: der erste Rateversuch gewaenne immer.
    /// </summary>
    private static RecordingHandler Mirror(params string[] knownSlugs)
    {
        var known = knownSlugs.ToHashSet(StringComparer.Ordinal);
        return new RecordingHandler(request =>
        {
            var slug = request.RequestUri!.Segments[^1].Replace(".svg", string.Empty);
            return known.Contains(slug)
                ? Reply(HttpStatusCode.OK, Icon)
                : Reply(HttpStatusCode.NotFound, "not found");
        });
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/svg+xml");
        return response;
    }

    private static BrandLogoResearchService Service(IntelligenceDbContext db, HttpMessageHandler mirror)
    {
        var registry = new IntelligenceProviderRegistry([]);
        var store = new IntelligenceStore(db, FullWorth.Backend.Security.FieldCipher.Null, registry);
        return new BrandLogoResearchService(
            db,
            store,
            new AiAccessResolver(db, store, registry),
            new AiBudgetGuard(db),
            new AiCostEstimator(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()),
            // Die KI-Sprosse wird in diesen Tests nie erreicht: es gibt keinen Zugang.
            new BrandLogoFetcher(new HttpClient(new OfflineHandler())),
            new SimpleIconsCdnFetcher(new HttpClient(mirror)),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BrandLogoResearchService>.Instance);
    }

    /// <summary>Schreibt jede gerufene Adresse mit - die Adresse IST hier die Nutzlast.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(reply(request));
        }
    }
}
