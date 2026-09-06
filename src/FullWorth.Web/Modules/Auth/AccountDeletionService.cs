using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FullWorth.Web.Data;
using FullWorth.Web.Modules.Bootstrap;
using FullWorth.Web.Modules.Sessions;
using FullWorth.Web.Security.BackendContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Auth;

public sealed class AccountDeletionService(
    AuthDbContext db,
    UserManager<AuthUser> users,
    SessionService sessions,
    IHttpClientFactory httpClientFactory,
    BackendContextOptions backendOptions,
    IOptions<AccountDeletionOptions> configuredOptions,
    TimeProvider timeProvider)
{
    private readonly AccountDeletionOptions options = Validate(configuredOptions.Value);

    public async Task<(AccountDeletionStatusDto? Status, string? Error)> RequestAsync(
        ClaimsPrincipal principal,
        string currentPassword,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(principal);
        if (user is null) return (null, "invalid_user");

        if (!await users.CheckPasswordAsync(user, currentPassword ?? string.Empty))
            return (null, "invalid_password");

        var now = timeProvider.GetUtcNow();
        if (user.DeletionRequestedAt.HasValue)
            return (ToStatus(user, now), null);

        var deactivated = await SetBackendActiveAsync(user.FinanceUserId, active: false, ct);
        if (!deactivated) return (null, "backend_deactivation_failed");

        user.DeletionRequestedAt = now;
        user.DeletionScheduledFor = now.Add(options.RecoveryWindow);
        user.DeletionLeaseUntil = null;
        user.DeletionLastError = null;

        try
        {
            var result = await users.UpdateAsync(user);
            if (!result.Succeeded)
            {
                _ = await SetBackendActiveAsync(user.FinanceUserId, active: true, CancellationToken.None);
                return (null, "state_update_failed");
            }
        }
        catch
        {
            _ = await SetBackendActiveAsync(user.FinanceUserId, active: true, CancellationToken.None);
            throw;
        }

        if (SessionClaims.TryGetSessionId(principal, out var currentSessionId))
            await sessions.RevokeAllOtherSessionsAsync(user.Id, currentSessionId, ct);
        else
            await sessions.RevokeAllSessionsAsync(user.Id, ct);

        return (ToStatus(user, now), null);
    }

    public async Task<AccountDeletionStatusDto?> GetStatusAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(principal);
        return user is null ? null : ToStatus(user, timeProvider.GetUtcNow());
    }

    public async Task<(AccountDeletionStatusDto? Status, string? Error)> CancelAsync(
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(principal);
        if (user is null) return (null, "invalid_user");
        if (!user.DeletionRequestedAt.HasValue)
            return (ToStatus(user, timeProvider.GetUtcNow()), null);

        var now = timeProvider.GetUtcNow();
        if (user.DeletionScheduledFor is null || now >= user.DeletionScheduledFor.Value)
            return (null, "deletion_deadline_passed");
        if (user.DeletionLeaseUntil is { } lease && lease > now)
            return (null, "purge_in_progress");

        if (!await SetBackendActiveAsync(user.FinanceUserId, active: true, ct))
            return (null, "backend_reactivation_failed");

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionLeaseUntil = null;
        user.DeletionLastError = null;
        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            _ = await SetBackendActiveAsync(user.FinanceUserId, active: false, CancellationToken.None);
            return (null, "state_update_failed");
        }

        return (ToStatus(user, now), null);
    }

    public async Task<bool> IsPendingAsync(Guid authUserId, CancellationToken ct)
    {
        if (authUserId == Guid.Empty) return false;
        return await db.Users.AsNoTracking()
            .AnyAsync(x => x.Id == authUserId && x.DeletionRequestedAt != null, ct);
    }

    private async Task<AuthUser?> GetCurrentUserAsync(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var authUserId)
            ? await users.FindByIdAsync(authUserId.ToString())
            : null;
    }

    private async Task<bool> SetBackendActiveAsync(Guid financeUserId, bool active, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(FirstRunBootstrapper.BackendClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            active ? "api/bootstrap/reactivate-user" : "api/bootstrap/deactivate-user")
        {
            Content = JsonContent.Create(new { financeUserId })
        };
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.InternalKey, backendOptions.InternalKey);
        using var response = await client.SendAsync(request, ct);
        return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK;
    }

    private static AccountDeletionStatusDto ToStatus(AuthUser user, DateTimeOffset now)
    {
        var pending = user.DeletionRequestedAt.HasValue;
        var canReactivate = pending &&
            user.DeletionScheduledFor is { } deadline &&
            now < deadline &&
            (user.DeletionLeaseUntil is null || user.DeletionLeaseUntil <= now);
        return new(
            pending,
            user.DeletionRequestedAt,
            user.DeletionScheduledFor,
            canReactivate,
            user.DeletionLastError);
    }

    private static AccountDeletionOptions Validate(AccountDeletionOptions value)
    {
        value.Validate();
        return value;
    }
}
