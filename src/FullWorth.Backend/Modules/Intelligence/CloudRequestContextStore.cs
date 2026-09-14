using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Was eine Cloud-Anfrage ueber den Space wissen muss, bevor sie hinausgeht.
///
/// Bisher genau eine Frage - in welchem Land dieser Haushalt lebt -, und die stand dreimal
/// gleichlautend im Code: in CloudPriceEndpoints, CloudBenchmarkEndpoints und
/// CloudMerchantBenchmarkEndpoints. Eine kopierte Abfrage wird irgendwann nur an einer der Kopien
/// korrigiert, und diese hier entscheidet, welche Vergleichsdaten der Benutzer sieht.
/// </summary>
public sealed class CloudRequestContextStore(FullWorthDbContext db)
{
    /// <summary>
    /// Das Land des Space, abgeleitet aus seinen Bankverbindungen.
    ///
    /// Bei mehr als einem Land kommt nichts zurueck, und das ist Absicht: ein Haushalt mit einem
    /// deutschen und einem oesterreichischen Konto hat keine eindeutige Vergleichsgruppe, und eine
    /// geratene ist schlechter als keine.
    /// </summary>
    public async Task<string?> SpaceCountryAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var countries = (await db.BankConnections.AsNoTracking()
                .Where(connection => connection.FullWorthSpaceId == fullWorthSpaceId
                                  && connection.Country != null
                                  && connection.Country != "")
                .Select(connection => connection.Country)
                .Distinct()
                .Take(3)
                .ToListAsync(ct))
            .Select(NormalizeCountry)
            .Where(country => country is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return countries.Count == 1 ? countries[0] : null;
    }

    /// <summary>Ein Laendercode ist zwei Buchstaben. Alles andere ist kein Land.</summary>
    public static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 2 && normalized.All(char.IsAsciiLetter) ? normalized : null;
    }

    /// <summary>Eine Waehrung ist drei Buchstaben. Alles andere ist keine.</summary>
    public static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 3 && normalized.All(char.IsAsciiLetter) ? normalized : null;
    }
}
