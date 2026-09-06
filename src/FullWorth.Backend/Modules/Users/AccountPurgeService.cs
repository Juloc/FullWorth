using System.Data.Common;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.BankConnections;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Users;

public sealed record AccountPurgeResult(
    bool Succeeded,
    bool AlreadyPurged,
    int PersonalSpacesPurged,
    int SharedSpacesLeft,
    string? Error = null);

public sealed class AccountPurgeService(
    FullWorthDbContext db,
    IntelligenceDbContext intelligenceDb,
    UserStore users,
    IOptions<PurchaseStorageOptions> purchaseStorage,
    ILogger<AccountPurgeService> logger)
{
    private static readonly HashSet<string> OwnershipUserPropertyNames = new(StringComparer.Ordinal)
    {
        "UserId",
        "FinanceUserId",
        "OwnerUserId",
        "AuthUserId"
    };

    private readonly PurchaseStorageOptions storage = purchaseStorage.Value;

    public async Task<AccountPurgeResult> PurgeAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            return new(false, false, 0, 0, "invalid_user");

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null)
            return new(false, false, 0, 0, "user_not_found");
        if (user.IsTombstone)
            return new(true, true, 0, 0);

        var unknown = PersonalDataPurgeManifest.Unclassified(db.Model);
        if (unknown.Count > 0)
        {
            var names = string.Join(", ", unknown.Select(x => x.EntityType.Name));
            logger.LogError("Account purge refused because entities are unclassified: {Entities}", names);
            return new(false, false, 0, 0, "purge_manifest_incomplete");
        }

        var memberships = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.FullWorthSpaceId)
            .ToListAsync(ct);

        var personalSpaces = new List<Guid>();
        var sharedSpaces = new List<Guid>();
        foreach (var spaceId in memberships)
        {
            var memberCount = await db.FullWorthSpaceMembers.AsNoTracking()
                .CountAsync(x => x.FullWorthSpaceId == spaceId, ct);
            if (memberCount <= 1) personalSpaces.Add(spaceId);
            else sharedSpaces.Add(spaceId);
        }

        foreach (var spaceId in sharedSpaces)
            await PrepareSharedSpaceAsync(userId, spaceId, ct);

        foreach (var spaceId in personalSpaces)
        {
            await DeleteStoredPurchaseFilesAsync(spaceId, ct);
            await PurgePersonalSpaceAsync(spaceId, ct);
        }

        await CloseSharedBankingAuthorizationAsync(userId, sharedSpaces, ct);
        await DeleteInvitesCreatedByAsync(userId, ct);
        await PurgeUserOwnedRowsAsync(userId, ct);
        await PurgeIntelligenceUserDataAsync(userId, personalSpaces, ct);

        if (!await users.TombstoneAsync(userId, ct))
            return new(false, false, personalSpaces.Count, sharedSpaces.Count, "tombstone_failed");

        return new(true, false, personalSpaces.Count, sharedSpaces.Count);
    }

    private async Task PrepareSharedSpaceAsync(Guid userId, Guid spaceId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var members = await db.FullWorthSpaceMembers
            .Where(x => x.FullWorthSpaceId == spaceId)
            .OrderBy(x => x.JoinedAt)
            .ToListAsync(ct);
        var deleting = members.SingleOrDefault(x => x.UserId == userId);
        if (deleting is null)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        var replacement = members.FirstOrDefault(x => x.UserId != userId && x.Role == FullWorthSpaceRoles.Owner)
            ?? members.FirstOrDefault(x => x.UserId != userId);

        if (replacement is null)
            throw new InvalidOperationException("Shared space has no replacement member.");

        if (deleting.Role == FullWorthSpaceRoles.Owner &&
            members.Count(x => x.Role == FullWorthSpaceRoles.Owner) == 1)
        {
            replacement.Role = FullWorthSpaceRoles.Owner;
        }

        var ownedAccounts = await db.AccountOwners
            .Where(x => x.UserId == userId &&
                        x.OwnershipType == AccountOwnershipTypes.Owner &&
                        x.Account.FullWorthSpaceId == spaceId)
            .Select(x => x.AccountId)
            .ToListAsync(ct);

        foreach (var accountId in ownedAccounts)
        {
            var otherOwnerExists = await db.AccountOwners.AnyAsync(x =>
                x.AccountId == accountId &&
                x.UserId != userId &&
                x.OwnershipType == AccountOwnershipTypes.Owner, ct);
            if (otherOwnerExists) continue;

            var replacementOwner = await db.AccountOwners
                .SingleOrDefaultAsync(x => x.AccountId == accountId && x.UserId == replacement.UserId, ct);
            if (replacementOwner is null)
            {
                db.AccountOwners.Add(new AccountOwner
                {
                    AccountId = accountId,
                    UserId = replacement.UserId,
                    OwnershipType = AccountOwnershipTypes.Owner,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                replacementOwner.OwnershipType = AccountOwnershipTypes.Owner;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task CloseSharedBankingAuthorizationAsync(
        Guid userId,
        IReadOnlyCollection<Guid> sharedSpaceIds,
        CancellationToken ct)
    {
        if (sharedSpaceIds.Count == 0) return;

        await db.BankConnections
            .Where(x => sharedSpaceIds.Contains(x.FullWorthSpaceId) && x.AuthorizationUserId == userId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.AuthorizationState, (string?)null)
                .SetProperty(x => x.AuthorizationStateExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.AuthorizationId, (string?)null)
                .SetProperty(x => x.ProviderSessionId, (string?)null)
                .SetProperty(x => x.ProviderSessionIdLookup, (string?)null)
                .SetProperty(x => x.AuthorizationUserId, (Guid?)null)
                .SetProperty(x => x.EnableBankingProfileId, (Guid?)null)
                .SetProperty(x => x.Status, "CLOSED")
                .SetProperty(x => x.NextSyncAllowedAt, (DateTimeOffset?)null)
                .SetProperty(x => x.ConsecutiveFailures, 0)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
    }

    private Task<int> DeleteInvitesCreatedByAsync(Guid userId, CancellationToken ct) =>
        db.FullWorthSpaceInvites
            .Where(x => x.InvitedByUserId == userId)
            .ExecuteDeleteAsync(ct);

    private async Task PurgePersonalSpaceAsync(Guid spaceId, CancellationToken ct)
    {
        var descriptors = PersonalDataPurgeManifest.Describe(db.Model)
            .Where(x => x.IsSpaceOwned)
            .ToArray();
        var ordered = OrderForDelete(descriptors);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var descriptor in ordered)
            await ClearRestrictSelfReferencesAsync(db, descriptor, spaceId, ct);

        foreach (var descriptor in ordered)
        {
            var predicate = BuildSpacePredicate(descriptor.EntityType, "t0", 0, new HashSet<IEntityType>());
            await ExecuteDeleteAsync(db, descriptor, predicate, "spaceId", spaceId, ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task PurgeUserOwnedRowsAsync(Guid userId, CancellationToken ct)
    {
        // In the finance model, only DIRECT user-owned roots are deleted here. Their configured DB
        // cascades may remove private children (e.g. TaxProfile -> candidates, CoachConversation ->
        // messages). We deliberately do not infer ownership through arbitrary FKs: a user's BYO bank
        // profile can be referenced by a bank connection in a shared space, and that must never make
        // the shared accounts/transactions "owned" by the deleting user.
        var direct = db.Model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null && entity.ClrType != typeof(FullWorthUser))
            .Select(entity => new
            {
                Entity = entity,
                Ownership = entity.GetProperties()
                    .Where(property => OwnershipUserPropertyNames.Contains(property.Name))
                    .ToArray()
            })
            .Where(x => x.Ownership.Length > 0)
            .Select(x => new PurgeEntityDescriptor(
                x.Entity,
                x.Entity.GetTableName()!,
                x.Entity.GetSchema(),
                null,
                x.Ownership,
                Array.Empty<IProperty>(),
                false,
                false,
                false))
            .ToArray();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var descriptor in OrderForDelete(direct))
        {
            // BankConnection.AuthorizationUserId is intentionally NOT one of the direct-ownership
            // property names. Shared banking authorization is stripped explicitly before this stage.
            var predicate = "(" + string.Join(" OR ", descriptor.OwnershipUserProperties.Select(property =>
                $"t0.{QuoteColumnStatic(descriptor.EntityType, property)} = @userId")) + ")";
            await ExecuteDeleteAsync(db, descriptor, predicate, "userId", userId, ct);
        }
        await transaction.CommitAsync(ct);
    }

    private async Task PurgeIntelligenceUserDataAsync(
        Guid userId,
        IReadOnlyCollection<Guid> personalSpaceIds,
        CancellationToken ct)
    {
        // Intelligence uses a separate EF model. Delete user-owned roots and their dependent rows by
        // following that model's FKs. Historical actor/reviewer references are not ownership roots and
        // therefore remain distinct GUID tombstones rather than being collapsed into one shared user.
        var userOwned = intelligenceDb.Model.GetEntityTypes()
            .Where(x => x.GetTableName() is not null)
            .Select(entity => new
            {
                Entity = entity,
                Depth = FindUserOwnershipDepth(entity, new HashSet<IEntityType>())
            })
            .Where(x => x.Depth.HasValue)
            .Select(x => new PurgeEntityDescriptor(
                x.Entity,
                x.Entity.GetTableName()!,
                x.Entity.GetSchema(),
                x.Depth,
                x.Entity.GetProperties().Where(p => OwnershipUserPropertyNames.Contains(p.Name)).ToArray(),
                Array.Empty<IProperty>(),
                false,
                false,
                false))
            .ToArray();

        var spaceOwned = intelligenceDb.Model.GetEntityTypes()
            .Where(x => x.GetTableName() is not null)
            .Select(entity => new
            {
                Entity = entity,
                Depth = FindGenericSpaceOwnershipDepth(entity, new HashSet<IEntityType>())
            })
            .Where(x => x.Depth.HasValue)
            .Select(x => new PurgeEntityDescriptor(
                x.Entity,
                x.Entity.GetTableName()!,
                x.Entity.GetSchema(),
                x.Depth,
                Array.Empty<IProperty>(),
                Array.Empty<IProperty>(),
                false,
                false,
                false))
            .ToArray();

        await using var transaction = await intelligenceDb.Database.BeginTransactionAsync(ct);

        var feedbackIds = await intelligenceDb.IntelligenceFeedbackEvents.AsNoTracking()
            .Where(x => x.UserId == userId ||
                        (x.FullWorthSpaceId.HasValue && personalSpaceIds.Contains(x.FullWorthSpaceId.Value)))
            .Select(x => x.Id)
            .ToListAsync(ct);
        if (feedbackIds.Count > 0)
        {
            await intelligenceDb.CloudSubmissionOutbox
                .Where(x => x.FeedbackEventId.HasValue && feedbackIds.Contains(x.FeedbackEventId.Value))
                .ExecuteDeleteAsync(ct);
        }

        foreach (var descriptor in OrderForDelete(userOwned))
        {
            var predicate = BuildUserPredicate(descriptor.EntityType, "t0", 0, new HashSet<IEntityType>());
            await ExecuteDeleteAsync(intelligenceDb, descriptor, predicate, "userId", userId, ct);
        }

        foreach (var spaceId in personalSpaceIds)
        {
            foreach (var descriptor in OrderForDelete(spaceOwned))
            {
                var predicate = BuildGenericSpacePredicate(descriptor.EntityType, "t0", 0, new HashSet<IEntityType>());
                await ExecuteDeleteAsync(intelligenceDb, descriptor, predicate, "spaceId", spaceId, ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    private async Task DeleteStoredPurchaseFilesAsync(Guid spaceId, CancellationToken ct)
    {
        var paths = await db.PurchaseDocuments.AsNoTracking()
            .Where(x => x.Purchase.FullWorthSpaceId == spaceId && x.StoragePath != "")
            .Select(x => x.StoragePath)
            .ToListAsync(ct);

        var legacyPaths = await db.Purchases.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == spaceId && x.ReceiptImagePath != null && x.ReceiptImagePath != "")
            .Select(x => x.ReceiptImagePath!)
            .ToListAsync(ct);

        foreach (var path in paths.Concat(legacyPaths).Distinct(StringComparer.Ordinal))
        {
            var absolute = SafeStoragePath(path);
            if (absolute is null)
                throw new InvalidOperationException("Stored receipt path escaped the configured purchase root.");
            if (File.Exists(absolute))
                File.Delete(absolute);
        }
    }

    private string? SafeStoragePath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;
        var root = Path.GetFullPath(storage.RootPath);
        var candidate = Path.IsPathRooted(storedPath)
            ? Path.GetFullPath(storedPath)
            : Path.GetFullPath(Path.Combine(root, storedPath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal) ? candidate : null;
    }

    private static IReadOnlyList<PurgeEntityDescriptor> OrderForDelete(
        IReadOnlyCollection<PurgeEntityDescriptor> descriptors)
    {
        var byTable = descriptors
            .GroupBy(x => TableKey(x.EntityType), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(x => x.SpaceOwnershipDepth ?? 0).First(),
                StringComparer.Ordinal);

        var edges = byTable.Keys.ToDictionary(x => x, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var indegree = byTable.Keys.ToDictionary(x => x, _ => 0, StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            var from = TableKey(descriptor.EntityType);
            foreach (var foreignKey in descriptor.EntityType.GetForeignKeys())
            {
                var to = TableKey(foreignKey.PrincipalEntityType);
                if (from == to || !byTable.ContainsKey(to)) continue;
                if (edges[from].Add(to)) indegree[to]++;
            }
        }

        var ready = new PriorityQueue<string, (int Depth, string Name)>();
        foreach (var key in byTable.Keys.Where(key => indegree[key] == 0))
        {
            var depth = byTable[key].SpaceOwnershipDepth ?? 0;
            ready.Enqueue(key, (-depth, key));
        }

        var result = new List<PurgeEntityDescriptor>(byTable.Count);
        while (ready.TryDequeue(out var key, out _))
        {
            result.Add(byTable[key]);
            foreach (var principal in edges[key])
            {
                indegree[principal]--;
                if (indegree[principal] == 0)
                {
                    var depth = byTable[principal].SpaceOwnershipDepth ?? 0;
                    ready.Enqueue(principal, (-depth, principal));
                }
            }
        }

        if (result.Count != byTable.Count)
            throw new InvalidOperationException("Purge dependency graph contains a table cycle that requires an explicit handler.");

        return result;
    }

    private static IReadOnlyList<IEntityType> OrderEntityTypesForDelete(IReadOnlyCollection<IEntityType> entities)
    {
        var descriptors = entities.Select(entity => new PurgeEntityDescriptor(
            entity, entity.GetTableName()!, entity.GetSchema(), null,
            Array.Empty<IProperty>(), Array.Empty<IProperty>(), false, false, false)).ToArray();
        return OrderForDelete(descriptors).Select(x => x.EntityType).ToArray();
    }

    private static string BuildSpacePredicate(
        IEntityType entity,
        string alias,
        int depth,
        HashSet<IEntityType> visiting)
    {
        if (entity.ClrType == typeof(FullWorthSpace))
        {
            var id = entity.FindProperty("Id") ?? throw new InvalidOperationException("Space root has no Id property.");
            return $"{alias}.{QuoteColumnStatic(entity, id)} = @spaceId";
        }

        if (entity.FindProperty("FullWorthSpaceId") is { } direct)
            return $"{alias}.{QuoteColumnStatic(entity, direct)} = @spaceId";

        if (!visiting.Add(entity))
            throw new InvalidOperationException($"Cycle while resolving space ownership for {entity.Name}.");

        try
        {
            var candidates = entity.GetForeignKeys()
                .Where(fk => fk.PrincipalEntityType != entity)
                .Select(fk => new
                {
                    ForeignKey = fk,
                    Depth = PersonalDataPurgeManifest.Describe(fk.PrincipalEntityType).SpaceOwnershipDepth
                })
                .Where(x => x.Depth.HasValue)
                .OrderBy(x => x.Depth)
                .ToArray();

            var chosen = candidates.FirstOrDefault()
                ?? throw new InvalidOperationException($"No FullWorthSpace ownership path for {entity.Name}.");

            var principal = chosen.ForeignKey.PrincipalEntityType;
            var principalTable = DelimitTableStatic(principal);
            var principalAlias = $"t{depth + 1}";
            var joins = chosen.ForeignKey.Properties
                .Zip(chosen.ForeignKey.PrincipalKey.Properties)
                .Select(pair =>
                    $"{principalAlias}.{QuoteColumnStatic(principal, pair.Second)} = {alias}.{QuoteColumnStatic(entity, pair.First)}");

            var principalPredicate = BuildSpacePredicate(principal, principalAlias, depth + 1, visiting);
            return $"EXISTS (SELECT 1 FROM {principalTable} AS {principalAlias} WHERE {string.Join(" AND ", joins)} AND {principalPredicate})";
        }
        finally
        {
            visiting.Remove(entity);
        }
    }

    private static int? FindGenericSpaceOwnershipDepth(IEntityType entity, HashSet<IEntityType> visiting)
    {
        if (entity.FindProperty("FullWorthSpaceId") is not null) return 1;
        if (!visiting.Add(entity)) return null;
        try
        {
            int? best = null;
            foreach (var fk in entity.GetForeignKeys())
            {
                if (fk.PrincipalEntityType == entity) continue;
                var depth = FindGenericSpaceOwnershipDepth(fk.PrincipalEntityType, visiting);
                if (!depth.HasValue) continue;
                var candidate = depth.Value + 1;
                if (!best.HasValue || candidate < best.Value) best = candidate;
            }
            return best;
        }
        finally
        {
            visiting.Remove(entity);
        }
    }

    private static string BuildGenericSpacePredicate(
        IEntityType entity,
        string alias,
        int depth,
        HashSet<IEntityType> visiting)
    {
        if (entity.FindProperty("FullWorthSpaceId") is { } direct)
            return $"{alias}.{QuoteColumnStatic(entity, direct)} = @spaceId";

        if (!visiting.Add(entity))
            throw new InvalidOperationException($"Cycle while resolving generic space ownership for {entity.Name}.");

        try
        {
            var chosen = entity.GetForeignKeys()
                .Where(fk => fk.PrincipalEntityType != entity)
                .Select(fk => new
                {
                    ForeignKey = fk,
                    Depth = FindGenericSpaceOwnershipDepth(fk.PrincipalEntityType, new HashSet<IEntityType>())
                })
                .Where(x => x.Depth.HasValue)
                .OrderBy(x => x.Depth)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"No FullWorthSpaceId ownership path for {entity.Name}.");

            var principal = chosen.ForeignKey.PrincipalEntityType;
            var principalAlias = $"s{depth + 1}";
            var joins = chosen.ForeignKey.Properties
                .Zip(chosen.ForeignKey.PrincipalKey.Properties)
                .Select(pair =>
                    $"{principalAlias}.{QuoteColumnStatic(principal, pair.Second)} = {alias}.{QuoteColumnStatic(entity, pair.First)}");
            var principalPredicate = BuildGenericSpacePredicate(principal, principalAlias, depth + 1, visiting);
            return $"EXISTS (SELECT 1 FROM {DelimitTableStatic(principal)} AS {principalAlias} WHERE {string.Join(" AND ", joins)} AND {principalPredicate})";
        }
        finally
        {
            visiting.Remove(entity);
        }
    }

    private static int? FindUserOwnershipDepth(IEntityType entity, HashSet<IEntityType> visiting)
    {
        if (entity.ClrType == typeof(FullWorthUser)) return null;
        if (entity.GetProperties().Any(p => OwnershipUserPropertyNames.Contains(p.Name))) return 1;
        if (!visiting.Add(entity)) return null;

        try
        {
            int? best = null;
            foreach (var fk in entity.GetForeignKeys())
            {
                if (fk.PrincipalEntityType == entity || fk.PrincipalEntityType.ClrType == typeof(FullWorthUser))
                    continue;
                var depth = FindUserOwnershipDepth(fk.PrincipalEntityType, visiting);
                if (!depth.HasValue) continue;
                var candidate = depth.Value + 1;
                if (!best.HasValue || candidate < best.Value) best = candidate;
            }
            return best;
        }
        finally
        {
            visiting.Remove(entity);
        }
    }

    private static string BuildUserPredicate(
        IEntityType entity,
        string alias,
        int depth,
        HashSet<IEntityType> visiting)
    {
        var direct = entity.GetProperties().Where(p => OwnershipUserPropertyNames.Contains(p.Name)).ToArray();
        if (direct.Length > 0)
            return "(" + string.Join(" OR ", direct.Select(p => $"{alias}.{QuoteColumnStatic(entity, p)} = @userId")) + ")";

        if (!visiting.Add(entity))
            throw new InvalidOperationException($"Cycle while resolving user ownership for {entity.Name}.");

        try
        {
            var chosen = entity.GetForeignKeys()
                .Where(fk => fk.PrincipalEntityType != entity && fk.PrincipalEntityType.ClrType != typeof(FullWorthUser))
                .Select(fk => new { ForeignKey = fk, Depth = FindUserOwnershipDepth(fk.PrincipalEntityType, new HashSet<IEntityType>()) })
                .Where(x => x.Depth.HasValue)
                .OrderBy(x => x.Depth)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"No user ownership path for {entity.Name}.");

            var principal = chosen.ForeignKey.PrincipalEntityType;
            var principalAlias = $"u{depth + 1}";
            var joins = chosen.ForeignKey.Properties
                .Zip(chosen.ForeignKey.PrincipalKey.Properties)
                .Select(pair =>
                    $"{principalAlias}.{QuoteColumnStatic(principal, pair.Second)} = {alias}.{QuoteColumnStatic(entity, pair.First)}");
            var principalPredicate = BuildUserPredicate(principal, principalAlias, depth + 1, visiting);
            return $"EXISTS (SELECT 1 FROM {DelimitTableStatic(principal)} AS {principalAlias} WHERE {string.Join(" AND ", joins)} AND {principalPredicate})";
        }
        finally
        {
            visiting.Remove(entity);
        }
    }

    private static async Task ClearRestrictSelfReferencesAsync(
        DbContext context,
        PurgeEntityDescriptor descriptor,
        Guid spaceId,
        CancellationToken ct)
    {
        var selfReferences = descriptor.EntityType.GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType == descriptor.EntityType &&
                         fk.DeleteBehavior is DeleteBehavior.Restrict or DeleteBehavior.NoAction)
            .ToArray();
        if (selfReferences.Length == 0) return;

        var spacePredicate = BuildSpacePredicate(
            descriptor.EntityType, "t0", 0, new HashSet<IEntityType>());

        foreach (var foreignKey in selfReferences)
        {
            if (foreignKey.Properties.Any(property => !property.IsNullable))
                throw new InvalidOperationException(
                    $"Purge cannot safely clear non-nullable self reference on {descriptor.EntityType.Name}.");

            var assignments = string.Join(", ", foreignKey.Properties.Select(property =>
                $"{QuoteColumnStatic(descriptor.EntityType, property)} = NULL"));
            var connection = context.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText =
                $"UPDATE {DelimitTable(context, descriptor.EntityType)} AS t0 SET {assignments} WHERE {spacePredicate};";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "spaceId";
            parameter.Value = spaceId;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task ExecuteDeleteAsync(
        DbContext context,
        PurgeEntityDescriptor descriptor,
        string predicate,
        string parameterName,
        Guid value,
        CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"DELETE FROM {DelimitTable(context, descriptor.EntityType)} AS t0 WHERE {predicate};";
        var parameter = command.CreateParameter();
        parameter.ParameterName = parameterName;
        parameter.Value = value;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string QuoteColumn(DbContext context, IEntityType entity, IProperty property)
    {
        var helper = context.GetService<ISqlGenerationHelper>();
        return helper.DelimitIdentifier(ColumnName(entity, property));
    }

    private static string QuoteColumnStatic(IEntityType entity, IProperty property) =>
        """ + ColumnName(entity, property).Replace(""", """") + """;

    private static string ColumnName(IEntityType entity, IProperty property)
    {
        var table = entity.GetTableName() ?? throw new InvalidOperationException($"No table for {entity.Name}.");
        var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
        return property.GetColumnName(store)
            ?? throw new InvalidOperationException($"No column mapping for {entity.Name}.{property.Name}.");
    }

    private static string DelimitTable(DbContext context, IEntityType entity)
    {
        var helper = context.GetService<ISqlGenerationHelper>();
        return helper.DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());
    }

    private static string DelimitTableStatic(IEntityType entity)
    {
        static string Q(string value) => """ + value.Replace(""", """") + """;
        var table = Q(entity.GetTableName()!);
        return entity.GetSchema() is { Length: > 0 } schema ? $"{Q(schema)}.{table}" : table;
    }

    private static string TableKey(IEntityType entity) =>
        $"{entity.GetSchema() ?? "public"}.{entity.GetTableName()}";
}
