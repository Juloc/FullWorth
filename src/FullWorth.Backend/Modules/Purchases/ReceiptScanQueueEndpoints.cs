using FullWorth.Backend.Security;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Modules.Purchases;

public static class ReceiptScanQueueEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/receipt-scan/jobs", EnqueueAsync);
        group.MapGet("/receipt-scan/jobs", ListAsync);
        group.MapGet("/receipt-scan/jobs/{jobId:guid}", GetAsync);
        group.MapGet("/receipt-scan/jobs/{jobId:guid}/sources", SourcesAsync);
        group.MapPost("/receipt-scan/jobs/{jobId:guid}/sources", AddSourcesAsync);
        group.MapPut("/receipt-scan/jobs/{jobId:guid}/sources/order", ReorderSourcesAsync);
        group.MapPut("/receipt-scan/jobs/{jobId:guid}/sources/{sourceId:guid}", ReplaceSourceAsync);
        group.MapDelete("/receipt-scan/jobs/{jobId:guid}/sources/{sourceId:guid}", DeleteSourceAsync);
        group.MapPost("/receipt-scan/jobs/{jobId:guid}/start", StartAsync);
        group.MapPost("/receipt-scan/jobs/{jobId:guid}/retry", RetryAsync);
    }

    private static async Task<IResult> EnqueueAsync(
        Guid fullWorthSpaceId,
        HttpRequest request,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanQueueService queue,
        IOptions<PurchaseStorageOptions> options,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var fileLimitError = await ValidatePhysicalFileLimitAsync(request, 0, options.Value.MaxReceiptFiles, ct);
        if (fileLimitError is not null) return Results.BadRequest(new { error = fileLimitError });
        var outcome = await queue.EnqueueAsync(userId, fullWorthSpaceId, request, ct);
        return outcome.Success && outcome.Job is not null
            ? Results.Json(outcome.Job, statusCode: StatusCodes.Status202Accepted)
            : Results.BadRequest(new { error = outcome.Error ?? "Receipt draft could not be created." });
    }

    private static async Task<IResult> ListAsync(
        Guid fullWorthSpaceId,
        bool? includeCompleted,
        int? limit,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var rows = await jobs.ListForUserAsync(userId, fullWorthSpaceId, includeCompleted ?? true, Math.Clamp(limit ?? 30, 1, 100), ct);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetAsync(
        Guid jobId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var row = await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        return row is null ? Results.NotFound() : Results.Ok(row);
    }

    private static async Task<IResult> SourcesAsync(
        Guid jobId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        var sources = await jobs.ListSourcesForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        return sources is null ? Results.NotFound() : Results.Ok(sources);
    }

    private static async Task<IResult> AddSourcesAsync(
        Guid jobId,
        Guid fullWorthSpaceId,
        HttpRequest request,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        ReceiptScanQueueService queue,
        IOptions<PurchaseStorageOptions> options,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct) is null) return Results.NotFound();
        var existing = await jobs.ListSourcesForUserAsync(userId, fullWorthSpaceId, jobId, ct);
        if (existing is null) return Results.NotFound();
        var physicalCount = existing.Where(x => x.PurchaseDocumentId.HasValue).Select(x => x.PurchaseDocumentId!.Value).Distinct().Count();
        var fileLimitError = await ValidatePhysicalFileLimitAsync(request, physicalCount, options.Value.MaxReceiptFiles, ct);
        if (fileLimitError is not null) return Results.BadRequest(new { error = fileLimitError });
        var outcome = await queue.AddSourcesAsync(userId, fullWorthSpaceId, jobId, request, ct);
        return outcome.Success ? Results.Ok(outcome.Sources ?? []) : Results.BadRequest(new { error = outcome.Error });
    }

    private static async Task<IResult> ReorderSourcesAsync(
        Guid jobId,
        Guid fullWorthSpaceId,
        ReceiptScanOrderRequest request,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        ReceiptScanQueueService queue,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct) is null) return Results.NotFound();
        var outcome = await queue.ReorderAsync(userId, fullWorthSpaceId, jobId, request, ct);
        return outcome.Success ? Results.Ok(outcome.Sources ?? []) : Results.BadRequest(new { error = outcome.Error });
    }

    private static async Task<IResult> ReplaceSourceAsync(
        Guid jobId,
        Guid sourceId,
        Guid fullWorthSpaceId,
        HttpRequest request,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        ReceiptScanQueueService queue,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct) is null) return Results.NotFound();
        var outcome = await queue.ReplaceSourceAsync(userId, fullWorthSpaceId, jobId, sourceId, request, ct);
        return outcome.Success ? Results.Ok(outcome.Sources ?? []) : Results.BadRequest(new { error = outcome.Error });
    }

    private static async Task<IResult> DeleteSourceAsync(
        Guid jobId,
        Guid sourceId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        ReceiptScanQueueService queue,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct) is null) return Results.NotFound();
        var outcome = await queue.DeleteSourceAsync(userId, fullWorthSpaceId, jobId, sourceId, ct);
        return outcome.Success ? Results.Ok(outcome.Sources ?? []) : Results.BadRequest(new { error = outcome.Error });
    }

    private static async Task<IResult> StartAsync(
        Guid jobId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        ReceiptScanQueueService queue,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct) is null) return Results.NotFound();
        var outcome = await queue.StartAsync(userId, fullWorthSpaceId, jobId, ct);
        return outcome.Success && outcome.Job is not null
            ? Results.Json(outcome.Job, statusCode: StatusCodes.Status202Accepted)
            : Results.BadRequest(new { error = outcome.Error });
    }

    private static async Task<IResult> RetryAsync(
        Guid jobId,
        Guid fullWorthSpaceId,
        CurrentUserContext currentUser,
        PurchaseAuthorizationStore authorization,
        ReceiptScanJobStore jobs,
        ReceiptScanQueueService queue,
        CancellationToken ct)
    {
        var userId = currentUser.RequireUserId();
        if (!await authorization.IsFullWorthSpaceMemberAsync(userId, fullWorthSpaceId, ct)) return Results.NotFound();
        if (await jobs.GetForUserAsync(userId, fullWorthSpaceId, jobId, ct) is null) return Results.NotFound();
        var outcome = await queue.RetryAsync(userId, fullWorthSpaceId, jobId, ct);
        return outcome.Success && outcome.Job is not null
            ? Results.Json(outcome.Job, statusCode: StatusCodes.Status202Accepted)
            : Results.BadRequest(new { error = outcome.Error });
    }

    private static async Task<string?> ValidatePhysicalFileLimitAsync(HttpRequest request, int existingPhysicalFiles, int maxFiles, CancellationToken ct)
    {
        if (!request.HasFormContentType) return null;
        var form = await request.ReadFormAsync(ct);
        var incoming = form.Files.GetFiles("receipt");
        var count = incoming.Count == 0 ? form.Files.Count : incoming.Count;
        return existingPhysicalFiles + count > maxFiles
            ? $"receipt scan may contain at most {maxFiles} uploaded files."
            : null;
    }
}
