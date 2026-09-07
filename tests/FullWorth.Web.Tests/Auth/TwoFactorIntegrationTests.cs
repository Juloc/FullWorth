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
            var sharedKey = await users.GetAuthenticatorKeyAsync(user!);
            Assert.False(string.IsNullOrWhiteSpace(sharedKey));
            currentCode = ComputeAuthenticatorCode(sharedKey!);
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

    // The authenticator token provider intentionally does not emit codes server-side:
    // GenerateTwoFactorTokenAsync(..., DefaultAuthenticatorProvider) returns an empty string, so the
    // test must derive the current 6-digit TOTP from the shared key exactly the way the provider
    // validates it (RFC 6238: HMAC-SHA1 over a 30-second time step, dynamic truncation, no modifier).
    private static string ComputeAuthenticatorCode(string base32Key)
    {
        var key = Base32Decode(base32Key);
        var timestep = (long)(DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds / 30;
        var counter = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(timestep));
        var hash = System.Security.Cryptography.HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        return (binary % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(value.Length * 5 / 8);
        var bits = 0;
        var accumulator = 0;
        foreach (var c in value.TrimEnd('=').ToUpperInvariant())
        {
            var index = alphabet.IndexOf(c);
            if (index < 0)
                continue;
            accumulator = (accumulator << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((accumulator >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }

        return bytes.ToArray();
    }
}
