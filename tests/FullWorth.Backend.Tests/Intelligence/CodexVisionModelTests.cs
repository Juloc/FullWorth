using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// #156: das Vision-Modell des Benutzers wurde entgegengenommen, geprueft, gespeichert und im
/// Einstellungsbild wieder angezeigt - und dann nirgends benutzt. Im Belegscan stand
/// <c>model = (string?)null</c> fest im Anfragekoerper, also waehlte die Bruecke immer automatisch.
/// Der Benutzer sah eine Einstellung, die nichts tat.
///
/// Beim Verdrahten kommt eine zweite Regel dazu, die es vorher nicht brauchte: ein Modellname gehoert
/// dem Anbieter, bei dem er eingetragen wurde. Der Belegscan laeuft immer ueber Codex - auch bei
/// jemandem, der einen OpenAI-Schluessel hinterlegt hat -, und dessen Modellname hat fuer die
/// Codex-Befehlszeile keine Bedeutung. Deshalb wird nur weitergereicht, was auch zu Codex gehoert.
/// </summary>
public sealed class CodexVisionModelTests
{
    private static async Task<(IntelligenceStore Store, Guid User)> SetUpAsync(
        IServiceScope scope, string provider, string? visionModel)
    {
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();
        var user = Guid.NewGuid();
        var credential = await store.CreateCredentialAsync(user, provider, "Test", "secret-value-1234", CancellationToken.None);
        await store.SelectUserCredentialAsync(user, credential.Id, "text-model", visionModel, CancellationToken.None);
        return (store, user);
    }

    /// <summary>Der eigentliche Fehler: eingestellt, aber nie mitgeschickt.</summary>
    [Fact]
    public async Task A_codex_user_gets_the_model_he_configured()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();

        var (store, user) = await SetUpAsync(scope, IntelligenceProviders.Codex, "gpt-5.6-vision");

        Assert.Equal("gpt-5.6-vision", await store.CodexVisionModelAsync(user, CancellationToken.None));
    }

    /// <summary>
    /// Leer heisst bei Codex "automatisch" und ist ein gueltiger Wert - kein Grund, irgendein Modell
    /// zu erfinden.
    /// </summary>
    [Fact]
    public async Task No_model_stays_no_model()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();

        var (store, user) = await SetUpAsync(scope, IntelligenceProviders.Codex, null);

        Assert.Null(await store.CodexVisionModelAsync(user, CancellationToken.None));
    }

    /// <summary>
    /// Die Regel, die das Verdrahten ueberhaupt erst braucht. Ohne sie landete der Modellname eines
    /// OpenAI-Zugangs auf der Codex-Befehlszeile.
    /// </summary>
    [Theory]
    [InlineData(IntelligenceProviders.OpenAi)]
    [InlineData(IntelligenceProviders.OpenAiCompatible)]
    public async Task A_model_from_another_provider_never_reaches_codex(string provider)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();

        var (store, user) = await SetUpAsync(scope, provider, "gpt-5.6-vision");

        Assert.Null(await store.CodexVisionModelAsync(user, CancellationToken.None));
    }

    /// <summary>Wer gar keinen Zugang gewaehlt hat, hat auch kein Modell.</summary>
    [Fact]
    public async Task A_user_without_a_credential_has_no_model()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();

        Assert.Null(await store.CodexVisionModelAsync(Guid.NewGuid(), CancellationToken.None));
    }

    /// <summary>
    /// Die Aufloesung oben nuetzt nichts, wenn der Anfragekoerper den Wert wieder wegwirft. Genau das
    /// war der Zustand, und eine Bruecke laeuft im Test nicht mit - also wird die Stelle im Quelltext
    /// festgehalten.
    /// </summary>
    [Fact]
    public void The_scan_payload_no_longer_hardcodes_an_empty_model()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "FullWorth.Backend", "Modules", "Purchases", "CodexReceiptBridgeClient.cs"));

        Assert.DoesNotContain("model = (string?)null", source);
        Assert.Contains("CodexVisionModelAsync(userId, ct)", source);
    }
}
