namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Logs the FullWorth Cloud endpoint this instance actually resolved — once, at startup.
///
/// The resolved endpoint used to appear in no response, no UI field and no log, so an operator who set
/// FullWorthCloud:BaseUrl had no way to tell whether it had taken effect short of reading the config file
/// back. That mattered most in the case where it had NOT: the value was silently discarded outside
/// Development, and observations kept going to the official Cloud.
///
/// It is no longer discarded and no longer honoured either — outside Development a non-official endpoint
/// switches the Cloud client off, because the Cloud server is a private repository and there is nothing
/// else to point at. This is where an operator finds that out, in one line, instead of from a stream of
/// failing Cloud calls.
///
/// Reads configuration only: no HTTP call, no credential, and it never fails startup. FullWorth must keep
/// starting without Cloud regardless.
/// </summary>
public sealed class CloudEndpointStartupLogger(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<CloudEndpointStartupLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (FullWorthCloudClient.EndpointIsRefused(configuration, environment))
        {
            logger.LogWarning(
                "FullWorth Cloud is disabled: FullWorthCloud:BaseUrl is set to something other than {Official}. " +
                "This build talks to the official Cloud only. Remove the setting to use it, or leave Cloud off — " +
                "local finance features do not need it.",
                FullWorthCloudClient.OfficialBaseUrl);
            return Task.CompletedTask;
        }

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
