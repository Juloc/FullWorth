using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Security.Antiforgery;

public static class FullWorthAntiforgeryEndpointExtensions
{
    public static IEndpointRouteBuilder MapFullWorthAntiforgeryTokenEndpoint(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(FullWorthAntiforgeryDefaults.TokenEndpointPath, GetToken)
            .AllowAnonymous();

        return endpoints;
    }

    private static IResult GetToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        if (string.IsNullOrWhiteSpace(tokens.RequestToken))
            throw new InvalidOperationException("The antiforgery request token was not generated.");

        context.Response.Headers.CacheControl = "no-store, private";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";

        return Results.Ok(new FullWorthAntiforgeryTokenResponse(tokens.RequestToken));
    }
}

public sealed record FullWorthAntiforgeryTokenResponse(string Token);
