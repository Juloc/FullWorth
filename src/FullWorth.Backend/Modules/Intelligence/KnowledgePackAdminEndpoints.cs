using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record KnowledgePackRollbackRequest(string PackId, string Version);

public static class KnowledgePackAdminEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgePackAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/admin/cloud/knowledge-pack").WithTags("Intelligence Admin");

        group.MapGet("/status", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            KnowledgePackService packs,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated || !await authorizer.IsAdminAsync(currentUser.UserId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await packs.GetStatusAsync(ct));
        });

        group.MapPost("/sync", async (
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            IntelligenceDbContext db,
            KnowledgePackSyncService sync,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var userId = currentUser.UserId;
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var result = await sync.SyncLatestAsync(ct);
            IntelligenceAuditWriter.Record(
                db,
                userId,
                "cloud.knowledge_pack.sync",
                "KnowledgePackInstallation",
                outcome: result.ErrorCode ?? (result.Updated ? $"updated:{result.Version}" : "unchanged"));
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        group.MapPost("/rollback", async (
            KnowledgePackRollbackRequest request,
            CurrentUserContext currentUser,
            IntelligenceAdminAuthorizer authorizer,
            CloudIntelligenceStateService cloudState,
            IntelligenceDbContext db,
            KnowledgePackService packs,
            CancellationToken ct) =>
        {
            if (!currentUser.IsAuthenticated)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var userId = currentUser.UserId;
            if (!await authorizer.IsAdminAsync(userId, ct))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!await cloudState.HasCurrentActiveConsentAsync(ct))
                return Results.Conflict(new { error = "cloud_intelligence_consent_required" });
            if (string.IsNullOrWhiteSpace(request.PackId) || string.IsNullOrWhiteSpace(request.Version))
                return Results.BadRequest(new { error = "pack_id_and_version_required" });

            try
            {
                var result = await packs.RollbackAsync(request.PackId.Trim(), request.Version.Trim(), ct);
                IntelligenceAuditWriter.Record(
                    db,
                    userId,
                    "cloud.knowledge_pack.rollback",
                    "KnowledgePackInstallation",
                    outcome: $"{result.PackId}:{result.Version}");
                await db.SaveChangesAsync(ct);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new { error = "knowledge_pack_archive_not_found" });
            }
            catch (KnowledgePackVerificationException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: ex.ErrorCode);
            }
        });

        return app;
    }
}
