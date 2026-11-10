using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

public static class PayslipEndpoints
{
    public static IEndpointRouteBuilder MapCompensationPayslipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation/payslips").WithTags("Compensation");

        group.MapPost("/extract", async (IFormFile file, CurrentUserContext currentUser, PayslipExtractionService extraction, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            try
            {
                return Results.Ok(await ExtractOneAsync(file, userId, extraction, ct));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        group.MapPost("/extract-batch", async (HttpRequest request, CurrentUserContext currentUser, PayslipExtractionService extraction, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Formulardaten mit Dateien werden erwartet." });

            var form = await request.ReadFormAsync(ct);
            var files = form.Files;
            if (files.Count == 0) return Results.BadRequest(new { error = "Keine Lohnabrechnungen ausgewählt." });
            if (files.Count > 40) return Results.BadRequest(new { error = "Höchstens 40 Abrechnungen pro Durchlauf." });

            var items = new List<PayslipBatchItem>(files.Count);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file.FileName);
                try
                {
                    items.Add(new PayslipBatchItem(name, await ExtractOneAsync(file, userId, extraction, ct), null));
                }
                catch (ArgumentException exception)
                {
                    items.Add(new PayslipBatchItem(name, null, exception.Message));
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    items.Add(new PayslipBatchItem(name, null, "Die Abrechnung konnte nicht verarbeitet werden."));
                }
            }
            return Results.Ok(new PayslipBatchResult(items));
        }).DisableAntiforgery();

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, PayslipStore store, CancellationToken ct) =>
        {
            var payslips = await store.ListAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return payslips is null ? Results.NotFound() : Results.Ok(payslips);
        });

        group.MapGet("/latest-delta", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, PayslipStore store, CancellationToken ct) =>
        {
            var delta = await store.GetLatestDeltaAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return delta is null ? Results.NoContent() : Results.Ok(delta);
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, Guid? importId, PayslipRecordWrite request, CurrentUserContext currentUser, PayslipStore store, CancellationToken ct) =>
        {
            try
            {
                var saved = await store.SaveAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, importId, ct);
                return saved is null ? Results.NotFound() : Results.Created($"/api/compensation/payslips/{saved.Id}?fullWorthSpaceId={fullWorthSpaceId}", saved);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, DocumentImportService imports, CancellationToken ct) =>
        {
            var deleted = await imports.DeletePayslipAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return deleted switch
            {
                null => Results.NotFound(),
                true => Results.NoContent(),
                false => Results.NotFound()
            };
        });

        return app;
    }

    private static async Task<PayslipExtractionResult> ExtractOneAsync(
        IFormFile file, Guid userId, PayslipExtractionService extraction, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        return await extraction.ExtractAsync(userId, stream, file.FileName, ct);
    }
}
