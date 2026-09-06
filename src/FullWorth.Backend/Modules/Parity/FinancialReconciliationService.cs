using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public static class ContributionKinds
{
    public const string Expense = "expense";
    public const string Refund = "refund";
    public const string Income = "income";
}

public sealed record CanonicalContribution(
    Guid TransactionId,
    Guid AccountId,
    Guid? CategoryId,
    DateOnly Date,
    string Merchant,
    string Currency,
    decimal NativeAmount,
    decimal ReportingAmount,
    string Kind,
    IReadOnlySet<Guid> TagIds,
    IReadOnlySet<Guid> ContractIds,
    decimal ContractLinkedNativeAmount);

public sealed record CanonicalContributionLoad(
    IReadOnlyList<CanonicalContribution> Items,
    string ReportingCurrency,
    bool IncompleteFx,
    DateOnly From,
    DateOnly To);

public sealed class FinancialReconciliationService(FullWorthDbContext db, CurrencyConverter converter)
{
    public async Task<CanonicalContributionLoad?> LoadAsync(
        Guid userId,
        Guid fullWorthSpaceId,
        DateOnly from,
        DateOnly to,
        string reportingCurrency,
        IReadOnlyCollection<Guid>? requestedAccountIds,
        bool includeTransfers,
        bool includePending,
        bool includeIgnored,
        string refundMode,
        CancellationToken ct)
    {
        if (from > to) throw new ArgumentException("Invalid date range.");
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;

        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        if (requestedAccountIds is not null && requestedAccountIds.Any(id => !visible.Contains(id)))
            throw new UnauthorizedAccessException("Report contains an inaccessible account.");

        // null means "all visible accounts". An explicitly empty collection means "no accounts" and
        // must stay empty; otherwise an empty group/filter intersection would accidentally widen back
        // to the user's full account scope.
        var accountIds = requestedAccountIds is null
            ? visible
            : requestedAccountIds.ToHashSet();

        var txQuery = db.Transactions.AsNoTracking().Where(t =>
            accountIds.Contains(t.AccountId) &&
            (t.BookingDate ?? t.ValueDate) >= from &&
            (t.BookingDate ?? t.ValueDate) <= to);
        if (!includeIgnored) txQuery = txQuery.Where(t => !t.IsIgnored);
        if (!includeTransfers) txQuery = txQuery.Where(t => !t.IsTransfer);
        if (!includePending) txQuery = txQuery.Where(t => t.Status != "PDNG");
        var transactions = await txQuery.ToListAsync(ct);

        var originalIds = transactions
            .Where(t => t.Amount > 0 && t.RefundOfTransactionId.HasValue)
            .Select(t => t.RefundOfTransactionId!.Value)
            .Distinct()
            .ToArray();
        // Refund inheritance must never widen a filtered report. If the original purchase lives on a
        // different visible-but-unselected account, keep the refund inside its own selected account
        // instead of pulling categories/tags/contracts from outside the report scope.
        var originals = originalIds.Length == 0
            ? []
            : await db.Transactions.AsNoTracking()
                .Where(t => originalIds.Contains(t.Id) && accountIds.Contains(t.AccountId))
                .ToListAsync(ct);
        var originalById = originals.ToDictionary(t => t.Id);

        var allIds = transactions.Select(t => t.Id).Concat(originals.Select(t => t.Id)).Distinct().ToArray();
        var allocations = allIds.Length == 0
            ? []
            : await db.TransactionAllocations.AsNoTracking()
                .Where(a => allIds.Contains(a.TransactionId))
                .ToListAsync(ct);
        var allocationMap = allocations.GroupBy(a => a.TransactionId).ToDictionary(g => g.Key, g => g.ToList());
        var tagMap = await LoadTagMap(allIds, ct);
        var contractMap = await LoadContractMap(allIds, ct);

        var fx = await converter.PrepareAsync(reportingCurrency, from, to, ct);
        var incomplete = false;
        var result = new List<CanonicalContribution>();

        foreach (var tx in transactions)
        {
            var date = tx.BookingDate ?? tx.ValueDate ?? from;
            var tags = tagMap.GetValueOrDefault(tx.Id) ?? [];
            var contracts = contractMap.GetValueOrDefault(tx.Id) ?? ContractInfo.Empty;
            var merchant = MerchantNormalization.Normalize(tx.NormalizedCounterparty ?? tx.Counterparty) ?? "Unknown";

            void Add(Guid accountId, Guid? categoryId, decimal nativeAmount, string kind,
                IReadOnlySet<Guid> contributionTags, ContractInfo contributionContracts,
                string contributionMerchant, decimal linkedNative)
            {
                var converted = fx.ToBaseOn(nativeAmount, tx.Currency, date);
                if (!converted.HasValue)
                {
                    incomplete = true;
                    return;
                }
                result.Add(new CanonicalContribution(
                    tx.Id,
                    accountId,
                    categoryId,
                    date,
                    contributionMerchant,
                    tx.Currency,
                    nativeAmount,
                    converted.Value,
                    kind,
                    contributionTags,
                    contributionContracts.ContractIds,
                    Math.Max(0m, linkedNative)));
            }

            if (tx.Amount < 0)
            {
                var lines = allocationMap.GetValueOrDefault(tx.Id);
                if (lines is { Count: > 0 })
                {
                    var total = lines.Sum(line => Math.Abs(line.Amount));
                    foreach (var line in lines)
                    {
                        var magnitude = Math.Abs(line.Amount);
                        var linked = total > 0 ? contracts.LinkedAmount * magnitude / total : 0m;
                        Add(tx.AccountId, line.CategoryId, -magnitude, ContributionKinds.Expense,
                            tags, contracts, merchant, linked);
                    }
                }
                else
                {
                    Add(tx.AccountId, tx.CategoryId, tx.Amount, ContributionKinds.Expense,
                        tags, contracts, merchant, Math.Min(Math.Abs(tx.Amount), contracts.LinkedAmount));
                }
                continue;
            }

            if (tx.Amount > 0 && tx.RefundOfTransactionId.HasValue)
            {
                if (string.Equals(refundMode, "exclude", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(refundMode, "reverse", StringComparison.OrdinalIgnoreCase))
                {
                    Add(tx.AccountId, tx.CategoryId, tx.Amount, ContributionKinds.Income,
                        tags, contracts, merchant, 0m);
                    continue;
                }

                if (!originalById.TryGetValue(tx.RefundOfTransactionId.Value, out var original))
                {
                    Add(tx.AccountId, tx.RefundCategoryId ?? tx.CategoryId, tx.Amount, ContributionKinds.Refund,
                        tags, contracts, merchant, 0m);
                    continue;
                }

                var originalTags = tagMap.GetValueOrDefault(original.Id) ?? tags;
                var originalContracts = contractMap.GetValueOrDefault(original.Id) ?? contracts;
                var originalMerchant = MerchantNormalization.Normalize(original.NormalizedCounterparty ?? original.Counterparty) ?? merchant;
                var originalMagnitude = Math.Abs(original.Amount);
                var linkedRatio = originalMagnitude <= 0 ? 0m : Math.Clamp(originalContracts.LinkedAmount / originalMagnitude, 0m, 1m);
                var refundLinked = tx.Amount * linkedRatio;

                if (tx.RefundCategoryId.HasValue)
                {
                    Add(original.AccountId, tx.RefundCategoryId, tx.Amount, ContributionKinds.Refund,
                        originalTags, originalContracts, originalMerchant, refundLinked);
                    continue;
                }

                var originalLines = allocationMap.GetValueOrDefault(original.Id);
                if (originalLines is { Count: > 0 })
                {
                    var splitTotal = originalLines.Sum(line => Math.Abs(line.Amount));
                    if (splitTotal > 0)
                    {
                        foreach (var line in originalLines)
                        {
                            var ratio = Math.Abs(line.Amount) / splitTotal;
                            Add(original.AccountId, line.CategoryId, tx.Amount * ratio, ContributionKinds.Refund,
                                originalTags, originalContracts, originalMerchant, refundLinked * ratio);
                        }
                        continue;
                    }
                }

                Add(original.AccountId, original.CategoryId ?? tx.CategoryId, tx.Amount, ContributionKinds.Refund,
                    originalTags, originalContracts, originalMerchant, refundLinked);
                continue;
            }

            if (tx.Amount > 0)
            {
                Add(tx.AccountId, tx.CategoryId, tx.Amount, ContributionKinds.Income,
                    tags, contracts, merchant, 0m);
            }
        }

        return new CanonicalContributionLoad(result, FxSnapshot.Normalize(reportingCurrency), incomplete, from, to);
    }

    public static decimal Spend(IEnumerable<CanonicalContribution> items)
    {
        var expense = items.Where(i => i.Kind == ContributionKinds.Expense).Sum(i => -i.ReportingAmount);
        var refunds = items.Where(i => i.Kind == ContributionKinds.Refund).Sum(i => i.ReportingAmount);
        return Math.Max(0m, Math.Round(expense - refunds, 2));
    }

    public static decimal Income(IEnumerable<CanonicalContribution> items) =>
        Math.Round(items.Where(i => i.Kind == ContributionKinds.Income).Sum(i => i.ReportingAmount), 2);

    public static decimal Net(IEnumerable<CanonicalContribution> items) =>
        Math.Round(items.Sum(i => i.ReportingAmount), 2);

    public static decimal VariableSpend(IEnumerable<CanonicalContribution> items)
    {
        decimal expense = 0m;
        decimal refund = 0m;
        foreach (var item in items)
        {
            var nativeMagnitude = Math.Abs(item.NativeAmount);
            var variableRatio = nativeMagnitude <= 0m
                ? 1m
                : Math.Clamp((nativeMagnitude - Math.Min(nativeMagnitude, item.ContractLinkedNativeAmount)) / nativeMagnitude, 0m, 1m);
            if (item.Kind == ContributionKinds.Expense) expense += -item.ReportingAmount * variableRatio;
            else if (item.Kind == ContributionKinds.Refund) refund += item.ReportingAmount * variableRatio;
        }
        return Math.Max(0m, Math.Round(expense - refund, 2));
    }

    private async Task<Dictionary<Guid, HashSet<Guid>>> LoadTagMap(Guid[] transactionIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, HashSet<Guid>>();
        if (transactionIds.Length == 0) return map;
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection,
            "SELECT \"TransactionId\",\"TagId\" FROM \"TransactionTags\" WHERE \"TransactionId\"=ANY(@ids)",
            ("@ids", transactionIds));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tx = ParitySql.Guid(reader, "TransactionId");
            if (!map.TryGetValue(tx, out var tags)) map[tx] = tags = [];
            tags.Add(ParitySql.Guid(reader, "TagId"));
        }
        return map;
    }

    private async Task<Dictionary<Guid, ContractInfo>> LoadContractMap(Guid[] transactionIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, ContractInfo>();
        if (transactionIds.Length == 0) return map;
        var ids = new Dictionary<Guid, HashSet<Guid>>();
        var amounts = new Dictionary<Guid, decimal>();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, """
SELECT l."TransactionId",
       COALESCE(source_contract."MergedIntoContractId",l."ContractId") AS "ContractId",
       l."Amount"
FROM "ContractTransactionLinks" l
JOIN "Contracts" source_contract ON source_contract."Id"=l."ContractId"
WHERE l."TransactionId"=ANY(@ids)
""", ("@ids", transactionIds));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tx = ParitySql.Guid(reader, "TransactionId");
            if (!ids.TryGetValue(tx, out var contracts)) ids[tx] = contracts = [];
            contracts.Add(ParitySql.Guid(reader, "ContractId"));
            amounts[tx] = amounts.GetValueOrDefault(tx) + Math.Abs(ParitySql.Decimal(reader, "Amount"));
        }
        foreach (var pair in ids)
            map[pair.Key] = new ContractInfo(pair.Value, amounts.GetValueOrDefault(pair.Key));
        return map;
    }

    private sealed record ContractInfo(IReadOnlySet<Guid> ContractIds, decimal LinkedAmount)
    {
        public static readonly ContractInfo Empty = new(new HashSet<Guid>(), 0m);
    }
}
