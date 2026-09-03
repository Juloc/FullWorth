using System.Security.Claims;
using FullWorth.Web.Security.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Web.Tests.Security.Antiforgery;

internal sealed class AntiforgeryTestApplication : IAsyncDisposable
{
    private const string AuthenticationScheme = "FullWorthAntiforgeryTests";

    public const string AuthCookieName = "Finance.Test.Auth";
    public const string BackendSecret = "backend-secret-must-not-leak";
    public const string BankingSecret = "banking-secret-must-not-leak";

    private readonly WebApplication app;

    private AntiforgeryTestApplication(WebApplication app)
    {
        this.app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<AntiforgeryTestApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:BackendApiKey"] = BackendSecret,
            ["Services:BankingApiKey"] = BankingSecret
        });

        builder.Services.AddFullWorthAntiforgery(secureCookie: false);
        builder.Services.AddAuthentication(AuthenticationScheme)
            .AddCookie(AuthenticationScheme, options =>
            {
                options.Cookie.Name = AuthCookieName;
                options.Events = new CookieAuthenticationEvents
                {
                    OnRedirectToLogin = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    },
                    OnRedirectToAccessDenied = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }
                };
            });
        builder.Services.AddAuthorization();

        var app = builder.Build();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseFullWorthAntiforgery();

        app.MapFullWorthAntiforgeryTokenEndpoint();

        app.MapPost("/test/sign-in", async (HttpContext context) =>
        {
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
                AuthenticationScheme);
            await context.SignInAsync(AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.NoContent();
        }).AllowAnonymous();

        app.MapMethods(
            "/auth/probe",
            ["GET", "POST", "PUT", "PATCH", "DELETE"],
            () => Results.Ok(new { ok = true }))
            .AllowAnonymous();

        app.MapPost("/auth/login", () => Results.NoContent()).AllowAnonymous();
        app.MapPost("/auth/logout", () => Results.NoContent()).RequireAuthorization();
        app.MapPost("/auth/change-password", () => Results.NoContent()).RequireAuthorization();
        app.MapPost("/auth/password-reset/request", () => Results.NoContent()).AllowAnonymous();
        app.MapPost("/auth/password-reset/complete", () => Results.NoContent()).AllowAnonymous();
        app.MapPost("/auth/recovery-codes/regenerate", () => Results.NoContent()).RequireAuthorization();
        app.MapDelete("/auth/sessions/{sessionId:guid}", () => Results.NoContent()).RequireAuthorization();
        app.MapPost("/auth/sessions/revoke-others", () => Results.NoContent()).RequireAuthorization();

        app.MapPost("/auth/passkeys/register/begin", () => Results.NoContent()).RequireAuthorization();
        app.MapPost("/auth/passkeys/register/complete", () => Results.NoContent()).RequireAuthorization();
        app.MapDelete("/auth/passkeys/{credentialId}", () => Results.NoContent()).RequireAuthorization();
        app.MapPost("/auth/passkeys/login/begin", () => Results.NoContent()).AllowAnonymous();
        app.MapPost("/auth/passkeys/login/complete", () => Results.NoContent()).AllowAnonymous();

        app.MapMethods(
            "/bff/backend/probe",
            ["GET", "POST", "PUT", "PATCH", "DELETE"],
            () => Results.Ok(new { ok = true }))
            .RequireAuthorization();

        app.MapPost("/bff/banking/probe", () => Results.NoContent()).RequireAuthorization();

        app.MapPost("/bff/backend/json", (ProbePayload payload) => Results.Ok(payload))
            .RequireAuthorization();

        app.MapPost("/bff/backend/upload", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            return Results.Ok(new
            {
                fields = form.Count,
                files = form.Files.Count
            });
        }).RequireAuthorization();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapGet("/auth/app.js", () => Results.Text("console.log('auth');", "application/javascript"))
            .AllowAnonymous();

        await app.StartAsync();
        return new AntiforgeryTestApplication(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    internal sealed record ProbePayload(string Value);
}
