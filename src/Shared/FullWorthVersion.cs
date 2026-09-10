using System.Reflection;

namespace FullWorth;

/// <summary>
/// The one place that answers "which build is this".
///
/// Nine call sites each wrote <c>Assembly.GetName().Version?.ToString() ?? "unknown"</c>, and no
/// version was set anywhere, so all of them reported <c>1.0.0.0</c>: the Cloud could not tell one
/// client from another, and the minimum-client-version check compared against a constant.
///
/// Two forms, because they answer different questions:
/// <list type="bullet">
///   <item><see cref="Full"/> - what to report and log. Carries the prerelease label
///         (<c>1.3.0-alpha.18</c>), which is exactly what distinguishes two alphas of one version.</item>
///   <item><see cref="Numeric"/> - what to compare. A prerelease label is not orderable as a
///         <see cref="System.Version"/>, so the comparison uses the numeric part alone.</item>
/// </list>
/// </summary>
internal static class FullWorthVersion
{
    private static readonly Assembly Assembly = typeof(FullWorthVersion).Assembly;

    /// <summary>
    /// The full informational version, prerelease label included. Falls back to the numeric version and
    /// then to "unknown" - never throws, because a version string is never worth failing a request for.
    /// </summary>
    public static string Full { get; } = Resolve();

    /// <summary>The numeric version, for an ordered comparison. Null when the build set none.</summary>
    public static Version? Numeric { get; } = Assembly.GetName().Version;

    private static string Resolve()
    {
        // MSBuild appends "+<commit sha>" to the informational version by default; the sha is build
        // provenance, not a version, and it makes every reported value unique for no benefit.
        var informational = Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            var trimmed = plus < 0 ? informational : informational[..plus];
            if (!string.IsNullOrWhiteSpace(trimmed)) return trimmed.Trim();
        }

        return Assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
