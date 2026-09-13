using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Modules.BankConnections;

using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// The FinTS product id is set in the admin menu now, as one entry among the other installation
/// settings. What is left here is the fallback for installations that stored it before that existed.
///
/// These tests pin the two things that still have to be true: the value keeps being readable, and the
/// table cannot be written any more. The second one is the point — a second way to change how this
/// installation identifies itself to every bank is exactly the kind of quiet disagreement that makes
/// an admin form save happily and change nothing.
/// </summary>
public sealed class BankingInstanceSettingsTests
{
    [Fact]
    public async Task An_installation_that_has_never_been_configured_reports_an_empty_id()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<BankingInstanceSettingsStore>();

        var settings = await store.GetAsync(CancellationToken.None);

        // Empty, never null: a caller that forgets to check gets "not configured", not a crash.
        Assert.Equal(string.Empty, settings.FinTsProductId);
    }

    /// <summary>
    /// The banking service reads the fallback over the internal API when it opens a FinTS dialog and
    /// nothing is configured. An id stored by the old accounts-page form still arrives.
    /// </summary>
    [Fact]
    public async Task An_id_stored_before_the_admin_menu_existed_is_still_readable()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        await using (var seed = factory.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<FullWorthDbContext>();
            db.Add(new BankingInstanceSettings { FinTsProductId = "LEGACY-ID" });
            await db.SaveChangesAsync();
        }

        using var read = new HttpRequestMessage(HttpMethod.Get, "/internal/banking/settings");
        read.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        var response = await client.SendAsync(read);
        response.EnsureSuccessStatusCode();
        var settings = await response.Content.ReadFromJsonAsync<BankingInstanceSettingsDto>();

        Assert.Equal("LEGACY-ID", settings!.FinTsProductId);
    }

    /// <summary>
    /// The internal route is GET-only. 405 rather than 404: the route exists and deliberately refuses
    /// the verb, which is a clearer answer than pretending the endpoint is not there.
    /// </summary>
    [Fact]
    public async Task The_banking_service_cannot_write_it_back()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        using var write = new HttpRequestMessage(HttpMethod.Put, "/internal/banking/settings")
        {
            Content = JsonContent.Create(new BankingInstanceSettingsDto("FROM-BANKING"))
        };
        write.Headers.Add("X-FullWorth-Ingest-Key", BackendWebApplicationFactory.IngestKey);
        var refused = await client.SendAsync(write);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
    }

    /// <summary>
    /// The admin route the accounts page used is gone entirely. Asserted against the routing table and
    /// not through a request, because every /api route answers 401 before routing has a say — which
    /// would let a route that still exists pass for one that does not.
    ///
    /// It mattered beyond tidiness: that route authorised against the Intelligence admin grant, which is
    /// bootstrapped onto the oldest finance user and never follows a demotion in auth.
    /// </summary>
    [Fact]
    public void The_admin_route_on_the_accounts_page_no_longer_exists()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty);

        Assert.DoesNotContain(routes, route => route.Contains("instance-settings", StringComparison.OrdinalIgnoreCase));
    }
}
