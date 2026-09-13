using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Tests.BankConnections;

/// <summary>
/// The FinTS product id has exactly one source: <c>FinTs:ProductId</c> from the instance settings in
/// the admin menu.
///
/// It briefly had two — the admin setting and a read-only fallback to the old per-installation table —
/// and the #104 cutover removed the fallback, because a second way to answer the same question is how
/// an admin form comes to save happily and change nothing.
///
/// What is left to guard is the absence: no route may offer the old path again. The positive side,
/// that a configured id is what reaches the bank, is covered by FinTsProductIdTests in the banking
/// suite.
/// </summary>
public sealed class BankingInstanceSettingsTests
{
    /// <summary>
    /// Asserted against the routing table rather than through a request: every /api route answers 401
    /// before routing has a say, which would let a route that still exists pass for one that is gone.
    /// </summary>
    [Theory]
    [InlineData("internal/banking/settings")]
    [InlineData("banking/instance-settings")]
    public void The_old_product_id_routes_are_gone(string path)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateClient();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty);

        Assert.DoesNotContain(routes, route => route.Contains(path, StringComparison.OrdinalIgnoreCase));
    }
}
