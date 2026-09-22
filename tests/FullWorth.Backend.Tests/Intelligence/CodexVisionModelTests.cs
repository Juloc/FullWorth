namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// #156: das Vision-Modell des Benutzers wurde entgegengenommen, geprueft, gespeichert und im
/// Einstellungsbild wieder angezeigt - und dann nirgends benutzt. Im Belegscan stand
/// <c>model = (string?)null</c> fest im Anfragekoerper.
///
/// Die erste Fassung dieses Tests hat den Wert ueber eine eigene Store-Methode geholt. Die gibt es
/// nicht mehr: der Scan loest jetzt wie jede andere KI-Funktion ueber
/// <see cref="FullWorth.Backend.Modules.Intelligence.AiAccessResolver"/> auf, und welches Modell bei
/// Bildarbeit gewaehlt wird, steht in <c>AiAccessResolverTests</c>.
///
/// Was hier bleibt, sind die zwei Regeln, die nur an DIESER Stelle gelten und die eine Bruecke
/// braeuchten, um sie am laufenden System zu pruefen - deshalb als Quelltextpruefung:
/// der Scan fragt ueberhaupt, und er reicht keinen fremden Modellnamen an die Codex-Befehlszeile.
/// </summary>
public sealed class CodexVisionModelTests
{
    private static string BridgeClient()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "FullWorth.Backend", "Modules", "Purchases", "CodexReceiptBridgeClient.cs"));
    }

    /// <summary>
    /// Der Scan lief an jeder Aufloesung vorbei: keine Instanz-Einstellung, keine Freigabe, kein
    /// Modell. Genau deshalb tat <c>DefaultVisionModel</c> nichts - die Zeile fehlte nicht, es fragte
    /// nur niemand.
    /// </summary>
    [Fact]
    public void The_receipt_scan_asks_the_ai_system_who_may_serve_it()
    {
        var source = BridgeClient();

        Assert.DoesNotContain("model = (string?)null", source);
        Assert.Contains("AiModules.Receipts", source);
        Assert.Contains("AiModelKind.Vision", source);
        // Ohne Freigabe kein Scan - nicht "Scan ohne Modell".
        Assert.Contains("if (access is null) return null;", source);
    }

    /// <summary>
    /// Ein Modellname gehoert dem Anbieter, bei dem er eingetragen wurde. Der Belegscan laeuft ueber
    /// die Codex-Bruecke; loest die Freigabe auf OpenAI auf, waere der Name dort geraten. Dann lieber
    /// der lokale OCR-Rueckfall als eine Befehlszeile mit einem erfundenen Modell.
    /// </summary>
    [Fact]
    public void A_model_from_another_provider_never_reaches_the_codex_command_line()
    {
        var source = BridgeClient();

        Assert.Contains("access.Credential.Provider != Intelligence.IntelligenceProviders.Codex", source);
    }
}
