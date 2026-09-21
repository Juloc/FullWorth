using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// Der lokale Zwischenspeicher des Enable-Banking-Gesundheitsfeeds (#165).
///
/// Die eine Regel, an der alles haengt: ein gescheiterter Abruf laesst die gespeicherten Zeilen in
/// Ruhe. Wuerde er sie loeschen, machte ein zweiminuetiger Ausfall des Control Panels aus "alle
/// Banken erreichbar" ein "Zustand unbekannt" - und die Bankauswahl waere genauso kaputt wie mit dem
/// Live-Abruf davor, nur langsamer im Kaputtgehen.
/// </summary>
public sealed class BankingProviderStatusStoreTests
{
    private static readonly BankingProviderStatusRow[] Feed =
    [
        new("DE", "Sparkasse", "personal", "OK"),
        new("DE", "Sparkasse", "business", "DEGRADED"),
        new("AT", "Erste Bank", "personal", "OK")
    ];

    [Fact]
    public async Task AStatusThatWasNeverFetchedIsUnknownRatherThanHealthy()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            var snapshot = await store.ReadAsync(null, default);

            // Der Unterschied, der zaehlt: eine leere Liste allein waere zweideutig, und die
            // Bankauswahl wuerde nach einer frischen Installation jede Bank als gesund ausgeben.
            Assert.False(snapshot.Known);
            Assert.Null(snapshot.LastSuccessfulAt);
            Assert.Empty(snapshot.Statuses);
        });
    }

    [Fact]
    public async Task ASuccessfulRefreshStoresEveryInstitutionAndMarksTheFeedKnown()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceAsync(Feed, default);
            var snapshot = await store.ReadAsync(null, default);

            Assert.True(snapshot.Known);
            Assert.NotNull(snapshot.LastSuccessfulAt);
            Assert.Null(snapshot.LastError);
            Assert.Equal(3, snapshot.Statuses.Count);
            // Dasselbe Institut kann je PSU-Typ anders dastehen - beide Zeilen muessen ueberleben.
            Assert.Contains(snapshot.Statuses, row => row is { Brand: "Sparkasse", PsuType: "business", Status: "DEGRADED" });
        });
    }

    [Fact]
    public async Task ReadingByCountryReturnsOnlyThatCountry()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceAsync(Feed, default);

            var german = await store.ReadAsync("de", default);

            // Kleinschreibung muss durchgehen: das Land kommt aus einer Abfragezeichenkette.
            Assert.Equal(2, german.Statuses.Count);
            Assert.All(german.Statuses, row => Assert.Equal("DE", row.Country));
        });
    }

    /// <summary>
    /// Der Kern dieses Issues. Nach einem Fehlschlag steht der letzte gute Stand unveraendert da, und
    /// nur die Buchfuehrung darueber aendert sich: der Versuch ist vermerkt, der Grund auch, und
    /// <c>LastSuccessfulAt</c> sagt weiterhin, wie alt die Angaben wirklich sind.
    /// </summary>
    [Fact]
    public async Task AFailedRefreshKeepsTheLastGoodDataAndOnlyRecordsTheAttempt()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceAsync(Feed, default);
            var before = await store.ReadAsync(null, default);

            await store.RecordFailureAsync("control_panel_login_expired", default);
            var after = await store.ReadAsync(null, default);

            Assert.True(after.Known);
            Assert.Equal(before.Statuses.Count, after.Statuses.Count);
            Assert.Equal(before.LastSuccessfulAt, after.LastSuccessfulAt);
            Assert.Equal("control_panel_login_expired", after.LastError);
            Assert.NotNull(after.LastAttemptAt);
            Assert.True(after.LastAttemptAt >= before.LastAttemptAt);
        });
    }

    /// <summary>
    /// Ein erfolgreicher Abruf ERSETZT den Feed. Ein Institut, das der Anbieter nicht mehr meldet,
    /// soll verschwinden und nicht mit einem Zustand von vorletzter Woche stehen bleiben - ein
    /// Zusammenfuehren waere genau das.
    /// </summary>
    [Fact]
    public async Task ARefreshDropsInstitutionsTheProviderNoLongerReports()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceAsync(Feed, default);
            await store.ReplaceAsync([new("DE", "Sparkasse", "personal", "OUTAGE")], default);

            var snapshot = await store.ReadAsync(null, default);

            Assert.Single(snapshot.Statuses);
            Assert.Equal("OUTAGE", snapshot.Statuses[0].Status);
        });
    }

    /// <summary>
    /// Ein erfolgreicher Abruf nach einem Fehlschlag raeumt den Fehler weg. Bliebe er stehen, wuerde
    /// die Oberflaeche frische Angaben dauerhaft als veraltet kennzeichnen.
    /// </summary>
    [Fact]
    public async Task ASuccessfulRefreshClearsAPreviousError()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.RecordFailureAsync("provider_status_unavailable", default);
            await store.ReplaceAsync(Feed, default);

            var snapshot = await store.ReadAsync(null, default);

            Assert.Null(snapshot.LastError);
            Assert.True(snapshot.Known);
        });
    }

    /// <summary>
    /// Der Feed darf dasselbe Institut zweimal nennen; der eindeutige Index vertraegt das nicht. Ohne
    /// die Entdopplung stuerbe der ganze Durchlauf an einer Zeile, die der Anbieter doppelt geschickt
    /// hat - und der Zwischenspeicher bliebe fuer immer leer.
    /// </summary>
    [Fact]
    public async Task ADuplicateRowInTheFeedDoesNotBreakTheRefresh()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceAsync(
                [new("DE", "Sparkasse", "personal", "OK"), new("DE", "Sparkasse", "personal", "OUTAGE")],
                default);

            var snapshot = await store.ReadAsync(null, default);

            Assert.Single(snapshot.Statuses);
            // Die letzte Nennung gewinnt - eine Regel, die es braucht, damit das Ergebnis nicht von
            // der Aufzaehlungsreihenfolge abhaengt.
            Assert.Equal("OUTAGE", snapshot.Statuses[0].Status);
        });
    }

    private static async Task WithStoreAsync(
        BackendWebApplicationFactory factory, Func<BankingProviderStatusStore, Task> body)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await body(scope.ServiceProvider.GetRequiredService<BankingProviderStatusStore>());
    }
}
