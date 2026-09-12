using Microsoft.Extensions.Configuration;

namespace FullWorth.Shared;

/// <summary>
/// Where the in-process services find each other.
///
/// FullWorth ships as ONE container: Web, Backend and Banking in a single Kestrel process that still
/// talks to itself over loopback HTTP. The split <c>fullworth-web</c>/<c>-backend</c>/<c>-banking</c>
/// images are no longer built.
///
/// This existed three times, with two different answers. <c>Program.cs</c> picked loopback or the split
/// hostname depending on <c>FullWorthHost:Unified</c>, while <c>BackendContextOptions</c> and
/// <c>BackendOptions</c> each hardcoded the split hostname. That mattered: the internal key is only
/// attached to requests whose origin matches <c>BackendContextOptions.BackendBaseAddress</c>, so the two
/// disagreeing meant the key silently never went out. It did not bite because appsettings.json shipped
/// the split URL and every deployment therefore had to set the loopback URL by hand — which made the
/// unified default unreachable code, and put a line in every compose file that only restated the shape
/// of the container it was running in.
///
/// One rule now: a configured value wins, blank means the container's own shape.
/// </summary>
public static class UnifiedHost
{
    /// <summary>The unified container's own address. Not a production domain, never routable.</summary>
    public const string LoopbackBaseUrl = "http://127.0.0.1:8080";

    private const string UnifiedKey = "FullWorthHost:Unified";

    /// <summary>
    /// Whether this process hosts all three services itself.
    ///
    /// The answer belongs to the IMAGE, which sets FullWorthHost__Unified=true in its own ENV - not to
    /// appsettings.json and not to a compose file. The same assemblies also run embedded in the test
    /// hosts, where the backend is deliberately stubbed rather than booted, so a default of true here
    /// would silently change what those hosts are.
    /// </summary>
    public static bool IsUnified(IConfiguration configuration) => configuration.GetValue(UnifiedKey, false);

    /// <summary>Base URL of the finance backend, from this process's point of view.</summary>
    public static string BackendBaseUrl(IConfiguration configuration) =>
        Resolve(configuration, "Services:BackendUrl", "fullworth-backend");

    /// <summary>Base URL of the banking service, from this process's point of view.</summary>
    public static string BankingBaseUrl(IConfiguration configuration) =>
        Resolve(configuration, "Services:BankingUrl", "fullworth-banking");

    /// <summary>
    /// The backend as the BANKING service addresses it. Same target, different configuration key -
    /// banking is its own host with its own <c>Backend:BaseUrl</c> section.
    /// </summary>
    public static string BackendBaseUrlForBanking(IConfiguration configuration) =>
        Resolve(configuration, "Backend:BaseUrl", "fullworth-backend");

    private static string Resolve(IConfiguration configuration, string key, string splitHostname)
    {
        var configured = configuration[key];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        return IsUnified(configuration) ? LoopbackBaseUrl : $"http://{splitHostname}:8080";
    }
}
