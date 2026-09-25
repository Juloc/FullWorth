using FullWorth.Backend.Documents;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Ein Schritt vor allen Importwegen: welche Quelle ist das ueberhaupt (#131, Schritt 2).
///
/// Die Antwort schreibt nichts - sie liest die Datei und sagt, welcher Weg sie verarbeiten kann. Die
/// Oberflaeche schickt dieselbe Datei danach an genau diesen Weg; sie liegt ohnehin noch im Browser,
/// also kostet das kein zweites Hochladen und der Nutzer waehlt sie kein zweites Mal aus.
/// </summary>
public static class ImportDetectionEndpoints
{
    /// <summary>Dieselbe Obergrenze wie an jedem Importweg - eine Datei, die dort zu gross ist, muss hier nicht gelesen werden.</summary>
    private const long MaxUploadBytes = 25L * 1024 * 1024;

    public static IEndpointRouteBuilder MapImportDetectionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/import/detect", Detect).WithTags("Import");
        return app;
    }

    private static async Task<IResult> Detect(
        Guid fullWorthSpaceId, HttpRequest request, CurrentUserContext currentUser, SpaceAccess space,
        IPdfWordSource pdf, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        // Erkennen heisst die Datei lesen. Wer hier nicht importieren duerfte, darf sie auch nicht
        // gelesen bekommen - dieselbe Berechtigung wie am eigentlichen Import.
        if (!await space.HasCapabilityAsync(userId, fullWorthSpaceId, "transactions.write", ct))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart/form-data." });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No file uploaded." });
        if (file.Length > MaxUploadBytes) return Results.BadRequest(new { error = "Maximum file size is 25 MB." });

        await using var buffer = new MemoryStream(checked((int)file.Length));
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        // Ein PDF, das sich nicht lesen laesst, ist hier keine Fehlermeldung: die Erkennung sagt dann
        // "Broker-PDF", und der Depot-Weg meldet sich selbst, wenn auch er es nicht lesen kann.
        IReadOnlyList<IReadOnlyList<PdfLine>>? pages = null;
        if (BankStatementPdf.IsPdf(bytes))
        {
            try { pages = await pdf.ReadLinesAsync(bytes, ct); }
            catch (PdfWordsException) { }
        }
        return Results.Ok(ImportSourceDetector.Detect(file.FileName, bytes, pages));
    }
}
