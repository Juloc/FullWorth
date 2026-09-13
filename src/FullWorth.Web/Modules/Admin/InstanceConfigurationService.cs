using Microsoft.Extensions.Configuration;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// Reads and writes the settings an administrator may change, and — the part that matters — says
/// which layer each value actually came from.
///
/// Precedence is not implemented here. It is the position of <see cref="InstancePublicUrlConfigurationSource"/>
/// in the configuration chain: inserted immediately before the environment variables, so appsettings
/// loses to a stored value and a stored value loses to an environment variable. One insert, every key,
/// no per-setting code. What this class adds is the ability to SEE that, which is what keeps an
/// administrator from editing a field an environment variable is quietly overriding.
/// </summary>
public sealed class InstanceConfigurationService(
    IConfiguration configuration,
    InstanceConfigurationStore store,
    InstancePublicUrlConfigurationSource source)
{
    public const string SourceEnvironment = "environment";
    public const string SourceStored = "stored";
    public const string SourceDefault = "default";

    public async Task<IReadOnlyList<InstanceSettingView>> ListAsync(CancellationToken ct)
    {
        var stored = (await store.ListAsync(ct))
            .ToDictionary(row => row.Key, row => row, StringComparer.OrdinalIgnoreCase);

        return InstanceSettingCatalogue.All
            .Select(descriptor =>
            {
                var isStored = stored.ContainsKey(descriptor.Key);
                var winner = WinningSource(descriptor.Key, isStored);
                var secret = descriptor.Kind == InstanceSettingKind.Secret;

                return new InstanceSettingView(
                    descriptor.Key,
                    descriptor.Label,
                    descriptor.Hint,
                    descriptor.Section,
                    descriptor.Kind.ToString().ToLowerInvariant(),
                    // A secret never travels back, in any state. Everything else does, because seeing
                    // the value in force is the whole point of the screen.
                    secret ? null : configuration[descriptor.Key],
                    isStored,
                    winner,
                    // Read-only in the catalogue, or pinned by the environment: either way the form
                    // must not offer an edit that cannot take effect.
                    descriptor.ReadOnly || winner == SourceEnvironment,
                    descriptor.Minimum,
                    descriptor.Maximum,
                    descriptor.Choices);
            })
            .ToArray();
    }

    /// <summary>
    /// Saves one setting and republishes everything, so the change applies without a restart.
    /// </summary>
    public async Task SetAsync(string key, string? value, Guid? actor, CancellationToken ct)
    {
        await store.SetAsync(key, value, actor, ct);
        await RepublishAsync(ct);
    }

    /// <summary>Pushes the stored settings into configuration. Called at startup and after each save.</summary>
    public async Task RepublishAsync(CancellationToken ct) =>
        source.Provider.PublishStored(await store.ReadAllAsync(ct));

    /// <summary>
    /// Which provider actually answers for this key.
    ///
    /// Asked of the configuration root rather than reasoned about, by walking the providers in reverse
    /// — the order they are consulted — and taking the first that has the key. That is the same walk
    /// the configuration system itself does, so the answer cannot drift from the truth.
    /// </summary>
    private string WinningSource(string key, bool isStored)
    {
        if (configuration is not IConfigurationRoot root)
            return isStored ? SourceStored : SourceDefault;

        foreach (var provider in root.Providers.Reverse())
        {
            if (!provider.TryGet(key, out var value) || string.IsNullOrEmpty(value)) continue;
            if (ReferenceEquals(provider, source.Provider)) return isStored ? SourceStored : SourceDefault;
            return provider is Microsoft.Extensions.Configuration.EnvironmentVariables.EnvironmentVariablesConfigurationProvider
                ? SourceEnvironment
                : SourceDefault;
        }

        return SourceDefault;
    }
}
