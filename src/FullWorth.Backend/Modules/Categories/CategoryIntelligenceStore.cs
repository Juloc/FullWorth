using System.Data;
using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Categories;

/// <summary>Ob eine Buchung von Hand als geprueft markiert wurde.</summary>
public sealed record TransactionReviewState(bool IsReviewed);

/// <summary>Was eine bestaetigte Zuordnung ueber ihre Kategorie mitteilen muss.</summary>
public sealed record CategoryLearningFacts(string Key, string Name, bool IsSystem);

/// <summary>Die Farbe, die eine Kategorie in der Oberflaeche bekommt.</summary>
public sealed record CategoryAppearance(Guid CategoryId, string? Color);

/// <summary>
/// Buchungen, Regeln, Etiketten, Pruefzustaende und Kategoriefarben fuer die Kategorie-Ansicht.
///
/// Eine Sache zieht sich durch alles: <see cref="AccessibleTransactions"/> ist die einzige Stelle, an
/// der entschieden wird, welche Buchungen jemand sehen darf. Sie kennt zwei Stufen - Mitlesende
/// duerfen lesen, aendern darf nur, wer <c>Owner</c> des Kontos ist. Jede Abfrage und jede Aenderung
/// hier geht durch sie hindurch; wer an ihr vorbei auf <c>db.Transactions</c> zugreift, umgeht die
/// Kontofreigabe.
/// </summary>
public sealed class CategoryIntelligenceStore(FullWorthDbContext db)
{
    public Task<bool> IsMemberAsync(Guid userId, Guid space, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking()
            .AnyAsync(member => member.FullWorthSpaceId == space && member.UserId == userId, ct);

    public Task<bool> CanCategorizeAsync(Guid userId, Guid space, CancellationToken ct) =>
        SpaceCapabilities.HasCapabilityAsync(db, userId, space, "transactions.categorize", ct);

    /// <summary>Die juengsten Buchungen, die jemand sehen darf - die Ansicht zeigt nicht mehr.</summary>
    public Task<List<FinanceTransaction>> RecentVisibleAsync(
        Guid userId, Guid space, int take, CancellationToken ct) =>
        AccessibleTransactions(userId, space, ownerOnly: false)
            .AsNoTracking()
            .OrderByDescending(transaction => transaction.BookingDate)
            .ThenByDescending(transaction => transaction.UpdatedAt)
            .Take(take)
            .ToListAsync(ct);

    public Task<List<FinanceTransaction>> WritableAsync(
        Guid userId, Guid space, IReadOnlyList<Guid> ids, CancellationToken ct) =>
        AccessibleTransactions(userId, space, ownerOnly: true)
            .Where(transaction => ids.Contains(transaction.Id))
            .ToListAsync(ct);

    public Task<FinanceTransaction?> FindWritableAsync(
        Guid userId, Guid space, Guid transactionId, CancellationToken ct) =>
        AccessibleTransactions(userId, space, ownerOnly: true)
            .SingleOrDefaultAsync(transaction => transaction.Id == transactionId, ct);

    public Task<bool> IsWritableAsync(Guid userId, Guid space, Guid transactionId, CancellationToken ct) =>
        AccessibleTransactions(userId, space, ownerOnly: true)
            .AnyAsync(transaction => transaction.Id == transactionId, ct);

    /// <summary>Alle aenderbaren Buchungen einer Richtung - Ausgaben oder Einnahmen.</summary>
    public Task<List<FinanceTransaction>> WritableByDirectionAsync(
        Guid userId, Guid space, bool expenses, CancellationToken ct)
    {
        var query = AccessibleTransactions(userId, space, ownerOnly: true);
        return (expenses
            ? query.Where(transaction => transaction.Amount < 0)
            : query.Where(transaction => transaction.Amount > 0)).ToListAsync(ct);
    }

    public Task<List<CategorizationRule>> ActiveRulesAsync(Guid space, CancellationToken ct) =>
        db.CategorizationRules.AsNoTracking()
            .Where(rule => rule.FullWorthSpaceId == space && rule.IsEnabled && rule.Target == "transaction")
            .OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id)
            .ToListAsync(ct);

    public Task<bool> CategoryUsableAsync(Guid space, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking().AnyAsync(category =>
            category.Id == categoryId && category.FullWorthSpaceId == space && !category.IsArchived, ct);

    public Task<bool> CategoryExistsAsync(Guid space, Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking().AnyAsync(category =>
            category.Id == categoryId && category.FullWorthSpaceId == space, ct);

    /// <summary>
    /// Was die Cloud-Rueckmeldung ueber eine Kategorie braucht: ihr Schluessel, ihr Name und ob sie
    /// selbst angelegt wurde. Drei Felder aus einer Zeile - drei Abfragen dafuer waeren drei Wege,
    /// auf denen sie auseinanderlaufen koennen.
    /// </summary>
    public Task<CategoryLearningFacts?> CategoryFactsAsync(Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(category => category.Id == categoryId)
            .Select(category => new CategoryLearningFacts(category.Key, category.Name, category.IsSystem))
            .SingleOrDefaultAsync(ct);

    public Task<string> CategoryNameAsync(Guid categoryId, CancellationToken ct) =>
        db.Categories.AsNoTracking()
            .Where(category => category.Id == categoryId)
            .Select(category => category.Name)
            .SingleAsync(ct);

    /// <summary>Setzt Kategorie und Herkunft auf einer Menge von Buchungen und speichert.</summary>
    public async Task ApplyAsync(
        IReadOnlyList<FinanceTransaction> transactions, bool updateCategory, Guid? categoryId, string source,
        bool? isIgnored, CancellationToken ct)
    {
        foreach (var transaction in transactions)
        {
            if (updateCategory)
            {
                transaction.CategoryId = categoryId;
                transaction.CategorizationSource = source;
            }
            if (isIgnored.HasValue) transaction.IsIgnored = isIgnored.Value;
            transaction.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Die Regel zu einem Haendler und einer Richtung - vorhanden oder neu. Es gibt je Haendler und
    /// Richtung genau eine; eine zweite wuerde je nach Reihenfolge gewinnen oder verlieren.
    /// </summary>
    public async Task<Guid> SaveMerchantRuleAsync(
        Guid space, string merchant, string direction, Guid categoryId, string ruleName, CancellationToken ct)
    {
        var rule = await db.CategorizationRules.SingleOrDefaultAsync(candidate =>
            candidate.FullWorthSpaceId == space && candidate.Target == "transaction" &&
            candidate.MatchField == "normalized_counterparty" && candidate.MatchMode == "equals" &&
            candidate.Pattern == merchant && candidate.Direction == direction, ct);

        if (rule is null)
        {
            rule = new CategorizationRule
            {
                FullWorthSpaceId = space,
                Name = ruleName,
                IsEnabled = true,
                Priority = 10,
                Target = "transaction",
                MatchField = "normalized_counterparty",
                MatchMode = "equals",
                Pattern = merchant,
                Direction = direction,
                CategoryId = categoryId,
                StopProcessing = true,
                MarkAsTransfer = false
            };
            db.CategorizationRules.Add(rule);
        }
        else
        {
            rule.CategoryId = categoryId;
            rule.IsEnabled = true;
            rule.Priority = Math.Min(rule.Priority, 10);
            rule.StopProcessing = true;
            rule.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return rule.Id;
    }

    public async Task<Dictionary<Guid, TransactionReviewState>> ReviewStatesAsync(Guid space, CancellationToken ct)
    {
        var result = new Dictionary<Guid, TransactionReviewState>();
        await using var command = await CommandAsync(
            "SELECT \"TransactionId\", \"IsReviewed\" FROM \"TransactionReviewStates\" WHERE \"FullWorthSpaceId\"=@space",
            ct, ("space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result[reader.GetGuid(0)] = new(reader.GetBoolean(1));
        return result;
    }

    public Task SetReviewedAsync(Guid space, Guid transactionId, bool reviewed, CancellationToken ct) =>
        ExecuteAsync(
            "INSERT INTO \"TransactionReviewStates\" (\"TransactionId\", \"FullWorthSpaceId\", \"IsReviewed\", \"UpdatedAt\") VALUES (@transaction, @space, @reviewed, @now) ON CONFLICT (\"TransactionId\") DO UPDATE SET \"IsReviewed\"=EXCLUDED.\"IsReviewed\", \"UpdatedAt\"=EXCLUDED.\"UpdatedAt\"",
            ct, ("transaction", transactionId), ("space", space), ("reviewed", reviewed), ("now", DateTimeOffset.UtcNow));

    public async Task<IReadOnlyList<IntelligenceTag>> TagsAsync(Guid space, CancellationToken ct)
    {
        var result = new List<IntelligenceTag>();
        await using var command = await CommandAsync(
            "SELECT \"Id\", \"Name\", \"Color\" FROM \"FinanceTags\" WHERE \"FullWorthSpaceId\"=@space ORDER BY \"Name\"",
            ct, ("space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return result;
    }

    public async Task<Dictionary<Guid, IReadOnlyList<IntelligenceTag>>> TagsByTransactionAsync(
        Guid space, CancellationToken ct)
    {
        var buffer = new Dictionary<Guid, List<IntelligenceTag>>();
        await using var command = await CommandAsync(
            "SELECT tt.\"TransactionId\", t.\"Id\", t.\"Name\", t.\"Color\" FROM \"TransactionTags\" tt JOIN \"FinanceTags\" t ON t.\"Id\"=tt.\"TagId\" WHERE t.\"FullWorthSpaceId\"=@space ORDER BY t.\"Name\"",
            ct, ("space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var transactionId = reader.GetGuid(0);
            if (!buffer.TryGetValue(transactionId, out var items)) buffer[transactionId] = items = [];
            items.Add(new(reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return buffer.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<IntelligenceTag>)pair.Value);
    }

    public Task InsertTagAsync(
        Guid space, Guid id, string name, string normalized, string? color, CancellationToken ct) =>
        ExecuteAsync(
            "INSERT INTO \"FinanceTags\" (\"Id\", \"FullWorthSpaceId\", \"Name\", \"NormalizedName\", \"Color\", \"CreatedAt\", \"UpdatedAt\") VALUES (@id, @space, @name, @normalized, @color, @now, @now)",
            ct, ("id", id), ("space", space), ("name", name), ("normalized", normalized), ("color", color),
            ("now", DateTimeOffset.UtcNow));

    public async Task<bool> UpdateTagAsync(
        Guid space, Guid tagId, string name, string normalized, string? color, CancellationToken ct) =>
        await ExecuteAsync(
            "UPDATE \"FinanceTags\" SET \"Name\"=@name, \"NormalizedName\"=@normalized, \"Color\"=@color, \"UpdatedAt\"=@now WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",
            ct, ("name", name), ("normalized", normalized), ("color", color), ("now", DateTimeOffset.UtcNow),
            ("id", tagId), ("space", space)) > 0;

    public async Task<bool> DeleteTagAsync(Guid space, Guid tagId, CancellationToken ct) =>
        await ExecuteAsync("DELETE FROM \"FinanceTags\" WHERE \"Id\"=@id AND \"FullWorthSpaceId\"=@space",
            ct, ("id", tagId), ("space", space)) > 0;

    // AddTagAsync und RemoveTagAsync standen hier bis #177 und hatten nach dem Loeschen der
    // dritten Massenaenderung keinen Aufrufer mehr. Stichworte an Buchungen setzt seither nur noch
    // /api/transaction-bulk/apply, und das benutzt seinen eigenen Store.

    public Task ClearTagsAsync(Guid transactionId, CancellationToken ct) =>
        ExecuteAsync("DELETE FROM \"TransactionTags\" WHERE \"TransactionId\"=@transaction",
            ct, ("transaction", transactionId));

    public async Task<IReadOnlyList<CategoryAppearance>> AppearancesAsync(Guid space, CancellationToken ct)
    {
        var result = new List<CategoryAppearance>();
        await using var command = await CommandAsync(
            "SELECT \"CategoryId\", \"Color\" FROM \"CategoryAppearances\" WHERE \"FullWorthSpaceId\"=@space",
            ct, ("space", space));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return result;
    }

    public Task SetAppearanceAsync(Guid space, Guid categoryId, string? color, CancellationToken ct) =>
        ExecuteAsync(
            "INSERT INTO \"CategoryAppearances\" (\"CategoryId\", \"FullWorthSpaceId\", \"Color\", \"UpdatedAt\") VALUES (@category, @space, @color, @now) ON CONFLICT (\"CategoryId\") DO UPDATE SET \"Color\"=EXCLUDED.\"Color\", \"UpdatedAt\"=EXCLUDED.\"UpdatedAt\"",
            ct, ("category", categoryId), ("space", space), ("color", color), ("now", DateTimeOffset.UtcNow));

    /// <summary>
    /// Lesen darf, wer Mitglied des Bereichs und am Konto beteiligt ist; aendern nur, wer dem Konto
    /// als <c>Owner</c> zugeordnet ist.
    /// </summary>
    private IQueryable<FinanceTransaction> AccessibleTransactions(Guid userId, Guid space, bool ownerOnly) =>
        db.Transactions.Where(transaction => db.Accounts.Any(account =>
            account.Id == transaction.AccountId && account.FullWorthSpaceId == space &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == space && member.UserId == userId) &&
            account.Owners.Any(owner =>
                owner.UserId == userId && (!ownerOnly || owner.OwnershipType == AccountOwnershipTypes.Owner))));

    private async Task<int> ExecuteAsync(
        string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        await using var command = await CommandAsync(sql, ct, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<DbCommand> CommandAsync(
        string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync(ct);

        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
        return command;
    }
}
