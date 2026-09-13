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
    bool ReadOnly = false)
{
    /// <summary>
    /// What the setting is called on screen. It lives here rather than in the frontend for the same
    /// reason the rest of the descriptor does: a new setting is one entry in this list and nothing
    /// else, including nothing in a translation file that somebody has to remember.
    ///
    /// Falls back to the configuration key, so a setting added without a label is ugly rather than
    /// broken. The key is shown underneath either way — it is what an operator needs when they want
    /// to pin the same value with an environment variable instead.
    /// </summary>
    public string Label { get; init; } = Key;

    /// <summary>One line saying what changes. Empty is allowed; a wrong one is not.</summary>
    public string Hint { get; init; } = string.Empty;
}

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
        new("EnableBanking:RedirectUrl", SectionEnableBanking, InstanceSettingKind.Url, ReadOnly: true)
        {
            Label = "Rückleit-Adresse",
            Hint = "Abgeleitet aus der Adresse, unter der diese Installation erreicht wurde. Muss zeichengenau zur Registrierung im Control Panel passen."
        },
        new("EnableBanking:ApplicationName", SectionEnableBanking, MaxLength: 120)
        {
            Label = "Anwendungsname",
            Hint = "So heißt diese Installation gegenüber Enable Banking und den Banken."
        },
        new("EnableBanking:ApplicationDescription", SectionEnableBanking, MaxLength: 300)
        {
            Label = "Anwendungsbeschreibung",
            Hint = "Kurztext bei der Registrierung."
        },
        new("EnableBanking:PrivacyUrl", SectionEnableBanking, InstanceSettingKind.Url)
        {
            Label = "Datenschutz-Adresse",
            Hint = "Wird bei der Registrierung mitgegeben."
        },
        new("EnableBanking:TermsUrl", SectionEnableBanking, InstanceSettingKind.Url)
        {
            Label = "AGB-Adresse",
            Hint = "Wird bei der Registrierung mitgegeben."
        },
        new("EnableBanking:BaseUrl", SectionEnableBanking, InstanceSettingKind.Url)
        {
            Label = "API-Adresse",
            Hint = "Nur ändern, wenn Enable Banking eine andere nennt."
        },
        new("EnableBanking:ControlPanelBaseUrl", SectionEnableBanking, InstanceSettingKind.Url)
        {
            Label = "Control-Panel-Adresse",
            Hint = "Registrierung und Bankstatus."
        },
        new("EnableBanking:DefaultCountry", SectionEnableBanking, MaxLength: 2)
        {
            Label = "Standardland",
            Hint = "Zwei Buchstaben, etwa DE."
        },
        new("EnableBanking:DefaultPsuType", SectionEnableBanking, InstanceSettingKind.Choice,
            Choices: ["personal", "business"])
        {
            Label = "Kontoart",
            Hint = "Privat- oder Geschäftskonto, wenn die Bank danach fragt."
        },
        new("EnableBanking:MinimumRequestSpacingMilliseconds", SectionEnableBanking,
            InstanceSettingKind.Integer, Minimum: 250, Maximum: 10_000)
        {
            Label = "Mindestabstand zwischen Anfragen (ms)",
            Hint = "Schützt vor Sperren durch die Bank."
        },
        new("EnableBanking:TransientRetryCount", SectionEnableBanking,
            InstanceSettingKind.Integer, Minimum: 0, Maximum: 3)
        {
            Label = "Wiederholungen bei Störungen",
            Hint = "Nur für 408 und 5xx, nicht für Ablehnungen."
        },
        new("EnableBanking:AuthorizationStateTtlMinutes", SectionEnableBanking,
            InstanceSettingKind.Integer, Minimum: 1, Maximum: 60)
        {
            Label = "Gültigkeit der Freigabe (Minuten)",
            Hint = "Wie lange eine begonnene Bankfreigabe abgeschlossen werden kann."
        },

        // --- FinTS ----------------------------------------------------------------------------
        new("FinTs:ProductId", SectionBanking, MaxLength: 64)
        {
            Label = "Produkt-ID",
            Hint = "Banken wie die ING verlangen für FinTS eine registrierte Produkt-ID. Ohne sie lehnt die Bank jeden Dialog ab."
        },
        new("FinTs:HistoryDays", SectionBanking, InstanceSettingKind.Integer, Minimum: 1, Maximum: 3650)
        {
            Label = "Abgeholter Zeitraum (Tage)",
            Hint = "Wie weit ein FinTS-Abruf zurückgeht."
        },
        new("FinTs:MaxPages", SectionBanking, InstanceSettingKind.Integer, Minimum: 1, Maximum: 200)
        {
            Label = "Seitenlimit",
            Hint = "Sicherung gegen einen Abruf, der nicht endet."
        },

        // --- Bank sync ------------------------------------------------------------------------
        new("Sync:IntervalMinutes", SectionSync, InstanceSettingKind.Integer, Minimum: 5, Maximum: 1440)
        {
            Label = "Aufwachintervall (Minuten)",
            Hint = "Wie oft der Hintergrunddienst nachsieht — nicht, wie oft eine Bank abgefragt wird."
        },
        new("Sync:MinimumBackgroundSyncIntervalMinutes", SectionSync,
            InstanceSettingKind.Integer, Minimum: 5, Maximum: 10_080)
        {
            Label = "Mindestabstand je Bankverbindung (Minuten)",
            Hint = "Kann nur erhöht werden; darunter greift die eingebaute Untergrenze."
        },
        new("Sync:RateLimitCooldownMinutes", SectionSync, InstanceSettingKind.Integer, Minimum: 1, Maximum: 10_080)
        {
            Label = "Pause nach einer Sperre (Minuten)",
            Hint = "Nach einer Ratenbegrenzung der Bank."
        },
        new("Sync:OverlapDays", SectionSync, InstanceSettingKind.Integer, Minimum: 0, Maximum: 90)
        {
            Label = "Überlappung (Tage)",
            Hint = "Wie weit jeder Abruf in bereits Geholtes zurückgreift, damit Nachbuchungen nicht durchfallen."
        },
        new("Sync:PersistBatchSize", SectionSync, InstanceSettingKind.Integer, Minimum: 10, Maximum: 5000)
        {
            Label = "Speicher-Bündelgröße",
            Hint = "Wie viele Umsätze auf einmal geschrieben werden."
        },

        // --- Registration ---------------------------------------------------------------------
        new("Registration:Enabled", SectionRegistration, InstanceSettingKind.Boolean)
        {
            Label = "Registrierung offen",
            Hint = "Nach dem ersten Konto schließt sie sich automatisch. Hier wieder öffnen, um jemanden aufzunehmen."
        },
        new("Registration:SpaceName", SectionRegistration, MaxLength: 120)
        {
            Label = "Name des ersten Bereichs",
            Hint = "Nur beim allerersten Konto verwendet."
        },
        new("Registration:BaseCurrency", SectionRegistration, MaxLength: 3)
        {
            Label = "Basiswährung",
            Hint = "Drei Buchstaben, etwa EUR."
        },

        // --- Logging --------------------------------------------------------------------------
        //
        // The level, not the rotation. Rotation (max-size, max-file) is written by the Docker daemon,
        // which this process never sees - a field for it would be a field with no effect, so it stays
        // in the compose file where it belongs.
        //
        // The level takes effect immediately: the generic host binds Logging through a change token,
        // so a reload re-binds the filters mid-flight. That is exactly when you want to change it.
        new("Logging:LogLevel:Default", SectionLogging, InstanceSettingKind.Choice, Choices: LogLevels)
        {
            Label = "Protokollstufe",
            Hint = "Wirkt sofort, ohne Neustart."
        },
        new("Logging:LogLevel:Microsoft.AspNetCore", SectionLogging, InstanceSettingKind.Choice, Choices: LogLevels)
        {
            Label = "Protokollstufe · Web",
            Hint = "Anfragen und Routing."
        },
        new("Logging:LogLevel:Microsoft.EntityFrameworkCore", SectionLogging, InstanceSettingKind.Choice, Choices: LogLevels)
        {
            Label = "Protokollstufe · Datenbank",
            Hint = "Ab Debug steht jede SQL-Anweisung samt Parametern im Container-Log."
        }
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
