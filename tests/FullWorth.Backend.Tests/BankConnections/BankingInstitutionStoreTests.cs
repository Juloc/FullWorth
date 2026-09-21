using System.Text.Json;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// Der lokale Institutionenkatalog (#169).
///
/// Zwei Regeln tragen ihn, und beide sind der Grund, warum es ihn gibt: ein gescheiterter Abruf
/// laesst den Katalog stehen, und ein Institut, das der Anbieter nicht mehr meldet, wird stillgelegt
/// statt geloescht. Faellt eine davon, ist die Bankauswahl wieder von der Erreichbarkeit eines
/// Fremdsystems abhaengig - nur mit einem Umweg dazwischen.
/// </summary>
public sealed class BankingInstitutionStoreTests
{
    private static JsonElement Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

    private static BankingInstitutionRow Bank(
        string name, string psuTypes = """["personal"]""", string? logo = null, bool beta = false) =>
        new("DE", name, Json(psuTypes), Json("""{"name":"Gruppe"}"""), logo, beta,
            Json("""[{"name":"redirect","credentials":[{"name":"username"}]}]"""));

    [Fact]
    public async Task ACountryThatWasNeverFetchedIsUnknownRatherThanEmpty()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            var catalog = await store.ReadAsync("DE", default);

            // Der Unterschied, der zaehlt: "wir haben nie nachgesehen" darf nicht aussehen wie "der
            // Anbieter kennt hier keine Bank" - sonst zeigt der Dialog beim ersten Mal ein leeres
            // Verzeichnis, statt zu sagen, dass er noch nichts hat.
            Assert.False(catalog.Known);
            Assert.Empty(catalog.Institutions);
        });
    }

    [Fact]
    public async Task ASuccessfulRefreshStoresTheCatalogWithTheProvidersOwnFields()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceCountryAsync("DE",
                [Bank("Sparkasse", logo: "https://example.test/logo.png", beta: true)], default);

            var catalog = await store.ReadAsync("de", default);

            Assert.True(catalog.Known);
            var row = Assert.Single(catalog.Institutions);
            Assert.Equal("Sparkasse", row.Name);
            Assert.Equal("https://example.test/logo.png", row.Logo);
            Assert.True(row.Beta);
            // Die Protokollangaben des Anbieters muessen unveraendert zurueckkommen: die Oberflaeche
            // baut daraus die Anmeldefelder, und ein verlorenes Feld heisst eine Verbindung, die sich
            // nicht mehr herstellen laesst.
            Assert.Equal(JsonValueKind.Array, row.AuthMethods!.Value.ValueKind);
            Assert.Contains("credentials", row.AuthMethods.Value.GetRawText());
            Assert.Equal(JsonValueKind.Object, row.Group!.Value.ValueKind);
        });
    }

    /// <summary>
    /// Dieselbe Bank kommt beim Anbieter als getrennter Privat- und Geschaeftseintrag. Beide muessen
    /// nebeneinander stehen koennen - die Oberflaeche fuehrt sie selbst zusammen.
    /// </summary>
    [Fact]
    public async Task TheSameBankCanExistOncePerPsuVariant()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceCountryAsync("DE",
                [Bank("Sparkasse"), Bank("Sparkasse", """["business"]""")], default);

            var catalog = await store.ReadAsync("DE", default);

            Assert.Equal(2, catalog.Institutions.Count);
        });
    }

    /// <summary>
    /// Der Schluessel muss aus derselben Antwort immer derselbe sein. Kaeme die PSU-Liste einmal in
    /// der einen und einmal in der anderen Reihenfolge, legte der zweite Durchlauf dieselbe Bank ein
    /// zweites Mal an - und die Auswahl zeigte sie doppelt.
    /// </summary>
    [Fact]
    public async Task ADifferentOrderOfPsuTypesIsStillTheSameEntry()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceCountryAsync("DE", [Bank("Sparkasse", """["personal","business"]""")], default);
            await store.ReplaceCountryAsync("DE", [Bank("Sparkasse", """["business","personal"]""")], default);

            var catalog = await store.ReadAsync("DE", default);

            Assert.Single(catalog.Institutions);
        });
    }

    /// <summary>
    /// Der Kern dieses Issues. Nach einem Fehlschlag steht der Katalog unveraendert da - andernfalls
    /// waere die Bankauswahl bei jedem Anbieterausfall leer, also genau so kaputt wie mit dem
    /// Live-Abruf davor.
    /// </summary>
    [Fact]
    public async Task AFailedRefreshKeepsTheCatalogAndOnlyRecordsTheAttempt()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceCountryAsync("DE", [Bank("Sparkasse"), Bank("ING")], default);
            var before = await store.ReadAsync("DE", default);

            await store.RecordFailureAsync("DE", "provider_request_failed", default);
            var after = await store.ReadAsync("DE", default);

            Assert.True(after.Known);
            Assert.Equal(before.Institutions.Count, after.Institutions.Count);
            Assert.Equal(before.LastSuccessfulAt, after.LastSuccessfulAt);
            Assert.Equal("provider_request_failed", after.LastError);
            Assert.NotNull(after.LastAttemptAt);
        });
    }

    /// <summary>
    /// Ein Institut, das der Anbieter nicht mehr meldet, verschwindet aus der Auswahl - aber nicht
    /// aus der Datenbank. Eine bestehende Verbindung zeigt weiterhin auf diesen Namen, und ein
    /// Anbieter, der eine Bank fuer einen Durchlauf vergisst, soll sie nicht tilgen.
    /// </summary>
    [Fact]
    public async Task AnInstitutionTheProviderStopsReportingIsDeactivatedNotDeleted()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceCountryAsync("DE", [Bank("Sparkasse"), Bank("ING")], default);
            await store.ReplaceCountryAsync("DE", [Bank("Sparkasse")], default);

            var catalog = await store.ReadAsync("DE", default);

            Assert.Single(catalog.Institutions);
            Assert.Equal("Sparkasse", catalog.Institutions[0].Name);
        });
    }

    /// <summary>Meldet der Anbieter sie wieder, wird dieselbe Zeile wieder aktiv - keine zweite.</summary>
    [Fact]
    public async Task AReturningInstitutionBecomesActiveAgainWithoutADuplicate()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceCountryAsync("DE", [Bank("ING")], default);
            await store.ReplaceCountryAsync("DE", [], default);
            await store.ReplaceCountryAsync("DE", [Bank("ING")], default);

            var catalog = await store.ReadAsync("DE", default);

            Assert.Single(catalog.Institutions);
        });
    }

    /// <summary>Ein Land beruehrt das andere nicht - sonst leerte ein Durchlauf fuer DE den AT-Katalog.</summary>
    [Fact]
    public async Task RefreshingOneCountryLeavesAnotherAlone()
    {
        using var factory = new BackendWebApplicationFactory();

        await WithStoreAsync(factory, async store =>
        {
            await store.ReplaceCountryAsync("AT", [new("AT", "Erste Bank", Json("""["personal"]"""), null, null, false, null)], default);
            await store.ReplaceCountryAsync("DE", [Bank("Sparkasse")], default);

            Assert.Single((await store.ReadAsync("AT", default)).Institutions);
            Assert.Single((await store.ReadAsync("DE", default)).Institutions);
        });
    }

    private static async Task WithStoreAsync(
        BackendWebApplicationFactory factory, Func<BankingInstitutionStore, Task> body)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await body(scope.ServiceProvider.GetRequiredService<BankingInstitutionStore>());
    }
}
