using System.Globalization;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Contracts;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Export;

/// <summary>
/// Was in der Tabellenmappe steht: Buchungen, Kategorien, Vertraege - je ein Blatt, alles nur die
/// Konten, die dieser Benutzer sehen darf.
///
/// Der Aufbau der Zeilen ist die Fachaussage des Exports; das Schreiben der Datei uebernimmt
/// <see cref="XlsxWriter"/>, der von Finanzen nichts weiss.
/// </summary>
public sealed class XlsxExportService(FullWorthDbContext db)
{
    /// <summary>Mehr Zeilen nimmt niemand in einer Tabelle entgegen.</summary>
    private const int MaxTransactions = 100_000;

    public async Task<byte[]> BuildAsync(
        Guid fullWorthSpaceId, IReadOnlySet<Guid> visibleAccountIds, CancellationToken ct)
    {
        var transactions = await db.Transactions.AsNoTracking()
            .Where(transaction => visibleAccountIds.Contains(transaction.AccountId))
            .OrderByDescending(transaction => transaction.BookingDate)
            .Take(MaxTransactions)
            .ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .ToListAsync(ct);
        var contracts = await db.Contracts.AsNoTracking()
            .Where(contract => contract.FullWorthSpaceId == fullWorthSpaceId
                && contract.MergedIntoContractId == null
                && (contract.AccountId == null || visibleAccountIds.Contains(contract.AccountId.Value)))
            .ToListAsync(ct);
        var accountNames = await db.Accounts.AsNoTracking()
            .Where(account => visibleAccountIds.Contains(account.Id))
            .ToDictionaryAsync(account => account.Id, account => account.DisplayName, ct);

        var categoryNames = categories.ToDictionary(category => category.Id, category => category.Name);

        return XlsxWriter.Write(new Dictionary<string, List<IReadOnlyList<string>>>
        {
            ["Transactions"] = TransactionSheet(transactions, accountNames, categoryNames),
            ["Categories"] = CategorySheet(categories),
            ["Contracts"] = ContractSheet(contracts)
        });
    }

    private static List<IReadOnlyList<string>> TransactionSheet(
        IReadOnlyList<FinanceTransaction> transactions,
        IReadOnlyDictionary<Guid, string> accountNames,
        IReadOnlyDictionary<Guid, string> categoryNames)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "Date", "Account", "Amount", "Currency", "Counterparty", "Description", "Category", "Transfer", "Ignored" }
        };
        foreach (var transaction in transactions)
            rows.Add(new[]
            {
                (transaction.BookingDate ?? transaction.ValueDate)?.ToString("yyyy-MM-dd") ?? string.Empty,
                accountNames.GetValueOrDefault(transaction.AccountId, string.Empty),
                transaction.Amount.ToString(CultureInfo.InvariantCulture),
                transaction.Currency,
                transaction.Counterparty ?? string.Empty,
                transaction.Description ?? string.Empty,
                transaction.CategoryId.HasValue
                    ? categoryNames.GetValueOrDefault(transaction.CategoryId.Value, string.Empty)
                    : string.Empty,
                transaction.IsTransfer ? "true" : "false",
                transaction.IsIgnored ? "true" : "false"
            });
        return rows;
    }

    private static List<IReadOnlyList<string>> CategorySheet(IReadOnlyList<FinanceCategory> categories)
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "Key", "Name", "ParentId", "Archived" } };
        foreach (var category in categories)
            rows.Add(new[]
            {
                category.Key,
                category.Name,
                category.ParentId?.ToString() ?? string.Empty,
                category.IsArchived ? "true" : "false"
            });
        return rows;
    }

    private static List<IReadOnlyList<string>> ContractSheet(IReadOnlyList<RecurringContract> contracts)
    {
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "Name", "Provider", "Amount", "Currency", "Cycle", "NextDue", "Active" }
        };
        foreach (var contract in contracts)
            rows.Add(new[]
            {
                contract.Name,
                contract.ProviderName ?? string.Empty,
                contract.Amount.ToString(CultureInfo.InvariantCulture),
                contract.Currency,
                contract.BillingCycle,
                contract.NextDueDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                contract.IsActive ? "true" : "false"
            });
        return rows;
    }
}
