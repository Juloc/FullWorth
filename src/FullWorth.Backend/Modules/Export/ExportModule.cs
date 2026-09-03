using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Export;

public sealed record ExportAccount(
    Guid Id,
    Guid FullWorthSpaceId,
    string InstitutionName,
    string DisplayName,
    string? Product,
    string? AccountType,
    string Currency,
    string? IbanLast4,
    bool IsActive,
    bool IncludeInNetWorth,
    int SortOrder,
    DateTimeOffset UpdatedAt);

public sealed record ExportBalance(Guid AccountId, decimal Amount, string Currency, string BalanceType, DateOnly? ReferenceDate, DateTimeOffset CapturedAt);
public sealed record ExportTransaction(Guid Id, Guid AccountId, Guid? CategoryId, string Status, DateOnly? BookingDate, DateOnly? ValueDate, decimal Amount, string Currency, string? Counterparty, string? Description, string? MerchantCategoryCode, string? UserNote, bool IsIgnored, bool IsTransfer, string CategorizationSource, DateTimeOffset UpdatedAt);
public sealed record ExportCategory(Guid Id, string Key, string Name, Guid? ParentId, string? Icon, bool IsSystem, int SortOrder);
public sealed record ExportRule(Guid Id, string Name, bool IsEnabled, int Priority, string Target, string MatchField, string MatchMode, string Pattern, string Direction, decimal? MinAmount, decimal? MaxAmount, string? MerchantCategoryCode, Guid CategoryId, bool MarkAsTransfer, bool StopProcessing);
public sealed record ExportContract(Guid Id, string Name, string? ProviderName, string Kind, Guid? CategoryId, Guid? AccountId, decimal Amount, string Currency, string BillingCycle, int Interval, DateOnly? StartDate, DateOnly? EndDate, DateOnly? NextDueDate, bool AutoDetected, bool IsActive, string? Notes);
public sealed record ExportBudget(Guid Id, string Name, Guid? CategoryId, decimal Amount, string Currency, string Period, bool CarryOver, bool IsActive, DateOnly? StartDate, DateOnly? EndDate);
public sealed record ExportAsset(Guid Id, string Name, string Kind, decimal CurrentValue, string Currency, DateOnly? ValuedAt, decimal? AnnualGrowthRate, bool IncludeInNetWorth, string? Notes);
public sealed record ExportLiability(Guid Id, string Name, string Kind, decimal CurrentBalance, string Currency, decimal? InterestRate, decimal? RegularPayment, string PaymentCycle, DateOnly? NextDueDate, DateOnly? EndDate, bool IncludeInNetWorth, string? Notes);
public sealed record ExportPurchaseItem(Guid Id, Guid? CategoryId, string Name, string? Brand, string? Sku, string? Asin, decimal Quantity, decimal? UnitPrice, decimal TotalPrice, string Currency, string CategorizationSource, string? Notes);
public sealed record ExportPurchase(Guid Id, Guid? TransactionId, string Source, string Merchant, string? ExternalOrderId, DateOnly? PurchaseDate, decimal TotalAmount, string Currency, string Status, decimal? MatchConfidence, string? Notes, bool HasReceipt, IReadOnlyList<ExportPurchaseItem> Items);
public sealed record ExportNetWorth(Guid Id, DateOnly Date, string Currency, decimal Accounts, decimal Assets, decimal Liabilities, decimal NetWorth, DateTimeOffset CreatedAt);

public sealed record ExportSnapshot(
    DateTimeOffset GeneratedAt,
    Guid FullWorthSpaceId,
    IReadOnlyList<ExportAccount> Accounts,
    IReadOnlyList<ExportBalance> Balances,
    IReadOnlyList<ExportTransaction> Transactions,
    IReadOnlyList<ExportCategory> Categories,
    IReadOnlyList<ExportRule> Rules,
    IReadOnlyList<ExportContract> Contracts,
    IReadOnlyList<ExportBudget> Budgets,
    IReadOnlyList<ExportAsset> Assets,
    IReadOnlyList<ExportLiability> Liabilities,
    IReadOnlyList<ExportPurchase> Purchases,
    IReadOnlyList<ExportNetWorth> NetWorthHistory);

public sealed class ExportService(FullWorthDbContext db)
{
    public async Task<ExportSnapshot?> SnapshotForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var isMember = await db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);
        if (!isMember) return null;

        var accessibleAccounts = db.Accounts.AsNoTracking().Where(account =>
            account.FullWorthSpaceId == fullWorthSpaceId &&
            account.Owners.Any(owner => owner.UserId == userId));
        var accountIds = await accessibleAccounts.Select(account => account.Id).ToListAsync(ct);

        var accounts = await accessibleAccounts
            .OrderBy(account => account.SortOrder).ThenBy(account => account.DisplayName)
            .Select(account => new ExportAccount(
                account.Id,
                account.FullWorthSpaceId,
                account.InstitutionName,
                account.DisplayName,
                account.Product,
                account.AccountType,
                account.Currency,
                account.IbanLast4,
                account.IsActive,
                account.IncludeInNetWorth,
                account.SortOrder,
                account.UpdatedAt))
            .ToListAsync(ct);

        var balances = await db.BalanceSnapshots.AsNoTracking()
            .Where(balance => accountIds.Contains(balance.AccountId))
            .GroupBy(balance => balance.AccountId)
            .Select(group => group.OrderByDescending(balance => balance.CapturedAt)
                // Deterministic balance-type preference (see BalanceSnapshotQueries.CurrentFirst): a sync
                // stamps every balance_type with the same CapturedAt, so tiebreak to avoid a flipping pick.
                // One concatenated rank-prefix + type-name key so the ordering stays translatable here.
                .ThenBy(balance => (balance.BalanceType == "interimAvailable" ? "0"
                                  : balance.BalanceType == "closingAvailable" ? "1"
                                  : balance.BalanceType == "closingBooked" ? "2"
                                  : balance.BalanceType == "interimBooked" ? "3"
                                  : balance.BalanceType == "expected" ? "4" : "5") + balance.BalanceType)
                .Select(balance => new ExportBalance(
                    balance.AccountId,
                    balance.Amount,
                    balance.Currency,
                    balance.BalanceType,
                    balance.ReferenceDate,
                    balance.CapturedAt))
                .First())
            .ToListAsync(ct);

        var transactions = await db.Transactions.AsNoTracking()
            .Where(transaction => accountIds.Contains(transaction.AccountId))
            .OrderByDescending(transaction => transaction.BookingDate).ThenByDescending(transaction => transaction.UpdatedAt)
            .Select(transaction => new ExportTransaction(
                transaction.Id,
                transaction.AccountId,
                transaction.CategoryId,
                transaction.Status,
                transaction.BookingDate,
                transaction.ValueDate,
                transaction.Amount,
                transaction.Currency,
                transaction.Counterparty,
                transaction.Description,
                transaction.MerchantCategoryCode,
                transaction.UserNote,
                transaction.IsIgnored,
                transaction.IsTransfer,
                transaction.CategorizationSource,
                transaction.UpdatedAt))
            .ToListAsync(ct);

        var categories = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(category => category.SortOrder).ThenBy(category => category.Name)
            .Select(category => new ExportCategory(category.Id, category.Key, category.Name, category.ParentId, category.Icon, category.IsSystem, category.SortOrder))
            .ToListAsync(ct);

        var rules = await db.CategorizationRules.AsNoTracking()
            .Where(rule => rule.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(rule => rule.Target).ThenBy(rule => rule.Priority)
            .Select(rule => new ExportRule(
                rule.Id,
                rule.Name,
                rule.IsEnabled,
                rule.Priority,
                rule.Target,
                rule.MatchField,
                rule.MatchMode,
                rule.Pattern,
                rule.Direction,
                rule.MinAmount,
                rule.MaxAmount,
                rule.MerchantCategoryCode,
                rule.CategoryId,
                rule.MarkAsTransfer,
                rule.StopProcessing))
            .ToListAsync(ct);

        var contracts = await db.Contracts.AsNoTracking()
            .Where(contract => contract.FullWorthSpaceId == fullWorthSpaceId &&
                               (contract.AccountId == null || accountIds.Contains(contract.AccountId.Value)))
            .OrderBy(contract => contract.NextDueDate).ThenBy(contract => contract.Name)
            .Select(contract => new ExportContract(
                contract.Id,
                contract.Name,
                contract.ProviderName,
                contract.Kind,
                contract.CategoryId,
                contract.AccountId,
                contract.Amount,
                contract.Currency,
                contract.BillingCycle,
                contract.Interval,
                contract.StartDate,
                contract.EndDate,
                contract.NextDueDate,
                contract.AutoDetected,
                contract.IsActive,
                contract.Notes))
            .ToListAsync(ct);

        var budgets = await db.Budgets.AsNoTracking()
            .Where(budget => budget.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(budget => budget.Name)
            .Select(budget => new ExportBudget(
                budget.Id,
                budget.Name,
                budget.CategoryId,
                budget.Amount,
                budget.Currency,
                budget.Period,
                budget.CarryOver,
                budget.IsActive,
                budget.StartDate,
                budget.EndDate))
            .ToListAsync(ct);

        var assets = await db.Assets.AsNoTracking()
            .Where(asset => asset.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(asset => asset.Name)
            .Select(asset => new ExportAsset(
                asset.Id,
                asset.Name,
                asset.Kind,
                asset.CurrentValue,
                asset.Currency,
                asset.ValuedAt,
                asset.AnnualGrowthRate,
                asset.IncludeInNetWorth,
                asset.Notes))
            .ToListAsync(ct);

        var liabilities = await db.Liabilities.AsNoTracking()
            .Where(liability => liability.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(liability => liability.Name)
            .Select(liability => new ExportLiability(
                liability.Id,
                liability.Name,
                liability.Kind,
                liability.CurrentBalance,
                liability.Currency,
                liability.InterestRate,
                liability.RegularPayment,
                liability.PaymentCycle,
                liability.NextDueDate,
                liability.EndDate,
                liability.IncludeInNetWorth,
                liability.Notes))
            .ToListAsync(ct);

        var accessibleTransactionIds = transactions.Select(transaction => transaction.Id).ToArray();
        var purchases = await db.Purchases.AsNoTracking()
            .Where(purchase => purchase.FullWorthSpaceId == fullWorthSpaceId &&
                               (purchase.TransactionId == null || accessibleTransactionIds.Contains(purchase.TransactionId.Value)))
            .OrderByDescending(purchase => purchase.PurchaseDate).ThenByDescending(purchase => purchase.CreatedAt)
            .Select(purchase => new ExportPurchase(
                purchase.Id,
                purchase.TransactionId,
                purchase.Source,
                purchase.Merchant,
                purchase.ExternalOrderId,
                purchase.PurchaseDate,
                purchase.TotalAmount,
                purchase.Currency,
                purchase.Status,
                purchase.MatchConfidence,
                purchase.Notes,
                purchase.ReceiptImagePath != null,
                purchase.Items.OrderBy(item => item.Name).Select(item => new ExportPurchaseItem(
                    item.Id,
                    item.CategoryId,
                    item.Name,
                    item.Brand,
                    item.Sku,
                    item.Asin,
                    item.Quantity,
                    item.UnitPrice,
                    item.TotalPrice,
                    item.Currency,
                    item.CategorizationSource,
                    item.Notes)).ToList()))
            .ToListAsync(ct);

        var netWorthHistory = await db.NetWorthSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.FullWorthSpaceId == fullWorthSpaceId && snapshot.UserId == userId)
            .OrderBy(snapshot => snapshot.Date)
            .Select(snapshot => new ExportNetWorth(
                snapshot.Id,
                snapshot.Date,
                snapshot.Currency,
                snapshot.Accounts,
                snapshot.Assets,
                snapshot.Liabilities,
                snapshot.NetWorth,
                snapshot.CreatedAt))
            .ToListAsync(ct);

        return new ExportSnapshot(
            DateTimeOffset.UtcNow,
            fullWorthSpaceId,
            accounts,
            balances,
            transactions,
            categories,
            rules,
            contracts,
            budgets,
            assets,
            liabilities,
            purchases,
            netWorthHistory);
    }
}

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/export/snapshot", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, ExportService service, CancellationToken ct) =>
        {
            var snapshot = await service.SnapshotForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).WithTags("Export");

        app.MapGet("/api/capabilities", () => Results.Ok(new
        {
            version = "0.3.0",
            resources = new[] { "accounts", "transactions", "purchases", "purchase-items", "categories", "categorization-rules", "contracts", "budgets", "assets", "liabilities", "analytics", "net-worth", "export" },
            fullSnapshot = "/api/export/snapshot"
        })).WithTags("Meta");
        return app;
    }
}
