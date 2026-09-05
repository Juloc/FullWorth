namespace FullWorth.Web.Modules.Auth;

public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";

    public bool Enabled { get; set; }

    public string SpaceName { get; set; } = "Household";

    public string BaseCurrency { get; set; } = "EUR";
}
