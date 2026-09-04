namespace FullWorth.Web.Modules.Landing;

/// <summary>Whether the instance is provisioned for a single household user or multiple users.</summary>
public enum InstallationMode
{
    SingleUser,
    MultiUser,
}

/// <summary>
/// The sanitized installation state a landing/setup page is allowed to see. It deliberately carries
/// no raw user counts and no user identities — only the coarse facts the public landing needs to
/// decide what to show: is this a single- or multi-user install, has the first user been created yet,
/// and (for multi-user) how new users are admitted.
/// </summary>
public sealed record InstallationState(InstallationMode Mode, bool Initialized, RegistrationMode Registration)
{
    /// <summary>The exact JSON contract handed to the browser. Enum values are emitted as stable
    /// camelCase strings so the internal enum representation never leaks.</summary>
    public object ToSanitizedPayload() => new
    {
        mode = Mode == InstallationMode.MultiUser ? "multiUser" : "singleUser",
        initialized = Initialized,
        registration = Registration switch
        {
            RegistrationMode.Open => "open",
            RegistrationMode.InviteOnly => "inviteOnly",
            _ => "disabled",
        },
    };
}
