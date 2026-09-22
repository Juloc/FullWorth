using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>Welches Modell gebraucht wird. Text oder Bild - mehr unterscheidet die Anwendung nicht.</summary>
public enum AiModelKind { Text, Vision }

/// <summary>
/// Ein aufgeloester Zugang: wessen er ist, welcher Anbieter, welches Geheimnis, welches Modell.
/// <see cref="Source"/> ist <c>user</c> oder <c>instance</c> - daran haengt, wer bezahlt.
/// </summary>
public sealed record AiAccess(
    string Source,
    AiCredential Credential,
    IIntelligenceProvider Provider,
    string Secret,
    string Model);

/// <summary>
/// Die EINE Stelle, an der entschieden wird, welche KI eine Funktion bedient.
///
/// Vorher gab es drei Wege, und sie waren sich nicht einig:
///
/// <list type="bullet">
/// <item>Der Coach fragte zuerst den Benutzer, dann die Instanz - das richtige Modell, aber es lag in
/// <c>Modules/Coach</c> und galt nur fuer ihn.</item>
/// <item>Die geplanten Jobs nahmen fest den Zugang der Instanz und haben einen eigenen Zugang des
/// Benutzers nie auch nur gesucht.</item>
/// <item>Der Belegscan ging direkt an die Codex-Bruecke und kannte die Instanz nicht. Deshalb tat
/// <c>DefaultVisionModel</c> nichts: der einzige Bildpfad lief an der Aufloesung vorbei.</item>
/// </list>
///
/// Zwei Regeln, und die erste ist eine Entscheidung des Besitzers, keine technische:
///
/// 1. <b>Der Benutzer zuerst, die Instanz als Netz darunter.</b> Wer einen eigenen Zugang hinterlegt
///    hat, hat das bewusst getan - und zahlt dann auch selbst.
/// 2. <b>Der Zugang der Instanz arbeitet nur fuer die Module, fuer die er freigegeben ist.</b> Ein
///    eigener Zugang des Benutzers braucht keine Freigabe; er ist seiner.
/// </summary>
public sealed class AiAccessResolver(
    IntelligenceDbContext db,
    IntelligenceStore store,
    IntelligenceProviderRegistry providers)
{
    public async Task<AiAccess?> ResolveAsync(
        string module,
        Guid? userId,
        AiModelKind kind,
        CancellationToken ct)
    {
        var normalizedModule = AiModules.Normalize(module);
        if (normalizedModule is null) return null;

        var instance = await db.AiInstanceSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == AiInstanceSettings.InstanceScopeKey, ct);
        var userSettings = userId is { } id
            ? await db.AiUserSettings.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == id, ct)
            : null;

        var instanceReady = instance?.Enabled == true && instance.CredentialId.HasValue;
        var userAllowed = !instanceReady || instance!.AllowUserCredentials;

        if (userAllowed && userId is { } ownerId &&
            userSettings?.Enabled == true && userSettings.CredentialId is { } userCredentialId)
        {
            var userAccess = await TryResolveAsync(
                userCredentialId, ownerId, ModelOf(userSettings, kind), "user", ct);
            if (userAccess is not null) return userAccess;
        }

        if (instanceReady && instance!.CredentialId is { } instanceCredentialId &&
            await IsGrantedAsync(instanceCredentialId, normalizedModule, ct))
        {
            var instanceAccess = await TryResolveAsync(
                instanceCredentialId, null, ModelOf(instance, kind), "instance", ct);
            if (instanceAccess is not null) return instanceAccess;
        }

        // Ein eigener Zugang bleibt auf einer Instanz nutzbar, die selbst keinen aktiven hat - auch
        // wenn AllowUserCredentials dort nie ausdruecklich gesetzt wurde. Sonst waere eine Instanz
        // ohne eigene KI eine Instanz ohne KI, obwohl der Benutzer eine mitbringt.
        if (!instanceReady && userId is { } fallbackOwnerId &&
            userSettings?.Enabled == true && userSettings.CredentialId is { } fallbackCredentialId)
            return await TryResolveAsync(
                fallbackCredentialId, fallbackOwnerId, ModelOf(userSettings, kind), "user", ct);

        return null;
    }

    /// <summary>Fuer welche Module dieser Zugang arbeiten darf.</summary>
    public Task<List<string>> GrantedModulesAsync(Guid credentialId, CancellationToken ct) =>
        db.AiModuleGrants.AsNoTracking()
            .Where(grant => grant.CredentialId == credentialId)
            .Select(grant => grant.Module)
            .ToListAsync(ct);

    private Task<bool> IsGrantedAsync(Guid credentialId, string module, CancellationToken ct) =>
        db.AiModuleGrants.AsNoTracking()
            .AnyAsync(grant => grant.CredentialId == credentialId && grant.Module == module, ct);

    private static string? ModelOf(AiUserSettings settings, AiModelKind kind) =>
        kind == AiModelKind.Vision ? settings.VisionModel : settings.TextModel;

    private static string? ModelOf(AiInstanceSettings settings, AiModelKind kind) =>
        kind == AiModelKind.Vision ? settings.DefaultVisionModel : settings.DefaultTextModel;

    private async Task<AiAccess?> TryResolveAsync(
        Guid credentialId,
        Guid? ownerUserId,
        string? configuredModel,
        string source,
        CancellationToken ct)
    {
        var credential = await db.AiCredentials.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == credentialId && x.OwnerUserId == ownerUserId, ct);
        if (credential is null) return null;

        IIntelligenceProvider provider;
        try { provider = providers.GetRequired(credential.Provider); }
        catch (InvalidOperationException) { return null; }

        string secret;
        try { secret = await store.ResolveCredentialSecretAsync(credential.Id, ownerUserId, ct); }
        catch (KeyNotFoundException) { return null; }

        var model = NormalizeModel(configuredModel, credential.Provider);
        // Ein eigener Endpunkt hat keine Modellnamen, die man raten koennte - ohne Angabe gibt es
        // nichts aufzurufen. Codex waehlt dagegen selbst, dort ist leer ein gueltiger Wert.
        if (credential.Provider == IntelligenceProviders.OpenAiCompatible && string.IsNullOrWhiteSpace(model))
            return null;

        return new(source, credential, provider, secret, model);
    }

    private static string NormalizeModel(string? model, string provider)
    {
        var value = model?.Trim() ?? string.Empty;
        if (value.Length > 120) value = value[..120];
        if (string.IsNullOrWhiteSpace(value) && provider == IntelligenceProviders.OpenAi)
            return "gpt-5.6-terra";
        return value;
    }
}
