using System.Net;
using System.Net.Http.Headers;
using FullWorth.Backend.Modules.Purchases.ReceiptImports;
using Microsoft.Extensions.Options;
using Xunit;

namespace FullWorth.Backend.Tests.Purchases;

/// <summary>
/// Der Paperless-Import darf die Instanz nicht umbringen (#127).
///
/// Beobachtet wurde ein Lauf mit 115 Dokumenten, nach dem Paperless praktisch nicht mehr ansprechbar
/// war. Die Ursache war nicht Parallelitaet - es gab keine - sondern dass jede Antwort ignoriert
/// wurde: auf ein "zu viel" (429) folgte sofort die naechste Anfrage.
///
/// Diese Tests halten das Gegenteil fest: die Antwort wird gelesen, gewartet wird so lange, wie der
/// Server sagt, und mehr als eine Handvoll Anfragen ist nie gleichzeitig unterwegs.
/// </summary>
public sealed class PaperlessRequestPolitenessTests
{
    [Fact]
    public async Task ATooManyRequestsAnswerIsWaitedOutAndThenRetried()
    {
        var handler = new CountingHandler(
            [
                Rejected(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(1)),
                Ok("{\"count\":0,\"results\":[]}")
            ]);
        var client = ClientFor(handler);

        var started = DateTimeOffset.UtcNow;
        var result = await client.TestAsync("https://paperless.local", "token", CancellationToken.None);
        var waited = DateTimeOffset.UtcNow - started;

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, handler.Requests);
        // Retry-After sagte eine Sekunde; weniger darf es nicht gewesen sein.
        Assert.True(waited >= TimeSpan.FromSeconds(0.9), $"gewartet wurde {waited}");
    }

    [Fact]
    public async Task AfterTheLastAttemptTheRejectionIsReportedInsteadOfHammeringOn()
    {
        // Immer 429. Es darf genau so oft versucht werden, wie eingestellt - nicht endlos.
        var handler = new CountingHandler([], _ => Rejected(HttpStatusCode.TooManyRequests, TimeSpan.Zero));
        var client = ClientFor(handler, retries: 3);

        var result = await client.TestAsync("https://paperless.local", "token", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task NeverMoreThanTheConfiguredNumberOfRequestsAreInFlightAtOnce()
    {
        var handler = new CountingHandler([], async _ =>
        {
            await Task.Delay(60);
            return Ok("{\"count\":0,\"results\":[]}");
        });
        var client = ClientFor(handler, concurrency: 2);

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.TestAsync("https://paperless.local", "token", CancellationToken.None)));

        Assert.Equal(8, handler.Requests);
        Assert.True(handler.MaxConcurrent <= 2, $"gleichzeitig waren {handler.MaxConcurrent}");
    }

    // ---- Helfer ----

    private static PaperlessReceiptClient ClientFor(HttpMessageHandler handler, int retries = 3, int concurrency = 2)
    {
        var options = Options.Create(new ReceiptImportOptions
        {
            PaperlessMaxRetries = retries,
            PaperlessMaxConcurrentRequests = concurrency,
            PaperlessTimeoutSeconds = 30
        });
        return new PaperlessReceiptClient(new SingleClientFactory(handler), options);
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Rejected(HttpStatusCode status, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        if (retryAfter > TimeSpan.Zero)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler(
        IReadOnlyList<HttpResponseMessage> scripted,
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? always = null) : HttpMessageHandler
    {
        private readonly Lock gate = new();
        private int index;
        private int inFlight;

        public int Requests { get; private set; }
        public int MaxConcurrent { get; private set; }

        public CountingHandler(IReadOnlyList<HttpResponseMessage> scripted, Func<HttpRequestMessage, HttpResponseMessage> always)
            : this(scripted, request => Task.FromResult(always(request))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (gate)
            {
                Requests++;
                inFlight++;
                if (inFlight > MaxConcurrent) MaxConcurrent = inFlight;
            }
            try
            {
                if (always is not null) return await always(request);
                var response = scripted[Math.Min(index, scripted.Count - 1)];
                index++;
                return response;
            }
            finally
            {
                lock (gate) inFlight--;
            }
        }
    }
}
