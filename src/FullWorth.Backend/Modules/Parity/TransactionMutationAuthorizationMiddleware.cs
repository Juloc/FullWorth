using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Modules.Transactions;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Parity;

/// <summary>
/// Capability gate for transaction and category-intelligence mutation routes. Account ownership remains
/// enforced by the stores/endpoints; this middleware adds the orthogonal FullWorth-Space capability layer
/// without weakening existing account-level checks.
/// </summary>
public sealed class TransactionMutationAuthorizationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUserContext currentUser, FullWorthDbContext db)
    {
        var transactionRoute = context.Request.Path.StartsWithSegments("/api/transactions");
        var intelligenceRoute = context.Request.Path.StartsWithSegments("/api/category-intelligence");
        if ((!transactionRoute && !intelligenceRoute) ||
            HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var fullWorthSpaceId))
        {
            // Let minimal-API parameter binding return the canonical 400 response.
            await next(context);
            return;
        }

        var userId = currentUser.RequireUserId();
        // Non-members must not learn that the FullWorth Space or its transactions exist: answer 404 exactly
        // like the resource endpoints, and reserve 403 for members who lack the required capability.
        if (!await ParitySql.IsMemberAsync(db, userId, fullWorthSpaceId, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var capability = transactionRoute
            ? await RequiredTransactionCapabilityAsync(context, db, fullWorthSpaceId)
            : await RequiredIntelligenceCapabilityAsync(context);
        if (!await PermissionsErgonomicsParityEndpoints.HasCapabilityAsync(
                db, userId, fullWorthSpaceId, capability, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // Future learning materializes a FullWorth-Space-global rule. A user who can write only some
        // accounts must never be able to create a rule that later categorizes hidden accounts.
        if (intelligenceRoute && IsLearnRoute(context) && await IsFutureLearnAsync(context) &&
            !await HasFullWritableAccountCoverageAsync(db, userId, fullWorthSpaceId, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static async Task<string> RequiredTransactionCapabilityAsync(HttpContext context, FullWorthDbContext db, Guid fullWorthSpaceId)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.EndsWith("/allocations", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPut(context.Request.Method))
            return "transactions.categorize";

        if (path.EndsWith("/classification", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPatch(context.Request.Method))
            return await ClassificationCapabilityAsync(context, db, fullWorthSpaceId);

        // Creating/deleting ledger rows, transfer pairing and refund linking modify transaction
        // semantics beyond categorization and therefore require the broader write capability.
        return "transactions.write";
    }

    private static async Task<string> RequiredIntelligenceCapabilityAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path.EndsWith("/bulk", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(context.Request.Method))
        {
            var request = await ReadJsonBodyAsync<BulkCategoryAction>(context);
            // Ignore/unignore changes analytics/ledger semantics. Category, review and tag changes are
            // categorization metadata and use the narrower capability.
            return request?.IsIgnored is not null ? "transactions.write" : "transactions.categorize";
        }

        return "transactions.categorize";
    }

    private static async Task<string> ClassificationCapabilityAsync(HttpContext context, FullWorthDbContext db, Guid fullWorthSpaceId)
    {
        var request = await ReadJsonBodyAsync<TransactionClassification>(context);
        if (request is null) return "transactions.write";
        var segments = context.Request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Length < 4 || !Guid.TryParse(segments[2], out var transactionId))
            return "transactions.write";

        var current = await db.Transactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId &&
                                  db.Accounts.Any(account => account.Id == transaction.AccountId && account.FullWorthSpaceId == fullWorthSpaceId))
            .Select(transaction => new
            {
                transaction.IsIgnored,
                transaction.IsTransfer,
                transaction.TransferPurpose,
                transaction.UserNote
            })
            .SingleOrDefaultAsync(context.RequestAborted);

        if (current is null) return "transactions.categorize"; // endpoint will return the non-leaking 404

        var changesGeneralState = request.IsIgnored != current.IsIgnored ||
                                  request.IsTransfer != current.IsTransfer ||
                                  !SameText(request.TransferPurpose, current.TransferPurpose) ||
                                  !SameText(request.UserNote, current.UserNote);
        return changesGeneralState ? "transactions.write" : "transactions.categorize";
    }

    private static bool IsLearnRoute(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method) &&
        (context.Request.Path.Value ?? string.Empty).EndsWith("/learn", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsFutureLearnAsync(HttpContext context)
    {
        var request = await ReadJsonBodyAsync<LearnCategoryWrite>(context);
        return string.Equals(request?.Scope?.Trim(), "future", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HasFullWritableAccountCoverageAsync(
        FullWorthDbContext db, Guid userId, Guid fullWorthSpaceId, CancellationToken ct)
    {
        var activeAccountIds = await db.Accounts.AsNoTracking()
            .Where(account => account.FullWorthSpaceId == fullWorthSpaceId && account.IsActive)
            .Select(account => account.Id)
            .ToListAsync(ct);
        if (activeAccountIds.Count == 0) return true;

        var writable = await db.AccountOwners.AsNoTracking()
            .Where(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner &&
                            activeAccountIds.Contains(owner.AccountId))
            .Select(owner => owner.AccountId)
            .Distinct()
            .ToListAsync(ct);
        return writable.Count == activeAccountIds.Count;
    }

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpContext context)
    {
        context.Request.EnableBuffering();
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                context.Request.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web),
                context.RequestAborted);
        }
        catch (JsonException)
        {
            // Preserve normal endpoint validation behavior for malformed bodies.
            return default;
        }
        finally
        {
            context.Request.Body.Position = 0;
        }
    }

    private static bool SameText(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
