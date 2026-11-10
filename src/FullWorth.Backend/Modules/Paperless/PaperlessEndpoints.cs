using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Paperless;

public static class PaperlessEndpoints
{
    /// <summary>
    /// Verbindung, Pruefung und Presets. Die Adressen stehen weiter unter dem Beleg-Import, in dem sie
    /// entstanden sind: sie zu verlegen hiesse, jeden Aufrufer anzufassen, und an der Fachlichkeit
    /// aendert es nichts.
    /// </summary>
    public static IEndpointRouteBuilder MapPaperlessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/receipt-imports/paperless").WithTags("Paperless");

        group.MapGet("/connection", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessService service,
            CancellationToken ct) =>
        {
            if (!await access.IsMemberAsync(user.RequireUserId(), fullWorthSpaceId, ct)) return Results.NotFound();
            var connection = await service.GetConnectionAsync(fullWorthSpaceId, ct);
            return connection is null
                ? Results.Ok(new { configured = false })
                : Results.Ok(connection);
        });

        group.MapPut("/connection", async (
            Guid fullWorthSpaceId,
            PaperlessConnectionWrite request,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            var result = await service.SaveConnectionAsync(userId, fullWorthSpaceId, request, ct);
            return result.Error is null
                ? Results.Ok(new { connection = result.Connection, serverVersion = result.ServerVersion })
                : Results.BadRequest(new { error = result.Error, serverVersion = result.ServerVersion });
        });

        group.MapDelete("/connection", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessService service,
            CancellationToken ct) =>
        {
            if (!await access.IsMemberAsync(user.RequireUserId(), fullWorthSpaceId, ct)) return Results.NotFound();
            await service.DeleteConnectionAsync(fullWorthSpaceId, ct);
            return Results.NoContent();
        });

        group.MapPost("/test", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessService service,
            CancellationToken ct) =>
        {
            if (!await access.IsMemberAsync(user.RequireUserId(), fullWorthSpaceId, ct)) return Results.NotFound();
            try
            {
                var result = await service.TestAsync(fullWorthSpaceId, ct);
                return result.Success
                    ? Results.Ok(new { success = true, serverVersion = result.ServerVersion })
                    : Results.BadRequest(new { success = false, error = result.Error, serverVersion = result.ServerVersion });
            }
            catch (PaperlessException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapGet("/options", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessService service,
            CancellationToken ct) =>
        {
            if (!await access.IsMemberAsync(user.RequireUserId(), fullWorthSpaceId, ct)) return Results.NotFound();
            try { return Results.Ok(await service.GetFilterOptionsAsync(fullWorthSpaceId, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapGet("/presets", async (
            Guid fullWorthSpaceId,
            string? kind,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessStore store,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            return Results.Ok(await store.ListPaperlessPresetsAsync(userId, fullWorthSpaceId, kind ?? PaperlessPresetKinds.Receipt, ct));
        });

        group.MapPost("/presets", async (
            Guid fullWorthSpaceId,
            PaperlessImportPresetWrite request,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { return Results.Ok(await service.SavePresetAsync(userId, fullWorthSpaceId, null, request, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPut("/presets/{presetId:guid}", async (
            Guid presetId,
            Guid fullWorthSpaceId,
            PaperlessImportPresetWrite request,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { return Results.Ok(await service.SavePresetAsync(userId, fullWorthSpaceId, presetId, request, ct)); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Ein Lauf von Hand, ohne auf den Takt des Hintergrunddienstes zu warten. Er holt, was der
        // Importer seiner Art im Request holen darf - bei Lohnabrechnungen nur die Funde, das Auslesen
        // laeuft danach im Hintergrund.
        group.MapPost("/presets/{presetId:guid}/run", async (
            Guid presetId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessStore store,
            PaperlessService service,
            IEnumerable<IPaperlessPresetImporter> importers,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            var preset = await store.GetPaperlessPresetAsync(userId, fullWorthSpaceId, presetId, ct);
            if (preset is null) return Results.NotFound();
            try
            {
                var connection = await service.RequireAsync(fullWorthSpaceId, ct);
                var outcome = await importers.Single(x => x.Kinds.Contains(preset.Kind)).ImportAsync(preset, connection, ct);
                await store.UpdatePaperlessPresetCheckAsync(preset.Id, outcome.MaxSeenDocumentId, outcome.Imported, outcome.Error, ct);
                return Results.Ok(await store.GetPaperlessPresetAsync(userId, fullWorthSpaceId, presetId, ct));
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapDelete("/presets/{presetId:guid}", async (
            Guid presetId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            SpaceAccess access,
            PaperlessStore store,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await access.IsMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            await store.DeletePaperlessPresetAsync(userId, fullWorthSpaceId, presetId, ct);
            return Results.NoContent();
        });

        return app;
    }
}
