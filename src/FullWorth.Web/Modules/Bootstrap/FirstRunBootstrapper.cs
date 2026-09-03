using System.Net;
using System.Net.Http.Json;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Security.BackendContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Bootstrap;

/// <summary>
/// Creates the first admin login on startup when configured and the login store is empty.
/// Calls the backend's internal-key-guarded bootstrap endpoint (no user context) to create the
/// matching FullWorthUser + owner FullWorth Space, then creates the local Identity user carrying that
/// FinanceUserId. Never throws: a failed bootstrap logs and lets the app start.
/// </summary>
public static class FirstRunBootstrapper
{
    public const string BackendClientName = "backend-internal";

    public static async Task TryRunAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        try
        {
            var options = services.GetRequiredService<IConfiguration>()
                .GetSection(BootstrapOptions.SectionName).Get<BootstrapOptions>() ?? new BootstrapOptions();
            if (!options.IsConfigured)
                return;

            var userManager = services.GetRequiredService<UserManager<AuthUser>>();
            if (await userManager.Users.AnyAsync(ct))
                return;

            var backendOptions = services.GetRequiredService<BackendContextOptions>();
            var client = services.GetRequiredService<IHttpClientFactory>().CreateClient(BackendClientName);

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/bootstrap/first-admin")
            {
                Content = JsonContent.Create(new
                {
                    email = options.Email,
                    displayName = string.IsNullOrWhiteSpace(options.DisplayName) ? options.Email : options.DisplayName,
                    spaceName = options.SpaceName,
                    baseCurrency = options.BaseCurrency
                })
            };
            request.Headers.TryAddWithoutValidation(BackendContextHeaders.InternalKey, backendOptions.InternalKey);

            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                logger.LogWarning(
                    "First-run bootstrap: the backend already has a user but the login store is empty. " +
                    "Skipping; create the first login manually.");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "First-run bootstrap: backend responded {Status}; the first login was not created.",
                    (int)response.StatusCode);
                return;
            }

            var created = await response.Content.ReadFromJsonAsync<BootstrapAdminResponse>(ct);
            if (created is null || created.FinanceUserId == Guid.Empty)
            {
                logger.LogError("First-run bootstrap: the backend returned no finance user id.");
                return;
            }

            var auth = services.GetRequiredService<AuthService>();
            var result = await auth.CreateUserAsync(new CreateAuthUserRequest(created.FinanceUserId, options.Email!, options.Password!));
            if (result.Succeeded)
                logger.LogInformation("First-run bootstrap: created the initial admin login for {Email}.", options.Email);
            else
                logger.LogError("First-run bootstrap: could not create the login: {Errors}", string.Join("; ", result.Errors));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "First-run bootstrap failed; the application will start without an initial admin.");
        }
    }

    private sealed record BootstrapAdminResponse(Guid FinanceUserId, Guid FullWorthSpaceId);
}
