using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.Accounts;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Observes the existing transaction classification endpoint without changing its source-of-truth
/// mutation logic. Only successful manual category changes produce a sanitized feedback event.
/// </summary>
public sealed class TransactionClassificationFeedbackMiddleware(RequestDelegate next, ILogger<TransactionClassificationFeedbackMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        FullWorthDbContext financeDb,
        CurrentUserContext currentUser,
        IntelligenceFeedbackRecorder feedback)
    {
        var ct = context.RequestAborted;
        if (!IsClassificationPatch(context.Request, out var transactionId) ||
            !currentUser.IsAuthenticated ||
            !Guid.TryParse(context.Request.Query["fullWorthSpaceId"], out var fullWorthSpaceId))
        {
            await next(context);
            return;
        }

        Guid? requestedCategoryId;
        try
        {
            context.Request.EnableBuffering();
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            context.Request.Body.Position = 0;
            if (!body.RootElement.TryGetProperty("categoryId", out var categoryElement))
            {
                await next(context);
                return;
            }
            requestedCategoryId = categoryElement.ValueKind == JsonValueKind.Null
                ? null
                : categoryElement.GetGuid();
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            if (context.Request.Body.CanSeek) context.Request.Body.Position = 0;
            logger.LogDebug(exception, "Could not inspect transaction classification body for intelligence feedback.");
            await next(context);
            return;
        }

        var userId = currentUser.RequireUserId();
        var before = await financeDb.Transactions.AsNoTracking()
            .Where(transaction => transaction.Id == transactionId)
            .Join(financeDb.Accounts.AsNoTracking(),
                transaction => transaction.AccountId,
                account => account.Id,
                (transaction, account) => new { transaction, account })
            .Where(x => x.account.FullWorthSpaceId == fullWorthSpaceId &&
                        x.account.Owners.Any(owner => owner.UserId == userId && owner.OwnershipType == AccountOwnershipTypes.Owner))
            .Select(x => new
            {
                x.transaction.CategoryId,
                x.transaction.NormalizedCounterparty,
                x.transaction.Amount
            })
            .SingleOrDefaultAsync(ct);

        await next(context);

        if (before is null || context.Response.StatusCode != StatusCodes.Status204NoContent || before.CategoryId == requestedCategoryId)
            return;

        await feedback.RecordCategoryDecisionAsync(
            fullWorthSpaceId,
            userId,
            transactionId,
            before.NormalizedCounterparty,
            before.Amount >= 0 ? "income" : "expense",
            before.CategoryId,
            requestedCategoryId,
            "category_changed",
            ct);
    }

    private static bool IsClassificationPatch(HttpRequest request, out Guid transactionId)
    {
        transactionId = Guid.Empty;
        if (!HttpMethods.IsPatch(request.Method)) return false;
        var segments = request.Path.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: 4 } &&
               string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[1], "transactions", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(segments[3], "classification", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(segments[2], out transactionId);
    }
}
