namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>Was der Hintergrunddienst nach einem erfolgreichen Abruf schickt.</summary>
public sealed record BankingProviderStatusWrite(IReadOnlyList<BankingProviderStatusRow>? Statuses);

/// <summary>Was er nach einem gescheiterten Abruf schickt - ein bereinigter Grund, kein Rohtext.</summary>
public sealed record BankingProviderStatusFailureWrite(string Reason);

/// <summary>
/// Der lokale Anbieterzustand (#165), ausschliesslich als Maschinenpfad.
///
/// Hier gibt es bewusst KEINE Route fuer den Browser. Die Oberflaeche fragt weiterhin
/// <c>GET /api/banking/provider-status</c> in FullWorth.Banking - was sich geaendert hat, ist, dass
/// dieser Endpunkt seine Antwort jetzt von hier holt statt vom Enable-Banking-Control-Panel. Eine
/// zweite oeffentliche Adresse fuer dieselbe Auskunft haette nur die Frage aufgeworfen, welche von
/// beiden die Wahrheit sagt.
/// </summary>
public static class BankingProviderStatusEndpoints
{
    public static IEndpointRouteBuilder MapBankingProviderStatusEndpoints(this IEndpointRouteBuilder app)
    {
        // Authentifizierung fuer /internal/** wird getrennt erzwungen, wie beim Ingest-Pfad daneben.
        var group = app.MapGroup("/internal/banking/provider-status").WithTags("Internal banking");

        group.MapGet("/", async (string? country, BankingProviderStatusStore store, CancellationToken ct) =>
            Results.Ok(await store.ReadAsync(country, ct)));

        group.MapPut("/", async (
            BankingProviderStatusWrite request, BankingProviderStatusStore store, CancellationToken ct) =>
        {
            await store.ReplaceAsync(request.Statuses ?? [], ct);
            return Results.NoContent();
        });

        // Eigene Route statt eines Feldes im PUT: ein Fehlschlag ersetzt die Zeilen NICHT, und dieser
        // Unterschied soll an der Adresse ablesbar sein und nicht an einem Schalter im Rumpf, den man
        // vergessen kann.
        group.MapPost("/failures", async (
            BankingProviderStatusFailureWrite request, BankingProviderStatusStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
                return Results.BadRequest(new { error = "A failure reason is required." });

            await store.RecordFailureAsync(request.Reason.Trim(), ct);
            return Results.NoContent();
        });

        return app;
    }
}
