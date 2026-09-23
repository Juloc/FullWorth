using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.Reconciliation;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Budgets;

/// <summary>Worauf ein Budget zaehlt: Kategorien, Konten, Etiketten, Haendler - und die Schwellen.</summary>
public sealed record LoadedScope(
    List<CategoryScopeWrite> Categories, List<Guid> AccountIds, List<Guid> TagIds, List<string> Merchants,
    Guid? IncomeScheduleId, decimal AlertNearPercent, decimal AlertCriticalPercent, Guid? GroupId,
    bool PartialAccess = false);

/// <summary>Der Gehaltsrhythmus eines Budgets, das an den Lohn gekoppelt ist.</summary>
public sealed record IncomeRhythm(DateOnly? NextExpectedDate, string Cycle, int Interval);

/// <summary>
/// Der Geltungsbereich eines Budgets und alles, was dafuer gelesen und geschrieben wird.
///
/// Der Bereich liegt in vier Tabellen (Kategorien, Konten, Etiketten, Haendler) plus den
/// Einstellungen. Beim Speichern werden alle vier zuerst geleert und dann neu gefuellt, in EINER
/// Transaktion - ein Budget mit halb gesetztem Bereich zaehlt falsch, und niemand sieht es.
/// </summary>
public sealed class BudgetScopeStore(FullWorthDbContext db, AuditService audit)
{
    public Task<bool> BudgetExistsAsync(Guid fullWorthSpaceId, Guid budgetId, CancellationToken ct) =>
        db.Budgets.AsNoTracking()
            .AnyAsync(budget => budget.Id == budgetId && budget.FullWorthSpaceId == fullWorthSpaceId, ct);

    public Task<Budget?> FindBudgetAsync(Guid fullWorthSpaceId, Guid budgetId, CancellationToken ct) =>
        db.Budgets.AsNoTracking().SingleOrDefaultAsync(budget =>
            budget.Id == budgetId && budget.FullWorthSpaceId == fullWorthSpaceId, ct);

    public Task<List<Budget>> OtherActiveBudgetsAsync(
        Guid fullWorthSpaceId, Guid exceptBudgetId, CancellationToken ct) =>
        db.Budgets.AsNoTracking()
            .Where(budget => budget.FullWorthSpaceId == fullWorthSpaceId
                          && budget.Id != exceptBudgetId
                          && budget.IsActive)
            .ToListAsync(ct);

    public async Task<LoadedScope> LoadScopeAsync(Guid budgetId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        var categories = new List<CategoryScopeWrite>();
        var accounts = new List<Guid>();
        var tags = new List<Guid>();
        var merchants = new List<string>();

        await using (var cmd = RawSql.Command(connection,
            "SELECT \"CategoryId\",\"IncludeDescendants\" FROM \"BudgetCategories\" WHERE \"BudgetId\"=@b", ("@b", budgetId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                categories.Add(new CategoryScopeWrite(RawSql.Guid(reader, "CategoryId"), RawSql.Bool(reader, "IncludeDescendants")));

        await using (var cmd = RawSql.Command(connection,
            "SELECT \"AccountId\" FROM \"BudgetAccounts\" WHERE \"BudgetId\"=@b", ("@b", budgetId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) accounts.Add(RawSql.Guid(reader, "AccountId"));

        await using (var cmd = RawSql.Command(connection,
            "SELECT \"TagId\" FROM \"BudgetTags\" WHERE \"BudgetId\"=@b", ("@b", budgetId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tags.Add(RawSql.Guid(reader, "TagId"));

        await using (var cmd = RawSql.Command(connection,
            "SELECT \"NormalizedMerchant\" FROM \"BudgetMerchants\" WHERE \"BudgetId\"=@b", ("@b", budgetId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) merchants.Add(RawSql.String(reader, "NormalizedMerchant"));

        // Ohne eigene Einstellungen gelten 80 % als "wird knapp" und 100 % als "ueberschritten".
        Guid? income = null, group = null;
        decimal near = 80, critical = 100;
        await using (var cmd = RawSql.Command(connection,
            "SELECT \"IncomeScheduleId\",\"AlertNearPercent\",\"AlertCriticalPercent\",\"GroupId\" FROM \"BudgetAdvancedSettings\" WHERE \"BudgetId\"=@b", ("@b", budgetId)))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                income = RawSql.NullableGuid(reader, "IncomeScheduleId");
                near = RawSql.Decimal(reader, "AlertNearPercent");
                critical = RawSql.Decimal(reader, "AlertCriticalPercent");
                group = RawSql.NullableGuid(reader, "GroupId");
            }

        return new LoadedScope(categories, accounts, tags, merchants, income, near, critical, group);
    }

    /// <summary>
    /// Ein Gehaltsplan ist lesbar, wenn er an keinem Konto haengt oder an einem sichtbaren. Ohne
    /// Space-Angabe wird nur ueber die Id gesucht - so prueft die Redaktion einen Plan, dessen
    /// Herkunft sie noch nicht kennt.
    /// </summary>
    public async Task<bool> CanReadIncomeScheduleAsync(
        Guid fullWorthSpaceId, Guid scheduleId, IReadOnlySet<Guid> visibleAccountIds, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = fullWorthSpaceId == Guid.Empty
            ? RawSql.Command(connection, "SELECT \"AccountId\" FROM \"IncomeSchedules\" WHERE \"Id\"=@id", ("@id", scheduleId))
            : RawSql.Command(connection, "SELECT \"AccountId\" FROM \"IncomeSchedules\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space", ("@id", scheduleId), ("@space", fullWorthSpaceId));

        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null) return false;
        return value is DBNull || visibleAccountIds.Contains((Guid)value);
    }

    public async Task<bool> TagsExistAsync(Guid fullWorthSpaceId, Guid[] tagIds, CancellationToken ct)
    {
        if (tagIds.Length == 0) return true;

        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT count(*) FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space AND \"Id\"=ANY(@ids)",
            ("@space", fullWorthSpaceId), ("@ids", tagIds));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == tagIds.Length;
    }

    public async Task<bool> ActiveGroupExistsAsync(Guid fullWorthSpaceId, Guid groupId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT 1 FROM \"BudgetGroups\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space AND \"IsArchived\"=false",
            ("@id", groupId), ("@space", fullWorthSpaceId));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public Task<int> CountCategoriesAsync(Guid fullWorthSpaceId, Guid[] categoryIds, CancellationToken ct) =>
        db.Categories.CountAsync(category =>
            category.FullWorthSpaceId == fullWorthSpaceId && categoryIds.Contains(category.Id), ct);

    public async Task SaveScopeAsync(
        Guid userId, Guid fullWorthSpaceId, Guid budgetId,
        IReadOnlyList<CategoryScopeWrite> categories, IReadOnlyList<Guid> accounts, IReadOnlyList<Guid> tags,
        IReadOnlyList<string> merchants, Guid? incomeScheduleId, decimal alertNear, decimal alertCritical,
        Guid? groupId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await RawSql.OpenAsync(db, ct);

        foreach (var table in new[] { "BudgetCategories", "BudgetAccounts", "BudgetTags", "BudgetMerchants" })
        {
            await using var delete = RawSql.Command(connection, $"DELETE FROM \"{table}\" WHERE \"BudgetId\"=@b", ("@b", budgetId));
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var category in categories)
        {
            await using var cmd = RawSql.Command(connection,
                "INSERT INTO \"BudgetCategories\" (\"BudgetId\",\"CategoryId\",\"IncludeDescendants\") VALUES (@b,@c,@d)",
                ("@b", budgetId), ("@c", category.CategoryId), ("@d", category.IncludeDescendants));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var accountId in accounts)
        {
            await using var cmd = RawSql.Command(connection,
                "INSERT INTO \"BudgetAccounts\" (\"BudgetId\",\"AccountId\") VALUES (@b,@a)",
                ("@b", budgetId), ("@a", accountId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var tagId in tags)
        {
            await using var cmd = RawSql.Command(connection,
                "INSERT INTO \"BudgetTags\" (\"BudgetId\",\"TagId\") VALUES (@b,@t)",
                ("@b", budgetId), ("@t", tagId));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var merchant in merchants)
        {
            await using var cmd = RawSql.Command(connection,
                "INSERT INTO \"BudgetMerchants\" (\"BudgetId\",\"NormalizedMerchant\") VALUES (@b,@m)",
                ("@b", budgetId), ("@m", merchant));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var advanced = RawSql.Command(connection, """
INSERT INTO "BudgetAdvancedSettings" ("BudgetId","IncomeScheduleId","AlertNearPercent","AlertCriticalPercent","ScopeVersion","GroupId","UpdatedAt") VALUES (@b,@income,@near,@critical,1,@group,@now)
ON CONFLICT ("BudgetId") DO UPDATE SET "IncomeScheduleId"=EXCLUDED."IncomeScheduleId","AlertNearPercent"=EXCLUDED."AlertNearPercent","AlertCriticalPercent"=EXCLUDED."AlertCriticalPercent","ScopeVersion"=EXCLUDED."ScopeVersion","GroupId"=EXCLUDED."GroupId","UpdatedAt"=EXCLUDED."UpdatedAt"
""", ("@b", budgetId), ("@income", incomeScheduleId), ("@near", alertNear), ("@critical", alertCritical),
            ("@group", groupId), ("@now", DateTimeOffset.UtcNow)))
            await advanced.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "budget.scope.updated", "Budget", budgetId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>Die Buchungen im Budgetfenster - ignorierte, Umbuchungen und vorgemerkte zaehlen nie.</summary>
    public Task<List<FinanceTransaction>> TransactionsInWindowAsync(
        IReadOnlySet<Guid> visibleAccountIds, DateOnly from, DateOnly to, CancellationToken ct) =>
        db.Transactions.AsNoTracking()
            .Where(transaction => visibleAccountIds.Contains(transaction.AccountId)
                               && !transaction.IsIgnored
                               && !transaction.IsTransfer
                               && transaction.Status != "PDNG"
                               && (transaction.BookingDate ?? transaction.ValueDate) >= from
                               && (transaction.BookingDate ?? transaction.ValueDate) <= to)
            .ToListAsync(ct);

    public async Task<HashSet<Guid>> ExpandCategoryScopesAsync(
        Guid fullWorthSpaceId, IReadOnlyList<CategoryScopeWrite> scopes, CancellationToken ct)
    {
        var result = scopes.Select(scope => scope.CategoryId).ToHashSet();
        if (scopes.Count == 0) return result;

        var all = await db.Categories.AsNoTracking()
            .Where(category => category.FullWorthSpaceId == fullWorthSpaceId)
            .Select(category => new { category.Id, category.ParentId })
            .ToListAsync(ct);

        var descendants = scopes.Where(scope => scope.IncludeDescendants)
            .Select(scope => scope.CategoryId).ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var category in all)
                if (category.ParentId.HasValue && descendants.Contains(category.ParentId.Value)
                    && descendants.Add(category.Id))
                {
                    result.Add(category.Id);
                    changed = true;
                }
        }
        return result;
    }

    /// <summary>Der Gehaltsrhythmus hinter einem gehaltsgekoppelten Budget, falls einer gesetzt ist.</summary>
    public async Task<IncomeRhythm?> IncomeRhythmAsync(Guid budgetId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT s.\"NextExpectedDate\",s.\"Cycle\",s.\"Interval\" FROM \"BudgetAdvancedSettings\" a JOIN \"IncomeSchedules\" s ON s.\"Id\"=a.\"IncomeScheduleId\" WHERE a.\"BudgetId\"=@b",
            ("@b", budgetId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new IncomeRhythm(
            RawSql.NullableDate(reader, "NextExpectedDate"),
            RawSql.String(reader, "Cycle"),
            RawSql.Int(reader, "Interval"));
    }

    public async Task<IReadOnlyList<BudgetGroupRow>> ListGroupsAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "SELECT \"Id\",\"Name\",\"SortOrder\",\"IsArchived\" FROM \"BudgetGroups\" WHERE \"FullWorthSpaceId\"=@s ORDER BY \"SortOrder\",\"Name\"",
            ("@s", fullWorthSpaceId));
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var rows = new List<BudgetGroupRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new BudgetGroupRow(
                RawSql.Guid(reader, "Id"), RawSql.String(reader, "Name"),
                RawSql.Int(reader, "SortOrder"), RawSql.Bool(reader, "IsArchived")));
        return rows;
    }

    public async Task<Guid> CreateGroupAsync(
        Guid userId, Guid fullWorthSpaceId, string name, int sortOrder, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = RawSql.Command(connection,
            "INSERT INTO \"BudgetGroups\" (\"Id\",\"FullWorthSpaceId\",\"Name\",\"SortOrder\",\"IsArchived\",\"CreatedAt\",\"UpdatedAt\") VALUES (@id,@s,@n,@o,false,@now,@now)",
            ("@id", id), ("@s", fullWorthSpaceId), ("@n", name), ("@o", sortOrder), ("@now", DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);

        audit.Record(fullWorthSpaceId, userId, "budget_group.created", "BudgetGroup", id);
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>Archivieren statt loeschen: ein Budget koennte noch auf die Gruppe zeigen.</summary>
    public async Task<bool> WriteGroupAsync(
        Guid userId, Guid fullWorthSpaceId, Guid groupId, string name, int sortOrder, bool archive,
        CancellationToken ct)
    {
        var connection = await RawSql.OpenAsync(db, ct);
        await using var cmd = archive
            ? RawSql.Command(connection,
                "UPDATE \"BudgetGroups\" SET \"IsArchived\"=true,\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@s",
                ("@now", DateTimeOffset.UtcNow), ("@id", groupId), ("@s", fullWorthSpaceId))
            : RawSql.Command(connection,
                "UPDATE \"BudgetGroups\" SET \"Name\"=@n,\"SortOrder\"=@o,\"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@s",
                ("@n", name), ("@o", sortOrder), ("@now", DateTimeOffset.UtcNow), ("@id", groupId), ("@s", fullWorthSpaceId));
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return false;

        audit.Record(fullWorthSpaceId, userId,
            archive ? "budget_group.archived" : "budget_group.updated", "BudgetGroup", groupId);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Eine Budgetgruppe, wie die Liste sie zeigt.</summary>
public sealed record BudgetGroupRow(Guid Id, string Name, int SortOrder, bool IsArchived);
