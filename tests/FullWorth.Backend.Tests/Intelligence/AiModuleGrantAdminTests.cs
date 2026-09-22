using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Intelligence;

/// <summary>
/// Freigeben und wieder entziehen - die Schreibseite von <see cref="AiModules"/>.
///
/// Der Admin traf diese Entscheidung vorher ueber sieben feste Schalter. Dass die Oberflaeche nun
/// eine Liste zeichnet statt sieben Kaestchen, ist der sichtbare Teil; der Punkt ist, dass ein neues
/// Modul dafuer keinen Schemawechsel mehr braucht.
/// </summary>
public sealed class AiModuleGrantAdminTests
{
    [Fact]
    public async Task Releasing_and_withdrawing_a_module_is_a_row_that_comes_and_goes()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var (store, db, credentialId) = await SetUpAsync(scope);

        await SaveAsync(store, credentialId, [AiModules.Categorization, AiModules.Receipts]);
        Assert.Equal(
            [AiModules.Categorization, AiModules.Receipts],
            await GrantedAsync(db, credentialId));

        // Entziehen ist dasselbe Formular mit einem Haken weniger - und muss die Zeile wirklich
        // loeschen, nicht nur nicht mehr anzeigen.
        await SaveAsync(store, credentialId, [AiModules.Receipts]);
        Assert.Equal([AiModules.Receipts], await GrantedAsync(db, credentialId));

        await SaveAsync(store, credentialId, []);
        Assert.Empty(await GrantedAsync(db, credentialId));
    }

    /// <summary>
    /// Zweimal dasselbe Formular abschicken darf keine zweite Zeile erzeugen. Der zusammengesetzte
    /// Schluessel verhindert es in der Datenbank; hier wird geprueft, dass der Speicher gar nicht
    /// erst dagegenlaeuft.
    /// </summary>
    [Fact]
    public async Task Saving_the_same_release_twice_changes_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var (store, db, credentialId) = await SetUpAsync(scope);

        await SaveAsync(store, credentialId, [AiModules.Coach]);
        await SaveAsync(store, credentialId, [AiModules.Coach]);

        Assert.Equal([AiModules.Coach], await GrantedAsync(db, credentialId));
    }

    /// <summary>
    /// Ein Tippfehler ist keine Freigabe. Ihn still zu verwerfen waere schlimmer als abzulehnen: das
    /// Formular saehe gespeichert aus, und die Funktion liefe nicht - ohne dass irgendwo etwas steht.
    /// </summary>
    [Fact]
    public async Task An_unknown_module_is_refused_instead_of_quietly_dropped()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var (store, db, credentialId) = await SetUpAsync(scope);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SaveAsync(store, credentialId, [AiModules.Coach, "kategorisierung"]));

        Assert.Empty(await GrantedAsync(db, credentialId));
    }

    /// <summary>
    /// Ohne eingetragenen Zugang gibt es nichts freizugeben. Die Haken trotzdem zu speichern hiesse,
    /// sie beim naechsten eingetragenen Zugang ungefragt wirksam werden zu lassen.
    /// </summary>
    [Fact]
    public async Task Without_a_credential_nothing_is_released()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();
        var db = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();

        await store.SaveInstanceSettingsAsync(
            new AiInstanceSettings
            {
                Enabled = true,
                Provider = IntelligenceProviders.OpenAi,
                CredentialId = null,
                DefaultTextModel = "gpt-5.6",
                DefaultVisionModel = "gpt-5.6"
            },
            [AiModules.Coach],
            CancellationToken.None);

        Assert.Empty(await db.AiModuleGrants.AsNoTracking().ToListAsync());
    }

    private static Task SaveAsync(IntelligenceStore store, Guid credentialId, IReadOnlyList<string> modules) =>
        store.SaveInstanceSettingsAsync(
            new AiInstanceSettings
            {
                Enabled = true,
                Provider = IntelligenceProviders.OpenAi,
                CredentialId = credentialId,
                DefaultTextModel = "gpt-5.6",
                DefaultVisionModel = "gpt-5.6"
            },
            modules,
            CancellationToken.None);

    private static async Task<List<string>> GrantedAsync(IntelligenceDbContext db, Guid credentialId) =>
        await db.AiModuleGrants.AsNoTracking()
            .Where(grant => grant.CredentialId == credentialId)
            .Select(grant => grant.Module)
            .OrderBy(module => module)
            .ToListAsync();

    private static async Task<(IntelligenceStore Store, IntelligenceDbContext Db, Guid CredentialId)> SetUpAsync(
        IServiceScope scope)
    {
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();
        var db = scope.ServiceProvider.GetRequiredService<IntelligenceDbContext>();
        var credential = await store.CreateCredentialAsync(
            null, IntelligenceProviders.OpenAi, "Instanz", "instance-secret-1234", CancellationToken.None);
        return (store, db, credential.Id);
    }
}
