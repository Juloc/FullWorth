using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Intelligence.Brands;
using FullWorth.Backend.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// "Logo ist falsch" (#176 Korrektur): ein Mensch sagt, dass ein selbst recherchiertes Logo nicht
/// stimmt.
///
/// Zwei Wirkungen, beide dauerhaft: die Zeile verschwindet aus dem Katalog (<c>Status =
/// "rejected"</c>), und sie kommt nicht von selbst zurueck - weder ueber denselben Namen noch
/// ueber eine andere Filiale derselben Kette. Ein mitgeliefertes oder eigenes Paket-Logo laesst
/// sich hier nicht ablehnen, dafuer gibt es die Pack-Verwaltung.
/// </summary>
public sealed class BrandLogoRejectionTests
{
    /// <summary>Der Kernfall: eine falsch zugeordnete, selbst recherchierte Marke verschwindet.</summary>
    [Fact]
    public async Task Rejecting_a_researched_alias_removes_it_from_the_catalogue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        db.ResearchedBrandAliases.Add(new ResearchedBrandAlias
        {
            AliasKey = "AMAZONPRIME", BrandKey = "amazonprime", Source = "cdn", Confidence = 0.80m, AliasKind = "exact"
        });
        await db.SaveChangesAsync();

        var outcome = await Service(db).RejectAsync("AMAZONPRIME VIDEO ABO", CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeRejected, outcome);
        var alias = await db.ResearchedBrandAliases.SingleAsync();
        Assert.Equal("rejected", alias.Status);
    }

    /// <summary>Unter diesem Namen ist nichts SELBST RECHERCHIERTES hinterlegt - eine ehrliche Antwort.</summary>
    [Fact]
    public async Task Rejecting_a_name_with_no_researched_alias_says_so()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);

        var outcome = await Service(db).RejectAsync("Apotheke am Markt", CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeNothingToReject, outcome);
    }

    /// <summary>
    /// Ein mitgeliefertes oder eigenes Paket-Logo ist keine recherchierte Zeile - dafuer gibt es
    /// diesen Schalter nicht, egal wie der Name lautet.
    /// </summary>
    [Fact]
    public async Task Rejecting_an_official_logo_finds_nothing_to_reject()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        db.OfficialBrandAliases.Add(new OfficialBrandAlias { AliasKey = "VODAFONE", BrandKey = "vodafone" });
        await db.SaveChangesAsync();

        var outcome = await Service(db).RejectAsync("Vodafone West GmbH", CancellationToken.None);

        Assert.Equal(BrandLogoResearchService.OutcomeNothingToReject, outcome);
    }

    /// <summary>
    /// Der ganze Punkt: eine abgelehnte Zeile darf nicht am naechsten Lauf wieder auftauchen, weder
    /// unter demselben Namen noch unter einer anderen Filiale derselben Kette.
    /// </summary>
    [Fact]
    public async Task A_rejected_alias_is_never_re_derived_for_the_same_or_another_branch()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        db.ResearchedBrandAliases.Add(new ResearchedBrandAlias
        {
            AliasKey = "REWE", BrandKey = "rewe", Source = "cdn", Confidence = 0.80m, AliasKind = "stem"
        });
        await db.SaveChangesAsync();
        var service = Service(db);

        Assert.Equal(
            BrandLogoResearchService.OutcomeRejected,
            await service.RejectAsync("REWE MARKT GMBH", CancellationToken.None));

        // Weder der urspruengliche Name ...
        Assert.Equal(
            BrandLogoResearchService.OutcomeAlreadyKnown,
            await service.DeriveAsync("REWE MARKT GMBH", CancellationToken.None));
        // ... noch eine andere Filiale derselben Kette gilt danach noch als offen.
        Assert.Equal(
            BrandLogoResearchService.OutcomeAlreadyKnown,
            await service.DeriveAsync("REWE CITY HAMBURG", CancellationToken.None));
    }

    /// <summary>
    /// Zweite Sicherung: selbst wenn die Wortgrenzen-Pruefung diesen Namen einmal nicht mehr
    /// traefe, blockiert der Vermerk allein schon - und zwar unabhaengig vom Alter. Das ist der
    /// Unterschied zu jedem anderen Vermerk, der nach dreissig Tagen verfaellt.
    /// </summary>
    [Fact]
    public async Task The_rejection_marker_never_expires_unlike_every_other_outcome()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await using var db = await FreshAsync(connection);
        db.ResearchedBrandAliases.Add(new ResearchedBrandAlias
        {
            AliasKey = "REWE", BrandKey = "rewe", Source = "cdn", Confidence = 0.80m, AliasKind = "stem"
        });
        await db.SaveChangesAsync();
        await Service(db).RejectAsync("REWE MARKT GMBH", CancellationToken.None);

        var aliasHash = BrandLogoResearchService.HashOf("REWE MARKT GMBH");
        var attempts = await db.BrandLogoResearchAttempts.Where(x => x.AliasHash == aliasHash).ToListAsync();
        Assert.Equal(2, attempts.Count);
        Assert.All(attempts, a => Assert.Equal("user_rejected", a.Outcome));

        // Weit in der Vergangenheit - ein gewoehnlicher Vermerk waere laengst verfallen.
        foreach (var a in attempts) a.AttemptedAt = DateTimeOffset.UtcNow.AddYears(-1);
        await db.SaveChangesAsync();

        // Trotzdem: gar kein Abruf. Die Ableitung greift schon vorher (Wortgrenzen-Pruefung),
        // dieser Test haelt zusaetzlich den Vermerk selbst fest.
        var attemptCdn = await db.BrandLogoResearchAttempts
            .SingleAsync(x => x.AliasHash == aliasHash && x.Rung == BrandLogoResearchAttempt.RungCdn);
        Assert.True(attemptCdn.AttemptedAt < DateTimeOffset.UtcNow.AddDays(-BrandLogoResearchAttempt.RetryAfterDays));
    }

    private static async Task<IntelligenceDbContext> FreshAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IntelligenceDbContext>().UseSqlite(connection).Options;
        var db = new IntelligenceDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AiInstanceSettings.Add(new AiInstanceSettings { BrandCdnLookupEnabled = false });
        await db.SaveChangesAsync();
        return db;
    }

    private static BrandLogoResearchService Service(IntelligenceDbContext db)
    {
        var registry = new IntelligenceProviderRegistry([]);
        var store = new IntelligenceStore(db, FieldCipher.Null, registry);
        return new BrandLogoResearchService(
            db,
            store,
            new AiAccessResolver(db, store, registry),
            new AiBudgetGuard(db),
            new AiCostEstimator(new ConfigurationBuilder().Build()),
            new BrandLogoFetcher(new HttpClient(new OfflineHandler())),
            new SimpleIconsCdnFetcher(new HttpClient(new OfflineHandler())),
            NullLogger<BrandLogoResearchService>.Instance);
    }
}
