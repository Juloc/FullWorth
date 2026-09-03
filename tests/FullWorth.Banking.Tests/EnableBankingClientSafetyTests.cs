using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using FullWorth.Banking.EnableBanking;

namespace FullWorth.Banking.Tests;

public sealed class EnableBankingClientSafetyTests
{
    [Fact]
    public async Task TooManyRequests_is_not_retried_immediately()
    {
        using var environment = new TestBankingEnvironment();
        var handler = new RecordingHttpMessageHandler((_, number, _) => Task.FromResult(
            number == 1
                ? TestBankingEnvironment.JsonResponse("{\"error_code\":\"ASPSP_RATE_LIMIT_EXCEEDED\"}", HttpStatusCode.TooManyRequests)
                : TestBankingEnvironment.JsonResponse("{}")));
        var client = environment.CreateProvider(handler, retryCount: 3);

        var exception = await Assert.ThrowsAsync<EnableBankingApiException>(
            () => client.GetApplicationAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.True(exception.IsAspspRateLimit);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Only_allowed_transient_statuses_are_retried(HttpStatusCode status)
    {
        using var environment = new TestBankingEnvironment();
        var handler = new RecordingHttpMessageHandler((_, number, _) =>
        {
            if (number > 1) return Task.FromResult(TestBankingEnvironment.JsonResponse("{}"));
            var response = TestBankingEnvironment.JsonResponse("{}", status);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-5));
            return Task.FromResult(response);
        });
        var client = environment.CreateProvider(handler, retryCount: 2);

        await client.GetApplicationAsync(CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Transient_retries_are_capped_even_when_configuration_is_excessive()
    {
        using var environment = new TestBankingEnvironment();
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
        {
            var response = TestBankingEnvironment.JsonResponse("{}", HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(-5));
            return Task.FromResult(response);
        });
        var client = environment.CreateProvider(handler, retryCount: 99);

        await Assert.ThrowsAsync<EnableBankingApiException>(
            () => client.GetApplicationAsync(CancellationToken.None));

        Assert.Equal(4, handler.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Authentication_and_client_errors_are_not_blindly_retried(int statusCode)
    {
        using var environment = new TestBankingEnvironment();
        var handler = new RecordingHttpMessageHandler((_, number, _) => Task.FromResult(
            number == 1
                ? TestBankingEnvironment.JsonResponse("{}", (HttpStatusCode)statusCode)
                : TestBankingEnvironment.JsonResponse("{}")));
        var client = environment.CreateProvider(handler, retryCount: 3);

        await Assert.ThrowsAsync<EnableBankingApiException>(
            () => client.GetApplicationAsync(CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Concurrent_provider_calls_are_serialized_through_the_request_gate()
    {
        using var environment = new TestBankingEnvironment();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;

        var handler = new RecordingHttpMessageHandler(async (_, number, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, current);
            try
            {
                if (number == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(ct);
                }
                return TestBankingEnvironment.JsonResponse("{}");
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var client = environment.CreateProvider(handler, retryCount: 0, spacingMilliseconds: 250);

        var first = client.GetApplicationAsync(CancellationToken.None);
        await firstEntered.Task;
        var second = client.GetApplicationAsync(CancellationToken.None);

        await Task.Delay(30);
        Assert.Single(handler.Requests);
        Assert.Equal(1, maximumActive);

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task Request_policy_enforces_minimum_spacing()
    {
        var policy = new EnableBankingRequestPolicy();
        using (await policy.EnterAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None)) { }

        var stopwatch = Stopwatch.StartNew();
        using (await policy.EnterAsync(TimeSpan.FromMilliseconds(60), CancellationToken.None)) { }
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(40),
            $"Expected request spacing, measured only {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (value <= current) return;
            if (Interlocked.CompareExchange(ref maximum, value, current) == current) return;
        }
    }
}
