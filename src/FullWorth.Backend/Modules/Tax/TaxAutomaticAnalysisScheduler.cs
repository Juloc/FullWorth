using FullWorth.Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Tax;

/// <summary>
/// Periodically analyzes existing personal tax profiles that explicitly remain enabled. Core finance
/// ingestion never depends on this worker and failures are isolated per profile. Background runs are
/// deliberately deterministic-only: an AI provider is used only from a user-triggered analysis.
/// </summary>
public sealed class TaxAutomaticAnalysisWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<TaxAutomaticAnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
            do
            {
                await ExecuteCycleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Automatic tax analysis worker stopped unexpectedly.");
        }
    }

    private async Task ExecuteCycleAsync(CancellationToken ct)
    {
        List<TaxAutomaticTarget> targets;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
            var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
            targets = await (
                from profile in db.TaxProfiles.AsNoTracking()
                join settings in db.TaxSettings.AsNoTracking() on profile.FullWorthSpaceId equals settings.FullWorthSpaceId
                where profile.Active && profile.AssistantEnabled && profile.UserId.HasValue &&
                      settings.Enabled && settings.AutomaticAnalysisEnabled &&
                      !db.TaxAnalysisRuns.Any(run => run.TaxProfileId == profile.Id &&
                          run.TaxYear == settings.DefaultTaxYear && run.Status == "completed" &&
                          run.FinishedAt >= cutoff)
                orderby profile.UpdatedAt
                select new TaxAutomaticTarget(profile.UserId!.Value, profile.FullWorthSpaceId, settings.DefaultTaxYear))
                .Take(100)
                .ToListAsync(ct);
        }

        foreach (var target in targets)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FullWorthDbContext>();
                var store = scope.ServiceProvider.GetRequiredService<TaxStore>();
                var deterministic = scope.ServiceProvider.GetRequiredService<TaxAnalysisService>();
                var coordinator = new TaxAnalysisCoordinator(db, store, deterministic);
                await coordinator.AnalyzeAsync(target.UserId, target.FullWorthSpaceId, target.TaxYear, "automatic", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Automatic tax analysis failed for finance space {FullWorthSpaceId}.", target.FullWorthSpaceId);
            }
        }
    }

    private sealed record TaxAutomaticTarget(Guid UserId, Guid FullWorthSpaceId, int TaxYear);
}
