using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Merchants;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>
/// Die Datenseite des Umbuchungs-Gedaechtnisses (#146).
///
/// Gelernt wird nur aus einer BESTAETIGUNG - der Benutzer verknuepft zwei Buchungen oder sagt
/// ausdruecklich "extern, kein Gegenkonto". Aus einer automatisch erkannten Umbuchung wird nichts
/// gelernt; sonst schriebe die Mechanik ihre eigenen Treffer als Regel fest, und ein einmaliger
/// Fehltreffer waere von da an eine Regel.
/// </summary>
public sealed class TransferRuleStore(FullWorthDbContext db)
{
    /// <summary>
    /// Was der Benutzer bestaetigt hat, als Regel festhalten. Beide Richtungen einer Verknuepfung
    /// werden gelernt: eine Umbuchung sieht von jedem der zwei Konten aus anders aus, und wer sie
    /// nur einmal lernt, erkennt die Gegenseite beim naechsten Mal wieder nicht.
    /// </summary>
    public async Task LearnFromLinkAsync(
        Guid userId, Guid fullWorthSpaceId, FinanceTransaction first, FinanceTransaction second, CancellationToken ct)
    {
        await RememberAsync(userId, fullWorthSpaceId, first, second.AccountId, ct);
        await RememberAsync(userId, fullWorthSpaceId, second, first.AccountId, ct);
    }

    /// <summary>
    /// Eine Umbuchung ohne FullWorth-Gegenkonto. <c>TargetAccountId</c> bleibt NULL, und das ist die
    /// Aussage: das Ziel existiert, es wird hier nur nicht gefuehrt.
    /// </summary>
    public Task LearnExternalAsync(
        Guid userId, Guid fullWorthSpaceId, FinanceTransaction transaction, CancellationToken ct) =>
        RememberAsync(userId, fullWorthSpaceId, transaction, null, ct);

    private async Task RememberAsync(
        Guid userId, Guid fullWorthSpaceId, FinanceTransaction transaction, Guid? targetAccountId, CancellationToken ct)
    {
        var merchant = MerchantNormalization.Normalize(
            transaction.NormalizedCounterparty ?? transaction.Counterparty);
        // Ohne Gegenpartei gibt es nichts wiederzuerkennen. Eine Regel, die auf jede Buchung des
        // Kontos passt, waere schlimmer als keine.
        if (string.IsNullOrWhiteSpace(merchant)) return;

        var direction = transaction.Amount < 0m ? "expense" : "income";
        var existing = await db.TransferRules.SingleOrDefaultAsync(rule =>
            rule.FullWorthSpaceId == fullWorthSpaceId &&
            rule.AccountId == transaction.AccountId &&
            rule.NormalizedCounterparty == merchant &&
            rule.Direction == direction, ct);

        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            db.TransferRules.Add(new TransferRule
            {
                FullWorthSpaceId = fullWorthSpaceId,
                AccountId = transaction.AccountId,
                NormalizedCounterparty = merchant,
                Direction = direction,
                TargetAccountId = targetAccountId,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            });
            return;
        }

        // Die juengere Entscheidung gilt. Wer erst zu Konto A verknuepft und spaeter zu Konto B, hat
        // seine Meinung geaendert - die Regel ist sein Gedaechtnis, nicht ihr eigenes.
        existing.TargetAccountId = targetAccountId;
        existing.CreatedByUserId = userId;
        existing.UpdatedAt = now;
    }

    /// <summary>Alle Regeln eines Space, mit den Kontonamen - fuer die Oberflaeche.</summary>
    public Task<List<TransferRuleView>> ListAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.TransferRules.AsNoTracking()
            .Where(rule => rule.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(rule => rule.NormalizedCounterparty)
            .Select(rule => new TransferRuleView(
                rule.Id,
                rule.AccountId,
                db.Accounts.Where(account => account.Id == rule.AccountId)
                    .Select(account => account.DisplayName).FirstOrDefault() ?? string.Empty,
                rule.NormalizedCounterparty,
                rule.Direction,
                rule.TargetAccountId,
                rule.TargetAccountId == null
                    ? null
                    : db.Accounts.Where(account => account.Id == rule.TargetAccountId)
                        .Select(account => account.DisplayName).FirstOrDefault(),
                rule.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>
    /// Eine Regel loeschen. Sie muss loeschbar sein: eine gelernte Regel, die man nicht mehr los
    /// wird, ist eine Entscheidung, die der Benutzer einmal getroffen hat und nie zuruecknehmen kann.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid fullWorthSpaceId, Guid ruleId, CancellationToken ct)
    {
        var rule = await db.TransferRules.SingleOrDefaultAsync(
            item => item.Id == ruleId && item.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (rule is null) return false;
        db.TransferRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Die Regel zu einer Buchung, falls es eine gibt. Genau hier greift das Gedaechtnis: die
    /// Erkennung fragt danach, bevor sie ihre eigene Mechanik bemueht.
    /// </summary>
    public async Task<TransferRule?> FindAsync(
        Guid fullWorthSpaceId, Guid accountId, string? counterparty, decimal amount, CancellationToken ct)
    {
        var merchant = MerchantNormalization.Normalize(counterparty);
        if (string.IsNullOrWhiteSpace(merchant)) return null;
        var direction = amount < 0m ? "expense" : "income";

        return await db.TransferRules.AsNoTracking().SingleOrDefaultAsync(rule =>
            rule.FullWorthSpaceId == fullWorthSpaceId &&
            rule.AccountId == accountId &&
            rule.NormalizedCounterparty == merchant &&
            rule.Direction == direction, ct);
    }

    /// <summary>
    /// Buchungen, die zu einer Regel fuer ein EXTERNES Ziel passen und noch nicht als Umbuchung
    /// markiert sind - die Vorschlagsliste.
    ///
    /// Bewusst ein Vorschlag und keine automatische Aenderung: eine Umbuchung faellt aus jeder
    /// Auswertung heraus. Das ist die richtige Folge, wenn es eine ist, und ein stiller Verlust,
    /// wenn nicht.
    /// </summary>
    public async Task<List<FinanceTransaction>> ExternalCandidatesAsync(
        Guid fullWorthSpaceId, IReadOnlySet<Guid> visibleAccounts, int limit, CancellationToken ct)
    {
        var rules = await db.TransferRules.AsNoTracking()
            .Where(rule => rule.FullWorthSpaceId == fullWorthSpaceId && rule.TargetAccountId == null)
            .Select(rule => new { rule.AccountId, rule.NormalizedCounterparty, rule.Direction })
            .ToListAsync(ct);
        if (rules.Count == 0) return [];

        var accountIds = rules.Select(rule => rule.AccountId).Distinct().Where(visibleAccounts.Contains).ToArray();
        if (accountIds.Length == 0) return [];
        var merchants = rules.Select(rule => rule.NormalizedCounterparty).Distinct().ToArray();

        var candidates = await db.Transactions.AsNoTracking()
            .Where(transaction =>
                accountIds.Contains(transaction.AccountId) &&
                !transaction.IsTransfer &&
                !transaction.IsIgnored &&
                transaction.NormalizedCounterparty != null &&
                merchants.Contains(transaction.NormalizedCounterparty))
            .OrderByDescending(transaction => transaction.BookingDate)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

        // Die Regel gilt je Konto UND Richtung; die Abfrage oben kann das nicht ausdruecken, ohne je
        // Regel eine eigene zu werden. Der letzte Schritt ist deshalb hier - auf einer Liste, die
        // durch das Limit bereits klein ist.
        var index = rules.Select(rule => $"{rule.AccountId:N}\n{rule.NormalizedCounterparty}\n{rule.Direction}")
            .ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(transaction => index.Contains(
                $"{transaction.AccountId:N}\n{transaction.NormalizedCounterparty}\n{(transaction.Amount < 0m ? "expense" : "income")}"))
            .ToList();
    }
}
