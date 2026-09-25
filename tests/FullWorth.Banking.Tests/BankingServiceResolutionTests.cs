using FullWorth.Banking.EnableBanking;
using FullWorth.Banking.Services;
using FullWorth.Banking.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Banking.Tests;

/// <summary>
/// Every other test builds the sync services by hand, so nothing proved the real container can. A
/// constructor dependency without a registration - the clock, for one - compiled, passed all of them,
/// and would only have failed in production when BankSyncWorker resolved its first sync.
/// </summary>
public sealed class BankingServiceResolutionTests
{
    [Fact]
    public void The_container_builds_the_sync_services_and_hands_them_the_system_clock()
    {
        using var factory = new BankingWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<BankSyncService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IngFinTsService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EnableBankingClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<EnableBankingClientResolver>());
        Assert.Same(TimeProvider.System, scope.ServiceProvider.GetRequiredService<TimeProvider>());
    }
}
