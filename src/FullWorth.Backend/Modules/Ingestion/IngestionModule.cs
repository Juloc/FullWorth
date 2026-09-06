using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Import;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Modules.Merchants;
using FullWorth.Backend.Modules.Transactions;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Ingestion;

// FullWorthSpaceId is only consulted when the connection does not yet exist (which never happens on the
// production sync path — connect creates it first and ingest references it by ConnectionId). It must
// be an explicit, existing space; there is no silent LegacyId default any more (P0.2).
public sealed record BankConnectionBatch(Guid? ConnectionId, string Provider, string InstitutionName, string Country, string? ProviderSessionId, string Status, DateTimeOffset? ValidUntil, DateTimeOffset? LastSyncedAt, string? LastError, Guid? FullWorthSpaceId = null);
// HasDetails=false marks metadata as placeholder-quality (the provider's session payload carried no
// account details and the details resource was unavailable): it may seed a NEW account but must not
// overwrite previously stored real names/currency on existing ones.
public sealed record AccountBatchItem(string IdentificationHash, string ProviderAccountId, string InstitutionName, string DisplayName, string? Product, string? AccountType, string Currency, string? IbanLast4, bool IsActive, bool HasDetails = true, IReadOnlyList<string>? IdentificationHashes = null, string? Usage = null, string? PsuStatus = null, decimal? CreditLimitAmount = null, string? CreditLimitCurrency = null);
public sealed record BalanceBatchItem(string IdentificationHash, decimal Amount, string Currency, string BalanceType, DateOnly? ReferenceDate, DateTimeOffset CapturedAt);
public sealed record TransactionBatchItem(string IdentificationHash, string ExternalKey, string? ProviderTransactionId, string Status, DateOnly? BookingDate, DateOnly? ValueDate, decimal Amount, string Currency, string? Counterparty, string? Description, string? MerchantCategoryCode, string? EntryReference, string RawJson);
public sealed record FinanceIngestBatch(BankConnectionBatch Connection, IReadOnlyList<AccountBatchItem> Accounts, IReadOnlyList<BalanceBatchItem> Balances, IReadOnlyList<TransactionBatchItem> Transactions);

public sealed class IngestionService(
    FullWorthDbContext db,
    AuditService? auditService = null,
    FullWorth.Backend.Security.FieldCipher? fieldCipher = null,
    FullWorth.Backend.Modules.Notifications.BudgetNotificationService? budgetNotifications = null,
    FinanzguruAccountReconciliationService? finanzguruReconciliation = null,
    IntelligenceDbContext? intelligenceDb = null,
    CloudOntologyResolver? cloudOntologyResolver = null)
{
    private readonly FullWorth.Backend.Security.FieldCipher cipher = fieldCipher ?? FullWorth.Backend.Security.FieldCipher.Null;
    private readonly AuditService audit = auditService ?? new AuditService(db);
    public async Task<object> IngestAsync(FinanceIngestBatch batch, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var connection = await UpsertConnectionAsync(batch.Connection, ct);
        var accountMap = await UpsertAccountsAsync(connection, batch.Connection.Provider, batch.Accounts, ct);
        var balanceCount = await InsertBalancesAsync(accountMap, batch.Balances, ct);
        var (inserted, updated) = await UpsertTransactionsAsync(
            connection.FullWorthSpaceId,
            batch.Connection.Country,
            accountMap,
            batch.Transactions,
            ct);

        if (finanzguruReconciliation is not null)
            await finanzguruReconciliation.ReconcileAsync(connection.FullWorthSpaceId, accountMap.Values, ct);

        connection.LastSyncedAt = batch.Connection.LastSyncedAt ?? DateTimeOffset.UtcNow;
        connection.LastError = batch.Connection.LastError;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Record(
            connection.FullWorthSpaceId,
            null,
            string.IsNullOrWhiteSpace(connection.LastError) ? "bank_connection.synced" : "bank_connection.error",
            "BankConnection",
            connection.Id);
        audit.Record(connection.FullWorthSpaceId, null, "external.write.used", "BankingIngestion", connection.Id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (budgetNotifications is not null && (inserted > 0 || updated > 0))
            await budgetNotifications.EvaluateAndDispatchAsync(connection.FullWorthSpaceId, DateOnly.FromDateTime(DateTime.UtcNow), ct);

        return new { connectionId = connection.Id, accounts = accountMap.Count, balances = balanceCount, transactionsInserted = inserted, transactionsUpdated = updated };
    }

    private async Task<BankConnection> UpsertConnectionAsync(BankConnectionBatch request, CancellationToken ct)
    {
        var entity = request.ConnectionId.HasValue ? await db.BankConnections.SingleOrDefaultAsync(x => x.Id == request.ConnectionId.Value, ct) : null;
        var sessionLookup = cipher.BlindIndex(request.ProviderSessionId);
        entity ??= !string.IsNullOrWhiteSpace(request.ProviderSessionId)
            ? await db.BankConnections.SingleOrDefaultAsync(x => x.Provider == request.Provider && x.ProviderSessionIdLookup == sessionLookup, ct)
            : null;
        if (entity is null)
        {
            if (request.FullWorthSpaceId is not { } space || space == Guid.Empty)
                throw new InvalidOperationException("Ingest references an unknown bank connection; connect must create it with a validated FullWorthSpaceId first.");
            if (!await db.FullWorthSpaces.AsNoTracking().AnyAsync(x => x.Id == space, ct))
                throw new InvalidOperationException("Ingest references an unknown FullWorthSpaceId.");
            entity = new BankConnection { FullWorthSpaceId = space };
            db.BankConnections.Add(entity);
        }
        entity.Provider = request.Provider; entity.InstitutionName = request.InstitutionName; entity.Country = request.Country;
        entity.ProviderSessionId = cipher.Protect(request.ProviderSessionId); entity.ProviderSessionIdLookup = sessionLookup; entity.Status = request.Status; entity.ValidUntil = request.ValidUntil;
        entity.LastSyncedAt = request.LastSyncedAt; entity.LastError = request.LastError; entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return entity;
    }

    private async Task<Dictionary<string, FinanceAccount>> UpsertAccountsAsync(BankConnection connection, string provider, IReadOnlyList<AccountBatchItem> items, CancellationToken ct)
    {
        // Enable Banking documents identification_hash as the cross-session account identifier.
        // identification_hashes is only for fuzzy matching and may contain values shared by different
        // accounts, so stored fuzzy aliases must never be treated as unique account keys.
        var storedAccounts = await db.Accounts
            .Where(x => x.FullWorthSpaceId == connection.FullWorthSpaceId && x.Provider == provider)
            .ToListAsync(ct);

        var byPrimaryHash = storedAccounts
            .Where(x => !string.IsNullOrWhiteSpace(x.IdentificationHash))
            .ToDictionary(x => x.IdentificationHash, x => x, StringComparer.Ordinal);

        var result = new Dictionary<string, FinanceAccount>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var incomingHashes = NormalizeHashes(item.IdentificationHash, item.IdentificationHashes);
            byPrimaryHash.TryGetValue(item.IdentificationHash, out var entity);
            var isNew = entity is null;

            if (entity is null)
            {
                // A changed primary hash is safe to reconcile when one of the incoming hashes equals
                // a STORED PRIMARY hash. Do not match against stored fuzzy aliases.
                var primaryMatches = incomingHashes
                    .Where(byPrimaryHash.ContainsKey)
                    .Select(hash => byPrimaryHash[hash])
                    .DistinctBy(x => x.Id)
                    .ToList();

                if (primaryMatches.Count > 1)
                    throw new InvalidOperationException(
                        "Enable Banking account identification hashes ambiguously match multiple stored primary accounts.");
                entity = primaryMatches.SingleOrDefault();
                isNew = entity is null;
            }

            if (isNew)
            {
                entity = new FinanceAccount
                {
                    FullWorthSpaceId = connection.FullWorthSpaceId,
                    Provider = provider,
                    IdentificationHash = item.IdentificationHash
                };
                db.Accounts.Add(entity);
                storedAccounts.Add(entity);
                byPrimaryHash[entity.IdentificationHash] = entity;
            }
            else if (!string.Equals(entity!.IdentificationHash, item.IdentificationHash, StringComparison.Ordinal))
            {
                // Promote the provider's current primary hash while preserving the previous primary as
                // an alias. Refuse to steal a primary hash already owned by a different account.
                if (byPrimaryHash.TryGetValue(item.IdentificationHash, out var other) && other.Id != entity.Id)
                    throw new InvalidOperationException(
                        "Enable Banking primary identification hash is already assigned to another account.");

                var previousPrimary = entity.IdentificationHash;
                entity.IdentificationHash = item.IdentificationHash;
                if (byPrimaryHash.TryGetValue(previousPrimary, out var previous) && previous.Id == entity.Id)
                    byPrimaryHash.Remove(previousPrimary);
                byPrimaryHash[entity.IdentificationHash] = entity;
            }

            if (entity!.FullWorthSpaceId != connection.FullWorthSpaceId)
                throw new InvalidOperationException("Account reconciliation cannot move accounts between FullWorth Spaces.");

            var mergedHashes = NormalizeHashes(
                entity.IdentificationHash,
                AccountHashes(entity).Concat(incomingHashes));
            entity.IdentificationHashesJson = JsonSerializer.Serialize(mergedHashes);

            var mayRefreshDisplayName = isNew ||
                string.IsNullOrWhiteSpace(entity.DisplayName) ||
                string.Equals(entity.DisplayName, entity.InstitutionName, StringComparison.OrdinalIgnoreCase);

            entity.BankConnectionId = connection.Id; entity.ProviderAccountId = item.ProviderAccountId; entity.InstitutionName = item.InstitutionName;
            if (item.HasDetails || isNew)
            {
                if (mayRefreshDisplayName) entity.DisplayName = item.DisplayName;
                entity.Product = item.Product; entity.AccountType = item.AccountType; entity.Currency = item.Currency;
                entity.Usage = item.Usage; entity.PsuStatus = item.PsuStatus;
                entity.CreditLimitAmount = item.CreditLimitAmount; entity.CreditLimitCurrency = item.CreditLimitCurrency;
                entity.IbanLast4 = item.IbanLast4;
            }
            entity.IsActive = item.IsActive; entity.UpdatedAt = DateTimeOffset.UtcNow;
            result[item.IdentificationHash] = entity;
        }
        await db.SaveChangesAsync(ct);
        await EnsureOrphanedAccountsHaveOwnerAsync(connection, result.Values, ct);
        return result;
    }

    private static IReadOnlyList<string> AccountHashes(FinanceAccount account)
    {
        var aliases = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(account.IdentificationHashesJson))
        {
            try { aliases = JsonSerializer.Deserialize<string[]>(account.IdentificationHashesJson) ?? []; }
            catch (JsonException) { /* keep the current primary hash below */ }
        }
        return NormalizeHashes(account.IdentificationHash, aliases);
    }

    private static IReadOnlyList<string> NormalizeHashes(string primary, IEnumerable<string>? aliases)
    {
        var hashes = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) hashes.Add(primary.Trim());
        if (aliases is not null)
            hashes.AddRange(aliases.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        return hashes.Distinct(StringComparer.Ordinal).ToArray();
    }

    private async Task EnsureOrphanedAccountsHaveOwnerAsync(BankConnection connection, IEnumerable<FinanceAccount> accounts, CancellationToken ct)
    {
        var accountIds = accounts.Select(x => x.Id).Distinct().ToArray();
        if (accountIds.Length == 0 || connection.AuthorizationUserId is not { } authorizationUserId || authorizationUserId == Guid.Empty)
            return;

        var authorizationUserIsSpaceOwner = await db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member =>
            member.FullWorthSpaceId == connection.FullWorthSpaceId &&
            member.UserId == authorizationUserId &&
            member.Role == FullWorthSpaceRoles.Owner, ct);
        if (!authorizationUserIsSpaceOwner) return;

        var accountIdsWithOwners = (await db.AccountOwners.AsNoTracking()
            .Where(owner => accountIds.Contains(owner.AccountId))
            .Select(owner => owner.AccountId)
            .Distinct()
            .ToListAsync(ct))
            .ToHashSet();

        foreach (var accountId in accountIds.Where(accountId => !accountIdsWithOwners.Contains(accountId)))
        {
            db.AccountOwners.Add(new AccountOwner
            {
                AccountId = accountId,
                UserId = authorizationUserId,
                OwnershipType = AccountOwnershipTypes.Owner,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<int> InsertBalancesAsync(Dictionary<string, FinanceAccount> accounts, IReadOnlyList<BalanceBatchItem> items, CancellationToken ct)
    {
        var inserted = 0;
        foreach (var item in items)
        {
            if (!accounts.TryGetValue(item.IdentificationHash, out var account)) continue;
            db.BalanceSnapshots.Add(new BalanceSnapshot { AccountId = account.Id, Amount = item.Amount, Currency = item.Currency, BalanceType = item.BalanceType, ReferenceDate = item.ReferenceDate, CapturedAt = item.CapturedAt });
            inserted++;
        }
        await db.SaveChangesAsync(ct); return inserted;
    }

    private async Task<(int Inserted, int Updated)> UpsertTransactionsAsync(
        Guid fullWorthSpaceId,
        string? country,
        Dictionary<string, FinanceAccount> accounts,
        IReadOnlyList<TransactionBatchItem> items,
        CancellationToken ct)
    {
        var inserted = 0; var updated = 0;
        var rules = await db.CategorizationRules.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.IsEnabled && x.Target == "transaction")
            .OrderBy(x => x.Priority).ThenBy(x => x.Id).ToListAsync(ct);
        var activeCategoryRows = await db.Categories.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && !x.IsArchived)
            .Select(x => new { x.Key, x.Name, x.Id })
            .ToListAsync(ct);
        IReadOnlyDictionary<string, Guid> activeCategoryIdsByKey = activeCategoryRows
            .ToDictionary(x => x.Key, x => x.Id, StringComparer.OrdinalIgnoreCase);
        if (cloudOntologyResolver is not null && intelligenceDb is not null)
        {
            activeCategoryIdsByKey = await cloudOntologyResolver.ExpandCategoryMapAsync(
                activeCategoryRows
                    .Select(x => new LocalCategorySemanticCandidate(x.Id, x.Key, x.Name))
                    .ToList(),
                country,
                ct);
        }
        var activeCategoryIds = activeCategoryRows.Select(x => x.Id).ToArray();

        IReadOnlyList<LearnedMerchantCategoryMapping> learnedMappings = Array.Empty<LearnedMerchantCategoryMapping>();
        IReadOnlyList<OfficialMerchantCategoryMapping> cloudMappings = Array.Empty<OfficialMerchantCategoryMapping>();
        if (intelligenceDb is not null && activeCategoryIds.Length > 0)
        {
            learnedMappings = await intelligenceDb.LearnedMerchantMappings.AsNoTracking()
                .Where(x => x.FullWorthSpaceId == fullWorthSpaceId && x.IsActive && activeCategoryIds.Contains(x.CategoryId))
                .OrderBy(x => x.NormalizedCounterparty).ThenBy(x => x.Direction)
                .Select(x => new LearnedMerchantCategoryMapping(x.NormalizedCounterparty, x.Direction, x.CategoryId))
                .ToListAsync(ct);

            var normalizedCountry = string.IsNullOrWhiteSpace(country)
                ? "GLOBAL"
                : country.Trim().ToUpperInvariant();
            cloudMappings = await intelligenceDb.OfficialMerchantMappings.AsNoTracking()
                .Where(x => x.CategoryKey != null &&
                            (x.Country == "GLOBAL" || x.Country == normalizedCountry))
                .OrderBy(x => x.AliasKey)
                .ThenBy(x => x.Direction)
                .ThenByDescending(x => x.Confidence)
                .Select(x => new OfficialMerchantCategoryMapping(
                    x.AliasKey,
                    x.Direction,
                    x.CategoryKey!,
                    x.Confidence))
                .ToListAsync(ct);
        }

        foreach (var accountGroup in items.GroupBy(x => x.IdentificationHash))
        {
            if (!accounts.TryGetValue(accountGroup.Key, out var account)) continue;
            var sourceItems = accountGroup.ToList();
            var keys = sourceItems.Select(x => x.ExternalKey).Distinct().ToArray();
            var existing = await db.Transactions
                .Where(x => x.AccountId == account.Id && keys.Contains(x.ExternalKey))
                .ToDictionaryAsync(x => x.ExternalKey, ct);

            // Migration/reconciliation seam: older FullWorth builds used Enable Banking transaction_id
            // as ExternalKey. The provider documents transaction_id as an unstable detail pointer, while
            // entry_reference is the account-scoped cross-retrieval identifier. Match only UNIQUE stored
            // entry references; ambiguous historical rows are deliberately left untouched.
            var entryReferences = sourceItems
                .Select(x => x.EntryReference)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var entryReferenceCandidates = entryReferences.Length == 0
                ? new List<FinanceTransaction>()
                : await db.Transactions
                    .Where(x => x.AccountId == account.Id && x.EntryReference != null && entryReferences.Contains(x.EntryReference))
                    .ToListAsync(ct);
            var uniqueByEntryReference = entryReferenceCandidates
                .GroupBy(x => x.EntryReference!, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

            foreach (var item in sourceItems)
            {
                FinanceTransaction entity;
                var wasExisting = false;
                if (existing.TryGetValue(item.ExternalKey, out var exact))
                {
                    entity = exact;
                    wasExisting = true;
                    updated++;
                }
                else if (!string.IsNullOrWhiteSpace(item.EntryReference) &&
                         string.Equals(item.ExternalKey, "er:" + item.EntryReference, StringComparison.Ordinal) &&
                         uniqueByEntryReference.TryGetValue(item.EntryReference, out var stableMatch))
                {
                    // Only the canonical entry_reference-keyed row may adopt an older transaction_id-keyed
                    // row. The banking sync always keys a transaction that carries an entry_reference as
                    // "er:{entry_reference}" (see BankSyncService), so any incoming item whose ExternalKey is
                    // NOT that form is legacy/ambiguous historical data and must never merge into a row that
                    // merely happens to share the same entry_reference — otherwise two distinct pending rows
                    // collapse into one.
                    entity = stableMatch;
                    wasExisting = true;
                    entity.ExternalKey = item.ExternalKey;
                    existing[item.ExternalKey] = entity;
                    updated++;
                }
                else
                {
                    // Pending transactions commonly have no stable entry_reference. Never merge them
                    // into BOOK entries merely because amount/payee/date look similar; that can merge
                    // two real card transactions. The status-scoped deterministic fallback keeps them
                    // separate unless Enable Banking supplies a stable entry_reference.
                    entity = new FinanceTransaction { AccountId = account.Id, ExternalKey = item.ExternalKey };
                    db.Transactions.Add(entity);
                    existing[item.ExternalKey] = entity;
                    inserted++;
                }

                var previousStatus = entity.Status;
                var observedAt = DateTimeOffset.UtcNow;

                entity.ProviderTransactionId = item.ProviderTransactionId;
                entity.Status = item.Status;
                entity.BookingDate = item.BookingDate;
                entity.ValueDate = item.ValueDate;
                entity.Amount = item.Amount;
                entity.Currency = item.Currency;
                entity.Counterparty = item.Counterparty;
                entity.NormalizedCounterparty = MerchantNormalization.Normalize(item.Counterparty);
                entity.Description = item.Description;
                entity.MerchantCategoryCode = item.MerchantCategoryCode;
                entity.EntryReference = item.EntryReference;
                entity.RawJson = cipher.Protect(item.RawJson) ?? "{}";
                entity.UpdatedAt = observedAt;

                // Provider status history is observation-based. DateOnly booking/value dates never get
                // an invented time-of-day; the audit timestamp is when FullWorth actually saw the state.
                if (!wasExisting && string.Equals(item.Status, "PDNG", StringComparison.OrdinalIgnoreCase))
                {
                    audit.RecordTransactionPendingObserved(fullWorthSpaceId, entity.Id, observedAt);
                }
                else if (wasExisting && !string.Equals(previousStatus, item.Status, StringComparison.OrdinalIgnoreCase))
                {
                    audit.RecordTransactionStatusTransition(
                        fullWorthSpaceId,
                        entity.Id,
                        previousStatus,
                        item.Status,
                        observedAt);
                }

                if (entity.CategorizationSource != "manual")
                    ApplyCategorization(entity, rules, activeCategoryIdsByKey, learnedMappings, cloudMappings);
            }
        }
        await db.SaveChangesAsync(ct);
        return (inserted, updated);
    }

    private static void ApplyCategorization(
        FinanceTransaction tx,
        IReadOnlyList<CategorizationRule> rules,
        IReadOnlyDictionary<string, Guid> activeCategoryIdsByKey,
        IReadOnlyList<LearnedMerchantCategoryMapping> learnedMappings,
        IReadOnlyList<OfficialMerchantCategoryMapping> cloudMappings)
    {
        var classification = TransactionRuleEngine.EvaluateWithGermanyCatalog(
            tx,
            rules,
            activeCategoryIdsByKey,
            learnedMappings,
            cloudMappings);
        tx.CategoryId = classification.CategoryId;
        tx.IsTransfer = classification.IsTransfer;
        tx.CategorizationSource = classification.Source;
    }
}

public static class IngestionEndpoints
{
    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/banking/ingest", async (FinanceIngestBatch batch, IngestionService service, CancellationToken ct) => Results.Ok(await service.IngestAsync(batch, ct))).WithTags("Internal banking");
        return app;
    }
}
