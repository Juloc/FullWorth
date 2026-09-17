using System.Net;
using System.Text;
using FullWorth.Backend.Modules.Portfolio;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Backend.Tests.Portfolio;

public sealed class ConfigurableMarketDataProviderTests
{
    [Fact]
    public void Unconfigured_provider_reports_none_and_cannot_handle_anything()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("no network call expected"));
        var provider = CreateProvider(new MarketDataOptions { Provider = MarketDataPresets.None }, handler);

        Assert.Equal(MarketDataPresets.None, provider.ProviderKey);
        Assert.False(provider.CanHandle(TickerSecurity()));
    }

    [Fact]
    public void Configured_provider_can_handle_a_security_with_an_isin_or_a_ticker_but_not_neither()
    {
        var provider = CreateProvider(YahooOptions(), new StubHandler(_ => throw new InvalidOperationException("no network call expected")));

        Assert.True(provider.CanHandle(TickerSecurity(isin: null)));
        Assert.True(provider.CanHandle(TickerSecurity(ticker: "")));
        Assert.False(provider.CanHandle(TickerSecurity(ticker: "", isin: null)));
    }

    [Fact]
    public async Task Yahoo_preset_parses_the_canned_chart_body_into_the_expected_candidates()
    {
        const string chartBody = """
{"chart":{"result":[{"meta":{"currency":"EUR"},"timestamp":[1756969200,1757055600],"indicators":{"quote":[{"close":[105.27,104.53]}],"adjclose":[{"adjclose":[105.27,104.53]}]}}]}}
""";
        var handler = new StubHandler(_ => Ok(chartBody));
        var provider = CreateProvider(YahooOptions(), handler);

        var result = await provider.GetPricesAsync(
            TickerSecurity(ticker: "IWDA.AS"), new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 10), CancellationToken.None);

        var expectedDate1 = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(1756969200).UtcDateTime);
        var expectedDate2 = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(1757055600).UtcDateTime);
        Assert.Equal(
            [new SecurityPriceCandidate(expectedDate1, 105.27m, "EUR"), new SecurityPriceCandidate(expectedDate2, 104.53m, "EUR")],
            result);
        Assert.Contains("IWDA.AS", Assert.Single(handler.RequestedUrls));
    }

    [Fact]
    public async Task Missing_ticker_resolves_a_symbol_through_the_search_url_first()
    {
        const string searchBody = """{"quotes":[{"symbol":"IWDA.L"}]}""";
        const string chartBody = """
{"chart":{"result":[{"meta":{"currency":"EUR"},"timestamp":[1756969200],"indicators":{"quote":[{"close":[105.27]}],"adjclose":[{"adjclose":[105.27]}]}}]}}
""";
        var handler = new StubHandler(request => request.RequestUri!.ToString().Contains("finance/search")
            ? Ok(searchBody)
            : Ok(chartBody));
        var provider = CreateProvider(YahooOptions(), handler);

        var result = await provider.GetPricesAsync(
            TickerSecurity(ticker: null, isin: "IE00B4L5Y983"), new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 10), CancellationToken.None);

        Assert.Single(result);
        var priceRequest = Assert.Single(handler.RequestedUrls, url => url.Contains("finance/chart"));
        Assert.Contains("IWDA.L", priceRequest);
    }

    [Fact]
    public async Task No_resolvable_symbol_returns_an_empty_list_without_calling_the_price_url()
    {
        const string searchBody = """{"quotes":[]}""";
        var handler = new StubHandler(_ => Ok(searchBody));
        var provider = CreateProvider(YahooOptions(), handler);

        var result = await provider.GetPricesAsync(
            TickerSecurity(ticker: null, isin: "IE00B4L5Y983"), new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 10), CancellationToken.None);

        Assert.Empty(result);
        Assert.DoesNotContain(handler.RequestedUrls, url => url.Contains("finance/chart"));
    }

    [Fact]
    public async Task Http_failure_returns_an_empty_list_instead_of_throwing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = CreateProvider(YahooOptions(), handler);

        var result = await provider.GetPricesAsync(
            TickerSecurity(), new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 10), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task A_body_that_does_not_match_the_configured_paths_returns_an_empty_list_instead_of_throwing()
    {
        var handler = new StubHandler(_ => Ok("""{"unexpected":"shape"}"""));
        var provider = CreateProvider(YahooOptions(), handler);

        var result = await provider.GetPricesAsync(
            TickerSecurity(), new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 10), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Malformed_json_returns_an_empty_list_instead_of_throwing()
    {
        var handler = new StubHandler(_ => Ok("not json at all"));
        var provider = CreateProvider(YahooOptions(), handler);

        var result = await provider.GetPricesAsync(
            TickerSecurity(), new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 10), CancellationToken.None);

        Assert.Empty(result);
    }

    /// <summary>
    /// The URL template can carry the operator's API key as a query parameter. Neither a failed HTTP
    /// call nor a thrown exception may put that key into a log line - a warning names only the status
    /// code and the provider key, and the exception itself is never logged for exactly this reason.
    /// </summary>
    [Fact]
    public async Task Api_key_never_appears_in_the_logged_output()
    {
        const string apiKey = "super-secret-key-should-never-be-logged";
        var options = new MarketDataOptions
        {
            Provider = MarketDataPresets.Custom,
            PriceUrl = "https://example.test/price?symbol={symbol}&apiKey={apiKey}",
            DatePath = "dates",
            ValuePath = "values",
            ApiKey = apiKey,
        };
        var logger = new CapturingLogger<ConfigurableSecurityMarketDataProvider>();

        var failing = new ConfigurableSecurityMarketDataProvider(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))),
            new StaticOptionsMonitor<MarketDataOptions>(options), logger);
        await failing.GetPricesAsync(TickerSecurity(), new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 10), CancellationToken.None);

        var throwing = new ConfigurableSecurityMarketDataProvider(
            new HttpClient(new StubHandler(_ => throw new HttpRequestException($"connect failed for key {apiKey}"))),
            new StaticOptionsMonitor<MarketDataOptions>(options), logger);
        await throwing.GetPricesAsync(TickerSecurity(), new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 10), CancellationToken.None);

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(apiKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves the exact DI composition from BackendApplication.cs resolves at runtime: the typed HTTP
    /// client that AddHttpClient&lt;T&gt; registers is transient, so both interfaces must be resolved
    /// via AddTransient (never AddSingleton capturing a transient) for the scoped SecurityMarketDataService
    /// to construct successfully alongside the still-registered Null provider.
    /// </summary>
    [Fact]
    public async Task Backend_wiring_lets_SecurityMarketDataService_resolve_with_both_providers_present()
    {
        await using var sqlite = await SqliteFullWorthDatabase.CreateAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => sqlite.CreateContext());
        services.Configure<MarketDataOptions>(o => o.Provider = MarketDataPresets.None);

        services.AddSingleton<NullSecurityMarketDataProvider>();
        services.AddSingleton<ISecurityMetadataProvider>(sp => sp.GetRequiredService<NullSecurityMarketDataProvider>());
        services.AddSingleton<ISecurityPriceProvider>(sp => sp.GetRequiredService<NullSecurityMarketDataProvider>());

        services.AddHttpClient<ConfigurableSecurityMarketDataProvider>();
        services.AddTransient<ISecurityMetadataProvider>(sp => sp.GetRequiredService<ConfigurableSecurityMarketDataProvider>());
        services.AddTransient<ISecurityPriceProvider>(sp => sp.GetRequiredService<ConfigurableSecurityMarketDataProvider>());

        services.AddScoped<SecurityMarketDataService>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<SecurityMarketDataService>();
        Assert.NotNull(service);

        var priceProviders = scope.ServiceProvider.GetRequiredService<IEnumerable<ISecurityPriceProvider>>().ToList();
        var metadataProviders = scope.ServiceProvider.GetRequiredService<IEnumerable<ISecurityMetadataProvider>>().ToList();
        Assert.Equal(2, priceProviders.Count);
        Assert.Equal(2, metadataProviders.Count);
        Assert.Contains(priceProviders, p => p.ProviderKey == MarketDataPresets.None);
    }

    private static MarketDataOptions YahooOptions() => new() { Provider = MarketDataPresets.Yahoo.Key };

    private static SecurityMarketDescriptor TickerSecurity(string? ticker = "IWDA.AS", string? isin = "IE00B4L5Y983") =>
        new(Guid.NewGuid(), "Test ETF", isin, null, ticker, "etf", "USD", null, null);

    private static ConfigurableSecurityMarketDataProvider CreateProvider(MarketDataOptions options, HttpMessageHandler handler) =>
        new(new HttpClient(handler), new StaticOptionsMonitor<MarketDataOptions>(options), NullLogger<ConfigurableSecurityMarketDataProvider>.Instance);

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>Captures every formatted log line (and exception text, if any) so a test can assert a secret never appears in it.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null) Messages.Add(exception.ToString());
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
