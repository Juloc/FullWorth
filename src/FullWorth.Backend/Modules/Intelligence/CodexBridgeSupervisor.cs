using System.Diagnostics;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Starts the Codex bridge that lives in this container, the first time somebody actually needs it.
///
/// Codex used to be a second container. It is the same image now, which is what makes "always ready"
/// possible — but an installation that never signed Codex in should not be paying for a Node process
/// that can only answer "not signed in". So the container starts a launcher under the <c>codex</c>
/// user that sleeps until an arm file appears: a shell, about a megabyte, no Node.
///
/// The arm file is written from here. Two named clients decide who gets to write it:
///
/// <list type="bullet">
/// <item><see cref="ArmingClient"/> — the paths where a human is setting Codex up or looking at it.
/// Those want the bridge running; waiting a few seconds for it is the expected cost.</item>
/// <item><see cref="PassiveClient"/> — the background pipelines (receipts, payslips, pension papers).
/// They fall back to local OCR anyway, and starting Codex from them would mean a flag set without a
/// login keeps a bridge running forever that can only answer "not signed in".</item>
/// </list>
///
/// Outside the container the arm directory does not exist, and then this does nothing at all: the
/// bridge is either reachable or it is not, exactly as before.
/// </summary>
internal sealed class CodexBridgeSupervisor(
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<CodexBridgeSupervisor> logger)
{
    /// <summary>Writes the arm file before its first request. For paths a human triggered.</summary>
    internal const string ArmingClient = "codex-bridge-arming";

    /// <summary>Never starts anything. For pipelines that have a local fallback.</summary>
    internal const string PassiveClient = "codex-bridge-passive";

    /// <summary>
    /// Where the launcher watches. On tmpfs, so an armed bridge does not survive the container — a
    /// restart re-decides from the Codex home directory whether to arm straight away.
    /// </summary>
    internal const string DefaultArmDirectory = "/tmp/fullworth-codex";

    private const string ArmDirectoryKey = "AiAccess:CodexArmDirectory";

    /// <summary>
    /// How long the first caller waits for the bridge to answer. Node's own start is about a second;
    /// the rest is headroom for a loaded host. Running out is not an error here — the request goes out
    /// anyway and fails with the ordinary <c>codex_bridge_unavailable</c>, which is the truth.
    /// </summary>
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _ready;

    /// <summary>
    /// Makes sure the bridge is running, if this installation is one where that is possible. Returns
    /// once it answers, once the budget is spent, or immediately on any host without a launcher.
    /// </summary>
    internal async Task EnsureArmedAsync(CancellationToken ct)
    {
        if (_ready) return;

        var directory = configuration[ArmDirectoryKey] ?? DefaultArmDirectory;

        // The launcher creates this directory, so its absence means there is no launcher: a developer
        // machine, the test suite, or a stack still running the old sidecar. Nothing to arm.
        if (!Directory.Exists(directory))
        {
            _ready = true;
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready) return;

            var arm = Path.Combine(directory, "arm");
            if (!File.Exists(arm))
            {
                try
                {
                    // Empty on purpose: the launcher waits for the file to exist and nothing reads it.
                    await File.WriteAllTextAsync(arm, string.Empty, ct).ConfigureAwait(false);
                    logger.LogInformation("Codex bridge armed; waiting for it to answer.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A read-only mount or a directory this process may not write to. Say it once and
                    // carry on - the request below still runs and reports the real outcome.
                    logger.LogWarning(
                        "Could not arm the Codex bridge at {Path}: {Message}", arm, exception.Message);
                    _ready = true;
                    return;
                }
            }

            if (await WaitForHealthAsync(ct).ConfigureAwait(false)) _ready = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> WaitForHealthAsync(CancellationToken ct)
    {
        var baseUri = CodexBridgeConfiguration.BaseUri(configuration);
        if (baseUri is null) return false;

        var health = new Uri(baseUri, "/health");
        var clock = Stopwatch.StartNew();
        // Its own client, not the arming one: asking the arming client would re-enter this method.
        using var probe = clients.CreateClient();
        probe.Timeout = TimeSpan.FromSeconds(2);

        while (clock.Elapsed < StartupBudget)
        {
            try
            {
                using var response = await probe.GetAsync(health, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        logger.LogWarning(
            "The Codex bridge did not answer within {Seconds}s of being armed.", StartupBudget.TotalSeconds);
        return false;
    }
}

/// <summary>
/// Arms the bridge before the request that needs it. A handler rather than a call at every use site,
/// because "which callers may start Codex" is then a property of the client they asked for, and a new
/// caller cannot forget it or get it wrong.
/// </summary>
internal sealed class CodexBridgeArmingHandler(CodexBridgeSupervisor supervisor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await supervisor.EnsureArmedAsync(cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
