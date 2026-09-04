namespace FullWorth.Web.Modules.Landing;

/// <summary>
/// How a multi-user FullWorth instance admits new users. The default for a fresh self-hosted
/// deployment is <see cref="Disabled"/>: no self-service registration until the operator opts in.
/// </summary>
public enum RegistrationMode
{
    /// <summary>Anyone may register a new account.</summary>
    Open,

    /// <summary>New accounts require an invite issued by an existing member.</summary>
    InviteOnly,

    /// <summary>No new accounts may be created through the landing/setup surface.</summary>
    Disabled,
}
