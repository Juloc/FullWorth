using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Auth;

public sealed class TwoFactorIntegrationTests
{
    private const string TestPassword = "correct horse battery staple";

    [Fact]
    public async Task PasswordLoginRequiresValidAuthenticatorCodeWhenTwoFactorIsEnabled()
    {
        await using var factory = new FullWorthWebFactory();
        var account = await CreateUserAsync(factory);

        string currentCode;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            var user = await users.FindByIdAsync(account.Id.ToString());
            Assert.NotNull(user);

            Assert.True((await users.ResetAuthenticatorKeyAsync(user!)).Succeeded);
            Assert.True((await users.SetTwoFactorEnabledAsync(user!, true)).Succeeded);
            currentCode = await users.GenerateTwoFactorTokenAsync(
                user!,
                TokenOptions.DefaultAuthenticatorProvider);
            Assert.False(string.IsNullOrWhiteSpace(currentCode));
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using (var passwordOnly = await client.PostAsJsonAsync("/auth/login", new
               {
                   email = account.Email,
                   password = TestPassword
               }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, passwordOnly.StatusCode);
            using var payload = JsonDocument.Parse(await passwordOnly.Content.ReadAsStringAsync());
            Assert.True(payload.RootElement.GetProperty("requiresTwoFactor").GetBoolean());
        }

        using (var wrong = await client.PostAsJsonAsync("/auth/login", new
               {
                   email = account.Email,
                   password = TestPassword,
                   code = "000000"
               }))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var valid = await client.PostAsJsonAsync("/auth/login", new
        {
            email = account.Email,
            password = TestPassword,
            code = currentCode
        });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Contains(
            valid.Headers.GetValues("Set-Cookie"),
            value => value.Contains("Finance.Auth=", StringComparison.Ordinal));
    }

    private static async Task<(Guid Id, string Email)> CreateUserAsync(FullWorthWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var email = $"two-factor-{Guid.NewGuid():N}@example.com";
        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, TestPassword));
        Assert.True(created.Succeeded);
        return (created.User!.Id, email);
    }
}
