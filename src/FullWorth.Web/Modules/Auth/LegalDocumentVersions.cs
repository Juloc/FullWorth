namespace FullWorth.Web.Modules.Auth;

public static class LegalDocumentVersions
{
    public const string Terms = "2026-09-06";
    public const string Privacy = "2026-09-06";

    public const string TermsVersionClaim = "fullworth:terms-version";
    public const string TermsAcceptedAtClaim = "fullworth:terms-accepted-at";
    public const string PrivacyVersionClaim = "fullworth:privacy-version";
    public const string PrivacyAcknowledgedAtClaim = "fullworth:privacy-acknowledged-at";
    public const string AdultConfirmedAtClaim = "fullworth:adult-confirmed-at";
}
