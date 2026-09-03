using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FullWorth.Web.Tests;

public class FullWorthWebFactory : WebApplicationFactory<Program>
{
    public const string BackendInternalKey = "web-test-backend-internal-key-7f496f1e2a4b46c8";
    public const string BackendSecret = BackendInternalKey;
    public const string BankingSecret = "web-test-banking-secret-a2c951";
    public const string BackendUrl = "http://internal-backend.test:8181";
    public const string BankingUrl = "http://internal-banking.test:8282";

    private readonly ConcurrentQueue<RecordedProxyRequest> recordedRequests = new();

    public IReadOnlyList<RecordedProxyRequest> BackendRequests => recordedRequests
        .Where(x => x.ClientName == "backend")
        .ToArray();

    public IReadOnlyList<RecordedProxyRequest> BankingRequests => recordedRequests
        .Where(x => x.ClientName == "banking")
        .ToArray();

    public void ClearProxyRequests()
    {
        while (recordedRequests.TryDequeue(out _)) { }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var authDatabase = Environment.GetEnvironmentVariable("FULLWORTH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(authDatabase))
            throw new InvalidOperationException("FULLWORTH_TEST_POSTGRES must point to the PostgreSQL 18 test server.");
        // Give every test host its own isolated auth database (the app migrates it on startup), so tests
        // never collide on shared unique keys such as passkey credential ids. Bound the pool so parallel
        // hosts stay under the CI max_connections ceiling.
        authDatabase = $"{authDatabase.TrimEnd(';')};Database=fullworth_web_{Guid.NewGuid():N};Maximum Pool Size=10;Minimum Pool Size=0;Connection Idle Lifetime=5;Connection Pruning Interval=2";

        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AuthDatabase"] = authDatabase,
                ["Services:BackendUrl"] = BackendUrl,
                ["Services:BackendInternalKey"] = BackendInternalKey,
                ["Services:BankingUrl"] = BankingUrl,
                ["Services:BankingApiKey"] = BankingSecret,
                ["Passkeys:RelyingPartyId"] = "localhost",
                ["Passkeys:RelyingPartyName"] = "FullWorth Test",
                ["Passkeys:Origins:0"] = "http://localhost",
                ["Passkeys:Origins:1"] = "https://localhost",
                ["Passkeys:ChallengeLifetime"] = "00:05:00"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("backend")
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler("backend", recordedRequests));
            services.AddHttpClient("banking")
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler("banking", recordedRequests));
        });
    }

    public new HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

    public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        var inner = base.CreateClient(options);
        var environment = Services.GetRequiredService<IWebHostEnvironment>();
        if (environment.IsProduction() && inner.BaseAddress?.Scheme != Uri.UriSchemeHttps)
            inner.BaseAddress = new Uri("https://localhost");

        return new HttpClient(new AntiforgeryHandler(inner, options.HandleCookies), disposeHandler: true)
        {
            BaseAddress = inner.BaseAddress,
            Timeout = inner.Timeout
        };
    }

    public HttpClient CreateRawClient(WebApplicationFactoryClientOptions? options = null)
    {
        var client = base.CreateClient(options ?? new WebApplicationFactoryClientOptions());
        var environment = Services.GetRequiredService<IWebHostEnvironment>();
        if (environment.IsProduction() && client.BaseAddress?.Scheme != Uri.UriSchemeHttps)
            client.BaseAddress = new Uri("https://localhost");
        return client;
    }

    public sealed record RecordedProxyRequest(
        string ClientName,
        string Method,
        Uri? Uri,
        IReadOnlyDictionary<string, string[]> Headers);

    private sealed class AntiforgeryHandler(HttpClient inner, bool cookiesHandledByInner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RequiresAntiforgery(request))
                await AddAntiforgeryAsync(request, cancellationToken);

            using var forwarded = await CloneRequestAsync(request, cancellationToken);
            return await inner.SendAsync(forwarded, cancellationToken);
        }

        private async Task AddAntiforgeryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authCookie = request.Headers.TryGetValues("Cookie", out var cookieValues)
                ? string.Join("; ", cookieValues)
                : null;

            using var tokenRequest = new HttpRequestMessage(HttpMethod.Get, "/auth/antiforgery");
            if (!string.IsNullOrWhiteSpace(authCookie))
                tokenRequest.Headers.TryAddWithoutValidation("Cookie", authCookie);

            using var tokenResponse = await inner.SendAsync(tokenRequest, cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            var token = payload.RootElement.GetProperty("token").GetString()
                ?? throw new InvalidOperationException("Antiforgery token missing in test response.");
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);

            if (!cookiesHandledByInner)
            {
                var antiforgeryCookie = tokenResponse.Headers.TryGetValues("Set-Cookie", out var setCookies)
                    ? setCookies.Select(x => x.Split(';', 2)[0]).FirstOrDefault(x => x.StartsWith("Finance.Antiforgery=", StringComparison.Ordinal))
                    : null;
                if (string.IsNullOrWhiteSpace(antiforgeryCookie))
                    throw new InvalidOperationException("Antiforgery cookie missing in test response.");

                request.Headers.Remove("Cookie");
                request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrWhiteSpace(authCookie)
                    ? antiforgeryCookie
                    : $"{authCookie}; {antiforgeryCookie}");
            }
        }

        private static bool RequiresAntiforgery(HttpRequestMessage request)
        {
            var method = request.Method.Method;
            if (method is not ("POST" or "PUT" or "PATCH" or "DELETE"))
                return false;
            var path = request.RequestUri?.IsAbsoluteUri == true ? request.RequestUri.AbsolutePath : request.RequestUri?.OriginalString ?? string.Empty;
            return path.StartsWith("/auth", StringComparison.Ordinal) || path.StartsWith("/bff", StringComparison.Ordinal);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri)
            {
                Version = source.Version,
                VersionPolicy = source.VersionPolicy
            };

            foreach (var header in source.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            if (source.Content is not null)
            {
                var bytes = await source.Content.ReadAsByteArrayAsync(cancellationToken);
                clone.Content = new ByteArrayContent(bytes);
                foreach (var header in source.Content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var option in source.Options)
                clone.Options.TryAdd(option.Key, option.Value);

            return clone;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class StubHandler(
        string clientName,
        ConcurrentQueue<RecordedProxyRequest> recordedRequests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
            recordedRequests.Enqueue(new RecordedProxyRequest(
                clientName,
                request.Method.Method,
                request.RequestUri,
                headers));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
