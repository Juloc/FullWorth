namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Logs the FullWorth Cloud endpoint this instance actually resolved - once, at startup. The resolved
/// endpoint used to appear in no response, no UI field and no log, so a self-hoster who pointed
/// FullWorthCloud:BaseUrl at their own Cloud (honoured since <see cref="FullWorthCloudClient.ResolveBaseUri"/>
/// stopped silently discarding it outside Development) had no way to confirm it took effect short of
/// reading the config file back. This only reads configuration - no HTTP call, no credential involved -
/// and never fails startup: an invalid FullWorthCloud:BaseUrl still surfaces as a warning here, but the
/// product must keep starting without Cloud regardless.
/// </summary>
public sealed class CloudEndpointStartupLogger(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<CloudEndpointStartupLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = FullWorthCloudClient.ResolveBaseUri(configuration, environment);
            logger.LogInformation("FullWorth Cloud endpoint resolved to {CloudEndpoint}.", endpoint);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "FullWorth Cloud endpoint could not be resolved from configuration.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
