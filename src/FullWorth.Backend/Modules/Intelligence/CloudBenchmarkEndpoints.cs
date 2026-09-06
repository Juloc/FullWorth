using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Intelligence;

public static class CloudBenchmarkEndpoints
{
    public static IEndpointRouteBuilder MapCloudBenchmarkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/intelligence/benchmarks").WithTags("Intelligence Benchmarks");

        group.MapGet("/", async (
            string metricKey,
            string? currency,
            string? country,
            string? regionBucket,
            string? householdSizeBand,
            string? incomeBand,
            string? ageBand,
            string? observedMonth,
            CurrentUserContext currentUser,
            CloudIntelligenceStateService cloudState,
            CloudInstanceCredentialStore credentials,
            IFullWorthCloudClient cloud,
            CancellationToken ct) =>
        {
            _ = currentUser.RequireUserId();

            if (!await cloudState.HasCurrentActiveConsentAsync(ct))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var state = await cloudState.GetEnabledStateAsync(ct);
            if (state is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var secret = await credentials.GetSecretAsync(state.InstanceId, ct);
            if (string.IsNullOrWhiteSpace(secret))
            {
                try
                {
                    var registration = await cloud.RegisterAsync(
                        state.InstanceId,
                        CloudIntelligencePolicy.CurrentVersion,
                        typeof(CloudBenchmarkEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
                        ct);
                    await credentials.SaveAsync(registration, ct);
                    secret = registration.Credential;
                    await cloudState.SetTransportStatusAsync(
                        state.InstanceId, null, registration.EntitlementStatus,
                        DateTimeOffset.UtcNow, null, ct);
                }
                catch (FullWorthCloudException ex)
                {
                    await cloudState.SetTransportStatusAsync(state.InstanceId, ex.ErrorCode, null, null, null, ct);
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
            }

            try
            {
                var result = await cloud.GetBenchmarkAsync(
                    secret,
                    metricKey,
                    currency,
                    country,
                    regionBucket,
                    householdSizeBand,
                    incomeBand,
                    ageBand,
                    observedMonth,
                    ct);
                return result is null ? Results.NoContent() : Results.Ok(result);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = "invalid_benchmark_query", message = ex.Message });
            }
            catch (FullWorthCloudException ex)
            {
                await cloudState.SetTransportStatusAsync(state.InstanceId, ex.ErrorCode, null, null, null, ct);
                return Results.StatusCode(ex.StatusCode is { } status ? (int)status : StatusCodes.Status503ServiceUnavailable);
            }
        });

        return app;
    }
}
