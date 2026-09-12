using FullWorth.Web.Security.BackendContext;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests.Security.BackendContext;

/// <summary>
/// The backend HttpClient and the SSRF gate must agree on where the backend is.
///
/// <see cref="BackendContextOptions.BackendBaseAddress"/> is the single origin the internal key is ever
/// attached to — the outbound handler compares every request target against it. If the client is pointed
/// somewhere else, the key is simply never sent and every backend call fails as unauthenticated, which
/// reads like a broken login rather than a configuration mismatch.
///
/// Keeping them equal is easy to break by accident, because the two resolve at different MOMENTS. The
/// options load from DI at request time; the client's registration callback also runs late. A value read
/// once into a local during the top-level statements does not: a test host (and any host that adds
/// configuration after <c>Program</c> has run) supplies its settings in between, so the local holds the
/// appsettings value while the options hold the real one. That is exactly the bug this test caught.
/// </summary>
public sealed class BackendOriginAgreementTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public BackendOriginAgreementTests(FullWorthWebFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void The_backend_client_and_the_ssrf_gate_point_at_the_same_origin()
    {
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<BackendContextOptions>();
        var clients = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        using var backend = clients.CreateClient("backend");

        Assert.Equal(options.BackendBaseAddress, backend.BaseAddress);

        // And it is the configured one, not the compiled-in fallback: this factory configures its own
        // backend URL after Program's top-level statements, which is where the two used to diverge.
        Assert.Equal(
            new Uri(FullWorthWebFactory.BackendUrl.TrimEnd('/') + "/"),
            backend.BaseAddress);
    }

    /// <summary>
    /// The first-run bootstrap client carries the internal key too, and is gated on the same origin.
    /// </summary>
    [Fact]
    public void The_bootstrap_client_points_at_the_same_origin_as_well()
    {
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<BackendContextOptions>();
        var clients = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        using var bootstrap = clients.CreateClient(FullWorth.Web.Modules.Bootstrap.FirstRunBootstrapper.BackendClientName);

        Assert.Equal(options.BackendBaseAddress, bootstrap.BaseAddress);
    }
}
