using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Welche KI eine Funktion bedient - die eine Stelle, an der das entschieden wird.
///
/// Vorher gab es drei, und sie waren sich nicht einig: der Coach fragte zuerst den Benutzer und dann
/// die Instanz, die geplanten Jobs nahmen immer die Instanz und haben einen eigenen Zugang des
/// Benutzers nie gesucht, und der Belegscan ging an der Aufloesung ganz vorbei. Dass
/// <c>DefaultVisionModel</c> jahrelang nichts tat, war keine vergessene Zeile, sondern eine Folge
/// davon: der einzige Bildpfad fragte niemanden.
///
/// Die Regeln, die hier gepinnt sind, sind Entscheidungen und keine Technik - deshalb gehoeren sie
/// in Tests und nicht in einen Kommentar.
/// </summary>
public sealed class AiAccessResolverTests
{
    /// <summary>
    /// Wer einen eigenen Zugang hinterlegt hat, hat das bewusst getan - und zahlt dann auch selbst.
    /// Die Instanz ist das Netz darunter, nicht die erste Wahl.
    /// </summary>
    [Fact]
    public async Task The_users_own_access_wins_over_the_instance()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var world = await SetUpAsync(scope, grantedModules: [AiModules.Categorization], withUserCredential: true);

        var access = await Resolver(scope).ResolveAsync(
            AiModules.Categorization, world.User, AiModelKind.Text, CancellationToken.None);

        Assert.NotNull(access);
        Assert.Equal("user", access!.Source);
        Assert.Equal("user-text", access.Model);
    }

    /// <summary>Ohne eigenen Zugang uebernimmt die Instanz - aber nur, wenn sie freigegeben ist.</summary>
    [Fact]
    public async Task Without_an_own_access_the_granted_instance_takes_over()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var world = await SetUpAsync(scope, grantedModules: [AiModules.Categorization], withUserCredential: false);

        var access = await Resolver(scope).ResolveAsync(
            AiModules.Categorization, world.User, AiModelKind.Text, CancellationToken.None);

        Assert.NotNull(access);
        Assert.Equal("instance", access!.Source);
        Assert.Equal("instance-text", access.Model);
    }

    /// <summary>
    /// Der Kern der Freigabe: derselbe Zugang, dieselbe Instanz, ein anderes Modul - und es passiert
    /// nichts. Ohne diese Regel waere "freigeben" ein Alles-oder-nichts.
    /// </summary>
    [Fact]
    public async Task An_instance_access_does_not_work_for_a_module_it_was_not_released_for()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var world = await SetUpAsync(scope, grantedModules: [AiModules.Categorization], withUserCredential: false);

        Assert.Null(await Resolver(scope).ResolveAsync(
            AiModules.Receipts, world.User, AiModelKind.Text, CancellationToken.None));
    }

    /// <summary>
    /// Ein eigener Zugang braucht KEINE Freigabe. Er gehoert dem Benutzer; der Instanzadmin entscheidet
    /// ueber die KI der Instanz, nicht ueber die des Benutzers.
    /// </summary>
    [Fact]
    public async Task The_users_own_access_needs_no_grant()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var world = await SetUpAsync(scope, grantedModules: [], withUserCredential: true);

        var access = await Resolver(scope).ResolveAsync(
            AiModules.Receipts, world.User, AiModelKind.Text, CancellationToken.None);

        Assert.NotNull(access);
        Assert.Equal("user", access!.Source);
    }

    /// <summary>
    /// Genau der Fehler aus #156, eine Ebene tiefer behoben: wer ein Bild verarbeitet, bekommt das
    /// Bildmodell. Vorher fragte der einzige Bildpfad gar nicht erst - deshalb war DefaultVisionModel
    /// tot, nicht weil die Zeile fehlte.
    /// </summary>
    [Theory]
    [InlineData(true, AiModelKind.Vision, "user-vision")]
    [InlineData(true, AiModelKind.Text, "user-text")]
    [InlineData(false, AiModelKind.Vision, "instance-vision")]
    [InlineData(false, AiModelKind.Text, "instance-text")]
    public async Task The_kind_of_work_decides_which_model_is_used(
        bool withUserCredential, AiModelKind kind, string expected)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var world = await SetUpAsync(scope, [AiModules.Receipts], withUserCredential);

        var access = await Resolver(scope).ResolveAsync(
            AiModules.Receipts, world.User, kind, CancellationToken.None);

        Assert.Equal(expected, access?.Model);
    }

    /// <summary>
    /// Ein unbekanntes Modul ist ein Tippfehler. Es still durchzulassen hiesse, dass eine falsch
    /// geschriebene Freigabe wie eine unbeschraenkte wirkt.
    /// </summary>
    [Fact]
    public async Task An_unknown_module_resolves_to_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var world = await SetUpAsync(scope, grantedModules: [], withUserCredential: true);

        Assert.Null(await Resolver(scope).ResolveAsync(
            "kategorisierung", world.User, AiModelKind.Text, CancellationToken.None));
    }

    /// <summary>
    /// Die Uebernahme-Entscheidung der Migration, festgehalten am Quelltext: der Coach hatte nie einen
    /// Schalter - er lief, sobald ein Zugang da war. Ohne bedingungslose Freigabe waere er nach dem
    /// Update aus, und ein Update, das still ein Feature abschaltet, faellt niemandem auf.
    /// </summary>
    [Fact]
    public void The_migration_grants_the_coach_unconditionally()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var sql = File.ReadAllText(Path.Combine(
            dir!.FullName, "src", "FullWorth.Backend", "Modules", "Intelligence", "Migrations",
            "20260922120000_AiModuleGrants.cs"));

        // Jede andere Zeile haengt an einem WHERE; diese nicht.
        Assert.Contains("UNION ALL SELECT 'coach'\n", sql.Replace("\r\n", "\n"));
        // Und die zwei Schalter, die ohnehin nur gemeinsam abgefragt wurden, werden eine Freigabe.
        Assert.Contains("""'categorization' AS grant_module WHERE s."MerchantAiEnabled" AND s."CategoryAiEnabled" """.TrimEnd(), sql);
    }

    /// <summary>
    /// Die Einordnung im Loesch-Manifest behauptet, dass eine Freigabe mit ihrem Zugang faellt. Eine
    /// Behauptung im Manifest ist nur so viel wert wie der Fremdschluessel darunter - hier wird sie
    /// nachgerechnet, und zwar an beiden Seiten: die Freigabe des geloeschten Zugangs verschwindet,
    /// die der Instanz bleibt.
    /// </summary>
    [Fact]
    public async Task A_grant_dies_with_its_credential_and_leaves_the_others_alone()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();
        var world = await SetUpAsync(scope, [AiModules.Categorization], withUserCredential: false);

        var user = Guid.NewGuid();
        var ownCredential = await store.CreateCredentialAsync(
            user, IntelligenceProviders.OpenAi, "Eigener", "own-secret-1234", CancellationToken.None);
        db.AiModuleGrants.Add(new AiModuleGrant { CredentialId = ownCredential.Id, Module = AiModules.Receipts });
        await db.SaveChangesAsync();

        await store.ClearUserAccessAsync(user, CancellationToken.None);

        var remaining = await db.AiModuleGrants.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(remaining, grant => grant.CredentialId == ownCredential.Id);
        Assert.Contains(remaining, grant => grant.CredentialId == world.InstanceCredential);
    }

    private static AiAccessResolver Resolver(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<AiAccessResolver>();

    private sealed record World(Guid User, Guid InstanceCredential);

    private static async Task<World> SetUpAsync(
        IServiceScope scope, IReadOnlyList<string> grantedModules, bool withUserCredential)
    {
        var db = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();
        var user = Guid.NewGuid();

        var instanceCredential = await store.CreateCredentialAsync(
            null, IntelligenceProviders.OpenAi, "Instanz", "instance-secret-1234", CancellationToken.None);

        var settings = await db.AiInstanceSettings
            .SingleOrDefaultAsync(x => x.ScopeKey == AiInstanceSettings.InstanceScopeKey);
        if (settings is null)
        {
            settings = new AiInstanceSettings();
            db.AiInstanceSettings.Add(settings);
        }
        settings.Enabled = true;
        settings.CredentialId = instanceCredential.Id;
        settings.AllowUserCredentials = true;
        settings.DefaultTextModel = "instance-text";
        settings.DefaultVisionModel = "instance-vision";

        foreach (var module in grantedModules)
            db.AiModuleGrants.Add(new AiModuleGrant { CredentialId = instanceCredential.Id, Module = module });

        await db.SaveChangesAsync();

        if (withUserCredential)
        {
            var userCredential = await store.CreateCredentialAsync(
                user, IntelligenceProviders.OpenAi, "Eigener", "user-secret-1234", CancellationToken.None);
            await store.SelectUserCredentialAsync(
                user, userCredential.Id, "user-text", "user-vision", CancellationToken.None);
        }

        return new(user, instanceCredential.Id);
    }
}
