namespace FullWorth.Web.Modules.Admin;

public enum InstanceSettingKind
{
    Text,
    Integer,
    Boolean,
    Url,
    Choice,
    /// <summary>Stored encrypted, reported only as "there is one", never read back to a browser.</summary>
    Secret
}

/// <summary>
/// One settable thing: what it is called, what it may contain, and where it belongs on screen.
///
/// A single list, because it drives everything — validation, the API shape and a generic admin form.
/// Adding a setting is one entry here, not an endpoint plus a DTO plus a form plus four i18n keys.
/// That is the difference between "settings live in the UI" being a property of the system and being
/// a promise somebody has to keep by hand for every new value.
/// </summary>
/// <param name="Key">The configuration key, exactly as the app reads it.</param>
/// <param name="Section">Grouping for the admin form.</param>
/// <param name="Choices">For <see cref="InstanceSettingKind.Choice"/>: the allowed values.</param>
/// <param name="ReadOnly">
/// Shown, explained, not editable. A derived value like the Enable Banking redirect belongs here: it
/// has to match the Control Panel registration character for character, so offering a text box would
/// be offering a way to break bank access.
/// </param>
public sealed record InstanceSettingDescriptor(
    string Key,
    string Section,
    InstanceSettingKind Kind = InstanceSettingKind.Text,
    int? Minimum = null,
    int? Maximum = null,
    int MaxLength = 300,
    IReadOnlyList<string>? Choices = null,
    bool ReadOnly = false);

public static class InstanceSettingCatalogue
{
    public const string SectionEnableBanking = "enableBanking";
    public const string SectionBanking = "banking";
    public const string SectionSync = "sync";
    public const string SectionRegistration = "registration";
    public const string SectionLogging = "logging";

    private static readonly string[] LogLevels =
        ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];

    /// <summary>
    /// What an administrator may change from the browser.
    ///
    /// Two whole classes are deliberately absent, and the reasons are different:
    ///
    /// - Things that must be true BEFORE the database can be read — connection strings, Database:*,
    ///   Security:*, Services:*, DataProtection:KeyPath, FullWorthHost:Unified. A database-backed
    ///   source cannot supply them, so a field for them would be a field that does nothing.
    /// - Things that are security policy — AllowedHosts, ReverseProxy:*, RateLimits:*, the lockout
    ///   settings, Sessions:*. A stolen admin session must not be able to unpin the host, trust an
    ///   arbitrary proxy or switch off rate limiting. That is the line, and it is held on purpose.
    ///
    /// Passkeys:RelyingPartyId is absent for a third reason: changing it makes every passkey already
    /// registered against it unusable, silently, at the next sign-in.
    /// </summary>
    public static readonly IReadOnlyList<InstanceSettingDescriptor> All =
    [
        // --- Enable Banking -------------------------------------------------------------------
        new("EnableBanking:RedirectUrl", SectionEnableBanking, InstanceSettingKind.Url, ReadOnly: true),
        new("EnableBanking:ApplicationName", SectionEnableBanking, MaxLength: 120),
        new("EnableBanking:ApplicationDescription", SectionEnableBanking, MaxLength: 300),
        new("EnableBanking:PrivacyUrl", SectionEnableBanking, InstanceSettingKind.Url),
        new("EnableBanking:TermsUrl", SectionEnableBanking, InstanceSettingKind.Url),
        new("EnableBanking:BaseUrl", SectionEnableBanking, InstanceSettingKind.Url),
        new("EnableBanking:ControlPanelBaseUrl", SectionEnableBanking, InstanceSettingKind.Url),
        new("EnableBanking:DefaultCountry", SectionEnableBanking, MaxLength: 2),
        new("EnableBanking:DefaultPsuType", SectionEnableBanking, InstanceSettingKind.Choice,
            Choices: ["personal", "business"]),
        new("EnableBanking:MinimumRequestSpacingMilliseconds", SectionEnableBanking,
            InstanceSettingKind.Integer, Minimum: 250, Maximum: 10_000),
        new("EnableBanking:TransientRetryCount", SectionEnableBanking,
            InstanceSettingKind.Integer, Minimum: 0, Maximum: 3),
        new("EnableBanking:AuthorizationStateTtlMinutes", SectionEnableBanking,
            InstanceSettingKind.Integer, Minimum: 1, Maximum: 60),

        // --- FinTS ----------------------------------------------------------------------------
        new("FinTs:ProductId", SectionBanking, MaxLength: 64),
        new("FinTs:HistoryDays", SectionBanking, InstanceSettingKind.Integer, Minimum: 1, Maximum: 3650),
        new("FinTs:MaxPages", SectionBanking, InstanceSettingKind.Integer, Minimum: 1, Maximum: 200),

        // --- Bank sync ------------------------------------------------------------------------
        new("Sync:IntervalMinutes", SectionSync, InstanceSettingKind.Integer, Minimum: 5, Maximum: 1440),
        new("Sync:MinimumBackgroundSyncIntervalMinutes", SectionSync,
            InstanceSettingKind.Integer, Minimum: 5, Maximum: 10_080),
        new("Sync:RateLimitCooldownMinutes", SectionSync, InstanceSettingKind.Integer, Minimum: 1, Maximum: 10_080),
        new("Sync:OverlapDays", SectionSync, InstanceSettingKind.Integer, Minimum: 0, Maximum: 90),
        new("Sync:PersistBatchSize", SectionSync, InstanceSettingKind.Integer, Minimum: 10, Maximum: 5000),

        // --- Registration ---------------------------------------------------------------------
        new("Registration:Enabled", SectionRegistration, InstanceSettingKind.Boolean),
        new("Registration:SpaceName", SectionRegistration, MaxLength: 120),
        new("Registration:BaseCurrency", SectionRegistration, MaxLength: 3),

        // --- Logging --------------------------------------------------------------------------
        //
        // The level, not the rotation. Rotation (max-size, max-file) is written by the Docker daemon,
        // which this process never sees - a field for it would be a field with no effect, so it stays
        // in the compose file where it belongs.
        //
        // The level takes effect immediately: the generic host binds Logging through a change token,
        // so a reload re-binds the filters mid-flight. That is exactly when you want to change it.
        new("Logging:LogLevel:Default", SectionLogging, InstanceSettingKind.Choice, Choices: LogLevels),
        new("Logging:LogLevel:Microsoft.AspNetCore", SectionLogging, InstanceSettingKind.Choice, Choices: LogLevels),
        new("Logging:LogLevel:Microsoft.EntityFrameworkCore", SectionLogging, InstanceSettingKind.Choice, Choices: LogLevels)
    ];

    private static readonly Dictionary<string, InstanceSettingDescriptor> ByKey =
        All.ToDictionary(descriptor => descriptor.Key, StringComparer.OrdinalIgnoreCase);

    public static InstanceSettingDescriptor? Find(string? key) =>
        key is not null && ByKey.TryGetValue(key, out var descriptor) ? descriptor : null;

    /// <summary>
    /// Validates a value against its descriptor. Returns null when it is acceptable, otherwise a
    /// machine-readable reason.
    /// </summary>
    public static string? Validate(InstanceSettingDescriptor descriptor, string? value)
    {
        if (descriptor.ReadOnly) return "setting_read_only";
        if (string.IsNullOrWhiteSpace(value)) return null;   // clearing is always allowed

        var trimmed = value.Trim();
        if (trimmed.Length > descriptor.MaxLength) return "setting_too_long";

        switch (descriptor.Kind)
        {
            case InstanceSettingKind.Integer:
                if (!int.TryParse(trimmed, out var number)) return "setting_not_a_number";
                if (descriptor.Minimum is { } min && number < min) return "setting_out_of_range";
                if (descriptor.Maximum is { } max && number > max) return "setting_out_of_range";
                return null;

            case InstanceSettingKind.Boolean:
                return bool.TryParse(trimmed, out _) ? null : "setting_not_a_boolean";

            case InstanceSettingKind.Url:
                return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                       (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? null
                    : "setting_not_a_url";

            case InstanceSettingKind.Choice:
                return descriptor.Choices?.Contains(trimmed, StringComparer.Ordinal) == true
                    ? null
                    : "setting_not_a_choice";

            default:
                return null;
        }
    }
}
