namespace FullWorth.Web.Security.Headers;

public sealed class SecurityHeadersOptions
{
    public bool ReportOnly { get; set; }
    public bool AddLegacyFrameProtection { get; set; } = true;
}
