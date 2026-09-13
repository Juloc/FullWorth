using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Tests.Infrastructure;
using FullWorth.Shared;

namespace FullWorth.Backend.Tests.Admin;

/// <summary>
/// The proof the BFF sends along when it has already done the step-up, and the gates behind it.
///
/// Everything here is about one question: can somebody who holds the ingest key — which several
/// legitimate internal callers do — read a person's bank PIN? Each test is one way the answer could
/// silently become yes.
/// </summary>
public sealed class AdminVaultTicketTests
{
    private const string InternalKey = "internal-key-that-is-long-enough-for-the-check";
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_ticket_names_the_user_it_was_issued_for()
    {
        var user = Guid.NewGuid();
        var ticket = AdminVaultTicket.Issue(InternalKey, user, Now);

        Assert.True(AdminVaultTicket.TryValidate(InternalKey, ticket, Now, out var validated));
        Assert.Equal(user, validated);
    }

    /// <summary>
    /// Changing the user in the ticket is the attack this signature exists for: one legitimate ticket,
    /// one edit, and it names somebody else's rows.
    /// </summary>
    [Fact]
    public void Editing_the_user_invalidates_it()
    {
        var ticket = AdminVaultTicket.Issue(InternalKey, Guid.NewGuid(), Now);
        var parts = ticket.Split('.');
        var forged = $"{parts[0]}.{Guid.NewGuid():N}.{parts[2]}.{parts[3]}";

        Assert.False(AdminVaultTicket.TryValidate(InternalKey, forged, Now, out _));
    }

    [Fact]
    public void It_expires()
    {
        var ticket = AdminVaultTicket.Issue(InternalKey, Guid.NewGuid(), Now);

        Assert.True(AdminVaultTicket.TryValidate(InternalKey, ticket, Now, out _));
        Assert.False(AdminVaultTicket.TryValidate(
            InternalKey, ticket, Now.Add(AdminVaultTicket.Lifetime).AddSeconds(1), out _));
    }

    /// <summary>
    /// The derivation, asserted rather than described. Signing with the internal key itself would mean
    /// that anyone who learned it for the internal-context path could also mint one of these — the key
    /// would become a read-anything key without anybody deciding that.
    /// </summary>
    [Fact]
    public void The_signature_is_not_the_internal_key_itself()
    {
        var ticket = AdminVaultTicket.Issue(InternalKey, Guid.NewGuid(), Now);

        Assert.DoesNotContain(InternalKey, ticket, StringComparison.Ordinal);
        Assert.False(AdminVaultTicket.TryValidate("a-different-key-of-a-similar-length", ticket, Now, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    [InlineData("v1.notaguid.99999999999.signature")]
    [InlineData("v2.00000000000000000000000000000000.99999999999.signature")]
    public void Nonsense_is_refused(string ticket) =>
        Assert.False(AdminVaultTicket.TryValidate(InternalKey, ticket, Now, out _));

    /// <summary>
    /// The ingest key alone reaches the route — the <c>/internal</c> middleware lets it through — and
    /// then gets nothing. That is the whole point of the second gate: several internal callers hold the
    /// ingest key legitimately, and none of them is entitled to a person's stored credentials.
    /// </summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("reveal")]
    public async Task The_ingest_key_alone_opens_nothing(string path)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/admin/secrets/{path}")
        {
            Content = JsonContent.Create(new { reference = "ai.credential.00000000000000000000000000000000" })
        };
        request.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Neither does no key at all — the ordinary /internal gate, asserted once here too.</summary>
    [Fact]
    public async Task Without_the_ingest_key_the_route_is_not_even_reachable()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/internal/admin/secrets/inventory", new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// And it is not reachable through the BFF's pass-through either. <c>/bff/backend/**</c> forwards
    /// what a browser asks for; a route that decrypts credentials must never be one of them.
    /// </summary>
    [Fact]
    public async Task The_route_is_not_under_the_path_the_browser_can_reach()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/admin/secrets/inventory", new { });

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
