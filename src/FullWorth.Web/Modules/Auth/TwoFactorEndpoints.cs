using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FullWorth.Web.Modules.Auth;

public static class TwoFactorEndpoints
{
    public static IEndpointRouteBuilder MapTwoFactorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth/two-factor").RequireAuthorization();

        group.MapGet("/status", async (
            HttpContext context,
            TwoFactorService twoFactor) =>
        {
            var status = await twoFactor.GetStatusAsync(context.User);
            return status is null ? Results.Unauthorized() : Results.Ok(status);
        });

        group.MapPost("/setup", async (
            HttpContext context,
            TwoFactorService twoFactor) =>
        {
            try
            {
                var setup = await twoFactor.BeginSetupAsync(context.User);
                return setup is null ? Results.Unauthorized() : Results.Ok(setup);
            }
            catch (InvalidOperationException ex) when (ex.Message == "two_factor_enabled")
            {
                return Results.Conflict(new { error = "two_factor_enabled" });
            }
        });

        group.MapPost("/enable", async (
            HttpContext context,
            TwoFactorCodeRequest request,
            TwoFactorService twoFactor) =>
            await twoFactor.EnableAsync(context.User, request.Code)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "invalid_code" }));

        group.MapPost("/disable", async (
            HttpContext context,
            TwoFactorCodeRequest request,
            TwoFactorService twoFactor) =>
            await twoFactor.DisableAsync(context.User, request.Code)
                ? Results.NoContent()
                : Results.BadRequest(new { error = "invalid_code" }));

        return endpoints;
    }
}
