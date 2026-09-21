using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>Eine Zeile des Gesundheitsfeeds, wie sie ueber die Leitung geht.</summary>
public sealed record BankingProviderStatusRow(string Country, string Brand, string PsuType, string Status);

/// <summary>
/// Was die Oberflaeche ueber den Anbieterzustand erfaehrt.
///
/// <paramref name="Known"/> unterscheidet "es wurde noch nie erfolgreich geprueft" von "geprueft und
/// alles in Ordnung". Ohne diese Angabe waere eine leere Liste zweideutig, und der Bankdialog wuerde
/// nach einer frischen Installation jede Bank als gesund ausgeben, obwohl niemand nachgesehen hat.
/// </summary>
public sealed record BankingProviderStatusSnapshot(
    bool Known,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    IReadOnlyList<BankingProviderStatusRow> Statuses);

/// <summary>
/// Der lokale Zwischenspeicher des Enable-Banking-Gesundheitsfeeds (#165).
///
/// Die eine Regel, die diese Datei tragen muss: <see cref="RecordFailureAsync"/> laesst die Zeilen
/// unberuehrt. Ein Ausfall des Control Panels ist keine Aussage ueber die Banken - wuerde er den
/// letzten guten Stand loeschen, machte ein zweiminuetiger Anbieterausfall aus "alle erreichbar" ein
/// "Zustand unbekannt", und die Bankauswahl waere genau so kaputt wie vorher, nur langsamer im
/// Kaputtgehen.
/// </summary>
public sealed class BankingProviderStatusStore(FullWorthDbContext db)
{
    public async Task<BankingProviderStatusSnapshot> ReadAsync(string? country, CancellationToken ct)
    {
        var refresh = await db.BankingProviderStatusRefreshes.AsNoTracking()
            .FirstOrDefaultAsync(row => row.ScopeKey == BankingProviderStatusRefresh.InstanceScopeKey, ct);

        var query = db.BankingProviderStatuses.AsNoTracking();
        var normalized = Normalize(country);
        if (normalized is not null) query = query.Where(row => row.Country == normalized);

        var rows = await query
            .OrderBy(row => row.Brand).ThenBy(row => row.PsuType)
            .Select(row => new BankingProviderStatusRow(row.Country, row.Brand, row.PsuType, row.Status))
            .ToListAsync(ct);

        return new BankingProviderStatusSnapshot(
            // "Bekannt" haengt am erfolgreichen Abruf, nicht daran, ob fuer DIESES Land Zeilen da sind:
            // ein Land, das der Anbieter nicht bedient, ist geprueft und leer - nicht ungeprueft.
            Known: refresh?.LastSuccessfulAt is not null,
            LastSuccessfulAt: refresh?.LastSuccessfulAt,
            LastAttemptAt: refresh?.LastAttemptAt,
            LastError: refresh?.LastError,
            Statuses: rows);
    }

    /// <summary>
    /// Den ganzen Feed ersetzen. Ersetzen und nicht zusammenfuehren ist hier richtig: ein Institut,
    /// das der Anbieter nicht mehr meldet, soll verschwinden und nicht mit einem Zustand von vorletzter
    /// Woche stehen bleiben. Der Aufrufer ruft das ausschliesslich nach einem ERFOLGREICHEN Abruf.
    /// </summary>
    public async Task ReplaceAsync(IReadOnlyList<BankingProviderStatusRow> rows, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var wanted = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Country) && !string.IsNullOrWhiteSpace(row.Brand))
            .Select(row => new BankingProviderStatus
            {
                Country = Normalize(row.Country) ?? string.Empty,
                Brand = row.Brand.Trim(),
                PsuType = (row.PsuType ?? string.Empty).Trim(),
                Status = (row.Status ?? string.Empty).Trim(),
                UpdatedAt = now
            })
            // Der Feed darf dasselbe Institut zweimal nennen; der eindeutige Index vertraegt das nicht.
            .GroupBy(row => (row.Country, row.Brand, row.PsuType))
            .Select(group => group.Last())
            .ToList();

        var existing = await db.BankingProviderStatuses.ToListAsync(ct);
        var byKey = existing.ToDictionary(row => (row.Country, row.Brand, row.PsuType));

        foreach (var row in wanted)
        {
            if (byKey.Remove((row.Country, row.Brand, row.PsuType), out var current))
            {
                current.Status = row.Status;
                current.UpdatedAt = now;
            }
            else db.BankingProviderStatuses.Add(row);
        }

        db.BankingProviderStatuses.RemoveRange(byKey.Values);

        var refresh = await Refresh(ct);
        refresh.LastAttemptAt = now;
        refresh.LastSuccessfulAt = now;
        refresh.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Einen gescheiterten Versuch vermerken - und sonst nichts. Die Zeilen bleiben, wie sie sind,
    /// samt ihrem <c>LastSuccessfulAt</c>: daran sieht die Oberflaeche, wie alt die Angaben sind, und
    /// kann sie als veraltet kennzeichnen, statt sie zu verlieren.
    /// </summary>
    public async Task RecordFailureAsync(string reason, CancellationToken ct)
    {
        var refresh = await Refresh(ct);
        refresh.LastAttemptAt = DateTimeOffset.UtcNow;
        refresh.LastError = reason.Length > 200 ? reason[..200] : reason;
        await db.SaveChangesAsync(ct);
    }

    private async Task<BankingProviderStatusRefresh> Refresh(CancellationToken ct)
    {
        var refresh = await db.BankingProviderStatusRefreshes
            .FirstOrDefaultAsync(row => row.ScopeKey == BankingProviderStatusRefresh.InstanceScopeKey, ct);
        if (refresh is not null) return refresh;

        refresh = new BankingProviderStatusRefresh();
        db.BankingProviderStatusRefreshes.Add(refresh);
        return refresh;
    }

    private static string? Normalize(string? country)
    {
        var trimmed = country?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }
}
