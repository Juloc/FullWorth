using FullWorth.Banking.Services;

namespace FullWorth.Banking.Tests;

/// <summary>
/// Gemeldet: "ich habe alle vier gesehen - Giro, drei Pockets und mein Depot - und danach stand da
/// '4 Konten importiert'. Wo ist mein Depot?"
///
/// Beides stimmte und war zusammen die falsche Auskunft. Vier Konten waren wirklich entstanden. Das
/// Depot nicht: der Abruf war davor abgebrochen, <c>SyncConnectionAsync</c> hatte den Grund an die
/// Verbindung geschrieben statt zu werfen - und die Antwort der Uebernahme las ihn nie. Sie zaehlte,
/// was da war, und schwieg ueber das, was fehlte.
///
/// Deshalb zaehlt <see cref="IngFinTsService.Outcome"/> nicht nur, sondern vergleicht: alles, was die
/// Bank gemeldet hat, muss eine Konto-Id haben. Was keine hat, fehlt.
/// </summary>
public sealed class FinTsImportOutcomeTests
{
    [Fact]
    public void A_depot_that_never_came_into_being_is_reported_as_missing()
    {
        var outcome = IngFinTsService.Outcome(
            [Cash("giro"), Cash("pocket-1"), Cash("pocket-2"), Cash("pocket-3"), Depot("depot", exists: false)],
            lastError: "FINTS_BANK_ERROR");

        Assert.Equal(4, outcome.Accounts);
        Assert.Equal(0, outcome.Depots);
        // Das ist der Satz, der gefehlt hat.
        Assert.Equal(1, outcome.Missing);
        Assert.Equal("FINTS_BANK_ERROR", outcome.Error);
    }

    [Fact]
    public void A_complete_run_reports_nothing_missing_and_no_error()
    {
        var outcome = IngFinTsService.Outcome(
            [Cash("giro"), Cash("pocket-1"), Depot("depot")],
            lastError: null);

        Assert.Equal(2, outcome.Accounts);
        Assert.Equal(1, outcome.Depots);
        Assert.Equal(0, outcome.Missing);
        Assert.Null(outcome.Error);
        Assert.Equal(0, outcome.Hidden);
    }

    // Ausgeblendet heisst angelegt: das Konto entsteht, es wird nur nicht angezeigt. Es darf niemals
    // als fehlend gelten - sonst meldete jede Auswahl, die etwas abwaehlt, einen Fehlschlag.
    [Fact]
    public void A_hidden_account_exists_and_is_never_counted_as_missing()
    {
        var outcome = IngFinTsService.Outcome(
            [Cash("giro"), Cash("pocket-1", visible: false), Depot("depot", visible: false)],
            lastError: null);

        Assert.Equal(2, outcome.Accounts);
        Assert.Equal(1, outcome.Depots);
        Assert.Equal(2, outcome.Hidden);
        Assert.Equal(0, outcome.Missing);
    }

    // Ein Abbruch ganz am Anfang: nichts entstanden, und die Antwort sagt es.
    [Fact]
    public void A_run_that_created_nothing_says_everything_is_missing()
    {
        var outcome = IngFinTsService.Outcome(
            [Cash("giro", exists: false), Depot("depot", exists: false)],
            lastError: "FINTS_SYNC_FAILED");

        Assert.Equal(0, outcome.Accounts);
        Assert.Equal(0, outcome.Depots);
        Assert.Equal(2, outcome.Missing);
        Assert.Equal("FINTS_SYNC_FAILED", outcome.Error);
    }

    private static FinTsDiscoveredAccount Cash(string key, bool exists = true, bool visible = true)
        => new(key, "cash", key, "4321", "EUR", exists ? Guid.NewGuid() : null, visible);

    private static FinTsDiscoveredAccount Depot(string key, bool exists = true, bool visible = true)
        => new(key, "depot", key, null, "EUR", exists ? Guid.NewGuid() : null, visible);
}
