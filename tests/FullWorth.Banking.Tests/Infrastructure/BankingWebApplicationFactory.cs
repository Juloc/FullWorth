using FullWorth.Banking.Backend;
using FullWorth.Banking.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests.Infrastructure;

internal sealed class BankingWebApplicationFactory : WebApplicationFactory<BankSyncService>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Backend:IngestKey", "test-ingest-key");
        builder.ConfigureServices(services =>
        {
            var worker = services.FirstOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(BankSyncWorker));
            if (worker is not null) services.Remove(worker);

            // Status/profile routes now query the authoritative backend. Unit web tests use a backend
            // with no stored BYO profile; feature-specific tests can override this registration.
            services.RemoveAll<FullWorthBackendClient>();
            services.AddSingleton(sp =>
            {
                var handler = new NoProfileBackendHandler();
                var http = new HttpClient(handler) { BaseAddress = new Uri("https://backend.test/") };
                return new FullWorthBackendClient(
                    http,
                    Options.Create(new BackendOptions
                    {
                        BaseUrl = "https://backend.test",
                        IngestKey = "test-ingest-key"
                    }));
            });
        });
    }

    private sealed class NoProfileBackendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsolutePath.StartsWith("/internal/banking/profiles/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
