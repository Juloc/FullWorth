using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record AiInstanceSettingsView(
    bool Enabled,
    string Provider,
    Guid? CredentialId,
    bool AllowUserCredentials,
    string DefaultTextModel,
    string DefaultVisionModel,
    decimal? DailyBudgetEur,
    decimal? MonthlyBudgetEur,
    bool DailyScanEnabled,
    bool WeeklyDeepScanEnabled,
    bool MonthlyReviewEnabled,
    bool ReceiptAiEnabled,
    bool MerchantAiEnabled,
    bool CategoryAiEnabled,
    bool ContractAiEnabled,
    bool ProductAiEnabled,
    bool LogoResearchEnabled,
    bool InternetResearchEnabled,
    DateTimeOffset UpdatedAt);

public sealed record UpdateAiInstanceSettingsRequest(
    bool Enabled,
    string Provider,
    Guid? CredentialId,
    bool AllowUserCredentials,
    string DefaultTextModel,
    string DefaultVisionModel,
    decimal? DailyBudgetEur,
    decimal? MonthlyBudgetEur,
    bool DailyScanEnabled,
    bool WeeklyDeepScanEnabled,
    bool MonthlyReviewEnabled,
    bool ReceiptAiEnabled,
    bool MerchantAiEnabled,
    bool CategoryAiEnabled,
    bool ContractAiEnabled,
    bool ProductAiEnabled,
    bool LogoResearchEnabled,
    bool InternetResearchEnabled);

public sealed record CreateAiCredentialRequest(string Provider, string Name, string Secret);
public sealed record RunIntelligenceJobRequest(string? IdempotencyKey);

public static class IntelligenceAdminEndpoints
{
    public static IEndpointRouteBuilder MapIntelligenceAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/admin").WithTags("Intelligence Admin");

        group.MapGet("/overview", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceStore store,
            IntelligenceProviderRegistry providers,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var settings = await store.GetOrCreateInstanceSettingsAsync(ct);
            var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
            var month = new DateTimeOffset(new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc));
            var todayCost = await db.AiRuns.AsNoTracking().Where(x => x.StartedAt >= today)
                .SumAsync(x => x.ActualCostEur ?? x.EstimatedCostEur ?? 0m, ct);
            var monthCost = await db.AiRuns.AsNoTracking().Where(x => x.StartedAt >= month)
                .SumAsync(x => x.ActualCostEur ?? x.EstimatedCostEur ?? 0m, ct);

            return Results.Ok(new
            {
                settings = ToView(settings),
                providers = providers.Descriptors,
                credentialCount = await db.AiCredentials.AsNoTracking().CountAsync(x => x.OwnerUserId == null, ct),
                pendingSuggestions = await db.IntelligenceSuggestions.AsNoTracking().CountAsync(x => x.Status == IntelligenceSuggestionStatuses.Pending, ct),
                failedJobs = await db.IntelligenceJobs.AsNoTracking().CountAsync(x => x.Status == IntelligenceJobStatuses.Failed, ct),
                todayCostEur = todayCost,
                monthlyCostEur = monthCost
            });
        });

        group.MapGet("/cloud", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            CloudIntelligenceStateService cloudState,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await cloudState.GetAsync(ct));
        });

        group.MapPost("/cloud/enable", async (
            EnableCloudIntelligenceRequest request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            CloudIntelligenceStateService cloudState,
            IFullWorthCloudClient cloud,
            CloudInstanceCredentialStore credentialStore,
            CancellationToken ct) =>
        {
            var actorUserId = await GetAdminUserIdAsync(currentUser, authorizer, ct);
            if (actorUserId is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var state = await cloudState.EnableAsync(actorUserId.Value, request, ct);
                IntelligenceAuditWriter.Record(db, actorUserId.Value, "cloud.enabled", "CloudConnectionState", outcome: CloudIntelligencePolicy.CurrentVersion);
                await db.SaveChangesAsync(ct);

                // Registration is best-effort. Enabling Cloud Intelligence must never make setup fail
                // because the platform is temporarily unavailable; the outbox will retry later.
                try
                {
                    var registration = await cloud.RegisterAsync(
                        state.InstanceId,
                        CloudIntelligencePolicy.CurrentVersion,
                        request.ClientVersion ?? "unknown",
                        ct);
                    await credentialStore.SaveAsync(registration, ct);
                    await cloudState.SetTransportStatusAsync(
                        state.InstanceId, null, registration.EntitlementStatus,
                        DateTimeOffset.UtcNow, null, ct);
                }
                catch (FullWorthCloudException ex)
                {
                    await cloudState.SetTransportStatusAsync(
                        state.InstanceId, ex.ErrorCode, null, null, null, ct);
                }

                return Results.Ok(await cloudState.GetAsync(ct));
            }
            catch (ArgumentException ex)
            {
                return Results.Conflict(new
                {
                    error = "cloud_policy_stale",
                    message = ex.Message,
                    currentPolicyVersion = CloudIntelligencePolicy.CurrentVersion
                });
            }
        });

        group.MapPost("/cloud/disable", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            CloudIntelligenceStateService cloudState,
            CancellationToken ct) =>
        {
            var actorUserId = await GetAdminUserIdAsync(currentUser, authorizer, ct);
            if (actorUserId is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var state = await cloudState.DisableAsync(actorUserId.Value, ct);
            IntelligenceAuditWriter.Record(db, actorUserId.Value, "cloud.disabled", "CloudConnectionState", outcome: "revoked");
            await db.SaveChangesAsync(ct);
            return Results.Ok(state);
        });

        group.MapPost("/cloud/sync", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            CloudLearningOutboxUploader uploader,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var sent = await uploader.UploadOnceAsync(ct);
            return Results.Ok(new { sent });
        });

        group.MapGet("/providers", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceProviderRegistry providers,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(providers.Descriptors);
        });

        group.MapGet("/settings", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceStore store,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(ToView(await store.GetOrCreateInstanceSettingsAsync(ct)));
        });

        group.MapPut("/settings", async (
            UpdateAiInstanceSettingsRequest request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceStore store,
            CancellationToken ct) =>
        {
            var actorUserId = await GetAdminUserIdAsync(currentUser, authorizer, ct);
            if (actorUserId is null) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var provider = request.Provider?.Trim().ToLowerInvariant() ?? string.Empty;
            if (request.CredentialId.HasValue)
            {
                var validCredential = await db.AiCredentials.AsNoTracking().AnyAsync(x =>
                    x.Id == request.CredentialId.Value && x.OwnerUserId == null && x.Provider == provider, ct);
                if (!validCredential) return Results.BadRequest(new { error = "credential_provider_mismatch" });
            }

            try
            {
                var saved = await store.SaveInstanceSettingsAsync(new AiInstanceSettings
                {
                    Enabled = request.Enabled,
                    Provider = provider,
                    CredentialId = request.CredentialId,
                    AllowUserCredentials = request.AllowUserCredentials,
                    DefaultTextModel = request.DefaultTextModel ?? string.Empty,
                    DefaultVisionModel = request.DefaultVisionModel ?? string.Empty,
                    DailyBudgetEur = request.DailyBudgetEur,
                    MonthlyBudgetEur = request.MonthlyBudgetEur,
                    DailyScanEnabled = request.DailyScanEnabled,
                    WeeklyDeepScanEnabled = request.WeeklyDeepScanEnabled,
                    MonthlyReviewEnabled = request.MonthlyReviewEnabled,
                    ReceiptAiEnabled = request.ReceiptAiEnabled,
                    MerchantAiEnabled = request.MerchantAiEnabled,
                    CategoryAiEnabled = request.CategoryAiEnabled,
                    ContractAiEnabled = request.ContractAiEnabled,
                    ProductAiEnabled = request.ProductAiEnabled,
                    LogoResearchEnabled = request.LogoResearchEnabled,
                    InternetResearchEnabled = request.InternetResearchEnabled
                }, ct);
                IntelligenceAuditWriter.Record(db, actorUserId.Value, "settings.updated", "AiInstanceSettings", saved.Id);
                await db.SaveChangesAsync(ct);
                return Results.Ok(ToView(saved));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_settings", message = ex.Message });
            }
            catch (InvalidOperationException)
            {
                return Results.BadRequest(new { error = "unsupported_provider" });
            }
        });

        group.MapGet("/credentials", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceStore store,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await store.ListCredentialsAsync(null, ct));
        });

        group.MapPost("/credentials", async (
            CreateAiCredentialRequest request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceStore store,
            CancellationToken ct) =>
        {
            var actorUserId = await GetAdminUserIdAsync(currentUser, authorizer, ct);
            if (actorUserId is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var credential = await store.CreateCredentialAsync(null, request.Provider ?? string.Empty, request.Name ?? string.Empty, request.Secret ?? string.Empty, ct);
                IntelligenceAuditWriter.Record(db, actorUserId.Value, "credential.created", "AiCredential", credential.Id);
                await db.SaveChangesAsync(ct);
                return Results.Created($"/api/intelligence/admin/credentials/{credential.Id}", credential);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_credential", message = ex.Message });
            }
            catch (InvalidOperationException)
            {
                return Results.BadRequest(new { error = "unsupported_provider" });
            }
        });

        group.MapDelete("/credentials/{id:guid}", async (
            Guid id,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceStore store,
            CancellationToken ct) =>
        {
            var actorUserId = await GetAdminUserIdAsync(currentUser, authorizer, ct);
            if (actorUserId is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                await store.DeleteCredentialAsync(id, null, ct);
                IntelligenceAuditWriter.Record(db, actorUserId.Value, "credential.deleted", "AiCredential", id);
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        group.MapPost("/credentials/{id:guid}/test", async (
            Guid id,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceStore store,
            CancellationToken ct) =>
        {
            var actorUserId = await GetAdminUserIdAsync(currentUser, authorizer, ct);
            if (actorUserId is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var result = await store.TestCredentialAsync(id, null, ct);
                IntelligenceAuditWriter.Record(db, actorUserId.Value, "credential.tested", "AiCredential", id, result.Success ? "success" : "failed");
                await db.SaveChangesAsync(ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        group.MapGet("/runs", async (
            int? limit,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var rows = await db.AiRuns.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(take).ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapGet("/jobs", async (
            int? limit,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var rows = await db.IntelligenceJobs.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(take).ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapGet("/audit", async (
            int? limit,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            CancellationToken ct) =>
        {
            if (await GetAdminUserIdAsync(currentUser, authorizer, ct) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var take = Math.Clamp(limit ?? 100, 1, 500);
            var rows = await db.IntelligenceAuditEvents.AsNoTracking().OrderByDescending(x => x.OccurredAt).Take(take).ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapPost("/jobs/{type}/run", async (
            string type,
            RunIntelligenceJobRequest? request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            IntelligenceManualJobService jobs,
            CancellationToken ct) =>
        {
            var actorUserId = await GetAdminUserIdAsync(currentUser, authorizer, ct);
            if (actorUserId is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                var result = await jobs.RunAsync(type, request?.IdempotencyKey, ct);
                IntelligenceAuditWriter.Record(db, actorUserId.Value, "job.triggered", "IntelligenceJob", result.JobId,
                    result.Status == IntelligenceJobStatuses.Succeeded ? "success" : "failed");
                await db.SaveChangesAsync(ct);
                return result.Status == IntelligenceJobStatuses.Succeeded
                    ? Results.Ok(result)
                    : Results.UnprocessableEntity(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_job", message = ex.Message });
            }
        });

        return app;
    }

    private static async Task<Guid?> GetAdminUserIdAsync(CurrentUserContext currentUser, IntelligenceAdminAuthorizer authorizer, CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        return await authorizer.IsAdminAsync(userId, ct) ? userId : null;
    }

    private static AiInstanceSettingsView ToView(AiInstanceSettings x) => new(
        x.Enabled,
        x.Provider,
        x.CredentialId,
        x.AllowUserCredentials,
        x.DefaultTextModel,
        x.DefaultVisionModel,
        x.DailyBudgetEur,
        x.MonthlyBudgetEur,
        x.DailyScanEnabled,
        x.WeeklyDeepScanEnabled,
        x.MonthlyReviewEnabled,
        x.ReceiptAiEnabled,
        x.MerchantAiEnabled,
        x.CategoryAiEnabled,
        x.ContractAiEnabled,
        x.ProductAiEnabled,
        x.LogoResearchEnabled,
        x.InternetResearchEnabled,
        x.UpdatedAt);
}
