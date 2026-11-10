using FullWorth.Backend.Security;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Paperless;

/// <summary>
/// Die Verbindung zu einer Paperless-Instanz und was daran haengt: speichern (der Token nur
/// verschluesselt), pruefen, Presets anlegen. Wer Dokumente holen will, ruft <see cref="RequireAsync"/>
/// und redet danach selbst mit dem <see cref="PaperlessClient"/> - was er mit den Dokumenten tut, ist
/// seine Sache.
/// </summary>
public sealed class PaperlessService(
    PaperlessStore store,
    PaperlessClient client,
    FieldCipher cipher,
    IOptions<PaperlessOptions> options)
{
    private readonly PaperlessOptions settings = options.Value;

    public async Task<PaperlessConnectionView?> GetConnectionAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var stored = await store.GetPaperlessConnectionAsync(fullWorthSpaceId, ct);
        return stored is null ? null : ToView(stored);
    }

    public async Task<(PaperlessConnectionView? Connection, string? ServerVersion, string? Error)> SaveConnectionAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        PaperlessConnectionWrite request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiToken)) return (null, null, "Paperless API token is required.");
        Uri baseUri;
        try { baseUri = PaperlessClient.NormalizeBaseUri(request.BaseUrl); }
        catch (UriFormatException ex) { return (null, null, ex.Message); }

        var previous = await store.GetPaperlessConnectionAsync(fullWorthSpaceId, ct);
        var test = await client.TestAsync(baseUri.ToString(), request.ApiToken, ct);
        if (!test.Success) return (null, test.ServerVersion, test.Error);

        if (previous is not null &&
            !string.Equals(PaperlessClient.NormalizeBaseUri(previous.BaseUrl).ToString(), baseUri.ToString(), StringComparison.OrdinalIgnoreCase))
            await store.DisablePaperlessAutoImportAsync(fullWorthSpaceId, ct);

        var protectedToken = cipher.Protect(request.ApiToken.Trim())
            ?? throw new InvalidOperationException("Paperless token could not be protected.");
        await store.UpsertPaperlessConnectionAsync(fullWorthSpaceId, userId, baseUri.ToString(), protectedToken, request.DefaultQuery, request.IsEnabled, ct);
        var stored = await store.GetPaperlessConnectionAsync(fullWorthSpaceId, ct);
        return (stored is null ? null : ToView(stored), test.ServerVersion, null);
    }

    public async Task DeleteConnectionAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        await store.DisablePaperlessAutoImportAsync(fullWorthSpaceId, ct);
        await store.DeletePaperlessConnectionAsync(fullWorthSpaceId, ct);
    }

    public async Task<(bool Success, string? ServerVersion, string? Error)> TestAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RequireAsync(fullWorthSpaceId, ct);
        return await client.TestAsync(connection.BaseUrl, connection.Token, ct);
    }

    public async Task<PaperlessFilterOptionsView> GetFilterOptionsAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RequireAsync(fullWorthSpaceId, ct);
        return await client.GetFilterOptionsAsync(connection.BaseUrl, connection.Token, ct);
    }

    /// <summary>Die aktive Verbindung des Spaces mit entschluesseltem Token; <see cref="PaperlessException"/>, wenn es keine gibt.</summary>
    public async Task<PaperlessConnection> RequireAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var stored = await store.GetPaperlessConnectionAsync(fullWorthSpaceId, ct)
            ?? throw new PaperlessException("Paperless is not configured for this FullWorth Space.");
        if (!stored.IsEnabled) throw new PaperlessException("Paperless connection is disabled.");
        var token = cipher.Unprotect(stored.ApiTokenProtected);
        if (string.IsNullOrWhiteSpace(token)) throw new PaperlessException("Paperless token could not be loaded.");
        return new(stored.BaseUrl, token, stored.DefaultQuery);
    }

    public async Task<PaperlessImportPresetView> SavePresetAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        Guid? presetId,
        PaperlessImportPresetWrite request,
        CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PaperlessException("Preset name is required.");
        if (name.Length > 100) throw new PaperlessException("Preset name is too long.");

        if (!PaperlessPresetKinds.IsKnown(request.Kind)) throw new PaperlessException("Unknown Paperless preset kind.");

        var id = presetId is { } value && value != Guid.Empty ? value : Guid.NewGuid();
        var existing = presetId is { } existingId
            ? await store.GetPaperlessPresetAsync(userId, fullWorthSpaceId, existingId, ct)
            : null;
        if (presetId.HasValue && existing is null) throw new PaperlessException("Paperless preset was not found.");

        var siblings = await store.ListPaperlessPresetsAsync(userId, fullWorthSpaceId, null, ct);
        if (siblings.Any(x => x.Id != id && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new PaperlessException("A Paperless preset with this name already exists.");

        var query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        if (query?.Length > 4000) throw new PaperlessException("Paperless query is too long.");
        var editorJson = string.IsNullOrWhiteSpace(request.EditorJson) ? null : request.EditorJson;
        if (editorJson?.Length > 32000) throw new PaperlessException("Paperless preset editor data is too large.");

        var currency = request.Currency?.Trim() is { Length: 3 } code && code.All(char.IsLetter) ? code.ToUpperInvariant() : "EUR";
        var lastSeen = existing?.LastSeenDocumentId;
        var baselineRequired = request.AutoImport && PaperlessPresetKinds.StartsFromNow(request.Kind) &&
            (existing is null || !existing.AutoImport || !string.Equals(existing.Query, query, StringComparison.Ordinal));

        if (baselineRequired)
        {
            var connection = await RequireAsync(fullWorthSpaceId, ct);
            var baselineFilter = ApplyDefaultQuery(
                new PaperlessPreviewRequest(Query: query, Limit: settings.MaxBatchItems),
                connection.DefaultQuery);
            var preview = await client.PreviewAsync(connection.BaseUrl, connection.Token, baselineFilter, ct);
            lastSeen = preview.Documents.Count == 0 ? 0 : preview.Documents.Max(x => x.Id);
        }

        return await store.SavePaperlessPresetAsync(
            id,
            userId,
            fullWorthSpaceId,
            name,
            query,
            editorJson,
            request.AutoImport,
            request.AnalyzeAutomatically,
            currency,
            lastSeen,
            request.Kind,
            ct);
    }

    /// <summary>Scheme und Host in Kleinbuchstaben - der Teil der Adresse, an dem ein Dokument einer Instanz zugeordnet wird.</summary>
    public static string SourcePrefix(string baseUrl) =>
        PaperlessClient.NormalizeBaseUri(baseUrl).GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    public static PaperlessPreviewRequest ApplyDefaultQuery(PaperlessPreviewRequest request, string? defaultQuery) =>
        string.IsNullOrWhiteSpace(request.Query) && !string.IsNullOrWhiteSpace(defaultQuery)
            ? request with { Query = defaultQuery }
            : request;

    private static PaperlessConnectionView ToView(PaperlessStoredConnection stored) =>
        new(stored.FullWorthSpaceId, stored.BaseUrl, true, stored.DefaultQuery, stored.IsEnabled, stored.LastSyncAt, stored.UpdatedAt);
}
