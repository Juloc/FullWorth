using FullWorth.Banking.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Banking.Tests.Infrastructure;

internal sealed class BankingWebApplicationFactory : WebApplicationFactory<BankSyncService>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            var worker = services.FirstOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(BankSyncWorker));
            if (worker is not null) services.Remove(worker);
        });
    }
}
