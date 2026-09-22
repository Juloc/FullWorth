using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Was beim Bestaetigen einer Haendler-zu-Kategorie-Zuordnung hinausgeht - und was nicht.
///
/// Lokal wurde immer gelernt: eine bestaetigte Zuordnung legt eine Regel an, und die naechste Buchung
/// desselben Haendlers trifft sie. Die Cloud erfuhr davon nichts. Beide Wege - den angenommenen
/// KI-Vorschlag und die von Hand angelegte Regel - schrieben ihre Rueckmeldung mit
/// <c>CloudEligible = false</c>, und die Outbox blieb leer.
///
/// Das war kein Datenschutz, sondern eine Luecke: "REWE ist Lebensmittel" gilt fuer jeden und
/// enthaelt niemanden. Was es NICHT enthaelt, ist der eigentliche Punkt dieser Tests - kein Betrag,
/// kein Datum, keine Buchung, kein Konto.
/// </summary>
public sealed class LearningFeedsTheCloudTests
{
    [Fact]
    public async Task A_confirmed_merchant_mapping_reaches_the_cloud_outbox()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var (recorder, db, space) = SetUp(scope);
        await EnableCloudAsync(db);

        await recorder.RecordMerchantMappingConfirmedAsync(
            space, Guid.NewGuid(), "REWE", "expense", "food.groceries", "Lebensmittel",
            "merchant_rule_confirmed", CancellationToken.None);

        var feedback = await db.IntelligenceFeedbackEvents.AsNoTracking().SingleAsync();
        Assert.True(feedback.CloudEligible);
        Assert.Equal("merchant", feedback.SubjectType);

        var outbox = await db.CloudSubmissionOutbox.AsNoTracking().SingleAsync();
        Assert.Equal("merchant_mapping", outbox.EventType);
    }

    /// <summary>
    /// Die Grenze. Hinaus geht der Haendlername und der Kategorieschluessel - das ist die Zuordnung.
    /// Alles, was die Buchung ausmacht, bleibt hier: der Betrag, das Datum, das Konto, die Kennung
    /// der Buchung und die des Benutzers.
    /// </summary>
    [Fact]
    public async Task What_leaves_the_instance_is_the_mapping_and_nothing_of_the_booking()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var (recorder, db, space) = SetUp(scope);
        await EnableCloudAsync(db);
        var user = Guid.NewGuid();

        await recorder.RecordMerchantMappingConfirmedAsync(
            space, user, "REWE", "expense", "food.groceries", "Lebensmittel",
            "merchant_rule_confirmed", CancellationToken.None);

        var payload = (await db.CloudSubmissionOutbox.AsNoTracking().SingleAsync()).PayloadJson;
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;

        Assert.Equal("REWE", root.GetProperty("alias").GetString());
        Assert.Equal("food.groceries", root.GetProperty("mapping").GetProperty("categoryKey").GetString());
        Assert.Equal("expense", root.GetProperty("direction").GetString());

        // Weder der Benutzer noch der Space stehen darin - und schon gar keine Buchung.
        Assert.DoesNotContain(user.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user.ToString("N"), payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(space.ToString("D"), payload, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "amount", "transaction", "account", "bookingDate", "iban" })
            Assert.DoesNotContain(forbidden, payload, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ohne Kategorieschluessel gibt es nichts zu teilen. Die Rueckmeldung wird trotzdem lokal
    /// geschrieben - sie ist fuer die eigene Auswertung da -, aber sie geht nicht hinaus.
    /// </summary>
    [Fact]
    public async Task An_incomplete_mapping_is_recorded_locally_and_stays_here()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var (recorder, db, space) = SetUp(scope);
        await EnableCloudAsync(db);

        await recorder.RecordMerchantMappingConfirmedAsync(
            space, Guid.NewGuid(), "REWE", "expense", categoryKey: null, categoryName: null,
            "merchant_rule_confirmed", CancellationToken.None);

        Assert.False((await db.IntelligenceFeedbackEvents.AsNoTracking().SingleAsync()).CloudEligible);
        Assert.Empty(await db.CloudSubmissionOutbox.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// "Und auch an die Cloud, WENN AKTIV" - der zweite Teil ist keine Nebenbemerkung. Ohne
    /// angebundene und zugestimmte Cloud lernt die Instanz weiterhin lokal und schickt nichts. Das ist
    /// der Normalfall einer selbst gehosteten Installation und darf kein Sonderfall sein.
    /// </summary>
    [Fact]
    public async Task Without_a_connected_cloud_the_instance_still_learns_and_sends_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var (recorder, db, space) = SetUp(scope);

        await recorder.RecordMerchantMappingConfirmedAsync(
            space, Guid.NewGuid(), "REWE", "expense", "food.groceries", "Lebensmittel",
            "merchant_rule_confirmed", CancellationToken.None);

        // Die Rueckmeldung ist da und als teilbar markiert - es gibt nur niemanden, dem sie gehoert.
        Assert.True((await db.IntelligenceFeedbackEvents.AsNoTracking().SingleAsync()).CloudEligible);
        Assert.Empty(await db.CloudSubmissionOutbox.AsNoTracking().ToListAsync());
    }

    /// <summary>Angebunden UND zugestimmt - beides, sonst geht nichts hinaus.</summary>
    private static async Task EnableCloudAsync(IntelligenceDbContext db)
    {
        var instanceId = Guid.NewGuid();
        db.CloudConnectionStates.Add(new CloudConnectionState
        {
            ScopeKey = CloudConnectionState.InstanceScopeKey,
            InstanceId = instanceId,
            Mode = CloudIntelligenceModes.Enabled
        });
        db.CloudIntelligenceConsents.Add(new CloudIntelligenceConsent
        {
            InstanceId = instanceId,
            AcceptedByUserId = Guid.NewGuid(),
            PolicyVersion = CloudIntelligencePolicy.CurrentVersion
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Die beiden Aufrufer. Sie ueber Quelltext zu pruefen ist hier das richtige Mass: dass sie den
    /// Recorder benutzen, ist die Entscheidung - was er dann tut, steht in den Tests oben. Waeren es
    /// zwei eigene Cloud-Wege, wuerde frueher oder spaeter einer etwas anderes hinausschicken.
    /// </summary>
    [Fact]
    public void Both_ways_of_confirming_go_through_the_one_recorder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var review = File.ReadAllText(Path.Combine(dir!.FullName, "src", "FullWorth.Backend", "Modules",
            "Intelligence", "IntelligenceSuggestionReviewService.cs"));
        var learn = File.ReadAllText(Path.Combine(dir.FullName, "src", "FullWorth.Backend", "Modules",
            "Categories", "CategoryIntelligenceService.cs"));

        Assert.Contains("RecordMerchantMappingConfirmedAsync", review);
        Assert.Contains("RecordMerchantMappingConfirmedAsync", learn);
        // Und keiner von beiden schreibt seine Rueckmeldung noch selbst mit CloudEligible = false.
        Assert.DoesNotContain("CloudEligible = false", learn);
    }

    private static (IntelligenceFeedbackRecorder Recorder, IntelligenceDbContext Db, Guid Space) SetUp(
        IServiceScope scope) =>
        (scope.ServiceProvider.GetRequiredService<IntelligenceFeedbackRecorder>(),
         scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>(),
         Guid.NewGuid());
}
