using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Web.Data;
using FullWorth.Web.Modules.Admin;
using FullWorth.Web.Modules.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Admin;

/// <summary>
/// The vault, and the price of having one.
///
/// It turns "an admin session was taken over" into "the whole installation is gone, including every
/// other user's encrypted data". That follows from the decision to show these values at all and
/// cannot be designed away. What can be done is make the session cookie not enough on its own, keep
/// the window small, and make sure every reveal leaves a mark somewhere the thief cannot reach.
///
/// These tests pin the parts that make the difference. Every one of them corresponds to a way the
/// whole thing would silently become decoration.
/// </summary>
public sealed class AdminVaultTests
{
    private const string TestPassword = "correct horse battery staple";

    [Fact]
    public async Task A_normal_user_cannot_see_the_inventory_or_reveal_anything()
    {
        await using var factory = new FullWorthWebFactory();
        var user = await CreateUserAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, user.Email);

        using var inventory = await SendAsync(client, HttpMethod.Get, "/auth/admin/vault", cookie);
        using var reveal = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "infra.internal-key", secret = TestPassword });

        Assert.Equal(HttpStatusCode.Forbidden, inventory.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reveal.StatusCode);
    }

    /// <summary>
    /// Looking at the list is not looking at a secret. It says what exists and whether it is set,
    /// which an administrator needs in order to know what to ask for — and nothing else. No value, no
    /// prefix, no fingerprint: four characters of a key are four characters nobody has to guess.
    /// </summary>
    [Fact]
    public async Task The_inventory_carries_no_value_and_no_prefix_of_one()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        // The BFF's name for the same file the backend calls Security:InternalKey.
        var internalKey = factory.Services.GetRequiredService<IConfiguration>()["Services:BackendInternalKey"];
        Assert.False(string.IsNullOrWhiteSpace(internalKey));

        using var response = await SendAsync(client, HttpMethod.Get, "/auth/admin/vault", cookie);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(internalKey!, json, StringComparison.Ordinal);
        Assert.DoesNotContain(internalKey![..8], json, StringComparison.Ordinal);
        Assert.DoesNotContain(internalKey[^4..], json, StringComparison.Ordinal);
        Assert.Contains("\"stored\":true", json, StringComparison.Ordinal);

        // And no caching anywhere on the way, ever.
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task The_session_cookie_alone_reveals_nothing()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using var response = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "signin.google-client-secret", secret = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // elevation_required, not wrong_factor: no window was ever opened, so there was nothing to
        // check a factor against. The distinction is what lets the browser ask for the code instead of
        // telling somebody their password was wrong when they never typed one.
        Assert.Contains("elevation_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The four that own the installation need the factor for THIS reveal. An open window is not
    /// enough for them — proving it once and then reading all four is exactly the export this design
    /// is meant to prevent.
    /// </summary>
    [Fact]
    public async Task An_open_window_is_not_enough_for_the_four_that_own_the_installation()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using var elevate = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/elevate", cookie, new { secret = TestPassword });
        Assert.Equal(HttpStatusCode.OK, elevate.StatusCode);

        // Ordinary entry: the open window carries it.
        using var ordinary = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "infra.banking-key", secret = (string?)null });
        Assert.Equal(HttpStatusCode.OK, ordinary.StatusCode);

        // Crown jewel with the same open window and no factor: refused.
        using var refused = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "infra.internal-key", secret = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // With the factor: allowed, and it really is the value.
        using var allowed = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "infra.internal-key", secret = TestPassword });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var body = JsonDocument.Parse(await allowed.Content.ReadAsStringAsync());
        Assert.Equal(
            factory.Services.GetRequiredService<IConfiguration>()["Services:BackendInternalKey"],
            body.RootElement.GetProperty("value").GetString());
    }

    /// <summary>
    /// The regression guard that matters most. Verifying the password through SignInManager with
    /// lockoutOnFailure would hand somebody who already holds a session a way to lock the real
    /// administrator out of SIGNING IN, while their own stolen session keeps working. So the vault
    /// counts its own failures, and the account stays able to sign in.
    /// </summary>
    [Fact]
    public async Task Failing_the_step_up_never_locks_the_account_out_of_signing_in()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        for (var attempt = 0; attempt < AdminElevationLockout.MaxFailures + 2; attempt++)
        {
            using var wrong = await SendAsync(
                client, HttpMethod.Post, "/auth/admin/vault/elevate", cookie, new { secret = "not-it" });
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }

        // The vault is shut...
        using var locked = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/elevate", cookie, new { secret = TestPassword });
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        Assert.Contains("vault_locked", await locked.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ...and signing in is untouched.
        using var signIn = await client.PostAsJsonAsync(
            "/auth/login", new { email = admin.Email, password = TestPassword });
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var reloaded = await users.FindByIdAsync(admin.Id.ToString());
        Assert.False(await users.IsLockedOutAsync(reloaded!));
    }

    /// <summary>
    /// The elevation dies with the session, and it does so through a join rather than through code
    /// that has to remember. That is why signing out, a password change, a security-stamp bump and
    /// "end sessions" in the admin menu all work without anything being written for them.
    /// </summary>
    [Fact]
    public async Task Ending_the_session_ends_the_elevation()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using (var elevate = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/elevate", cookie, new { secret = TestPassword }))
            Assert.Equal(HttpStatusCode.OK, elevate.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.UserSessions
                .Where(x => x.AuthUserId == admin.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow));
        }

        using var afterwards = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "infra.banking-key", secret = (string?)null });

        Assert.NotEqual(HttpStatusCode.OK, afterwards.StatusCode);
    }

    /// <summary>
    /// A demotion takes the vault with it, immediately — not at the next sign-in. Checked against
    /// <c>AuthUser.IsAdmin</c>, which is the only notion of administrator this code may use: the
    /// backend's Intelligence grant is bootstrapped onto the oldest finance user and never follows a
    /// demotion here.
    /// </summary>
    [Fact]
    public async Task Losing_the_admin_role_closes_an_open_window()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        // A second admin, so demoting the first is not refused as "last admin".
        await CreateAdminAsync(factory);

        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using (var elevate = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/elevate", cookie, new { secret = TestPassword }))
            Assert.Equal(HttpStatusCode.OK, elevate.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
            var user = await users.FindByIdAsync(admin.Id.ToString());
            user!.IsAdmin = false;
            Assert.True((await users.UpdateAsync(user)).Succeeded);
        }

        using var afterwards = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "infra.banking-key", secret = (string?)null });

        Assert.NotEqual(HttpStatusCode.OK, afterwards.StatusCode);
    }

    /// <summary>
    /// The budget exists so that one window is not a script. Ten reveals, then the factor again.
    /// </summary>
    [Fact]
    public async Task One_window_is_worth_ten_reveals_and_not_more()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using (var elevate = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/elevate", cookie, new { secret = TestPassword }))
            Assert.Equal(HttpStatusCode.OK, elevate.StatusCode);

        for (var reveal = 0; reveal < AdminElevation.RevealBudget; reveal++)
        {
            using var response = await SendAsync(
                client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
                new { reference = "infra.banking-key", secret = (string?)null });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var exhausted = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "infra.banking-key", secret = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, exhausted.StatusCode);
        Assert.Contains(
            "elevation_required", await exhausted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Audited, and never with the value. The audit row is the only lasting record of who looked at
    /// what, so a value in it would turn the audit table into a second copy of the vault.
    /// </summary>
    [Fact]
    public async Task Every_reveal_leaves_a_row_that_names_the_reference_and_never_the_value()
    {
        await using var factory = new FullWorthWebFactory();
        var admin = await CreateAdminAsync(factory);
        using var client = CreateClient(factory);
        var cookie = await LoginAsync(client, admin.Email);

        using (var elevate = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/elevate", cookie, new { secret = TestPassword }))
            Assert.Equal(HttpStatusCode.OK, elevate.StatusCode);

        using (var reveal = await SendAsync(
            client, HttpMethod.Post, "/auth/admin/vault/reveal", cookie,
            new { reference = "infra.banking-key", secret = (string?)null }))
            Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var bankingKey = scope.ServiceProvider.GetRequiredService<IConfiguration>()["Services:BankingApiKey"];

        var events = await db.AdminAuditEvents.AsNoTracking()
            .Where(x => x.ActorAuthUserId == admin.Id)
            .ToListAsync();

        var row = Assert.Single(events, x => x.Action.StartsWith("vault.reveal", StringComparison.Ordinal));
        Assert.Contains("infra.banking-key", row.Action, StringComparison.Ordinal);
        Assert.DoesNotContain(bankingKey!, row.Action, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two classes that are deliberately not in the catalogue, asserted rather than described.
    /// Account passwords and PINs are PBKDF2 — nobody can show those and no setting changes it. The
    /// pension policy number is not a credential at all; it is a financial fact, and the admin
    /// surfaces do not show financial data.
    /// </summary>
    [Theory]
    [InlineData("auth.password")]
    [InlineData("auth.pin")]
    [InlineData("auth.recovery-codes")]
    [InlineData("auth.totp-key")]
    [InlineData("pension.policy-number")]
    public void The_things_that_must_never_be_shown_are_not_in_the_catalogue(string reference) =>
        Assert.Null(AdminVaultCatalogue.Find(reference));

    private static HttpClient CreateClient(FullWorthWebFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static async Task<(Guid Id, string Email)> CreateUserAsync(FullWorthWebFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var email = $"vault-test-{Guid.NewGuid():N}@example.com";
        var created = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, TestPassword));
        Assert.True(created.Succeeded);
        return (created.User!.Id, email);
    }

    private static async Task<(Guid Id, string Email)> CreateAdminAsync(FullWorthWebFactory factory)
    {
        var user = await CreateUserAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AuthUser>>();
        var found = await users.FindByIdAsync(user.Id.ToString());
        found!.IsAdmin = true;
        Assert.True((await users.UpdateAsync(found)).Succeeded);
        return user;
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/auth/login", new { email, password = TestPassword });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie")
            .Single(x => x.Contains("Finance.Auth=", StringComparison.Ordinal))
            .Split(';', 2)[0];
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string cookie, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}
