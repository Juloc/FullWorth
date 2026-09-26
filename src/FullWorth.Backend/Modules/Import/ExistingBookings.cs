using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Import;

/// <summary>
/// Die Buchungen eines Zielkontos, gegen die ein Import prueft, ob er eine Zeile schon hat (#131,
/// Abschnitt 6). Eine Stelle fuer den Kontoauszug- und den Tabellenimport, damit Vorschau und
/// Festschreiben dieselbe Frage nicht verschieden beantworten.
///
/// Zwei Antworten, und sie sind verschieden viel wert:
/// <list type="bullet">
/// <item><see cref="Has"/> - dieselbe Buchung: Tag, Betrag, Waehrung und Gegenpartei stimmen. Sie wird
/// nicht noch einmal gebucht.</item>
/// <item><see cref="Probably"/> - vermutlich dieselbe, aus einer anderen Quelle: Tag, Betrag und
/// Waehrung stimmen, der Name nicht. Finanzguru schreibt "Moebelhaus Beispiel GmbH", die Bank
/// "MOEBELHAUS BEISPIEL MUSTERSTADT". Das ist ein Vorschlag, keine Gewissheit - zwei echte, gleich hohe
/// Zahlungen am selben Tag gibt es auch. Die Zeile kommt deshalb nicht vorgewaehlt zur Pruefung, und
/// wer sie anhakt, bekommt sie gebucht. Als Tag zaehlt der Buchungs- ODER der Valutatag der
/// vorhandenen Buchung: Quellen nehmen mal den einen, mal den anderen.</item>
/// </list>
/// </summary>
public sealed class ExistingBookings
{
    /// <summary>Was eine vorhandene Buchung zeigt, wenn sie als "vermutlich dieselbe" genannt wird.</summary>
    public sealed record Booking(Guid AccountId, DateOnly? BookingDate, DateOnly? ValueDate, decimal Amount, string Currency,
        string? Counterparty, string? NormalizedCounterparty);

    private readonly HashSet<(Guid, string)> externalKeys = [];
    private readonly HashSet<(Guid, DateOnly, decimal, string, string?)> exact = [];
    private readonly Dictionary<(Guid, DateOnly, decimal, string), List<Booking>> byDay = [];

    internal static readonly ExistingBookings None = new();

    /// <summary>Die Buchungen dieser Konten. Die Abfrage kommt vom Store - hier wird nur gelesen.</summary>
    internal static async Task<ExistingBookings> LoadAsync(
        IQueryable<FinanceTransaction> transactions, IReadOnlyCollection<Guid> accountIds, CancellationToken ct)
    {
        var bookings = new ExistingBookings();
        if (accountIds.Count == 0) return bookings;

        var rows = await transactions
            .Where(transaction => accountIds.Contains(transaction.AccountId))
            .Select(transaction => new
            {
                transaction.AccountId,
                transaction.ExternalKey,
                transaction.BookingDate,
                transaction.ValueDate,
                transaction.Amount,
                transaction.Currency,
                transaction.Counterparty,
                transaction.NormalizedCounterparty
            })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.ExternalKey)) bookings.externalKeys.Add((row.AccountId, row.ExternalKey));
            // Dieselbe Regel wie bisher: der Tag einer Buchung ist ihr Buchungstag, sonst ihr Valutatag.
            if ((row.BookingDate ?? row.ValueDate) is { } day)
                bookings.exact.Add((row.AccountId, day, row.Amount, row.Currency, row.NormalizedCounterparty));

            var booking = new Booking(row.AccountId, row.BookingDate, row.ValueDate, row.Amount, row.Currency,
                row.Counterparty, row.NormalizedCounterparty);
            foreach (var date in new[] { row.BookingDate, row.ValueDate }.OfType<DateOnly>().Distinct())
            {
                var key = (row.AccountId, date, row.Amount, row.Currency);
                if (!bookings.byDay.TryGetValue(key, out var list)) bookings.byDay[key] = list = [];
                list.Add(booking);
            }
        }
        return bookings;
    }

    /// <summary>Ob das Quellsystem genau diese Buchungs-ID auf dem Konto schon geliefert hat.</summary>
    internal bool HasExternalKey(Guid accountId, string externalKey) => externalKeys.Contains((accountId, externalKey));

    /// <summary>Dieselbe Buchung: Tag, Betrag, Waehrung und normalisierte Gegenpartei.</summary>
    internal bool Has(Guid accountId, DateOnly date, decimal amount, string currency, string? normalizedCounterparty) =>
        exact.Contains((accountId, date, amount, currency, normalizedCounterparty));

    /// <summary>
    /// Vermutlich dieselbe Buchung aus einer anderen Quelle - oder <c>null</c>. Gilt nur, wo
    /// <see cref="Has"/> nicht gilt: eine sichere Dublette ist keine vermutliche.
    /// </summary>
    internal Booking? Probably(Guid accountId, DateOnly date, decimal amount, string currency, string? normalizedCounterparty)
    {
        if (Has(accountId, date, amount, currency, normalizedCounterparty)) return null;
        return byDay.TryGetValue((accountId, date, amount, currency), out var list) ? list[0] : null;
    }
}
