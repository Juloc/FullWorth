using System.Linq;
using FullWorth.Web.Security.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Security.RateLimiting;

/// <summary>
/// The middleware tests prove the Registration policy behaves correctly on a route that declares it.
/// This proves the real endpoint declares it - read off the booted host's endpoint metadata rather
/// than off the source text, so renaming the constant cannot make the assertion pass by accident.
/// </summary>
public sealed class RegistrationRateLimitWiringTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public RegistrationRateLimitWiringTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void RegisterEndpointIsLimitedByTheRegistrationPolicyNotTheLoginBudget()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var register = endpoints.OfType<RouteEndpoint>().Single(endpoint =>
            endpoint.RoutePattern.RawText == "/auth/register"
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);

        var limiting = register.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        Assert.NotNull(limiting);
        Assert.Equal(RateLimitPolicies.Registration, limiting!.PolicyName);
    }
}
