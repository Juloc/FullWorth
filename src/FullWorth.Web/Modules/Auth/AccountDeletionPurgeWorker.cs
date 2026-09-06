using System.Net;
using System.Net.Http.Json;
using FullWorth.Web.Data;
using FullWorth.Web.Modules.Bootstrap;
using FullWorth.Web.Security.BackendContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FullWorth.Web.Modules.Auth;

public sealed class AccountDeletionPurgeWorker(
    IServiceScopeFactory scopes,
    IOptions<AccountDeletionOptions> configuredOptions,
    TimeProvider timeProvider,
    ILogger<AccountDeletionPurgeWorker> logger) : BackgroundService
{
    private readonly AccountDeletionOptions options = Validate(configuredOptions.Value);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(options.PurgeInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var due = await db.Users.AsNoTracking()
            .Where(x => x.DeletionScheduledFor != null &&
                        x.DeletionScheduledFor <= timeProvider.GetUtcNow() &&
                        (x.DeletionLeaseUntil == null || x.DeletionLeaseUntil <= timeProvider.GetUtcNow()))
            .OrderBy(x => x.DeletionScheduledFor)
            .Select(x => x.Id)
            .Take(20)
            .ToListAsync(ct);

        foreach (var authUserId in due)
        {
            try { await PurgeOneAsync(scope.ServiceProvider, authUserId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Account purge failed for auth user {AuthUserId}", authUserId);
                await ReleaseFailedLeaseAsync(authUserId, "purge_exception", CancellationToken.None);
            }
        }
    }

    private async Task PurgeOneAsync(IServiceProvider services, Guid authUserId, CancellationToken ct)
    {
        var db = services.GetRequiredService<AuthDbContext>();
        var now = timeProvider.GetUtcNow();
        var leaseUntil = now.Add(options.PurgeLease);

        var acquired = await db.Users
            .Where(x => x.Id == authUserId &&
                        x.DeletionScheduledFor != null &&
                        x.DeletionScheduledFor <= now &&
                        (x.DeletionLeaseUntil == null || x.DeletionLeaseUntil <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DeletionLeaseUntil, leaseUntil)
                .SetProperty(x => x.DeletionLastError, (string?)null), ct);
        if (acquired != 1) return;

        var users = services.GetRequiredService<UserManager<AuthUser>>();
        var user = await users.FindByIdAsync(authUserId.ToString());
        if (user is null) return;

        var client = services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(FirstRunBootstrapper.BackendClientName);
        var backendOptions = services.GetRequiredService<BackendContextOptions>();
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/bootstrap/purge-user")
        {
            Content = JsonContent.Create(new { financeUserId = user.FinanceUserId })
        };
        request.Headers.TryAddWithoutValidation(BackendContextHeaders.InternalKey, backendOptions.InternalKey);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            await ReleaseFailedLeaseAsync(authUserId, $"backend_{(int)response.StatusCode}", ct);
            return;
        }

        var deleted = await users.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            await ReleaseFailedLeaseAsync(authUserId, "auth_delete_failed", ct);
            logger.LogError("Backend purge completed but auth deletion failed for {AuthUserId}: {Errors}",
                authUserId, string.Join("; ", deleted.Errors.Select(x => x.Code)));
        }
    }

    private async Task ReleaseFailedLeaseAsync(Guid authUserId, string error, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await db.Users.Where(x => x.Id == authUserId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.DeletionLeaseUntil, (DateTimeOffset?)null)
            .SetProperty(x => x.DeletionLastError, error[..Math.Min(error.Length, 120)]), ct);
    }

    private static AccountDeletionOptions Validate(AccountDeletionOptions value)
    {
        value.Validate();
        return value;
    }
}
