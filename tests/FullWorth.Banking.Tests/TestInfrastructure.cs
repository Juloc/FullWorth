using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullWorth.Banking.Backend;
using FullWorth.Banking.EnableBanking;
using FullWorth.Banking.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FullWorth.Banking.Tests;

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Body,
    IReadOnlyDictionary<string, string>? Headers = null);

internal sealed class RecordingHttpMessageHandler(
    Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    private int _requestNumber;
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var contentHeaders = request.Content is null
            ? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()
            : request.Content.Headers;
        var headers = request.Headers
            .Concat(contentHeaders)
            .ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
        Requests.Enqueue(new(request.Method, request.RequestUri!, body, headers));
        var requestNumber = Interlocked.Increment(ref _requestNumber);
        return await responder(request, requestNumber, cancellationToken);
    }
}

internal sealed class FakeBackendHandler : HttpMessageHandler
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public List<BankConnectionDto> Connections { get; } = [];
    public AccountSyncState? SyncState { get; set; }
    public List<BankConnectionWrite> Upserts { get; } = [];
    public List<FinanceIngestBatch> Ingests { get; } = [];
    public int ListConnectionCalls { get; private set; }
    public int AuthorizeCalls { get; private set; }
    public HttpStatusCode AuthorizeResponse { get; set; } = HttpStatusCode.NoContent;
    public List<string> ConsumedStates { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (request.Method == HttpMethod.Get && path == "/internal/banking/connections/")
        {
            ListConnectionCalls++;
            return Json(Connections);
        }

        if (request.Method == HttpMethod.Post && path == "/internal/banking/connections/consume-state")
        {
            var body = await request.Content!.ReadFromJsonAsync<ConsumeStateBody>(_json, cancellationToken)
                ?? throw new InvalidOperationException("Missing consume-state payload.");
            var index = Connections.FindIndex(x => x.AuthorizationState == body.State);
            if (index < 0) return new(HttpStatusCode.NotFound);
            // Mimic one-time consumption: clear the state (like the real backend) so a replay finds
            // nothing, and return the CLEARED connection so callers don't resurrect the state on upsert.
            var consumed = Connections[index] with { AuthorizationState = null };
            Connections[index] = consumed;
            ConsumedStates.Add(body.State);
            return Json(consumed);
        }

        if (request.Method == HttpMethod.Post && path == "/internal/banking/connections/authorize")
        {
            AuthorizeCalls++;
            return new(AuthorizeResponse);
        }

        if (request.Method == HttpMethod.Post && path == "/internal/banking/connections/")
        {
            var write = await request.Content!.ReadFromJsonAsync<BankConnectionWrite>(_json, cancellationToken)
                ?? throw new InvalidOperationException("Missing connection payload.");
            Upserts.Add(write);

            var id = write.Id ?? Guid.NewGuid();
            var dto = new BankConnectionDto(
                id,
                write.Provider,
                write.InstitutionName,
                write.Country,
                write.AuthorizationState,
                write.AuthorizationId,
                write.ProviderSessionId,
                write.Status,
                write.ValidUntil,
                write.LastAttemptAt,
                write.LastSyncedAt,
                write.NextSyncAllowedAt,
                write.ConsecutiveFailures,
                write.LastError,
                write.EnableBankingProfileId,
                write.PsuType,
                write.AuthMethod,
                write.RequiredPsuHeadersJson);

            var index = Connections.FindIndex(x => x.Id == id);
            if (index >= 0) Connections[index] = dto;
            else Connections.Add(dto);
            return Json(dto);
        }

        if (request.Method == HttpMethod.Get &&
            path.StartsWith("/internal/banking/connections/", StringComparison.Ordinal) &&
            path.Contains("/accounts/", StringComparison.Ordinal) &&
            path.EndsWith("/sync-state", StringComparison.Ordinal))
            return SyncState is null ? new(HttpStatusCode.NotFound) : Json(SyncState);

        if (request.Method == HttpMethod.Post && path == "/internal/banking/ingest")
        {
            var batch = await request.Content!.ReadFromJsonAsync<FinanceIngestBatch>(_json, cancellationToken)
                ?? throw new InvalidOperationException("Missing ingest payload.");
            Ingests.Add(batch);
            return new(HttpStatusCode.OK);
        }

        return new(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"Unhandled backend request: {request.Method} {path}")
        };
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value)
    };
}

internal sealed class TestBankingEnvironment : IDisposable
{
    private readonly string _privateKeyPath;

    public TestBankingEnvironment()
    {
        _privateKeyPath = Path.Combine(Path.GetTempPath(), $"fullworth-banking-tests-{Guid.NewGuid():N}.pem");
        using var rsa = RSA.Create(2048);
        File.WriteAllText(_privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
    }

    public EnableBankingClient CreateProvider(
        HttpMessageHandler handler,
        EnableBankingRequestPolicy? policy = null,
        int retryCount = 2,
        int spacingMilliseconds = 250)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://provider.test/") };
        var options = Options.Create(new EnableBankingOptions
        {
            BaseUrl = "https://provider.test",
            ApplicationId = "test-application",
            PrivateKeyPath = _privateKeyPath,
            MinimumRequestSpacingMilliseconds = spacingMilliseconds,
            TransientRetryCount = retryCount
        });
        return new EnableBankingClient(http, options, policy ?? new EnableBankingRequestPolicy());
    }

    public static FullWorthBackendClient CreateBackend(FakeBackendHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://backend.test/") };
        return new FullWorthBackendClient(http, Options.Create(new BackendOptions
        {
            BaseUrl = "https://backend.test",
            IngestKey = "test-ingest-key"
        }));
    }

    public BankSyncService CreateSyncService(
        HttpMessageHandler providerHandler,
        FakeBackendHandler backendHandler,
        BankingSyncOptions? sync = null,
        BankSyncConcurrencyGate? gate = null,
        int retryCount = 2)
    {
        var providerOptions = new EnableBankingOptions
        {
            BaseUrl = "https://provider.test",
            ApplicationId = "test-application",
            PrivateKeyPath = _privateKeyPath,
            RedirectUrl = "https://finance.test/connect/enable-banking/callback",
            MinimumRequestSpacingMilliseconds = 250,
            TransientRetryCount = retryCount
        };

        var providerHttp = new HttpClient(providerHandler) { BaseAddress = new Uri("https://provider.test/") };
        var provider = new EnableBankingClient(providerHttp, Options.Create(providerOptions), new EnableBankingRequestPolicy());
        var backend = CreateBackend(backendHandler);

        return new BankSyncService(
            provider,
            backend,
            gate ?? new BankSyncConcurrencyGate(),
            Options.Create(providerOptions),
            Options.Create(sync ?? new BankingSyncOptions()),
            NullLogger<BankSyncService>.Instance);
    }

    public static BankConnectionDto AuthorizedConnection(
        DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? nextSyncAllowedAt = null,
        string sessionId = "session-1") => new(
            Guid.NewGuid(),
            "enable-banking",
            "Test Bank",
            "DE",
            null,
            null,
            sessionId,
            "AUTHORIZED",
            DateTimeOffset.UtcNow.AddDays(30),
            lastAttemptAt,
            null,
            nextSyncAllowedAt,
            0,
            null);

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0) return result;

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            result[Uri.UnescapeDataString(pieces[0])] = pieces.Length == 2 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }
        return result;
    }

    public void Dispose()
    {
        try { File.Delete(_privateKeyPath); }
        catch { }
    }
}
