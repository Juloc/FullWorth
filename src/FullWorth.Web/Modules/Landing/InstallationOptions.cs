namespace FullWorth.Web.Modules.Landing;

/// <summary>
/// Operator-facing installation configuration. Bound from the <c>Installation</c> configuration
/// section (e.g. <c>Installation__Mode=MultiUser</c>, <c>Installation__Registration=InviteOnly</c>).
/// Both default to the safest choice for a fresh self-hosted deployment: a single-user household with
/// no self-service registration.
/// </summary>
public sealed class InstallationOptions
{
    public const string SectionName = "Installation";

    public InstallationMode Mode { get; set; } = InstallationMode.SingleUser;

    public RegistrationMode Registration { get; set; } = RegistrationMode.Disabled;
}
