using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Web.Data;
using FullWorth.Web.Modules.Auth;
using FullWorth.Web.Modules.Passkeys;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FullWorth.Web.Tests.Passkeys;

public sealed class PasskeyProgramIntegrationTests : IClassFixture<PasskeyProgramIntegrationTests.PasskeyFullWorthWebFactory>
{
    private const string Password = "correct horse battery staple";
    private readonly PasskeyFullWorthWebFactory factory;

    public PasskeyProgramIntegrationTests(PasskeyFullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task AnonymousRegistrationBegin_IsRejected()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.PostAsJsonAsync("/auth/passkeys/register/begin", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PasswordAuthenticatedRegistration_PersistsAndListsOwnPasskey()
    {
        var user = await CreateUserAsync();
        var credentialId = new byte[] { 12, 34, 56, 78 };
        factory.Fido2.RegistrationHandler = (args, _) => Task.FromResult(new RegisteredPublicKeyCredential
        {
            Id = credentialId,
            PublicKey = [1, 2, 3, 4],
            User = args.OriginalOptions.User,
            SignCount = 0,
            AaGuid = Guid.NewGuid(),
            IsBackupEligible = true,
            IsBackedUp = true
        });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await PasswordLoginAsync(client, user.Email);
        var begin = await BeginAsync(client, "/auth/passkeys/register/begin");

        using var complete = await client.PostAsJsonAsync("/auth/passkeys/register/complete", new
        {
            challengeId = begin.ChallengeId,
            displayName = "Laptop",
            credential = RegistrationCredential([9, 9, 9])
        });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        using var list = await client.GetAsync("/auth/passkeys");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var items = await list.Content.ReadFromJsonAsync<PasskeyCredentialDto[]>();
        var item = Assert.Single(items!);
        Assert.Equal("Laptop", item.DisplayName);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var stored = await db.PasskeyCredentials.AsNoTracking().SingleAsync(x => x.AuthUserId == user.Id && x.CredentialId == credentialId);
        Assert.Equal(user.Id.ToByteArray(), stored.UserHandle);
    }

    [Fact]
    public async Task PasskeyLogin_CreatesNormalSessionCookie_AndReplayFails()
    {
        var user = await CreateUserAsync();
        var credentialId = new byte[] { 91, 92, 93, 94 };
        await SeedCredentialAsync(user.Id, credentialId, "Phone");
        factory.Fido2.AssertionHandler = async (args, ct) =>
        {
            var owner = await args.IsUserHandleOwnerOfCredentialIdCallback(
                new IsUserHandleOwnerOfCredentialIdParams(credentialId, user.Id.ToByteArray()), ct);
            Assert.True(owner);
            return new VerifyAssertionResult { CredentialId = credentialId, SignCount = 0, IsBackedUp = true };
        };

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var begin = await BeginAsync(client, "/auth/passkeys/login/begin");
        var payload = new
        {
            challengeId = begin.ChallengeId,
            credential = AssertionCredential(credentialId, user.Id.ToByteArray())
        };

        using var complete = await client.PostAsJsonAsync("/auth/passkeys/login/complete", payload);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Contains(complete.Headers.GetValues("Set-Cookie"), x => x.StartsWith("Finance.Auth=", StringComparison.Ordinal));

        using var protectedPage = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, protectedPage.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.True(await db.UserSessions.AsNoTracking().AnyAsync(x => x.AuthUserId == user.Id && x.RevokedAt == null));
        }

        using var replay = await client.PostAsJsonAsync("/auth/passkeys/login/complete", payload);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task DisabledUser_CannotUseValidPasskey()
    {
        var user = await CreateUserAsync();
        var credentialId = new byte[] { 101, 102, 103 };
        await SeedCredentialAsync(user.Id, credentialId, "Disabled");
        factory.Fido2.AssertionHandler = (_, _) => Task.FromResult(new VerifyAssertionResult
        {
            CredentialId = credentialId,
            SignCount = 0,
            IsBackedUp = true
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var authUser = await db.Users.SingleAsync(x => x.Id == user.Id);
            authUser.IsDisabled = true;
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var begin = await BeginAsync(client, "/auth/passkeys/login/begin");
        using var response = await client.PostAsJsonAsync("/auth/passkeys/login/complete", new
        {
            challengeId = begin.ChallengeId,
            credential = AssertionCredential(credentialId, user.Id.ToByteArray())
        });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUser_CannotListOrDeleteForeignPasskey()
    {
        var first = await CreateUserAsync();
        var second = await CreateUserAsync();
        var ownId = await SeedCredentialAsync(first.Id, [111, 112], "Mine");
        var foreignId = await SeedCredentialAsync(second.Id, [121, 122], "Other");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await PasswordLoginAsync(client, first.Email);

        using var list = await client.GetAsync("/auth/passkeys");
        var items = await list.Content.ReadFromJsonAsync<PasskeyCredentialDto[]>();
        Assert.Single(items!);
        Assert.Equal(ownId, items![0].Id);

        using var delete = await client.DeleteAsync($"/auth/passkeys/{foreignId}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.True(await db.PasskeyCredentials.AsNoTracking().AnyAsync(x => x.Id == foreignId));
    }

    private async Task<(Guid Id, string Email)> CreateUserAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
        var email = $"passkey-program-{Guid.NewGuid():N}@example.com";
        var result = await auth.CreateUserAsync(new CreateAuthUserRequest(Guid.NewGuid(), email, Password));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return (result.User!.Id, email);
    }

    private async Task<Guid> SeedCredentialAsync(Guid authUserId, byte[] credentialId, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var item = new PasskeyCredential
        {
            Id = Guid.NewGuid(),
            AuthUserId = authUserId,
            CredentialId = credentialId,
            PublicKey = [4, 5, 6],
            UserHandle = authUserId.ToByteArray(),
            SignatureCounter = 0,
            DisplayName = name,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PasskeyCredentials.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private static async Task PasswordLoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(Guid ChallengeId, JsonElement PublicKey)> BeginAsync(HttpClient client, string path)
    {
        using var response = await client.PostAsJsonAsync(path, new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("challengeId").GetGuid(), json.RootElement.GetProperty("publicKey").Clone());
    }

    private static object RegistrationCredential(byte[] rawId) => new
    {
        id = Base64Url(rawId),
        rawId = Base64Url(rawId),
        type = "public-key",
        response = new
        {
            clientDataJSON = Base64Url([1]),
            attestationObject = Base64Url([2]),
            transports = Array.Empty<string>()
        },
        clientExtensionResults = new { }
    };

    private static object AssertionCredential(byte[] credentialId, byte[] userHandle) => new
    {
        id = Base64Url(credentialId),
        rawId = Base64Url(credentialId),
        type = "public-key",
        response = new
        {
            clientDataJSON = Base64Url([1]),
            authenticatorData = Base64Url([2]),
            signature = Base64Url([3]),
            userHandle = Base64Url(userHandle)
        },
        clientExtensionResults = new { }
    };

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public sealed class PasskeyFullWorthWebFactory : FullWorthWebFactory
    {
        internal StubFido2 Fido2 { get; } = new("localhost", "http://localhost");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFido2>();
                services.AddSingleton<IFido2>(Fido2);
            });
        }
    }
}
