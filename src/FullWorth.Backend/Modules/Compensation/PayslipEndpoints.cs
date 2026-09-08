using FullWorth.Backend.Data;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Compensation;

public static class PayslipEndpoints
{
    public static IEndpointRouteBuilder MapCompensationPayslipEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/compensation/payslips").WithTags("Compensation");

        group.MapPost("/extract", async (IFormFile file, CurrentUserContext currentUser, PayslipCodexExtractor codex, CancellationToken ct) =>
        {
            var userId = currentUser.RequireUserId();
            try
            {
                return Results.Ok(await ExtractOneAsync(file, userId, codex, ct));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        group.MapPost("/extract-batch", async (HttpRequest request, CurrentUserContext currentUser, PayslipCodexExtractor codex, CancellationToken ct) =>
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
                    items.Add(new PayslipBatchItem(name, await ExtractOneAsync(file, userId, codex, ct), null));
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

        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new PayslipStore(db);
            var payslips = await store.ListAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return payslips is null ? Results.NotFound() : Results.Ok(payslips);
        });

        group.MapGet("/latest-delta", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new PayslipStore(db);
            var delta = await store.GetLatestDeltaAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return delta is null ? Results.NoContent() : Results.Ok(delta);
        });

        group.MapPost("/", async (Guid fullWorthSpaceId, PayslipRecordWrite request, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            try
            {
                var store = new PayslipStore(db);
                var saved = await store.SaveAsync(currentUser.RequireUserId(), fullWorthSpaceId, request, ct);
                return saved is null ? Results.NotFound() : Results.Created($"/api/compensation/payslips/{saved.Id}?fullWorthSpaceId={fullWorthSpaceId}", saved);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, FullWorthDbContext db, CancellationToken ct) =>
        {
            var store = new PayslipStore(db);
            var deleted = await store.DeleteAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return deleted switch
            {
                null => Results.NotFound(),
                true => Results.NoContent(),
                false => Results.NotFound()
            };
        });

        return app;
    }

    // OCR once, then let Codex structure the text; fall back to the deterministic regex parser whenever Codex
    // is unavailable or returns nothing usable, so a payslip is never partially interpreted as complete.
    private static async Task<PayslipExtractionResult> ExtractOneAsync(
        IFormFile file, Guid userId, PayslipCodexExtractor codex, CancellationToken ct)
    {
        var text = await PayslipExtractor.OcrAsync(file, ct);
        if (string.IsNullOrWhiteSpace(text))
            return PayslipTextParser.Empty("OCR konnte keinen Text erkennen.");
        return await codex.TryStructureAsync(userId, text, ct) ?? PayslipTextParser.Parse(text);
    }
}
