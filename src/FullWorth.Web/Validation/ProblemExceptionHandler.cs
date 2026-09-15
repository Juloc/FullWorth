using FullWorth.Web.Modules.Passkeys;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FullWorth.Web.Validation;

/// <summary>
/// Turns any unhandled exception into an RFC 7807 <c>application/problem+json</c> 500 response.
/// The real exception is logged server-side; the client only ever sees a generic title, never an
/// exception message or stack trace (which the framework's developer exception page would leak).
/// </summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problemDetails, ILogger<ProblemExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Ein Zustand ist kein Absturz. Passkeys ohne bekannte oeffentliche Adresse sind noch nicht
        // verfuegbar - das gehoert als Auskunft heraus, mit der Meldung, und nicht als 500 mit einer
        // Stapelverfolgung im Protokoll.
        if (exception is PasskeysNotReadyException notReady)
        {
            logger.LogInformation(
                "Passkeys are not available yet on {Method} {Path}: the instance has not learned its public address.",
                httpContext.Request.Method, httpContext.Request.Path);

            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = notReady.Message,
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
}
