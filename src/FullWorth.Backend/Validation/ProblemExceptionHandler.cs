using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FullWorth.Backend.Validation;

/// <summary>
/// Turns unhandled exceptions into RFC 7807 responses without leaking exception details. A very small
/// set of database-enforced business conflicts is mapped explicitly; everything else stays a generic 500.
/// </summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problemDetails, ILogger<ProblemExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (IsPurchasePaymentAllocationConflict(exception))
        {
            // Service-level checks normally return 409 before SaveChanges. The database trigger is the
            // concurrency backstop: two simultaneous requests can race past those reads, so its check
            // violation is an expected conflict rather than an internal-server failure.
            logger.LogInformation("Purchase payment allocation conflict handling {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Purchase payment allocation conflict.",
                },
            });
        }

        logger.LogError(exception, "Unhandled exception handling {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
            },
        });
    }

    private static bool IsPurchasePaymentAllocationConflict(Exception exception)
    {
        if (exception is not DbUpdateException { InnerException: PostgresException postgres }) return false;
        if (postgres.SqlState != PostgresErrorCodes.CheckViolation) return false;
        return postgres.MessageText.StartsWith("Purchase payment allocation", StringComparison.Ordinal) ||
               postgres.MessageText.StartsWith("Purchase payment link must stay", StringComparison.Ordinal);
    }
}
