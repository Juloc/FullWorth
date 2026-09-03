namespace FullWorth.Web.Modules.Bootstrap;

/// <summary>
/// First-run admin bootstrap. When an Email and Password are configured and the login store is
/// empty, FullWorth.Web creates the very first user + FullWorth Space on startup so a fresh deployment
/// has a working first login. Provide these via environment/secret (Bootstrap__Email,
/// Bootstrap__Password) and rotate/remove them after the first successful sign-in.
/// </summary>
public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? DisplayName { get; set; }
    public string? SpaceName { get; set; }
    public string? BaseCurrency { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Password);
}
