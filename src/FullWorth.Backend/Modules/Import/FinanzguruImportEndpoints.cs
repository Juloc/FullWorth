using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Import;

/// <summary>Welche Zeilen der Vorschau uebernommen werden sollen. Leer heisst: alle neuen.</summary>
public sealed record FinanzguruStageCommitRequest(IReadOnlyList<Guid>? CandidateIds);

public static class FinanzguruImportEndpoints
{
    private const long MaxUploadBytes = 25L * 1024 * 1024;

    /// <summary>Die Datei einmal lesen und pruefen - fuer beide Wege dieselben Schranken.</summary>
    private static async Task<(MemoryStream? Content, string? FileName, string? Error)> ReadWorkbookAsync(
        HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
            return (null, null, "Expected multipart/form-data with an .xlsx file.");
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return (null, null, "No file was uploaded.");
        if (file.Length > MaxUploadBytes) return (null, null, "The import file is too large (maximum 25 MB).");
        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return (null, null, "Finanzguru import accepts .xlsx files only.");

        var buffer = new MemoryStream(capacity: checked((int)file.Length));
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return (buffer, file.FileName, null);
    }

    /// <summary>
    /// Die Vermoegenshistorie wird hier NICHT angestossen.
    ///
    /// Zwei Handler riefen nach dem Verknuepfen <c>RebuildHistoryForUserAsync</c> auf, und das war
    /// doppelt und gefaehrlich zugleich.
    ///
    /// Doppelt, weil das Verknuepfen vermoegenswirksame Daten committet und der SaveChanges-Interceptor
    /// den <c>FinancialDataConsistencyCoordinator</c> schon ueber den ganzen Space hat laufen lassen,
    /// bevor die Zeile ueberhaupt drankam.
    ///
    /// Gefaehrlich, weil dieser Koordinator ein Singleton mit einem Semaphor ist und seine Laeufe
    /// serialisiert - der Aufruf von hier lief daran vorbei. Der Neuaufbau liest die vorhandenen
    /// Snapshots, ergaenzt die fehlenden und speichert; zwei solche Laeufe nebeneinander sehen beide
    /// "heute fehlt" und legen beide an:
    ///
    /// <code>
    /// duplicate key value violates unique constraint
    ///   "IX_NetWorthSnapshots_FullWorthSpaceId_UserId_Date_Currency"
    /// </code>
    ///
    /// Der Koordinator ist damit der einzige Weg zur Historie. Wer hier einen zweiten aufmacht, macht
    /// denselben Fehler.
    /// </summary>
    public static IEndpointRouteBuilder MapFinanzguruImportEndpoints(this IEndpointRouteBuilder app)
    {
        // Was das Zuordnen tun WUERDE, bevor es etwas tut. Ohne diese Liste war es ein Knopf, nach dem
        // Buchungen verschwunden waren, ohne dass jemand vorher sagen konnte, welche.
        app.MapGet("/api/import/finanzguru/accounts/{importAccountId:guid}/link-preview", async (
            Guid importAccountId,
            Guid fullWorthSpaceId,
            Guid targetAccountId,
            CurrentUserContext currentUser,
            FinanzguruAccountReconciliationService reconciliation,
            CancellationToken ct) =>
        {
            var preview = await reconciliation.PreviewLinkAsync(
                currentUser.RequireUserId(), fullWorthSpaceId, importAccountId, targetAccountId, ct);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        }).WithTags("Import");

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
                    ct,
                    request.PreferImport,
                    request.ExcludedImportTransactionIds?.ToHashSet());
                if (result is null) return Results.NotFound();

                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).WithTags("Import");

        // Der Zwischenschritt (#131, Schritt 4): lesen, sagen was passieren wuerde, nichts schreiben.
        //
        // /api/import/finanzguru bleibt daneben bestehen und macht beides in einem Zug. Das ist kein
        // zweiter Weg, sondern derselbe ohne Halt: beide landen in ImportRowsAsync. Wer ihn
        // abschaffen will, muss vorher jeden Aufrufer auf die zwei Schritte umstellen - die
        // Oberflaeche ist einer davon, die Tests sind die anderen.
        app.MapPost("/api/import/finanzguru/stage", async (
            Guid fullWorthSpaceId,
            HttpRequest request,
            CurrentUserContext currentUser,
            FinanzguruStagingService staging,
            CancellationToken ct) =>
        {
            var file = await ReadWorkbookAsync(request, ct);
            if (file.Error is not null) return Results.BadRequest(new { error = file.Error });

            try
            {
                await using var buffer = file.Content!;
                var preview = await staging.StageAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, buffer, file.FileName!, ct);
                return preview is null ? Results.NotFound() : Results.Ok(preview);
            }
            catch (FinanzguruWorkbookException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).WithTags("Import");

        app.MapPost("/api/import/finanzguru/jobs/{jobId:guid}/commit", async (
            Guid jobId,
            Guid fullWorthSpaceId,
            FinanzguruStageCommitRequest? body,
            CurrentUserContext currentUser,
            FinanzguruStagingService staging,
            CancellationToken ct) =>
        {
            try
            {
                var result = await staging.CommitAsync(
                    currentUser.RequireUserId(), fullWorthSpaceId, jobId, body?.CandidateIds, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (FinanzguruImportConflictException exception)
            {
                return Results.Conflict(new { error = exception.Message });
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
