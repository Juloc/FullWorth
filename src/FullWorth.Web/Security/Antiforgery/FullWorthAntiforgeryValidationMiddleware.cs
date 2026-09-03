using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FullWorth.Web.Security.Antiforgery;

public sealed class FullWorthAntiforgeryValidationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!RequiresValidation(context))
        {
            await next(context);
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new FullWorthAntiforgeryErrorResponse(FullWorthAntiforgeryDefaults.InvalidTokenMessage),
                context.RequestAborted);
            return;
        }

        await next(context);
    }

    internal static bool RequiresValidation(HttpContext context)
    {
        if (context.GetEndpoint() is null || !IsUnsafeMethod(context.Request.Method))
            return false;

        var path = context.Request.Path;
        return path.StartsWithSegments("/auth") || path.StartsWithSegments("/bff");
    }

    private static bool IsUnsafeMethod(string method) =>
        HttpMethods.IsPost(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) ||
        HttpMethods.IsDelete(method);
}

public static class FullWorthAntiforgeryApplicationBuilderExtensions
{
    public static IApplicationBuilder UseFullWorthAntiforgery(this IApplicationBuilder app) =>
        app.UseMiddleware<FullWorthAntiforgeryValidationMiddleware>();
}

public sealed record FullWorthAntiforgeryErrorResponse(string Error);
