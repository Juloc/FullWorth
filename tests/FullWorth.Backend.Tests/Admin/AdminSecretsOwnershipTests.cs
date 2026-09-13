using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using FullWorth.Backend.Tests.Infrastructure;
using FullWorth.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.Admin;

/// <summary>
/// The line the vault must not cross: an administrator sees their own stored credentials, never
/// somebody else's.
///
/// The reference the browser sends names a row by id, which makes it a client-supplied string. If the
/// server trusted it, an administrator would only have to know another user's credential id to have
/// it decrypted for them — and ids travel, in logs, in exports, in support conversations. So every
/// query filters on the finance user the ticket names, and these tests are what keeps that true.
/// </summary>
public sealed class AdminSecretsOwnershipTests
{
    [Fact]
    public async Task An_administrator_sees_only_their_own_credentials()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        await CreateCredentialAsync(factory, mine, "My key");
        await CreateCredentialAsync(factory, theirs, "Their key");

        var inventory = await InventoryAsync(client, mine);

        Assert.Contains(inventory, entry => entry.GetProperty("label").GetString()!.StartsWith("My key"));
        Assert.DoesNotContain(inventory, entry => entry.GetProperty("label").GetString()!.StartsWith("Their key"));
    }

    [Fact]
    public async Task Naming_somebody_elses_row_returns_nothing()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var theirCredentialId = await CreateCredentialAsync(factory, theirs, "Their key");

        using var response = await SendAsync(
            client, "reveal", mine, new { reference = $"ai.credential.{theirCredentialId:N}" });

        // 404, not 403: whether that id exists is itself information this caller is not entitled to.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Naming_your_own_row_returns_the_value()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var mine = Guid.NewGuid();
        var credentialId = await CreateCredentialAsync(factory, mine, "My key", "sk-the-actual-secret-value");

        using var response = await SendAsync(
            client, "reveal", mine, new { reference = $"ai.credential.{credentialId:N}" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("sk-the-actual-secret-value", body.RootElement.GetProperty("value").GetString());
    }

    /// <summary>
    /// A reference that is not one of the shapes this module knows falls through to null rather than
    /// to a query. Nothing here interpolates the reference into SQL, and this keeps it that way.
    /// </summary>
    [Theory]
    [InlineData("ai.credential.not-a-guid")]
    [InlineData("ai.credential.' OR 1=1 --")]
    [InlineData("bank.fints")]
    [InlineData("something.else.entirely")]
    [InlineData("")]
    public async Task A_reference_that_is_not_a_known_shape_returns_nothing(string reference)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "reveal", Guid.NewGuid(), new { reference });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> CreateCredentialAsync(
        BackendWebApplicationFactory factory, Guid owner, string name, string secret = "sk-0123456789abcdef")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IntelligenceStore>();
        var created = await store.CreateCredentialAsync(owner, "openai", name, secret, CancellationToken.None);
        return created.Id;
    }

    private static async Task<List<JsonElement>> InventoryAsync(HttpClient client, Guid user)
    {
        using var response = await SendAsync(client, "inventory", user, new { });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
    }

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client, string path, Guid financeUserId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/admin/secrets/{path}")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        request.Headers.Add(
            AdminVaultTicket.HeaderName,
            AdminVaultTicket.Issue(
                BackendWebApplicationFactory.InternalKey, financeUserId, DateTimeOffset.UtcNow));
        return client.SendAsync(request);
    }
}
