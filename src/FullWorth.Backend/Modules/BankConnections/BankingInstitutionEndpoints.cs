namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>Was der Hintergrunddienst nach einem erfolgreichen Katalogabruf schickt.</summary>
public sealed record BankingInstitutionCatalogWrite(IReadOnlyList<BankingInstitutionRow>? Institutions);

/// <summary>Was er nach einem gescheiterten Abruf schickt - ein bereinigter Grund, kein Rohtext.</summary>
public sealed record BankingInstitutionFailureWrite(string Reason);

/// <summary>
/// Der lokale Institutionenkatalog (#169), ausschliesslich als Maschinenpfad.
///
/// Keine Route fuer den Browser, aus demselben Grund wie beim Anbieterstatus: die Oberflaeche fragt
/// weiterhin <c>GET /api/banking/institutions</c> in FullWorth.Banking. Geaendert hat sich, woher
/// dieser Endpunkt seine Antwort nimmt - eine zweite oeffentliche Adresse fuer dasselbe Verzeichnis
/// haette nur die Frage aufgeworfen, welche von beiden die Wahrheit sagt.
/// </summary>
public static class BankingInstitutionEndpoints
{
    public static IEndpointRouteBuilder MapBankingInstitutionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/banking/institutions").WithTags("Internal banking");

        group.MapGet("/{country}", async (
            string country, BankingInstitutionStore store, CancellationToken ct) =>
            Results.Ok(await store.ReadAsync(country, ct)));

        // Welche Laender ueberhaupt gepflegt werden muessen - der Dienst soll nicht jedes Land der
        // Welt durchgehen und Anbieterlast fuer Kataloge erzeugen, die niemand ansieht.
        group.MapGet("/countries", async (BankingInstitutionStore store, CancellationToken ct) =>
            Results.Ok(new { countries = await store.CountriesToRefreshAsync(ct) }));

        group.MapPut("/{country}", async (
            string country,
            BankingInstitutionCatalogWrite request,
            BankingInstitutionStore store,
            CancellationToken ct) =>
        {
            if (country.Trim().Length != 2)
                return Results.BadRequest(new { error = "A two-letter country code is required." });

            await store.ReplaceCountryAsync(country, request.Institutions ?? [], ct);
            return Results.NoContent();
        });

        // Eigene Route, damit der Unterschied an der Adresse ablesbar ist: ein Fehlschlag ersetzt den
        // Katalog NICHT.
        group.MapPost("/{country}/failures", async (
            string country,
            BankingInstitutionFailureWrite request,
            BankingInstitutionStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
                return Results.BadRequest(new { error = "A failure reason is required." });

            await store.RecordFailureAsync(country, request.Reason.Trim(), ct);
            return Results.NoContent();
        });

        return app;
    }
}
