using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

/// <summary>Der Kontostand am Ende eines Tages, in der Basiswaehrung des Bereichs.</summary>
/// <param name="Date">Der Tag, dessen Ende gemeint ist.</param>
/// <param name="Amount">Die Summe ueber die Konten des Bereichs, umgerechnet mit dem Kurs DIESES Tages.</param>
/// <param name="Currency">Die Basiswaehrung.</param>
/// <param name="Incomplete">
/// Wahr, wenn zu diesem Tag mindestens ein Betrag nicht umgerechnet werden konnte. Der genannte Betrag
/// ist dann die Summe der umrechenbaren Anteile - nie 1:1 und nie 0 fuer den Rest.
/// </param>
public sealed record DailyBalancePoint(DateOnly Date, decimal Amount, string Currency, bool Incomplete);

/// <summary>
/// Der historische Tagesendstand je Konto, Gruppe oder ueber alle Konten eines Bereichs (#126).
///
/// Es gab ihn nirgends. Die Buchungsseite haette ihn aus den gerade geladenen Zeilen rechnen muessen,
/// und damit haette jede Seite der Blaetterung eine andere Wahrheit gezeigt. Er entsteht deshalb hier,
/// aus derselben Quelle wie die Vermoegenskurve: der heutige GEBUCHTE Kontostand als Anker, und von dort
/// rueckwaerts durch die gebuchten Buchungen.
///
/// Drei Regeln, die nicht verhandelbar sind:
///
/// - Vorgemerkte Buchungen (<c>Status == "PDNG"</c>) zaehlen nicht. Der Anker ist deshalb der gebuchte
///   Kontostand und nicht der bevorzugte: der bevorzugte enthaelt die Vormerkungen schon, und von ihm
///   rueckwaerts zu laufen haette jeden vergangenen Tag um genau diesen Betrag verschoben.
/// - Umgerechnet wird mit dem Kurs DES TAGES, nicht mit dem von heute. Ein Stand vom letzten Jahr ist
///   der Stand von damals.
/// - Fehlt der Kurs, ist der Tag unvollstaendig und sagt das. Kein 1:1, kein 0.
/// </summary>
public sealed class AccountBalanceHistoryStore(FullWorthDbContext db, CurrencyConverter fx)
{
    private sealed record Anchor(Guid AccountId, string Currency, decimal Amount);
    private sealed record Movement(Guid AccountId, DateOnly Date, decimal Amount, string Currency);

    /// <summary>
    /// Die Tagesendstaende von <paramref name="from"/> bis <paramref name="to"/> fuer die Konten des
    /// Bereichs, die der Benutzer sehen darf. <paramref name="accountId"/> und <paramref name="groupId"/>
    /// schraenken weiter ein; beide weggelassen heisst: alle Konten des Bereichs.
    /// </summary>
    public async Task<IReadOnlyList<DailyBalancePoint>> DailyAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly from,
        DateOnly to,
        Guid? accountId,
        Guid? groupId,
        CancellationToken ct)
    {
        if (to < from) return [];

        var baseCurrency = await db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Select(space => space.BaseCurrency)
            .SingleOrDefaultAsync(ct) ?? "EUR";

        var accounts = await db.Accounts.AsNoTracking()
            .Where(account =>
                account.FullWorthSpaceId == fullWorthSpaceId &&
                db.FullWorthSpaceMembers.Any(member =>
                    member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId) &&
                account.Owners.Any(owner => owner.UserId == userId) &&
                // Ein Konto, das als dasselbe wie ein anderes gilt, steht in keiner Summe - sonst zaehlt
                // dasselbe Geld zweimal, genau wie in der Kontenuebersicht.
                account.IncludeInNetWorth &&
                (!accountId.HasValue || account.Id == accountId.Value) &&
                (!groupId.HasValue || account.GroupId == groupId.Value))
            .Select(account => account.Id)
            .ToListAsync(ct);
        if (accounts.Count == 0) return [];

        // Der Anker: der gebuchte Kontostand, wie er heute dasteht - je Konto UND Waehrung, denn eine
        // Geldboerse (PayPal, Wise) haelt mehrere.
        var balanceRows = await CurrentBalances.LoadRowsAsync(db, [.. accounts], ct);
        var anchors = CurrentBalances.PickBooked(balanceRows)
            .Select(balance => new Anchor(balance.AccountId, balance.Currency.ToUpperInvariant(), balance.Amount))
            .ToList();
        if (anchors.Count == 0) return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (to > today) to = today;
        if (from > to) return [];

        var anchored = anchors.Select(anchor => anchor.AccountId).Distinct().ToHashSet();
        var movementRows = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                anchored.Contains(transaction.AccountId) &&
                transaction.Status != "PDNG" &&
                transaction.UseForBalanceHistory &&
                (transaction.BookingDate != null || transaction.ValueDate != null))
            .Select(transaction => new
            {
                transaction.AccountId,
                transaction.BookingDate,
                transaction.ValueDate,
                transaction.Amount,
                transaction.Currency
            })
            .ToListAsync(ct);
        var movements = movementRows
            .Select(row => new Movement(
                row.AccountId,
                row.BookingDate ?? row.ValueDate!.Value,
                row.Amount,
                (row.Currency ?? baseCurrency).ToUpperInvariant()))
            .Where(row => row.Date <= today && row.Date > from)
            .ToList();

        // Rueckwaerts: der Stand am Ende von D ist der Stand am Ende von D+1 minus alles, was an D+1
        // gebucht wurde. Der Schluessel ist (Konto, Waehrung) - eine Buchung in USD verschiebt den
        // USD-Stand des Kontos, nicht seinen EUR-Stand.
        var running = anchors.ToDictionary(
            anchor => (anchor.AccountId, anchor.Currency),
            anchor => anchor.Amount);
        var movementsByDay = movements
            .GroupBy(movement => movement.Date)
            .ToDictionary(group => group.Key, group => group.ToList());

        var snapshot = await fx.PrepareAsync(baseCurrency, from, to, ct);
        var backwards = new List<DailyBalancePoint>();
        for (var day = today; day >= from; day = day.AddDays(-1))
        {
            if (day <= to) backwards.Add(Point(day, running, snapshot, baseCurrency));

            if (!movementsByDay.TryGetValue(day, out var ofDay)) continue;
            foreach (var movement in ofDay)
            {
                var key = (movement.AccountId, movement.Currency);
                // Eine Buchung in einer Waehrung, zu der es keinen Anker gibt, faengt bei 0 an. Das ist
                // kein erfundener Wert: sie hat den Stand dieses Tages nachweislich bewegt.
                running[key] = running.GetValueOrDefault(key) - movement.Amount;
            }
        }

        backwards.Reverse();
        return backwards;
    }

    private static DailyBalancePoint Point(
        DateOnly day,
        Dictionary<(Guid AccountId, string Currency), decimal> running,
        FxSnapshot snapshot,
        string baseCurrency)
    {
        var sum = 0m;
        var incomplete = false;
        foreach (var ((_, currency), amount) in running)
        {
            var converted = snapshot.ToBaseOn(amount, currency, day);
            if (converted is null)
            {
                // Genau hier wurde frueher still 1:1 gerechnet. Der Tag sagt jetzt, dass ihm etwas fehlt.
                if (amount != 0m) incomplete = true;
                continue;
            }
            sum += converted.Value;
        }
        return new DailyBalancePoint(day, sum, baseCurrency, incomplete);
    }
}
