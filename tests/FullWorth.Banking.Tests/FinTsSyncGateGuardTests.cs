using System.Text.RegularExpressions;

namespace FullWorth.Banking.Tests;

/// <summary>
/// Ein von aussen ausgeloester FinTS-Abruf laeuft im <c>BankSyncConcurrencyGate</c>.
///
/// Die Bank haelt pro Zugang einen Dialog. Laufen der Hintergrundlauf und die Uebernahme aus der
/// Kontenauswahl gleichzeitig, meldet sie sich mit einem abgebrochenen Dialog - ein Fehler, der
/// aussieht wie ein Protokollfehler und keiner ist.
///
/// <para>
/// Warum ein Quelltext-Waechter: der Schaden braucht echte Nebenlaeufigkeit. Ein Test, der
/// <c>ImportAsync</c> einmal aufruft, laeuft sequenziell und bleibt gruen, auch wenn das Gatter
/// wieder fehlt. Die Zusicherung ist strukturell ("es gibt genau eine Stelle, die den Abruf
/// betritt"), also wird sie strukturell geprueft.
/// </para>
///
/// <para>
/// Und das Gatter darf NICHT in <c>SyncConnectionAsync</c> selbst liegen: der Hintergrundlauf haelt
/// es dann schon, und ein <see cref="SemaphoreSlim"/> ist nicht wiedereintrittsfaehig - das waere
/// kein Fehler mehr, sondern ein stehender Prozess.
/// </para>
/// </summary>
public sealed class FinTsSyncGateGuardTests
{
    [Fact]
    public void OnlyTheGatedHelperStartsAnOwnerTriggeredSync()
    {
        var quelle = OhneKommentare(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "FullWorth.Banking", "Services", "IngFinTsService.cs")));

        // Ein Aufruf steht in SyncGatedAsync selbst. Jeder weitere umgeht das Gatter.
        var aufrufe = Regex.Matches(quelle, @"SyncConnectionAsync\s*\(\s*connection").Count;

        Assert.True(
            aufrufe == 1,
            $"SyncConnectionAsync wird {aufrufe}-mal aufgerufen. Genau einer davon gehoert nach "
            + "SyncGatedAsync; wer einen weiteren braucht, laesst ihn ueber SyncGatedAsync laufen - "
            + "sonst redet der Dienst mit der Bank, waehrend der Hintergrundlauf es auch tut.");
        Assert.Contains("syncGate.EnterAsync", quelle);
    }

    private static string OhneKommentare(string code) =>
        Regex.Replace(
            Regex.Replace(code, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline),
            @"(?m)^\s*///?.*$", string.Empty);

    private static string RepositoryRoot()
    {
        var verzeichnis = new DirectoryInfo(AppContext.BaseDirectory);
        while (verzeichnis is not null && !File.Exists(Path.Combine(verzeichnis.FullName, "FullWorth.slnx")))
            verzeichnis = verzeichnis.Parent;

        return verzeichnis?.FullName ?? throw new DirectoryNotFoundException("FullWorth.slnx not found.");
    }
}
