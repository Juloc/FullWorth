using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Bootstrap;
using FullWorth.Web.Modules.Sessions;
using FullWorth.Web.Security.BackendContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Admin;

public sealed record AdminCapabilitiesDto(bool Admin, bool TwoFactorEnabled);

public sealed record AdminOverviewDto(
    int Users,
    int Active,
    int Disabled,
    int PendingDeletion,
    int FailedDeletion,
    int Admins);

public sealed record AdminUserListItemDto(
    Guid Id,
    string Email,
    bool IsAdmin,
    bool IsDisabled,
    bool TwoFactorEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DeletionRequestedAt,
    DateTimeOffset? DeletionScheduledFor,
    string? DeletionLastError,
    int ActiveSessionCount,
    DateTimeOffset? LastSessionSeenAt);

public sealed record AdminUserDetailDto(
    AdminUserListItemDto User,
    IReadOnlyList<SessionDto> Sessions);

public sealed record AdminUserPageDto(
    IReadOnlyList<AdminUserListItemDto> Items,
    int Offset,
    int Limit,
    int Total);

public sealed record AdminMutationResult(bool Succeeded, string? Error = null);

public sealed class InstanceAdminService(
    AuthDbContext db,
    UserManager<AuthUser> users,
    SessionService sessions,
    AccountDeletionService deletion,
    IHttpClientFactory httpClientFactory,
    BackendContextOptions backendOptions,
    TimeProvider timeProvider)
{
    public async Task<AuthUser?> GetCurrentAdminAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var authUserId))
            return null;

        return await db.Users.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == authUserId &&
            x.IsAdmin &&
            !x.IsDisabled &&
            x.DeletionRequestedAt == null, ct);
    }

    public async Task<AdminCapabilitiesDto> GetCapabilitiesAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var authUserId))
            return new(false, false);

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == authUserId, ct);
        return user is null
            ? new(false, false)
            : new(
                user.IsAdmin && !user.IsDisabled && user.DeletionRequestedAt == null,
                user.TwoFactorEnabled);
    }

    public async Task<AdminOverviewDto> GetOverviewAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        return new(
            await db.Users.CountAsync(ct),
            await db.Users.CountAsync(x => !x.IsDisabled && x.DeletionRequestedAt == null, ct),
            await db.Users.CountAsync(x => x.IsDisabled, ct),
            await db.Users.CountAsync(x => x.DeletionRequestedAt != null, ct),
            await db.Users.CountAsync(x =>
                x.DeletionRequestedAt != null &&
                (x.DeletionLastError != null ||
                 (x.DeletionScheduledFor != null && x.DeletionScheduledFor < now.AddHours(-2))), ct),
            await db.Users.CountAsync(x => x.IsAdmin, ct));
    }

    public async Task<AdminUserPageDto> ListUsersAsync(
        string? search,
        string? status,
        int offset,
        int limit,
        CancellationToken ct)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);

        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var upper = term.ToUpperInvariant();
            if (Guid.TryParse(term, out var id))
                query = query.Where(x => x.Id == id);
            else
                query = query.Where(x =>
                    x.NormalizedEmail != null && x.NormalizedEmail.Contains(upper));
        }

        query = (status ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "active" => query.Where(x => !x.IsDisabled && x.DeletionRequestedAt == null),
            "disabled" => query.Where(x => x.IsDisabled),
            "deleting" => query.Where(x => x.DeletionRequestedAt != null),
            "admins" => query.Where(x => x.IsAdmin),
            _ => query
        };

        var total = await query.CountAsync(ct);
        var now = timeProvider.GetUtcNow();
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip(offset)
            .Take(limit)
            .Select(x => new AdminUserListItemDto(
                x.Id,
                x.Email ?? string.Empty,
                x.IsAdmin,
                x.IsDisabled,
                x.TwoFactorEnabled,
                x.CreatedAt,
                x.UpdatedAt,
                x.DeletionRequestedAt,
                x.DeletionScheduledFor,
                x.DeletionLastError,
                db.UserSessions.Count(s =>
                    s.AuthUserId == x.Id &&
                    s.RevokedAt == null &&
                    s.ExpiresAt > now &&
                    s.AbsoluteExpiresAt > now),
                db.UserSessions
                    .Where(s => s.AuthUserId == x.Id)
                    .Max(s => (DateTimeOffset?)s.LastSeenAt)))
            .ToListAsync(ct);

        return new(items, offset, limit, total);
    }

    public async Task<AdminUserDetailDto?> GetUserAsync(Guid authUserId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var user = await db.Users.AsNoTracking()
            .Where(x => x.Id == authUserId)
            .Select(x => new AdminUserListItemDto(
                x.Id,
                x.Email ?? string.Empty,
                x.IsAdmin,
                x.IsDisabled,
                x.TwoFactorEnabled,
                x.CreatedAt,
                x.UpdatedAt,
                x.DeletionRequestedAt,
                x.DeletionScheduledFor,
                x.DeletionLastError,
                db.UserSessions.Count(s =>
                    s.AuthUserId == x.Id &&
                    s.RevokedAt == null &&
                    s.ExpiresAt > now &&
                    s.AbsoluteExpiresAt > now),
                db.UserSessions
                    .Where(s => s.AuthUserId == x.Id)
                    .Max(s => (DateTimeOffset?)s.LastSeenAt)))
            .SingleOrDefaultAsync(ct);

        if (user is null) return null;

        var list = await sessions.ListSessionsAsync(authUserId, null, ct);
        return new(user, list.Sessions);
    }

    public async Task<AdminMutationResult> DisableAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        var target = await users.FindByIdAsync(targetId.ToString());
        if (target is null) return new(false, "not_found");
        if (target.IsDisabled) return new(true);

        if (target.IsAdmin && !await HasOtherOperationalAdminAsync(targetId, ct))
            return new(false, "last_admin");

        if (!await SetBackendActiveAsync(target.FinanceUserId, false, ct))
            return new(false, "backend_failed");

        target.IsDisabled = true;
        var updated = await users.UpdateAsync(target);
        if (!updated.Succeeded)
        {
            _ = await SetBackendActiveAsync(target.FinanceUserId, true, CancellationToken.None);
            return new(false, "update_failed");
        }

        await sessions.RevokeAllSessionsAsync(target.Id, ct);
        await AuditAsync(actorId, targetId, "user.disabled", ct);
        return new(true);
    }

    public async Task<AdminMutationResult> EnableAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        var target = await users.FindByIdAsync(targetId.ToString());
        if (target is null) return new(false, "not_found");
        if (target.DeletionRequestedAt is not null) return new(false, "pending_deletion");
        if (!target.IsDisabled) return new(true);

        if (!await SetBackendActiveAsync(target.FinanceUserId, true, ct))
            return new(false, "backend_failed");

        target.IsDisabled = false;
        var updated = await users.UpdateAsync(target);
        if (!updated.Succeeded)
        {
            _ = await SetBackendActiveAsync(target.FinanceUserId, false, CancellationToken.None);
            return new(false, "update_failed");
        }

        await AuditAsync(actorId, targetId, "user.enabled", ct);
        return new(true);
    }

    public async Task<AdminMutationResult> RevokeSessionsAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == targetId, ct))
            return new(false, "not_found");

        await sessions.RevokeAllSessionsAsync(targetId, ct);
        await AuditAsync(actorId, targetId, "sessions.revoked", ct);
        return new(true);
    }

    public async Task<AdminMutationResult> ScheduleDeletionAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        var target = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == targetId, ct);
        if (target is null) return new(false, "not_found");
        if (target.IsAdmin && !await HasOtherOperationalAdminAsync(targetId, ct))
            return new(false, "last_admin");

        var (status, error) = await deletion.RequestForAdminAsync(targetId, ct);
        if (status is null) return new(false, error ?? "deletion_failed");

        await AuditAsync(actorId, targetId, "deletion.scheduled", ct);
        return new(true);
    }

    public async Task<AdminMutationResult> CancelDeletionAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        var (status, error) = await deletion.CancelForAdminAsync(targetId, ct);
        if (status is null) return new(false, error ?? "deletion_cancel_failed");

        await AuditAsync(actorId, targetId, "deletion.cancelled", ct);
        return new(true);
    }

    public async Task<AdminMutationResult> GrantAdminAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        var target = await users.FindByIdAsync(targetId.ToString());
        if (target is null) return new(false, "not_found");
        if (target.IsDisabled || target.DeletionRequestedAt is not null)
            return new(false, "invalid_state");
        if (target.IsAdmin) return new(true);

        target.IsAdmin = true;
        var result = await users.UpdateAsync(target);
        if (!result.Succeeded) return new(false, "update_failed");

        await AuditAsync(actorId, targetId, "admin.granted", ct);
        return new(true);
    }

    public async Task<AdminMutationResult> RevokeAdminAsync(Guid actorId, Guid targetId, CancellationToken ct)
    {
        var target = await users.FindByIdAsync(targetId.ToString());
        if (target is null) return new(false, "not_found");
        if (!target.IsAdmin) return new(true);
        if (!await HasOtherOperationalAdminAsync(targetId, ct))
            return new(false, "last_admin");

        target.IsAdmin = false;
        var result = await users.UpdateAsync(target);
        if (!result.Succeeded) return new(false, "update_failed");

        await AuditAsync(actorId, targetId, "admin.revoked", ct);
        return new(true);
    }

    public async Task<IReadOnlyList<AdminAuditEvent>> ListAuditAsync(int limit, CancellationToken ct) =>
        await db.AdminAuditEvents.AsNoTracking()
            .OrderByDescending(x => x.OccurredAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToListAsync(ct);

    private Task<bool> HasOtherOperationalAdminAsync(Guid excludedId, CancellationToken ct) =>
        db.Users.AsNoTracking().AnyAsync(x =>
            x.Id != excludedId &&
            x.IsAdmin &&
            !x.IsDisabled &&
            x.DeletionRequestedAt == null, ct);

    private async Task AuditAsync(Guid actorId, Guid? targetId, string action, CancellationToken ct)
    {
        db.AdminAuditEvents.Add(new AdminAuditEvent
        {
            ActorAuthUserId = actorId,
            TargetAuthUserId = targetId,
            Action = action,
            Outcome = "success",
            OccurredAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> SetBackendActiveAsync(Guid financeUserId, bool active, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(FirstRunBootstrapper.BackendClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            active ? "api/bootstrap/reactivate-user" : "api/bootstrap/deactivate-user")
        {
            Content = JsonContent.Create(new { financeUserId })
        };
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.InternalKey, backendOptions.InternalKey);
        using var response = await client.SendAsync(request, ct);
        return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent;
    }
}
