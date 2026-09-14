using System.Text;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Architecture;

/// <summary>
/// Was der Server nach außen kann, Zeile für Zeile — gelesen aus der laufenden Anwendung, nicht aus
/// dem Quelltext geraten.
///
/// Das ist das Werkzeug für die drei Umbauten, die anstehen (#110, #111, #113). Alle drei verschieben
/// Code zwischen Ordnern, und alle drei dürfen dabei genau eines nicht: die Fläche verändern. Eine
/// Route, die beim Verschieben verschwindet, fällt sonst erst auf, wenn jemand sie aufruft — und bei
/// 648 Endpunkten ruft niemand alle auf.
///
/// Verglichen wird die Adresse und die Methode. Nicht verglichen wird, in welcher Datei sie steht —
/// das ist ja der Zweck der Übung.
///
/// Die Anmeldepflicht stand hier einmal als dritte Spalte und ist wieder verschwunden, weil sie nichts
/// gesagt hätte: 640 von 641 Endpunkten meldeten „anonym". Das ist kein Fehler des Tests und auch kein
/// Testartefakt, sondern Absicht des Produkts — <c>BackendApplication</c> markiert die ganze Gruppe
/// <c>AllowAnonymous</c>, weil die Autorisierung hier in der Backend-Middleware sitzt (interner
/// Schlüssel plus Benutzerkontext) und nicht in den Endpunkt-Metadaten. Eine Spalte, die immer
/// denselben Wert trägt, fängt nie etwas. Wer die Autorisierung prüfen will, findet sie in den
/// Autorisierungs-Integrationstests, nicht hier.
///
/// Neu aufnehmen: <c>FULLWORTH_WRITE_ROUTE_SURFACE=1</c> setzen und den Test laufen lassen. Die Datei
/// gehört dann in denselben Commit wie die Änderung, damit im Diff steht, was sich an der Fläche
/// wirklich getan hat.
/// </summary>
public sealed class RouteSurfaceTests
{
    private static string SnapshotPath => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Architecture", "route-surface.txt");

    [Fact]
    public async Task The_route_surface_is_what_it_was()
    {
        await using var factory = new BackendWebApplicationFactory();
        // Der Host entsteht erst beim ersten Zugriff; ohne das ist die Endpunktquelle leer.
        _ = factory.Services;

        var aktuell = Flaeche(factory.Services);
        var pfad = Path.GetFullPath(SnapshotPath);

        if (Environment.GetEnvironmentVariable("FULLWORTH_WRITE_ROUTE_SURFACE") == "1" || !File.Exists(pfad))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pfad)!);
            await File.WriteAllLinesAsync(pfad, aktuell);
            Assert.Fail($"Die Routenaufnahme wurde neu geschrieben ({aktuell.Length} Zeilen): {pfad}. "
                + "Bitte den Unterschied im Diff ansehen und den Test erneut laufen lassen.");
        }

        var frueher = await File.ReadAllLinesAsync(pfad);
        var verschwunden = frueher.Except(aktuell, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var neu = aktuell.Except(frueher, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var meldung = new StringBuilder();
        if (verschwunden.Length > 0)
            meldung.AppendLine("VERSCHWUNDEN - das merkt ein Aufrufer sofort:")
                .AppendLine("  " + string.Join(Environment.NewLine + "  ", verschwunden));
        if (neu.Length > 0)
            meldung.AppendLine("NEU:")
                .AppendLine("  " + string.Join(Environment.NewLine + "  ", neu));

        Assert.True(meldung.Length == 0,
            "Die Routenfläche hat sich geändert. Wenn das gewollt war, mit "
            + "FULLWORTH_WRITE_ROUTE_SURFACE=1 neu aufnehmen und die Datei mitcommitten."
            + Environment.NewLine + meldung);
    }

    /// <summary>
    /// Adresse und Methode, sortiert — damit die Datei sich nur ändert, wenn sich die Fläche ändert,
    /// und nicht, wenn jemand eine Registrierung eine Zeile höher schiebt.
    ///
    /// Ausgelassen wird, was es nur in der Testumgebung gibt: <c>BackendApplication</c> mappt dort
    /// <c>/api/__test/current-user-context</c> zusätzlich. Das gehört nicht zur Fläche des Produkts,
    /// und in der Aufnahme wäre es eine Zeile, die niemand ausliefert.
    /// </summary>
    private static string[] Flaeche(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpunkt => endpunkt.RoutePattern.RawText?.Contains("/__test/", StringComparison.Ordinal) != true)
            .Select(endpunkt =>
            {
                var roh = endpunkt.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                var methoden = roh is null || roh.Count == 0
                    ? new[] { "*" }
                    : roh.Order(StringComparer.Ordinal).ToArray();
                return $"{string.Join(',', methoden),-16} {endpunkt.RoutePattern.RawText}";
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
