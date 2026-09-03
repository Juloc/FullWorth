using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Audit;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

public sealed class BankConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Provider { get; set; } = "enable-banking";
    public string InstitutionName { get; set; } = string.Empty;
    public string Country { get; set; } = "DE";
    public string? AuthorizationState { get; set; }
    // The OAuth state is bound to the user who initiated the connect and expires; it is consumed
    // exactly once at callback so a replayed callback cannot re-drive the flow.
    public Guid? AuthorizationUserId { get; set; }
    public DateTimeOffset? AuthorizationStateExpiresAt { get; set; }
    public string? AuthorizationId { get; set; }
    // Encrypted at rest (P0.4). ProviderSessionIdLookup is a keyed blind index that keeps the value
    // uniquely constrained and findable (ingest batch -> connection) without storing it in the clear.
    public string? ProviderSessionId { get; set; }
    public string? ProviderSessionIdLookup { get; set; }
    public string Status { get; set; } = "PENDING_AUTHORIZATION";
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset? NextSyncAllowedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record BankConnectionStatusView(
    Guid Id,
    Guid FullWorthSpaceId,
    string Provider,
    string InstitutionName,
    string Country,
    string Status,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? NextSyncAllowedAt,
    DateTimeOffset UpdatedAt,
    string HealthStatus,
    int? DaysUntilExpiry);

public sealed class BankConnectionStore(FullWorthDbContext db, AuditService? auditService = null, FullWorth.Backend.Security.FieldCipher? fieldCipher = null, FullWorth.Backend.Modules.Notifications.NotificationDispatcher? notifications = null)
{
    private readonly AuditService audit = auditService ?? new AuditService(db);
    private readonly FullWorth.Backend.Security.FieldCipher cipher = fieldCipher ?? FullWorth.Backend.Security.FieldCipher.Null;
    // Internal Banking/ingest methods. These deliberately retain technical fields and are never used by
    // public user endpoints. ProviderSessionId/AuthorizationId are encrypted at rest (P0.4), so every
    // read that hands the raw entity to the Banking service MUST decrypt them again — otherwise the
    // service calls Enable Banking with the "v1:" ciphertext (→ 404) and re-encrypts it on write-back.
    public async Task<List<BankConnection>> ListAsync(CancellationToken ct)
    {
        var items = await db.BankConnections.AsNoTracking().OrderBy(x => x.InstitutionName).ToListAsync(ct);
        foreach (var item in items) DecryptSecrets(item);
        return items;
    }

    public async Task<BankConnection?> GetAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.BankConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return entity is null ? null : DecryptSecrets(entity);
    }

    public async Task<BankConnection?> GetByStateAsync(string state, CancellationToken ct)
    {
        var entity = await db.BankConnections.AsNoTracking().SingleOrDefaultAsync(x => x.AuthorizationState == state, ct);
        return entity is null ? null : DecryptSecrets(entity);
    }

    public async Task<List<BankConnection>> ListForSpaceAsync(Guid fullWorthSpaceId, CancellationToken ct)
    {
        var items = await db.BankConnections.AsNoTracking()
            .Where(x => x.FullWorthSpaceId == fullWorthSpaceId)
            .OrderBy(x => x.InstitutionName)
            .ToListAsync(ct);
        foreach (var item in items) DecryptSecrets(item);
        return items;
    }

    public async Task<BankConnection?> GetForSpaceAsync(Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var entity = await db.BankConnections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        return entity is null ? null : DecryptSecrets(entity);
    }

    public async Task<List<BankConnectionStatusView>?> ListForUserAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return null;
        var connections = await PublicConnections(userId, fullWorthSpaceId)
            .OrderBy(connection => connection.InstitutionName)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return connections.Select(connection => Project(connection, now)).ToList();
    }

    public async Task<BankConnectionStatusView?> GetForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        var connection = await PublicConnections(userId, fullWorthSpaceId)
            .SingleOrDefaultAsync(connection => connection.Id == id, ct);
        return connection is null ? null : Project(connection, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Permanently deletes a bank connection and everything synced under it — its accounts, their
    /// balance snapshots and transactions (transaction allocations cascade at the DB level) — for a
    /// member of the space. Restrict foreign keys that would otherwise block the delete are cleared
    /// first: recurring contracts are detached from the account, purchases (scanned receipts) are
    /// unlinked but kept, and price-change suggestions built from those transactions are removed.
    /// Loan.AccountId and refund links are SetNull by the database. Returns false for non-members or
    /// an unknown/foreign connection. This is irreversible (the user opts in explicitly).
    /// </summary>
    public async Task<bool> DeleteForUserAsync(Guid userId, Guid fullWorthSpaceId, Guid id, CancellationToken ct)
    {
        if (!await IsMemberAsync(userId, fullWorthSpaceId, ct)) return false;
        var connection = await db.BankConnections.SingleOrDefaultAsync(x => x.Id == id && x.FullWorthSpaceId == fullWorthSpaceId, ct);
        if (connection is null) return false;

        var accountIds = await db.Accounts
            .Where(a => a.BankConnectionId == id && a.FullWorthSpaceId == fullWorthSpaceId)
            .Select(a => a.Id)
            .ToListAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (accountIds.Count > 0)
        {
            var txIds = await db.Transactions.Where(t => accountIds.Contains(t.AccountId)).Select(t => t.Id).ToListAsync(ct);
            if (txIds.Count > 0)
            {
                await db.Purchases.Where(p => p.TransactionId != null && txIds.Contains(p.TransactionId!.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.TransactionId, (Guid?)null), ct);
                await db.PriceChangeSuggestions.Where(s => txIds.Contains(s.EvidenceTransactionId)).ExecuteDeleteAsync(ct);
            }
            await db.Contracts.Where(c => c.AccountId != null && accountIds.Contains(c.AccountId!.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AccountId, (Guid?)null), ct);
            await db.BalanceSnapshots.Where(x => accountIds.Contains(x.AccountId)).ExecuteDeleteAsync(ct);
            await db.Transactions.Where(x => accountIds.Contains(x.AccountId)).ExecuteDeleteAsync(ct);
            await db.Accounts.Where(x => accountIds.Contains(x.Id)).ExecuteDeleteAsync(ct);
        }
        db.BankConnections.Remove(connection);
        audit.Record(fullWorthSpaceId, userId, "bank_connection.disconnected", "BankConnection", id);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<BankConnection> UpsertAsync(BankConnectionWrite request, CancellationToken ct)
    {
        var entity = request.Id.HasValue ? await db.BankConnections.SingleOrDefaultAsync(x => x.Id == request.Id.Value, ct) : null;
        var isNew = entity is null;
        var wasConnected = entity?.ProviderSessionId is not null;
        if (entity is null)
        {
            // A brand-new connection MUST arrive with a validated, non-legacy space (P0.2). The old
            // silent LegacyId fallback is gone: it dumped every connection into the Default space.
            if (request.FullWorthSpaceId is not { } space || space == Guid.Empty || space == FullWorthSpaceDefaults.LegacyId)
                throw new ArgumentException("A new bank connection requires a validated FullWorthSpaceId.");
            if (!await db.FullWorthSpaces.AsNoTracking().AnyAsync(x => x.Id == space, ct))
                throw new ArgumentException("FullWorthSpaceId does not exist.");
            entity = new BankConnection { FullWorthSpaceId = space };
            db.BankConnections.Add(entity);
        }
        // Capture the connection's health BEFORE overwrite so we can notify owners on a genuine transition
        // (a new failure episode, or a slide into needs-reauth/expired) rather than on every sync.
        var transitionNow = DateTimeOffset.UtcNow;
        var prevFailures = isNew ? 0 : entity.ConsecutiveFailures;
        var prevHealth = isNew ? null : BankConnectionConsentHealthCalculator.Calculate(
            entity.Status, entity.ProviderSessionId, entity.ValidUntil, entity.ConsecutiveFailures,
            entity.LastError, entity.NextSyncAllowedAt, transitionNow).HealthStatus;

        // FullWorthSpaceId is set only at creation and never moved by an update.
        entity.Provider = request.Provider;
        entity.InstitutionName = request.InstitutionName;
        entity.Country = request.Country;
        entity.AuthorizationState = request.AuthorizationState;
        entity.AuthorizationUserId = request.AuthorizationUserId ?? entity.AuthorizationUserId;
        entity.AuthorizationStateExpiresAt = request.AuthorizationStateExpiresAt;
        entity.AuthorizationId = cipher.Protect(request.AuthorizationId);
        entity.ProviderSessionId = cipher.Protect(request.ProviderSessionId);
        entity.ProviderSessionIdLookup = cipher.BlindIndex(request.ProviderSessionId);
        entity.Status = request.Status;
        entity.ValidUntil = request.ValidUntil;
        entity.LastAttemptAt = request.LastAttemptAt;
        entity.LastSyncedAt = request.LastSyncedAt;
        entity.NextSyncAllowedAt = request.NextSyncAllowedAt;
        entity.ConsecutiveFailures = Math.Max(0, request.ConsecutiveFailures);
        entity.LastError = request.LastError;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        var action = entity.ConsecutiveFailures > 0 || !string.IsNullOrWhiteSpace(entity.LastError)
            ? "bank_connection.error"
            : isNew ? "bank_connection.connected"
            : wasConnected ? "bank_connection.reconnected" : "bank_connection.connected";
        audit.Record(entity.FullWorthSpaceId, entity.AuthorizationUserId, action, "BankConnection", entity.Id);
        await db.SaveChangesAsync(ct);
        // Detach BEFORE decrypting the returned copy so the plaintext can never be written back over the
        // encrypted column by a later SaveChanges — and so the Banking service receives the REAL Enable
        // Banking session id/authorization id, not the "v1:" ciphertext. Returning the ciphertext here
        // made the initial (and forced) sync call GET /sessions/{ciphertext} → 404, then re-encrypt it
        // on write-back. Every other read path already decrypts; UpsertAsync must too.
        db.Entry(entity).State = EntityState.Detached;

        // Best-effort notify space owners on a real transition (never blocks the sync write path): the
        // upsert is already committed, so a failing owner-lookup/notify must not surface as a 500.
        if (notifications is not null)
        {
            // Swallow everything: the write is durable and the returned connection must be intact. The
            // per-user DispatchAsync calls already log their own failures; this guards the owner-lookup.
            try { await NotifyConnectionTransitionsAsync(entity, isNew, prevFailures, prevHealth, request, transitionNow, ct); }
            catch { /* best-effort notification must never fail an already-committed sync write */ }
        }

        return DecryptSecrets(entity);
    }

    private static bool IsReauthHealth(string? health) => health is "reauthorization_required" or "expired";

    private async Task NotifyConnectionTransitionsAsync(
        BankConnection entity, bool isNew, int prevFailures, string? prevHealth, BankConnectionWrite request, DateTimeOffset now, CancellationToken ct)
    {
        var newFailures = Math.Max(0, request.ConsecutiveFailures);
        var newHealth = BankConnectionConsentHealthCalculator.Calculate(
            request.Status, request.ProviderSessionId, request.ValidUntil, newFailures,
            request.LastError, request.NextSyncAllowedAt, now).HealthStatus;

        var syncErrorEdge = prevFailures == 0 && newFailures > 0;
        var reauthEdge = !isNew && !IsReauthHealth(prevHealth) && IsReauthHealth(newHealth);
        if (!syncErrorEdge && !reauthEdge) return;

        // Only space OWNERS can reconnect a bank, so only they are notified.
        var owners = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(m => m.FullWorthSpaceId == entity.FullWorthSpaceId && m.Role == FullWorthSpaceRoles.Owner)
            .Select(m => m.UserId).ToListAsync(ct);
        if (owners.Count == 0) return;

        foreach (var userId in owners)
        {
            if (syncErrorEdge)
                await notifications!.DispatchAsync(userId, entity.FullWorthSpaceId, Notifications.NotificationTypes.BankSyncError,
                    Notifications.NotificationMessages.BankSyncError(entity.InstitutionName), null, ct);
            if (reauthEdge)
                await notifications!.DispatchAsync(userId, entity.FullWorthSpaceId, Notifications.NotificationTypes.BankReauth,
                    Notifications.NotificationMessages.BankReauth(entity.InstitutionName), null, ct);
        }
    }

    /// <summary>
    /// Atomically consumes an OAuth authorization state exactly once: returns the connection only if
    /// the state exists and has not expired, and clears the state in the same save so a replayed
    /// callback finds nothing. Returns null for unknown/expired/already-consumed states.
    /// </summary>
    public async Task<BankConnection?> ConsumeAuthorizationStateAsync(string state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        var now = DateTimeOffset.UtcNow;
        var entity = await db.BankConnections.SingleOrDefaultAsync(x => x.AuthorizationState == state, ct);
        if (entity is null) return null;
        if (entity.AuthorizationStateExpiresAt is { } expiresAt && expiresAt <= now)
        {
            entity.AuthorizationState = null;
            entity.AuthorizationStateExpiresAt = null;
            await db.SaveChangesAsync(ct);
            return null;
        }
        entity.AuthorizationState = null;
        entity.AuthorizationStateExpiresAt = null;
        await db.SaveChangesAsync(ct);
        // Detach BEFORE decrypting the returned copy so the plaintext can never be written back over
        // the encrypted column by a later SaveChanges on this tracked entity.
        db.Entry(entity).State = EntityState.Detached;
        return DecryptSecrets(entity);
    }

    /// <summary>
    /// P0.2 authorization gate for the Banking service: the caller must be an OWNER of the target
    /// space; when a connectionId is supplied it must belong to that space. Returns 404-equivalent
    /// (null) for unknown/foreign resources so IDs cannot be probed, and false only for a member who
    /// is not an owner.
    /// </summary>
    public async Task<BankConnectionAuthorizeResult> AuthorizeAsync(Guid userId, Guid fullWorthSpaceId, Guid? connectionId, CancellationToken ct)
    {
        if (userId == Guid.Empty || fullWorthSpaceId == Guid.Empty) return BankConnectionAuthorizeResult.NotFound;
        var membership = await db.FullWorthSpaceMembers.AsNoTracking()
            .Where(m => m.FullWorthSpaceId == fullWorthSpaceId && m.UserId == userId)
            .Select(m => m.Role)
            .SingleOrDefaultAsync(ct);
        if (membership is null) return BankConnectionAuthorizeResult.NotFound;

        if (connectionId is { } id)
        {
            var exists = await db.BankConnections.AsNoTracking().AnyAsync(x => x.Id == id && x.FullWorthSpaceId == fullWorthSpaceId, ct);
            if (!exists) return BankConnectionAuthorizeResult.NotFound;
        }

        return membership == FullWorthSpaceRoles.Owner
            ? BankConnectionAuthorizeResult.Authorized
            : BankConnectionAuthorizeResult.Forbidden;
    }

    private BankConnection DecryptSecrets(BankConnection entity)
    {
        entity.ProviderSessionId = DecryptFully(entity.ProviderSessionId);
        entity.AuthorizationId = DecryptFully(entity.AuthorizationId);
        return entity;
    }

    // Strip every encryption layer. Normally there is exactly one; a now-fixed round-trip bug (the DTO
    // handed to the Banking service carried the ciphertext, which was re-encrypted on write-back) could
    // stack several layers, so loop until Unprotect makes no further progress (or fails). This also lets
    // historically multi-encrypted rows self-heal on their next write. Null/legacy-plaintext pass through.
    private string? DecryptFully(string? stored)
    {
        for (var layer = 0; layer < 16 && stored is not null; layer++)
        {
            string? next;
            try { next = cipher.Unprotect(stored); }
            catch (System.Security.Cryptography.CryptographicException) { break; }
            catch (FormatException) { break; }
            if (next is null || ReferenceEquals(next, stored) || next == stored) return next;
            stored = next;
        }
        return stored;
    }

    private IQueryable<BankConnection> PublicConnections(Guid userId, Guid fullWorthSpaceId) =>
        db.BankConnections.AsNoTracking().Where(connection =>
            connection.FullWorthSpaceId == fullWorthSpaceId &&
            db.FullWorthSpaceMembers.Any(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId));

    private static BankConnectionStatusView Project(BankConnection connection, DateTimeOffset now)
    {
        var health = BankConnectionConsentHealthCalculator.Calculate(
            connection.Status,
            connection.ProviderSessionId,
            connection.ValidUntil,
            connection.ConsecutiveFailures,
            connection.LastError,
            connection.NextSyncAllowedAt,
            now);

        return new(
            connection.Id,
            connection.FullWorthSpaceId,
            connection.Provider,
            connection.InstitutionName,
            connection.Country,
            connection.Status,
            connection.ValidUntil,
            connection.LastSyncedAt,
            connection.NextSyncAllowedAt,
            connection.UpdatedAt,
            health.HealthStatus,
            health.DaysUntilExpiry);
    }

    private Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        db.FullWorthSpaceMembers.AsNoTracking().AnyAsync(member => member.FullWorthSpaceId == fullWorthSpaceId && member.UserId == userId, ct);
}

public sealed record BankConnectionWrite(
    Guid? Id,
    string Provider,
    string InstitutionName,
    string Country,
    string? AuthorizationState,
    string? AuthorizationId,
    string? ProviderSessionId,
    string Status,
    DateTimeOffset? ValidUntil,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSyncedAt,
    DateTimeOffset? NextSyncAllowedAt,
    int ConsecutiveFailures,
    string? LastError,
    // Required for a NEW connection (validated, owner-checked space). Absent/empty is rejected — there
    // is no LegacyId fallback on the live connect path any more.
    Guid? FullWorthSpaceId = null,
    Guid? AuthorizationUserId = null,
    DateTimeOffset? AuthorizationStateExpiresAt = null);

public sealed record BankConnectionAuthorizeRequest(Guid FullWorthSpaceId, Guid? ConnectionId);

public enum BankConnectionAuthorizeResult { Authorized, Forbidden, NotFound }

public sealed record ConsumeStateRequest(string State);

public static class BankConnectionEndpoints
{
    public static IEndpointRouteBuilder MapBankConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bank-connections").WithTags("Bank connections");
        group.MapGet("/", async (Guid fullWorthSpaceId, CurrentUserContext currentUser, BankConnectionStore store, CancellationToken ct) =>
        {
            var items = await store.ListForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, ct);
            return items is null ? Results.NotFound() : Results.Ok(items);
        });
        group.MapGet("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, BankConnectionStore store, CancellationToken ct) =>
        {
            var item = await store.GetForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        // Disconnect a bank: permanently deletes the connection and all of its synced accounts + data.
        group.MapDelete("/{id:guid}", async (Guid id, Guid fullWorthSpaceId, CurrentUserContext currentUser, BankConnectionStore store, CancellationToken ct) =>
            await store.DeleteForUserAsync(currentUser.RequireUserId(), fullWorthSpaceId, id, ct)
                ? Results.NoContent()
                : Results.NotFound());

        // Internal machine-to-machine Banking ingest path. Authentication is enforced separately for /internal/**.
        var internalGroup = app.MapGroup("/internal/banking/connections").WithTags("Internal banking");
        internalGroup.MapGet("/", async (BankConnectionStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));
        internalGroup.MapPost("/", async (BankConnectionWrite request, BankConnectionStore store, CancellationToken ct) =>
        {
            try { return Results.Ok(await store.UpsertAsync(request, ct)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        // One-time atomic state consumption (replaces the replayable read-only by-state lookup).
        internalGroup.MapPost("/consume-state", async (ConsumeStateRequest request, BankConnectionStore store, CancellationToken ct) =>
        {
            var item = await store.ConsumeAuthorizationStateAsync(request.State, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });
        // P0.2 owner authorization for the Banking service (connect + manual sync). The trusted user id
        // arrives in X-FullWorth-User-Id; /internal is already gated by the ingest key.
        internalGroup.MapPost("/authorize", async (HttpContext http, BankConnectionAuthorizeRequest request, BankConnectionStore store, CancellationToken ct) =>
        {
            if (!Guid.TryParse(http.Request.Headers["X-FullWorth-User-Id"], out var userId))
                return Results.BadRequest();
            return await store.AuthorizeAsync(userId, request.FullWorthSpaceId, request.ConnectionId, ct) switch
            {
                BankConnectionAuthorizeResult.Authorized => Results.NoContent(),
                BankConnectionAuthorizeResult.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
                _ => Results.NotFound()
            };
        });
        return app;
    }
}
