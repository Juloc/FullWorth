using System.Net;

namespace FullWorth.Banking.EnableBanking;

public sealed class EnableBankingRequestPolicy
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public async Task<IDisposable> EnterAsync(TimeSpan minimumSpacing, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var wait = _lastRequestAt + minimumSpacing - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);

            _lastRequestAt = DateTimeOffset.UtcNow;
            return new Releaser(_gate);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

public sealed class EnableBankingApiException(
    HttpStatusCode statusCode,
    string? errorCode,
    string responseBody,
    DateTimeOffset? retryAt = null)
    : HttpRequestException($"Enable Banking returned {(int)statusCode} ({errorCode ?? "unknown"}).", null, statusCode)
{
    public string? ErrorCode { get; } = errorCode;
    public string ResponseBody { get; } = responseBody;
    public DateTimeOffset? RetryAt { get; } = retryAt;
    public bool IsAspspRateLimit => string.Equals(ErrorCode, "ASPSP_RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase);
}
