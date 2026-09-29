using System.Text;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Brands;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Die vierte, bezahlte Sprosse: die KI wird gefragt, nachdem alles Kostenlose ergebnislos war.
///
/// Zwei Formen, in dieser Reihenfolge: nennt sie einen Simple-Icons-Kurznamen, wird der beim
/// bereits gepinnten Spiegel geholt - derselbe deterministische Weg wie Sprosse 3, nur mit einem
/// Kurznamen, den nicht die Instanz sich selbst ausgerechnet, sondern die KI genannt hat. Nennt sie
/// stattdessen nur eine Domain, uebernimmt <see cref="BrandLogoFetcher"/> das Raten der Bildpfade.
///
/// Was hier gezaehlt wird, ist nicht "findet es ein Logo", sondern die Rueckschreib-Kette: die
/// geschriebene Zeile traegt <c>Source = "ai"</c>, <c>Confidence = 0,55</c>, den kuerzesten
/// eindeutigen Namensteil statt des vollen Haendlernamens, und die <c>RunId</c> des Laufs, der sie
/// vorgeschlagen hat.
/// </summary>
public sealed class BrandAiRungTests
{
    private const string Icon =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><title>Icon</title><path d=\"M0 0h24v24H0z\"/></svg>";

    /// <summary>
    /// Der bevorzugte Fall: die KI nennt einen Kurznamen, der beim Spiegel existiert. Kein
    /// Domain-Ratespiel noetig.
    /// </summary>
    [Fact]
    public async Task An_icon_slug_named_by_the_ai_is_fetched_from_the_pinned_mirror()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var provider = new FakeProvider(iconSlug: "rewe", domain: "rewe.de");
        var mirror = Mirror("rewe");
        var run = await Setup(db, provider);

        var outcome = await run.Service(mirror).ResearchAsync("REWE MARKT GMBH", null, CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeOk, outcome);

        // Die Domain wurde genannt, aber nie gebraucht - der Kurzname war der sicherere erste
        // Griff und hat schon getroffen.
        Assert.Empty(run.DomainCalls);
        Assert.Single(mirror.Urls);
        Assert.EndsWith("/rewe.svg", mirror.Urls[0]);

        var asset = await db.ResearchedBrandAssets.SingleAsync();
        Assert.Equal("rewe", asset.BrandKey);

        var alias = await db.ResearchedBrandAliases.SingleAsync();
        Assert.Equal("REWE", alias.AliasKey);
        Assert.Equal("stem", alias.AliasKind);
        Assert.Equal("rewe", alias.BrandKey);
        Assert.Equal("ai", alias.Source);
        Assert.Equal(0.55m, alias.Confidence);

        // Der Beleg: welcher Lauf das geschrieben hat.
        Assert.NotNull(alias.RunId);
        var aiRun = await db.AiRuns.SingleAsync(x => x.Id == alias.RunId);
        Assert.Equal("logo-research", aiRun.Capability);
    }

    /// <summary>
    /// Nennt die KI einen Kurznamen, den der Spiegel nicht fuehrt, faellt es auf die genannte
    /// Domain zurueck - beide Vorschlaege sind erlaubt, und der zweite ist der Sicherheitsnetz.
    /// </summary>
    [Fact]
    public async Task A_slug_the_mirror_does_not_know_falls_back_to_the_named_domain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var provider = new FakeProvider(iconSlug: "notarealbrand", domain: "example.com");
        var mirror = Mirror(/* fuehrt nichts */);
        var run = await Setup(db, provider);

        // example.com ist die einzige Domain, die in einem Unit-Test ohne Mock erreichbar ist: eine
        // von der IANA reservierte, oeffentlich aufloesbare Testdomain (siehe BrandLogoResearchTests) -
        // BrandLogoFetcher loest den Namen wirklich per DNS auf, bevor irgendetwas gemockt wird.
        var outcome = await run.Service(mirror).ResearchAsync("EXAMPLE BRAND GMBH", null, CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeOk, outcome);
        Assert.Single(mirror.Urls);
        Assert.Single(run.DomainCalls);
        Assert.Equal("example.com", run.DomainCalls[0]);

        var alias = await db.ResearchedBrandAliases.SingleAsync();
        Assert.Equal("ai", alias.Source);
        Assert.Equal(0.55m, alias.Confidence);
    }

    /// <summary>Nennt die KI weder Kurzname noch Domain, gibt es nichts zu tun - ehrlich vermerkt.</summary>
    [Fact]
    public async Task Neither_a_slug_nor_a_domain_records_no_domain()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        var provider = new FakeProvider(iconSlug: null, domain: null);
        var run = await Setup(db, provider);

        var outcome = await run.Service(Mirror()).ResearchAsync("Apotheke am Markt", null, CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeNoDomain, outcome);
        Assert.Empty(await db.ResearchedBrandAliases.ToListAsync());
    }

    /// <summary>
    /// Ein Stamm, der schon einer ANDEREN Marke gehoert, wird nicht wiederverwendet - der volle
    /// Name gewinnt dann, nicht ein zweiter Rateversuch mit einem laengeren Stamm.
    ///
    /// Der Konflikt steht als <see cref="OfficialBrandAlias"/>, nicht als recherchierter: ein
    /// mitgelieferter oder eigener Alias ist ein EXAKTER Namenseintrag ohne Wortgrenzen-Wirkung auf
    /// "schon bekannt" (das gilt nur fuer recherchierte Staemme), also erreicht "AMAZON PRIME
    /// VIDEO" hier ueberhaupt erst die Schreib-Sperre, statt schon vorher als "bereits abgedeckt"
    /// zu gelten.
    /// </summary>
    [Fact]
    public async Task A_stem_owned_by_another_brand_falls_back_to_the_full_name()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        db.OfficialBrandAliases.Add(new OfficialBrandAlias { AliasKey = "AMAZON", BrandKey = "amazon" });
        await db.SaveChangesAsync();

        var provider = new FakeProvider(iconSlug: "amazonprime", domain: null);
        var mirror = Mirror("amazonprime");
        var run = await Setup(db, provider);

        var outcome = await run.Service(mirror).ResearchAsync("AMAZON PRIME VIDEO", null, CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeOk, outcome);
        var alias = await db.ResearchedBrandAliases.SingleAsync(x => x.BrandKey == "amazonprime");
        Assert.Equal("AMAZON PRIME VIDEO", alias.AliasKey);
        Assert.Equal("exact", alias.AliasKind);
    }

    private static async Task<IntelligenceDbContext> FreshAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    /// <summary>Richtet Zugang und Modulfreigabe ein und gibt einen Baukasten fuer den Dienst zurueck.</summary>
    private static async Task<RunFixture> Setup(IntelligenceDbContext db, FakeProvider provider)
    {
        var registry = new IntelligenceProviderRegistry([provider]);
        var store = new IntelligenceStore(db, FieldCipher.Null, registry);
        var credential = await store.CreateCredentialAsync(null, "fake", "test", "test-secret-value", CancellationToken.None);

        db.AiInstanceSettings.Add(new AiInstanceSettings
        {
            Enabled = true,
            Provider = "fake",
            CredentialId = credential.Id,
            DefaultTextModel = "fake-model",
            DefaultVisionModel = "fake-model",
            // Die Spiegel-Sprosse ist hier absichtlich aus: dieser Test prueft die KI-Sprosse, und
            // ohne den Schalter aus wuerde manch ein Fall schon dort statt bei der KI entschieden.
            BrandCdnLookupEnabled = false
        });
        db.AiModuleGrants.Add(new AiModuleGrant { CredentialId = credential.Id, Module = AiModules.LogoResearch });
        await db.SaveChangesAsync();

        var domainCalls = new List<string>();
        return new RunFixture(db, store, registry, domainCalls);
    }

    private sealed record RunFixture(
        IntelligenceDbContext Db, IntelligenceStore Store, IntelligenceProviderRegistry Registry, List<string> DomainCalls)
    {
        public BrandLogoResearchService Service(HttpMessageHandler mirrorHandler) => new(
            Db,
            Store,
            new AiAccessResolver(Db, Store, Registry),
            new AiBudgetGuard(Db),
            new AiCostEstimator(new ConfigurationBuilder().Build()),
            // Zeichnet auf, welcher Wirt angefragt wurde, und antwortet mit einem gueltigen SVG -
            // das Pfaderaten selbst ist BrandLogoResearchTests' Sache, hier geht es nur darum, ob
            // die Domain als Rueckfallpfad ueberhaupt erreicht wird.
            new BrandLogoFetcher(new HttpClient(new RecordingSvgHandler(DomainCalls))),
            new SimpleIconsCdnFetcher(new HttpClient(mirrorHandler)),
            NullLogger<BrandLogoResearchService>.Instance);
    }

    private sealed class RecordingSvgHandler(List<string> calls) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            calls.Add(request.RequestUri!.Host);
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Icon))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/svg+xml");
            return Task.FromResult(response);
        }
    }

    /// <summary>Ein Spiegel, der genau die angegebenen Kurznamen fuehrt - wie in BrandCdnRungTests.</summary>
    private static RecordingHandler Mirror(params string[] knownSlugs)
    {
        var known = knownSlugs.ToHashSet(StringComparer.Ordinal);
        return new RecordingHandler(request =>
        {
            var slug = request.RequestUri!.Segments[^1].Replace(".svg", string.Empty);
            var response = new HttpResponseMessage(
                known.Contains(slug) ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(known.Contains(slug) ? Icon : "not found"))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/svg+xml");
            return response;
        });
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(reply(request));
        }
    }

    /// <summary>Antwortet immer mit demselben Kurznamen/derselben Domain - die Eingabe ist fuer diese Tests egal.</summary>
    private sealed class FakeProvider(string? iconSlug, string? domain) : IIntelligenceProvider
    {
        public IntelligenceProviderDescriptor Descriptor { get; } = new(
            "fake", IntelligenceProviderCapabilities.TextClassification, 1024, ReportsUsage: false);

        public Task<IntelligenceProviderTestResult> TestCredentialAsync(string credential, CancellationToken ct) =>
            Task.FromResult(new IntelligenceProviderTestResult(true));

        public Task<IntelligenceProviderResponse> ExecuteAsync(
            IntelligenceProviderRequest request, string credential, CancellationToken ct)
        {
            var slugJson = iconSlug is null ? "null" : $"\"{iconSlug}\"";
            var domainJson = domain is null ? "null" : $"\"{domain}\"";
            return Task.FromResult(new IntelligenceProviderResponse(
                $$"""{"iconSlug":{{slugJson}},"domain":{{domainJson}}}""", 10, 5, null));
        }
    }
}
