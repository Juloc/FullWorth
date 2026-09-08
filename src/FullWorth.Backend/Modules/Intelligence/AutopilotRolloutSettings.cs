using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Modules.Intelligence;

public enum AutopilotRolloutState
{
    Off = 0,
    Shadow = 1,
    On = 2
}

public static class AutopilotFeatures
{
    public const string Signals = "signals";
    public const string Insights = "insights";
    public const string Actions = "actions";
    public const string AutomationRules = "automation-rules";
    public const string Scenarios = "scenarios";
    public const string ProactiveAiExplanations = "proactive-ai-explanations";
    public const string InsightPush = "insight-push";

    public static readonly IReadOnlyList<string> All =
    [
        Signals,
        Insights,
        Actions,
        AutomationRules,
        Scenarios,
        ProactiveAiExplanations,
        InsightPush
    ];
}

/// <summary>
/// Central rollout switchboard for the AI Autopilot migration.
///
/// Defaults follow the currently deployed rollout stage. Deploy 5 enables deterministic Signals and
/// read-only Insights; all write-capable Autopilot features remain Off. Configuration is optional and
/// therefore does not add a required
/// environment variable to normal FullWorth installations. A feature can later be moved through
/// Off -> Shadow -> On independently while main remains deployable.
///
/// Configuration shape:
/// Autopilot:Features:&lt;feature-name&gt; = off | shadow | on
/// </summary>
public sealed class AutopilotRolloutSettings
{
    public const string SectionName = "Autopilot:Features";

    private readonly IReadOnlyDictionary<string, AutopilotRolloutState> states;

    public AutopilotRolloutSettings(IConfiguration configuration)
    {
        states = AutopilotFeatures.All.ToDictionary(
            feature => feature,
            feature => Parse(configuration[$"{SectionName}:{feature}"], feature, DefaultState(feature)),
            StringComparer.Ordinal);
    }

    public AutopilotRolloutState Get(string feature)
    {
        if (!states.TryGetValue(feature, out var state))
            throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown Autopilot feature.");
        return state;
    }

    public bool IsShadowOrOn(string feature) => Get(feature) >= AutopilotRolloutState.Shadow;
    public bool IsOn(string feature) => Get(feature) == AutopilotRolloutState.On;

    public IReadOnlyDictionary<string, AutopilotRolloutState> Snapshot() => states;

    private static AutopilotRolloutState DefaultState(string feature) => feature switch
    {
        AutopilotFeatures.Signals => AutopilotRolloutState.On,
        AutopilotFeatures.Insights => AutopilotRolloutState.On,
        _ => AutopilotRolloutState.Off
    };

    private static AutopilotRolloutState Parse(string? value, string feature, AutopilotRolloutState defaultState)
    {
        if (string.IsNullOrWhiteSpace(value)) return defaultState;

        return value.Trim().ToLowerInvariant() switch
        {
            "off" => AutopilotRolloutState.Off,
            "shadow" => AutopilotRolloutState.Shadow,
            "on" => AutopilotRolloutState.On,
            _ => throw new InvalidOperationException(
                $"Invalid Autopilot rollout state '{value}' for '{feature}'. Expected off, shadow or on.")
        };
    }
}
