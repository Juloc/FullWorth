using FullWorth.Backend.Security;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases.ReceiptImports;

public static class ReceiptImportEndpoints
{
    public static IEndpointRouteBuilder MapReceiptImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/purchases/receipt-imports").WithTags("Purchase receipt imports");

        group.MapGet("/batches", async (
            Guid fullWorthSpaceId,
            int? limit,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportStore store,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            return Results.Ok(await store.ListBatchesAsync(userId, fullWorthSpaceId, limit ?? 20, ct));
        });

        group.MapGet("/batches/{batchId:guid}", async (
            Guid batchId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportStore store,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            var batch = await store.GetBatchAsync(userId, fullWorthSpaceId, batchId, ct);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        });

        group.MapPost("/upload", async (
            Guid fullWorthSpaceId,
            HttpRequest request,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            IOptions<ReceiptImportOptions> importOptions,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();

            // Keep the normal Kestrel request-body ceiling everywhere else. This endpoint is the only
            // backend route that needs to accept a large multi-file archive and it still applies its own
            // aggregate + per-file limits before anything reaches the receipt queue.
            var maxBodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxBodySize is { IsReadOnly: false })
                maxBodySize.MaxRequestBodySize = Math.Max(1L, importOptions.Value.MaxUploadBytes);

            try { return Results.Ok(await service.ImportUploadAsync(userId, fullWorthSpaceId, request, ct)); }
            catch (ReceiptImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();

        group.MapPost("/batches/{batchId:guid}/start-pending", async (
            Guid batchId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            var batch = await service.StartPendingAsync(userId, fullWorthSpaceId, batchId, ct);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        });

        group.MapPost("/batches/{batchId:guid}/retry-failed", async (
            Guid batchId,
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            var batch = await service.RetryFailedAsync(userId, fullWorthSpaceId, batchId, ct);
            return batch is null ? Results.NotFound() : Results.Ok(batch);
        });

        group.MapGet("/paperless/connection", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            var connection = await service.GetPaperlessConnectionAsync(fullWorthSpaceId, ct);
            return connection is null
                ? Results.Ok(new { configured = false })
                : Results.Ok(connection);
        });

        group.MapPut("/paperless/connection", async (
            Guid fullWorthSpaceId,
            PaperlessConnectionWrite request,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            var result = await service.SavePaperlessConnectionAsync(userId, fullWorthSpaceId, request, ct);
            return result.Error is null
                ? Results.Ok(new { connection = result.Connection, serverVersion = result.ServerVersion })
                : Results.BadRequest(new { error = result.Error, serverVersion = result.ServerVersion });
        });

        group.MapDelete("/paperless/connection", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            await service.DeletePaperlessConnectionAsync(fullWorthSpaceId, ct);
            return Results.NoContent();
        });

        group.MapPost("/paperless/test", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try
            {
                var result = await service.TestPaperlessAsync(fullWorthSpaceId, ct);
                return result.Success
                    ? Results.Ok(new { success = true, serverVersion = result.ServerVersion })
                    : Results.BadRequest(new { success = false, error = result.Error, serverVersion = result.ServerVersion });
            }
            catch (ReceiptImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapGet("/paperless/options", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { return Results.Ok(await service.GetPaperlessFilterOptionsAsync(fullWorthSpaceId, ct)); }
            catch (Exception ex) when (ex is ReceiptImportException or InvalidOperationException or HttpRequestException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/paperless/preview", async (
            Guid fullWorthSpaceId,
            PaperlessPreviewRequest request,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { return Results.Ok(await service.PreviewPaperlessAsync(fullWorthSpaceId, request, ct)); }
            catch (Exception ex) when (ex is ReceiptImportException or InvalidOperationException or HttpRequestException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/paperless/import", async (
            Guid fullWorthSpaceId,
            PaperlessImportRequest request,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { return Results.Ok(await service.ImportPaperlessAsync(userId, fullWorthSpaceId, request, ct)); }
            catch (Exception ex) when (ex is ReceiptImportException or InvalidOperationException or HttpRequestException)
            { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapGet("/folder/status", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            var preview = await service.PreviewFolderAsync(ct);
            return Results.Ok(new { configured = preview.Configured, count = preview.Count, totalBytes = preview.TotalBytes });
        });

        group.MapPost("/folder/preview", async (
            Guid fullWorthSpaceId,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            return Results.Ok(await service.PreviewFolderAsync(ct));
        });

        group.MapPost("/folder/import", async (
            Guid fullWorthSpaceId,
            FolderImportRequest request,
            CurrentUserContext user,
            PurchaseAuthorizationStore authorization,
            ReceiptImportService service,
            CancellationToken ct) =>
        {
            var userId = user.RequireUserId();
            if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
            try { return Results.Ok(await service.ImportFolderAsync(userId, fullWorthSpaceId, request.Currency, request.AutoStart, ct)); }
            catch (ReceiptImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }

    public sealed record FolderImportRequest(string? Currency = null, bool? AutoStart = null);
}
