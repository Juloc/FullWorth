using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Import;

public static class FinanzguruImportEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;

    public static IEndpointRouteBuilder MapFinanzguruImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/import/finanzguru/accounts", async (
            Guid fullWorthSpaceId,
            CurrentUserContext currentUser,
            FinanzguruAccountReconciliationService reconciliation,
            CancellationToken ct) =>
        {
            var result = await reconciliation.ListLinkOptionsAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithTags("Import");

        app.MapPost("/api/import/finanzguru/accounts/{targetAccountId:guid}/confirm-history", async (
            Guid targetAccountId,
            Guid fullWorthSpaceId,
            FinanzguruConfirmHistoryRequest request,
            CurrentUserContext currentUser,
            FinanzguruAccountReconciliationService reconciliation,
            FullWorth.Backend.Modules.Portfolio.NetWorthSnapshotService snapshots,
            CancellationToken ct) =>
        {
            try
            {
                var userId = currentUser.RequireUserId();
                var result = await reconciliation.ConfirmAttachedHistoryAsync(
                    userId,
                    fullWorthSpaceId,
                    targetAccountId,
                    request.CurrentBalance,
                    request.CurrentBalanceCurrency,
                    ct);
                if (result is null) return Results.NotFound();

                await snapshots.RebuildHistoryForUserAsync(fullWorthSpaceId, userId, null, ct);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).WithTags("Import");

        app.MapPost("/api/import/finanzguru/accounts/{importAccountId:guid}/link", async (
            Guid importAccountId,
            Guid fullWorthSpaceId,
            FinanzguruExplicitLinkRequest request,
            CurrentUserContext currentUser,
            FinanzguruAccountReconciliationService reconciliation,
            FullWorth.Backend.Modules.Portfolio.NetWorthSnapshotService snapshots,
            CancellationToken ct) =>
        {
            try
            {
                var userId = currentUser.RequireUserId();
                var result = await reconciliation.LinkExplicitAsync(
                    userId,
                    fullWorthSpaceId,
                    importAccountId,
                    request.TargetAccountId,
                    request.CurrentBalance,
                    request.CurrentBalanceCurrency,
                    ct);
                if (result is null) return Results.NotFound();

                await snapshots.RebuildHistoryForUserAsync(fullWorthSpaceId, userId, null, ct);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).WithTags("Import");

        app.MapPost("/api/import/finanzguru", async (
            Guid fullWorthSpaceId,
            HttpRequest request,
            CurrentUserContext currentUser,
            FinanzguruImportService service,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data with an .xlsx file." });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file was uploaded." });
            if (file.Length > MaxUploadBytes)
                return Results.BadRequest(new { error = "The import file is too large (maximum 25 MB)." });
            if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Finanzguru import accepts .xlsx files only." });

            try
            {
                await using var buffer = new MemoryStream(capacity: checked((int)file.Length));
                await file.CopyToAsync(buffer, ct);
                buffer.Position = 0;
                var result = await service.ImportAsync(currentUser.RequireUserId(), fullWorthSpaceId, buffer, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (FinanzguruImportConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (FinanzguruWorkbookException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).WithTags("Import");

        return app;
    }
}
