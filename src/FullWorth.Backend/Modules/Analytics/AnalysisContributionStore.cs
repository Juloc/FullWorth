using FullWorth.Backend.Modules.Reconciliation;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Analytics;

/// <summary>
/// Die Rohdaten einer freien Auswertung: welche Buchungen der Filter trifft, wie sie aufgeteilt sind,
/// und woran sie haengen.
///
/// Gerechnet wird hier nichts. Welche Kennzahl aus welchen Betraegen entsteht, entscheidet der
/// Endpunkt; dieser Store liefert nur, was dafuer gebraucht wird.
/// </summary>
public sealed class AnalysisContributionStore(FullWorthDbContext db)
{
    public Task<string> BaseCurrencyAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaces.AsNoTracking()
            .Where(space => space.Id == fullWorthSpaceId)
            .Select(space => space.BaseCurrency)
            .SingleAsync(ct);

    /// <summary>Die Konten aus diesen Gruppen, soweit der Fragende sie ueberhaupt sehen darf.</summary>
    public async Task<HashSet<Guid>> AccountIdsInGroupsAsync(
        IReadOnlySet<Guid> visibleAccountIds, IReadOnlyList<Guid> groupIds, CancellationToken ct) =>
        (await db.Accounts.AsNoTracking()
            .Where(account => visibleAccountIds.Contains(account.Id)
                           && account.GroupId.HasValue
                           && groupIds.Contains(account.GroupId.Value))
            .Select(account => account.Id)
            .ToListAsync(ct)).ToHashSet();

    public Task<List<FinanceTransaction>> TransactionsAsync(
        IReadOnlySet<Guid> accountIds, DateOnly from, DateOnly to,
        bool includeIgnored, bool includeTransfers, bool includePending, CancellationToken ct)
    {
        var query = db.Transactions.AsNoTracking()
            .Where(transaction => accountIds.Contains(transaction.AccountId)
                               && (transaction.BookingDate ?? transaction.ValueDate) >= from
                               && (transaction.BookingDate ?? transaction.ValueDate) <= to);
        if (!includeIgnored) query = query.Where(transaction => !transaction.IsIgnored);
        if (!includeTransfers) query = query.Where(transaction => !transaction.IsTransfer);
        if (!includePending) query = query.Where(transaction => transaction.Status != "PDNG");
        return query.ToListAsync(ct);
    }

    public async Task<Dictionary<Guid, List<TransactionAllocation>>> AllocationsByTransactionAsync(
        Guid[] transactionIds, CancellationToken ct) =>
        (await db.TransactionAllocations.AsNoTracking()
            .Where(allocation => transactionIds.Contains(allocation.TransactionId))
            .ToListAsync(ct))
        .GroupBy(allocation => allocation.TransactionId)
        .ToDictionary(group => group.Key, group => group.ToList());

    public async Task<Dictionary<Guid, HashSet<Guid>>> TagsByTransactionAsync(
        Guid[] transactionIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, HashSet<Guid>>();
        if (transactionIds.Length == 0) return map;

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"TransactionId\",\"TagId\" FROM \"TransactionTags\" WHERE \"TransactionId\"=ANY(@ids)",
            ("@ids", transactionIds));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = RawSql.Guid(reader, "TransactionId");
            if (!map.TryGetValue(id, out var set)) map[id] = set = [];
            set.Add(RawSql.Guid(reader, "TagId"));
        }
        return map;
    }

    /// <summary>
    /// Welche Vertraege an welcher Buchung haengen - und zwar der Vertrag, in den ein
    /// zusammengefuehrter aufgegangen ist. Sonst zeigt eine Auswertung Betraege unter einem Vertrag,
    /// den es nicht mehr gibt.
    /// </summary>
    public async Task<Dictionary<Guid, HashSet<Guid>>> ContractsByTransactionAsync(
        Guid[] transactionIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, HashSet<Guid>>();
        if (transactionIds.Length == 0) return map;

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection, """
SELECT l."TransactionId",COALESCE(source_contract."MergedIntoContractId",l."ContractId") AS "ContractId"
FROM "ContractTransactionLinks" l
JOIN "Contracts" source_contract ON source_contract."Id"=l."ContractId"
WHERE l."TransactionId"=ANY(@ids)
""", ("@ids", transactionIds));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = RawSql.Guid(reader, "TransactionId");
            if (!map.TryGetValue(id, out var set)) map[id] = set = [];
            set.Add(RawSql.Guid(reader, "ContractId"));
        }
        return map;
    }

    /// <summary>
    /// Die gewaehlten Kategorien, bei <c>IncludeDescendants</c> samt allen darunterliegenden. Der
    /// Baum wird in Runden abgestiegen, weil die Tiefe nicht begrenzt ist.
    /// </summary>
    public async Task<HashSet<Guid>> ExpandCategoriesAsync(
        Guid fullWorthSpaceId, IReadOnlyList<AnalysisCategoryScope> scopes, CancellationToken ct)
    {
        var selected = scopes.Select(scope => scope.CategoryId).ToHashSet();
        if (scopes.Count == 0) return selected;

        var descendants = scopes.Where(scope => scope.IncludeDescendants)
            .Select(scope => scope.CategoryId).ToHashSet();
        var rows = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .Select(category => new { category.Id, category.ParentId })
            .ToListAsync(ct);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var row in rows)
                if (row.ParentId.HasValue && descendants.Contains(row.ParentId.Value) && descendants.Add(row.Id))
                {
                    selected.Add(row.Id);
                    changed = true;
                }
        }
        return selected;
    }

    public async Task<Dictionary<Guid, string>> TagNamesAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var map = new Dictionary<Guid, string>();
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"Id\",\"Name\" FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space",
            ("@space", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            map[RawSql.Guid(reader, "Id")] = RawSql.String(reader, "Name");
        return map;
    }

    public Task<Dictionary<Guid, string>> CategoryNamesAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(category => category.Id, category => category.Name, ct);

    public Task<Dictionary<Guid, string>> AccountNamesAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Accounts.AsNoTracking()
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(account => account.Id, account => account.DisplayName, ct);

    public Task<Dictionary<Guid, string>> ContractNamesAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Contracts.AsNoTracking()
            .Where(contract => contract.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(contract => contract.Id, contract => contract.Name, ct);

    /// <summary>Die Kategoriebaeume, um einen Betrag seiner obersten Kategorie zuzuordnen.</summary>
    public Task<List<CategoryParent>> CategoryParentsAsync(Guid fullWorthSpaceId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .Select(category => new CategoryParent(category.Id, category.Name, category.ParentId))
            .ToListAsync(ct);
}

/// <summary>Eine Kategorie und ihr Elternteil - mehr braucht die Wurzelsuche nicht.</summary>
public sealed record CategoryParent(Guid Id, string Name, Guid? ParentId);
