using FullWorth.Backend.Modules.Reconciliation;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Merchants;

namespace FullWorth.Backend.Modules.Analytics;

/// <summary>Ein Betrag, der in eine Auswertung eingeht, samt allem, wonach man ihn gruppieren kann.</summary>
public sealed record StatisticalContribution(
    Guid TransactionId, Guid AccountId, Guid? CategoryId, DateOnly Date, string Merchant, string Currency,
    decimal NativeAmount, decimal BaseAmount, IReadOnlyList<Guid> TagIds, IReadOnlyList<Guid> ContractIds);

/// <summary>Das Ergebnis einer Abfrage, mit dem Zeitraum, der tatsaechlich gerechnet wurde.</summary>
public sealed record ContributionLoad(
    List<StatisticalContribution> Items, string BaseCurrency, bool Incomplete, DateOnly From, DateOnly To);

/// <summary>
/// Welche Betraege eine freie Auswertung zusammenzaehlt.
///
/// Drei Entscheidungen stecken hier, und sie sind der Grund, warum das ein eigener Ort ist:
///
/// Eine Buchung mit Aufteilungen zaehlt NICHT als ein Betrag, sondern als ihre Zeilen - sonst landet
/// ein Wocheneinkauf vollstaendig unter "Lebensmittel", obwohl die Haelfte Drogerie war.
///
/// Eine Erstattung kann die urspruengliche Ausgabe mindern statt als Einnahme zu zaehlen. Das ist
/// eine Frage der Darstellung und darum ein Schalter (<c>RefundMode</c>), keine feste Regel.
///
/// Fehlt ein Wechselkurs, wird der Betrag weggelassen UND das Ergebnis als unvollstaendig markiert.
/// Niemals 1:1 annehmen - das ist eine der Geldregeln des Projekts.
/// </summary>
public sealed class AnalysisContributionService(AnalysisContributionStore store, CurrencyConverter converter)
{
    public async Task<ContributionLoad> LoadAsync(
        Guid userId, Guid fullWorthSpaceId, IReadOnlySet<Guid> visibleAccountIds,
        AnalysisQueryWrite query, CancellationToken ct)
    {
        if (query.AccountIds is { Count: > 0 } && query.AccountIds.Any(id => !visibleAccountIds.Contains(id)))
            throw new InvalidOperationException("Analysis contains inaccessible accounts.");

        var to = query.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from = query.From ?? to.AddMonths(-12);
        var baseCurrency = await store.BaseCurrencyAsync(fullWorthSpaceId, ct);
        var fx = await converter.PrepareAsync(baseCurrency, from, to, ct);

        var accountIds = new HashSet<Guid>(visibleAccountIds);
        if (query.AccountGroupIds is { Count: > 0 })
            accountIds.IntersectWith(await store.AccountIdsInGroupsAsync(visibleAccountIds, query.AccountGroupIds, ct));
        if (query.AccountIds is { Count: > 0 }) accountIds.IntersectWith(query.AccountIds);

        var transactions = await store.TransactionsAsync(
            accountIds, from, to, query.IncludeIgnored, query.IncludeTransfers, query.IncludePending, ct);
        var transactionIds = transactions.Select(transaction => transaction.Id).ToArray();

        var allocations = await store.AllocationsByTransactionAsync(transactionIds, ct);
        var tagsByTransaction = await store.TagsByTransactionAsync(transactionIds, ct);
        var contractsByTransaction = await store.ContractsByTransactionAsync(transactionIds, ct);
        var categories = await store.ExpandCategoriesAsync(fullWorthSpaceId, query.CategoryScopes ?? [], ct);

        var merchants = (query.NormalizedMerchants ?? []).Select(MerchantNormalization.Normalize)
            .Where(merchant => merchant is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currencies = (query.Currencies ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directions = (query.Directions ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var incomplete = false;
        var result = new List<StatisticalContribution>();

        foreach (var transaction in transactions)
        {
            var merchant = MerchantNormalization.Normalize(
                transaction.NormalizedCounterparty ?? transaction.Counterparty) ?? "Unknown";
            if (merchants.Count > 0 && !merchants.Contains(merchant)) continue;
            if (currencies.Count > 0 && !currencies.Contains(transaction.Currency)) continue;
            if (directions.Count > 0 && !directions.Contains(transaction.Amount < 0 ? "expense" : "income")) continue;

            var tags = tagsByTransaction.GetValueOrDefault(transaction.Id) ?? [];
            if (query.TagIds is { Count: > 0 } && !tags.Overlaps(query.TagIds)) continue;
            var contracts = contractsByTransaction.GetValueOrDefault(transaction.Id) ?? [];
            if (query.ContractIds is { Count: > 0 } && !contracts.Overlaps(query.ContractIds)) continue;

            var date = transaction.BookingDate ?? transaction.ValueDate ?? from;

            void Add(Guid? categoryId, decimal nativeAmount)
            {
                if (categories.Count > 0 && (!categoryId.HasValue || !categories.Contains(categoryId.Value))) return;

                var baseAmount = fx.ToBaseOn(nativeAmount, transaction.Currency, date);
                if (!baseAmount.HasValue)
                {
                    incomplete = true;
                    return;
                }
                result.Add(new StatisticalContribution(
                    transaction.Id, transaction.AccountId, categoryId, date, merchant, transaction.Currency,
                    nativeAmount, baseAmount.Value, tags.ToArray(), contracts.ToArray()));
            }

            if (transaction.Amount < 0 && allocations.TryGetValue(transaction.Id, out var lines) && lines.Count > 0)
                foreach (var line in lines) Add(line.CategoryId, -Math.Abs(line.Amount));
            else if (transaction.Amount > 0 && transaction.RefundOfTransactionId.HasValue && query.RefundMode == "reverse")
                Add(transaction.RefundCategoryId ?? transaction.CategoryId, -Math.Abs(transaction.Amount));
            else
                Add(transaction.CategoryId, transaction.Amount);
        }

        return new ContributionLoad(result, baseCurrency, incomplete, from, to);
    }
}
