using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FullWorth.Backend.Validation;

/// <summary>
/// Preserves the backend's database-enforced purchase allocation conflict semantics when the backend
/// runs inside the unified Web host. Returns false for all other exceptions so the Web host's generic
/// non-leaking exception handler remains authoritative.
/// </summary>
public sealed class PurchasePaymentAllocationConflictExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<PurchasePaymentAllocationConflictExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!IsPurchasePaymentAllocationConflict(exception))
            return false;

        logger.LogInformation(
            "Purchase payment allocation conflict handling {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);
        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Purchase payment allocation conflict."
            }
        });
    }

    private static bool IsPurchasePaymentAllocationConflict(Exception exception)
    {
        if (exception is not DbUpdateException { InnerException: PostgresException postgres })
            return false;
        if (postgres.SqlState != PostgresErrorCodes.CheckViolation)
            return false;

        return postgres.MessageText.StartsWith("Purchase payment allocation", StringComparison.Ordinal)
            || postgres.MessageText.StartsWith("Purchase payment link must stay", StringComparison.Ordinal);
    }
}
