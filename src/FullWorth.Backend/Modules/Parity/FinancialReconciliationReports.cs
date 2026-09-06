using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Budgets.Cycles;
using FullWorth.Backend.Modules.Fx;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

public sealed class FinancialReconciliationReportService(
    FullWorthDbContext db,
    CurrencyConverter converter,
    FinancialReconciliationService reconciliation)
{
    public async Task<object?> AnalyticsAsync(Guid userId, Guid fullWorthSpaceId, AnalysisQueryWrite request, bool sankey, CancellationToken ct)
    {
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var validation = ValidateAnalysis(request);
        if (validation is not null) throw new ArgumentException(validation);

        var to = request.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var from = request.From ?? to.AddMonths(-12);
        var space = await db.FullWorthSpaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fullWorthSpaceId, ct);
        if (space is null) return null;

        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var requested = request.AccountIds is { Count: > 0 } ? request.AccountIds.ToHashSet() : visible.ToHashSet();
        if (requested.Any(id => !visible.Contains(id))) throw new ArgumentException("Analysis contains an inaccessible account.");
        if (request.AccountGroupIds is { Count: > 0 })
        {
            var grouped = (await db.Accounts.AsNoTracking()
                .Where(a => visible.Contains(a.Id) && a.GroupId.HasValue && request.AccountGroupIds.Contains(a.GroupId.Value))
                .Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            requested.IntersectWith(grouped);
        }

        var loaded = await reconciliation.LoadAsync(
            userId, fullWorthSpaceId, from, to, space.BaseCurrency, requested,
            request.IncludeTransfers, request.IncludePending, request.IncludeIgnored,
            string.IsNullOrWhiteSpace(request.RefundMode) ? "reverse" : request.RefundMode, ct);
        if (loaded is null) return null;

        var categoryScope = await ExpandCategories(fullWorthSpaceId, request.CategoryScopes ?? [], ct);
        var merchants = (request.NormalizedMerchants ?? [])
            .Select(MerchantNormalization.Normalize).Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currencies = (request.Currencies ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directions = (request.Directions ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagIds = (request.TagIds ?? []).ToHashSet();
        var contractIds = (request.ContractIds ?? []).ToHashSet();

        var filtered = loaded.Items.Where(item =>
            (categoryScope.Count == 0 || (item.CategoryId.HasValue && categoryScope.Contains(item.CategoryId.Value))) &&
            (merchants.Count == 0 || merchants.Contains(item.Merchant)) &&
            (currencies.Count == 0 || currencies.Contains(item.Currency)) &&
            (tagIds.Count == 0 || item.TagIds.Overlaps(tagIds)) &&
            (contractIds.Count == 0 || item.ContractIds.Overlaps(contractIds)) &&
            (directions.Count == 0 ||
             (directions.Contains("expense") && item.Kind is ContributionKinds.Expense or ContributionKinds.Refund) ||
             (directions.Contains("income") && item.Kind == ContributionKinds.Income)))
            .ToList();

        if (sankey) return await BuildSankey(fullWorthSpaceId, loaded, filtered, ct);

        var categories = await db.Categories.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var accounts = await db.Accounts.AsNoTracking().Where(x => requested.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var tags = await LoadNames("FinanceTags", fullWorthSpaceId, ct);
        var contracts = await db.Contracts.AsNoTracking().Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var buckets = new Dictionary<string, List<CanonicalContribution>>(StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in filtered)
        {
            foreach (var key in DimensionKeys(contribution, request.Dimension, categories, accounts, tags, contracts))
            {
                if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = [];
                bucket.Add(contribution);
            }
        }

        var series = buckets.Select(pair => new
        {
            key = pair.Key,
            value = Measure(pair.Value, request.Measure),
            count = pair.Value.Select(item => item.TransactionId).Distinct().Count()
        }).OrderBy(x => x.key, StringComparer.OrdinalIgnoreCase).ToArray();

        return new
        {
            currency = loaded.ReportingCurrency,
            incomplete = loaded.IncompleteFx,
            measure = request.Measure,
            dimension = request.Dimension,
            from = loaded.From,
            to = loaded.To,
            series,
            total = Measure(filtered, request.Measure)
        };
    }

    public async Task<object?> BudgetStatusAsync(Guid userId, Guid fullWorthSpaceId, Guid budgetId, DateOnly? asOf, CancellationToken ct)
    {
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var budget = await db.Budgets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == budgetId && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (budget is null) return null;

        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var allSpaceAccounts = await db.Accounts.AsNoTracking().Where(a => a.FullWorthSpaceId == fullWorthSpaceId && a.IsActive).Select(a => a.Id).ToListAsync(ct);
        var scope = await LoadBudgetScope(budgetId, ct);
        var effectiveAccounts = scope.AccountIds.Count == 0
            ? visible.ToHashSet()
            : scope.AccountIds.Where(visible.Contains).ToHashSet();
        var partialAccess = scope.AccountIds.Count == 0
            ? allSpaceAccounts.Any(id => !visible.Contains(id))
            : scope.AccountIds.Any(id => !visible.Contains(id));

        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var period = BudgetCycleCalculator.CurrentPeriod(BudgetCycleResolver.Resolve(budget.Period, budget.StartDate, budget.EndDate), day);
        var loaded = await reconciliation.LoadAsync(
            userId, fullWorthSpaceId, period.Start, period.End, budget.Currency, effectiveAccounts,
            includeTransfers: false, includePending: false, includeIgnored: false, refundMode: "reverse", ct);
        if (loaded is null) return null;

        var categoryIds = await ExpandCategories(fullWorthSpaceId, scope.Categories, ct);
        var merchantScope = scope.Merchants.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagScope = scope.TagIds.ToHashSet();
        var filtered = loaded.Items.Where(item =>
            (categoryIds.Count == 0 || (item.CategoryId.HasValue && categoryIds.Contains(item.CategoryId.Value))) &&
            (merchantScope.Count == 0 || merchantScope.Contains(item.Merchant)) &&
            (tagScope.Count == 0 || item.TagIds.Overlaps(tagScope)) &&
            item.Kind is ContributionKinds.Expense or ContributionKinds.Refund).ToList();

        var spent = FinancialReconciliationService.Spend(filtered);
        var remaining = budget.Amount - spent;
        var percent = budget.Amount == 0 ? 0m : Math.Round(spent / budget.Amount * 100m, 2);
        var totalDays = period.LengthInDays;
        var elapsed = Math.Clamp(day.DayNumber - period.Start.DayNumber + 1, 1, totalDays);
        var projected = Math.Round(spent / elapsed * totalDays, 2);
        var contributions = filtered
            .OrderByDescending(item => item.Date)
            .Take(200)
            .Select(item => new
            {
                transactionId = item.TransactionId,
                allocationId = (Guid?)null,
                date = item.Date,
                counterparty = item.Merchant,
                // Expenses are negative in the canonical ledger and therefore become positive spend rows;
                // refunds are positive and therefore become negative reversal rows.
                amount = -item.ReportingAmount,
                categoryId = item.CategoryId,
                kind = item.Kind
            }).ToArray();

        return new
        {
            budgetId,
            budget.Name,
            budget.Amount,
            budget.Currency,
            periodStart = period.Start,
            periodEnd = period.End,
            spent,
            remaining,
            percentUsed = percent,
            projectedEndSpend = projected,
            projectedOverUnder = projected - budget.Amount,
            partialAccess,
            incompleteFx = loaded.IncompleteFx,
            contributing = contributions
        };
    }

    public async Task<object?> CashflowAvailableAsync(Guid userId, Guid fullWorthSpaceId, DateOnly? asOf, CancellationToken ct)
    {
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, ct)) return null;
        var visible = await ParitySql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
        var day = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var space = await db.FullWorthSpaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == fullWorthSpaceId, ct);
        if (space is null) return null;
        var settings = await LoadCashflowSettings(fullWorthSpaceId, ct);
        var schedules = await LoadIncomeSchedules(fullWorthSpaceId, visible, ct);
        var nextIncome = schedules.Where(s => s.NextDate.HasValue && s.NextDate.Value >= day).OrderBy(s => s.NextDate).FirstOrDefault();
        var horizon = settings.HorizonMode == "end_of_month" || nextIncome is null
            ? new DateOnly(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month))
            : nextIncome.NextDate!.Value;
        var fx = await converter.PrepareAsync(space.BaseCurrency, day.AddMonths(-2), horizon, ct);
        var incomplete = false;

        decimal balances = 0m;
        foreach (var accountId in visible)
        {
            var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.IsActive, ct);
            if (account is null) continue;
            var snapshot = await db.BalanceSnapshots.AsNoTracking().Where(b => b.AccountId == accountId).CurrentFirst().FirstOrDefaultAsync(ct);
            if (snapshot is null) continue;
            var converted = fx.ToBaseOn(snapshot.Amount, snapshot.Currency, day);
            if (converted.HasValue) balances += converted.Value; else incomplete = true;
        }

        var lines = new List<CashflowLine>();
        var pendingIncomeTransactions = settings.IncludePendingIncome
            ? await db.Transactions.AsNoTracking().Where(t => visible.Contains(t.AccountId) && t.Amount > 0 && !t.IsIgnored && !t.IsTransfer && t.Status == "PDNG" && (t.BookingDate ?? t.ValueDate) >= day && (t.BookingDate ?? t.ValueDate) <= horizon).ToListAsync(ct)
            : [];
        decimal expectedIncome = 0m;
        var pendingParties = pendingIncomeTransactions.Select(t => MerchantNormalization.Normalize(t.NormalizedCounterparty ?? t.Counterparty)).Where(x => x is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pending in pendingIncomeTransactions)
        {
            var date = pending.BookingDate ?? pending.ValueDate ?? day;
            var converted = fx.ToBaseOn(pending.Amount, pending.Currency, date);
            if (converted.HasValue) expectedIncome += converted.Value; else incomplete = true;
            lines.Add(new CashflowLine("pending_income", pending.Counterparty ?? pending.Description ?? "Pending income", date, pending.Amount, pending.Currency, converted));
        }
        foreach (var schedule in schedules.Where(s => s.NextDate >= day && s.NextDate <= horizon && s.Amount.HasValue))
        {
            if (!string.IsNullOrWhiteSpace(schedule.NormalizedCounterparty) && pendingParties.Contains(schedule.NormalizedCounterparty)) continue;
            var converted = fx.ToBaseOn(schedule.Amount!.Value, schedule.Currency, schedule.NextDate!.Value);
            if (converted.HasValue) expectedIncome += converted.Value; else incomplete = true;
            lines.Add(new CashflowLine("income", schedule.Name, schedule.NextDate, schedule.Amount.Value, schedule.Currency, converted));
        }

        var futureContributions = await reconciliation.LoadAsync(
            userId, fullWorthSpaceId, day, horizon, space.BaseCurrency, visible,
            includeTransfers: false, includePending: true, includeIgnored: false, refundMode: "reverse", ct);
        var alreadyLinkedContracts = futureContributions?.Items.SelectMany(item => item.ContractIds).ToHashSet() ?? [];

        var contracts = await db.Contracts.AsNoTracking().Where(c =>
            c.FullWorthSpaceId == fullWorthSpaceId &&
            c.IsActive &&
            c.MergedIntoContractId == null &&
            c.NextDueDate >= day &&
            c.NextDueDate <= horizon &&
            (c.AccountId == null || visible.Contains(c.AccountId.Value))).ToListAsync(ct);
        decimal fixedCosts = 0m;
        foreach (var contract in contracts)
        {
            if (alreadyLinkedContracts.Contains(contract.Id)) continue;
            var converted = fx.ToBaseOn(contract.Amount, contract.Currency, contract.NextDueDate!.Value);
            if (converted.HasValue) fixedCosts += converted.Value; else incomplete = true;
            lines.Add(new CashflowLine("fixed", contract.Name, contract.NextDueDate, contract.Amount, contract.Currency, converted));
        }

        var historyFrom = day.AddDays(-30);
        var history = await reconciliation.LoadAsync(
            userId, fullWorthSpaceId, historyFrom, day.AddDays(-1), space.BaseCurrency, visible,
            includeTransfers: false, includePending: settings.IncludePendingExpenses, includeIgnored: false, refundMode: "reverse", ct);
        if (history?.IncompleteFx == true) incomplete = true;
        var historicVariableSpend = history is null ? 0m : FinancialReconciliationService.VariableSpend(history.Items);
        var days = Math.Max(0, horizon.DayNumber - day.DayNumber + 1);
        var variableForecast = Math.Round(historicVariableSpend / 30m * days, 2);

        decimal pendingVariable = 0m;
        if (settings.IncludePendingExpenses && futureContributions is not null)
        {
            var pendingExpenseIds = (await db.Transactions.AsNoTracking()
                .Where(t => visible.Contains(t.AccountId) && t.Status == "PDNG" && t.Amount < 0 && !t.IsIgnored && !t.IsTransfer &&
                            (t.BookingDate ?? t.ValueDate) >= day && (t.BookingDate ?? t.ValueDate) <= horizon)
                .Select(t => t.Id).ToListAsync(ct)).ToHashSet();
            pendingVariable = FinancialReconciliationService.VariableSpend(
                futureContributions.Items.Where(item => pendingExpenseIds.Contains(item.TransactionId)));
            if (futureContributions.IncompleteFx) incomplete = true;
        }

        var reserve = fx.ToBaseOn(settings.Reserve, settings.ReserveCurrency, day);
        if (!reserve.HasValue) { reserve = 0m; incomplete = true; }
        var available = balances + expectedIncome - fixedCosts - variableForecast - pendingVariable - reserve.Value;
        var perDay = days > 0 ? available / days : available;
        var quality = nextIncome is null || incomplete ? "limited" : history?.Items.Count < 10 || contracts.Count == 0 ? "medium" : "high";

        return new
        {
            asOf = day,
            horizonDate = horizon,
            horizonReason = nextIncome is null || settings.HorizonMode == "end_of_month" ? "end_of_month" : "next_income",
            currency = space.BaseCurrency,
            spendableBalances = Math.Round(balances, 2),
            expectedIncome = Math.Round(expectedIncome, 2),
            expectedFixedCosts = Math.Round(fixedCosts, 2),
            forecastVariableSpend = variableForecast,
            pendingVariableSpend = Math.Round(pendingVariable, 2),
            safetyReserve = Math.Round(reserve.Value, 2),
            available = Math.Round(available, 2),
            availablePerDay = Math.Round(perDay, 2),
            daysRemaining = days,
            quality,
            incompleteFx = incomplete,
            items = lines.OrderBy(line => line.Date).ToArray()
        };
    }

    private async Task<object> BuildSankey(Guid fullWorthSpaceId, CanonicalContributionLoad loaded, List<CanonicalContribution> filtered, CancellationToken ct)
    {
        var rows = await db.Categories.AsNoTracking().Where(c => c.FullWorthSpaceId == fullWorthSpaceId)
            .Select(c => new { c.Id, c.Name, c.ParentId }).ToListAsync(ct);
        var byId = rows.ToDictionary(c => c.Id);
        string Root(Guid? id)
        {
            if (!id.HasValue || !byId.TryGetValue(id.Value, out var category)) return "Uncategorized";
            var seen = new HashSet<Guid>();
            while (category.ParentId.HasValue && byId.TryGetValue(category.ParentId.Value, out var parent) && seen.Add(category.Id)) category = parent;
            return category.Name;
        }

        var income = FinancialReconciliationService.Income(filtered);
        var expenseGroups = filtered.Where(item => item.Kind is ContributionKinds.Expense or ContributionKinds.Refund)
            .GroupBy(item => Root(item.CategoryId))
            .Select(group => new { name = group.Key, value = FinancialReconciliationService.Spend(group) })
            .Where(row => row.value > 0).OrderByDescending(row => row.value).ToArray();
        var spent = expenseGroups.Sum(row => row.value);
        var remaining = Math.Max(0m, income - spent);
        var deficit = Math.Max(0m, spent - income);
        var nodes = new List<object> { new { id = "income", name = "Income" }, new { id = "available", name = "Available income" } };
        nodes.AddRange(expenseGroups.Select((row, index) => (object)new { id = $"cat-{index}", name = row.name }));
        if (remaining > 0) nodes.Add(new { id = "remaining", name = "Remaining" });
        if (deficit > 0) nodes.Add(new { id = "deficit", name = "Deficit" });
        var links = new List<object> { new { source = "income", target = "available", value = income } };
        if (deficit > 0) links.Add(new { source = "deficit", target = "available", value = deficit });
        for (var i = 0; i < expenseGroups.Length; i++) links.Add(new { source = "available", target = $"cat-{i}", value = expenseGroups[i].value });
        if (remaining > 0) links.Add(new { source = "available", target = "remaining", value = remaining });
        return new
        {
            currency = loaded.ReportingCurrency,
            incomplete = loaded.IncompleteFx,
            nodes,
            links,
            reconciles = Math.Round(income + deficit - spent - remaining, 2) == 0m
        };
    }

    private static decimal Measure(IReadOnlyCollection<CanonicalContribution> items, string measure)
    {
        if (items.Count == 0) return 0m;
        return measure.ToLowerInvariant() switch
        {
            "spend" => FinancialReconciliationService.Spend(items),
            "income" => FinancialReconciliationService.Income(items),
            "net" => FinancialReconciliationService.Net(items),
            "count" => items.Select(item => item.TransactionId).Distinct().Count(),
            "average" => Math.Round(items.GroupBy(item => item.TransactionId).Select(group => Math.Abs(group.Sum(item => item.ReportingAmount))).Average(), 2),
            "median" => Median(items.GroupBy(item => item.TransactionId).Select(group => Math.Abs(group.Sum(item => item.ReportingAmount)))),
            _ => FinancialReconciliationService.Spend(items)
        };
    }

    private static decimal Median(IEnumerable<decimal> source)
    {
        var values = source.OrderBy(x => x).ToArray();
        if (values.Length == 0) return 0m;
        return values.Length % 2 == 1 ? values[values.Length / 2] : Math.Round((values[values.Length / 2 - 1] + values[values.Length / 2]) / 2m, 2);
    }

    private static IEnumerable<string> DimensionKeys(CanonicalContribution item, string dimension,
        IReadOnlyDictionary<Guid, string> categories, IReadOnlyDictionary<Guid, string> accounts,
        IReadOnlyDictionary<Guid, string> tags, IReadOnlyDictionary<Guid, string> contracts)
    {
        switch (dimension.ToLowerInvariant())
        {
            case "day": yield return item.Date.ToString("yyyy-MM-dd"); yield break;
            case "week": yield return item.Date.AddDays(-(((int)item.Date.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd"); yield break;
            case "quarter": yield return $"{item.Date.Year}-Q{((item.Date.Month - 1) / 3) + 1}"; yield break;
            case "year": yield return item.Date.Year.ToString(); yield break;
            case "category": yield return item.CategoryId.HasValue ? categories.GetValueOrDefault(item.CategoryId.Value, "Uncategorized") : "Uncategorized"; yield break;
            case "merchant": yield return item.Merchant; yield break;
            case "account": yield return accounts.GetValueOrDefault(item.AccountId, "Unknown account"); yield break;
            case "tag":
                if (item.TagIds.Count == 0) { yield return "Untagged"; yield break; }
                foreach (var id in item.TagIds) yield return tags.GetValueOrDefault(id, "Unknown tag");
                yield break;
            case "contract":
                if (item.ContractIds.Count == 0) { yield return "No contract"; yield break; }
                foreach (var id in item.ContractIds) yield return contracts.GetValueOrDefault(id, "Unknown contract");
                yield break;
            default: yield return $"{item.Date.Year}-{item.Date.Month:00}"; yield break;
        }
    }

    private static string? ValidateAnalysis(AnalysisQueryWrite request)
    {
        if (!new[] { "spend", "income", "net", "count", "average", "median" }.Contains(request.Measure, StringComparer.OrdinalIgnoreCase)) return "Unsupported measure.";
        if (!new[] { "day", "week", "month", "quarter", "year", "category", "merchant", "account", "tag", "contract" }.Contains(request.Dimension, StringComparer.OrdinalIgnoreCase)) return "Unsupported dimension.";
        if (request.From.HasValue && request.To.HasValue && request.From > request.To) return "Invalid date range.";
        if (!string.IsNullOrWhiteSpace(request.RefundMode) && request.RefundMode is not ("reverse" or "income" or "exclude")) return "Unsupported refund mode.";
        return null;
    }

    private async Task<HashSet<Guid>> ExpandCategories(Guid fullWorthSpaceId, IReadOnlyList<AnalysisCategoryScope> scopes, CancellationToken ct)
    {
        var result = scopes.Select(scope => scope.CategoryId).ToHashSet();
        if (scopes.Count == 0) return result;
        var descendantRoots = scopes.Where(scope => scope.IncludeDescendants).Select(scope => scope.CategoryId).ToHashSet();
        var rows = await db.Categories.AsNoTracking().Where(c => c.FullWorthSpaceId == fullWorthSpaceId).Select(c => new { c.Id, c.ParentId }).ToListAsync(ct);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var row in rows)
            {
                if (row.ParentId.HasValue && descendantRoots.Contains(row.ParentId.Value) && descendantRoots.Add(row.Id))
                {
                    result.Add(row.Id);
                    changed = true;
                }
            }
        }
        return result;
    }

    private Task<HashSet<Guid>> ExpandCategories(Guid fullWorthSpaceId, IReadOnlyList<CategoryScopeWrite> scopes, CancellationToken ct) =>
        ExpandCategories(fullWorthSpaceId, scopes.Select(scope => new AnalysisCategoryScope(scope.CategoryId, scope.IncludeDescendants)).ToArray(), ct);

    private async Task<Dictionary<Guid, string>> LoadNames(string table, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, $"SELECT \"Id\",\"Name\" FROM \"{table}\" WHERE \"FullWorthSpaceId\"=@space", ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[ParitySql.Guid(reader, "Id")] = ParitySql.String(reader, "Name");
        return result;
    }

    private async Task<BudgetScope> LoadBudgetScope(Guid budgetId, CancellationToken ct)
    {
        var categories = new List<CategoryScopeWrite>();
        var accounts = new List<Guid>();
        var tags = new List<Guid>();
        var merchants = new List<string>();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using (var command = ParitySql.Command(connection, "SELECT \"CategoryId\",\"IncludeDescendants\" FROM \"BudgetCategories\" WHERE \"BudgetId\"=@id", ("@id", budgetId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) categories.Add(new CategoryScopeWrite(ParitySql.Guid(reader, "CategoryId"), ParitySql.Bool(reader, "IncludeDescendants")));
        await using (var command = ParitySql.Command(connection, "SELECT \"AccountId\" FROM \"BudgetAccounts\" WHERE \"BudgetId\"=@id", ("@id", budgetId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) accounts.Add(ParitySql.Guid(reader, "AccountId"));
        await using (var command = ParitySql.Command(connection, "SELECT \"TagId\" FROM \"BudgetTags\" WHERE \"BudgetId\"=@id", ("@id", budgetId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tags.Add(ParitySql.Guid(reader, "TagId"));
        await using (var command = ParitySql.Command(connection, "SELECT \"NormalizedMerchant\" FROM \"BudgetMerchants\" WHERE \"BudgetId\"=@id", ("@id", budgetId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) merchants.Add(ParitySql.String(reader, "NormalizedMerchant"));
        return new BudgetScope(categories, accounts, tags, merchants);
    }

    private async Task<CashflowSettings> LoadCashflowSettings(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, "SELECT \"HorizonMode\",\"SafetyReserveAmount\",\"SafetyReserveCurrency\",\"IncludePendingIncome\",\"IncludePendingExpenses\" FROM \"CashflowPlanSettings\" WHERE \"FullWorthSpaceId\"=@space", ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CashflowSettings(ParitySql.String(reader, "HorizonMode"), ParitySql.Decimal(reader, "SafetyReserveAmount"), ParitySql.String(reader, "SafetyReserveCurrency"), ParitySql.Bool(reader, "IncludePendingIncome"), ParitySql.Bool(reader, "IncludePendingExpenses"))
            : new CashflowSettings("next_income", 0m, "EUR", false, false);
    }

    private async Task<List<IncomeScheduleRow>> LoadIncomeSchedules(Guid fullWorthSpaceId, HashSet<Guid> visible, CancellationToken ct)
    {
        var result = new List<IncomeScheduleRow>();
        var connection = await ParitySql.OpenAsync(db, ct);
        await using var command = ParitySql.Command(connection, "SELECT \"Name\",\"AccountId\",\"NormalizedCounterparty\",\"ExpectedAmount\",\"Currency\",\"NextExpectedDate\" FROM \"IncomeSchedules\" WHERE \"FullWorthSpaceId\"=@space AND \"IsActive\"=true", ("@space", fullWorthSpaceId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var accountId = ParitySql.NullableGuid(reader, "AccountId");
            if (accountId.HasValue && !visible.Contains(accountId.Value)) continue;
            result.Add(new IncomeScheduleRow(
                ParitySql.String(reader, "Name"),
                MerchantNormalization.Normalize(ParitySql.NullableString(reader, "NormalizedCounterparty")),
                ParitySql.NullableDecimal(reader, "ExpectedAmount"),
                ParitySql.String(reader, "Currency"),
                ParitySql.NullableDate(reader, "NextExpectedDate")));
        }
        return result;
    }

    private sealed record BudgetScope(List<CategoryScopeWrite> Categories, List<Guid> AccountIds, List<Guid> TagIds, List<string> Merchants);
    private sealed record CashflowSettings(string HorizonMode, decimal Reserve, string ReserveCurrency, bool IncludePendingIncome, bool IncludePendingExpenses);
    private sealed record IncomeScheduleRow(string Name, string? NormalizedCounterparty, decimal? Amount, string Currency, DateOnly? NextDate);
}

/// <summary>
/// Compatibility adapter for the compact parity handlers. It keeps the public routes stable while all
/// financial reports use the same contribution semantics for splits, refunds, transfers, tags, contracts
/// and FX. Once the original handlers are folded into the shared engine this middleware can be removed
/// without changing clients.
/// </summary>
public sealed class FinancialReconciliationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUserContext currentUser, FinancialReconciliationReportService reports)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var handlesAnalytics = HttpMethods.IsPost(context.Request.Method) &&
            (path.Equals("/api/analytics/query", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/analytics/sankey", StringComparison.OrdinalIgnoreCase));
        var handlesCashflow = HttpMethods.IsGet(context.Request.Method) && path.Equals("/api/cashflow/available", StringComparison.OrdinalIgnoreCase);
        var budgetId = Guid.Empty;
        var handlesBudget = HttpMethods.IsGet(context.Request.Method) && TryBudgetStatusPath(path, out budgetId);
        if (!handlesAnalytics && !handlesCashflow && !handlesBudget)
        {
            await next(context);
            return;
        }

        var ct = context.RequestAborted;
        var userId = currentUser.RequireUserId();
        try
        {
            if (handlesAnalytics)
            {
                var request = await context.Request.ReadFromJsonAsync<AnalysisQueryWrite>(cancellationToken: ct);
                if (request is null) { context.Response.StatusCode = 400; return; }
                var sankey = path.Equals("/api/analytics/sankey", StringComparison.OrdinalIgnoreCase);
                var result = await reports.AnalyticsAsync(userId, RequiredSpace(context), request, sankey, ct);
                await Write(context, result, ct); return;
            }
            if (handlesCashflow)
            {
                var result = await reports.CashflowAvailableAsync(userId, RequiredSpace(context), OptionalDate(context, "asOf"), ct);
                await Write(context, result, ct); return;
            }
            if (handlesBudget)
            {
                var result = await reports.BudgetStatusAsync(userId, RequiredSpace(context), budgetId, OptionalDate(context, "asOf"), ct);
                await Write(context, result, ct); return;
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message }, cancellationToken: ct);
            return;
        }
        catch (ArgumentException exception)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message }, cancellationToken: ct);
            return;
        }
    }

    private static Guid RequiredSpace(HttpContext context)
    {
        if (Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var id)) return id;
        throw new ArgumentException("fullWorthSpaceId is required.");
    }

    private static DateOnly? OptionalDate(HttpContext context, string name) =>
        DateOnly.TryParse(context.Request.Query[name], out var date) ? date : null;

    private static bool TryBudgetStatusPath(string path, out Guid budgetId)
    {
        budgetId = Guid.Empty;
        const string prefix = "/api/budget-scopes/";
        const string suffix = "/status";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        var raw = path[prefix.Length..^suffix.Length].Trim('/');
        return Guid.TryParse(raw, out budgetId);
    }

    private static async Task Write(HttpContext context, object? result, CancellationToken ct)
    {
        if (result is null) { context.Response.StatusCode = 404; return; }
        context.Response.StatusCode = 200;
        await context.Response.WriteAsJsonAsync(result, cancellationToken: ct);
    }
}
