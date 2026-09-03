using System.Security.Claims;
using FullWorth.Web.Modules.Sessions;

namespace FullWorth.Web.Tests.Sessions;

public sealed class SessionContractTests
{
    [Fact]
    public void SessionClaim_RoundTripsSessionId()
    {
        var sessionId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([SessionClaims.CreateSessionIdClaim(sessionId)]));

        Assert.True(SessionClaims.TryGetSessionId(principal, out var parsed));
        Assert.Equal(sessionId, parsed);
    }

    [Fact]
    public void SessionDto_DoesNotExposeSensitiveInternalFields()
    {
        var properties = typeof(SessionDto).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(nameof(UserSession.UserAgent), properties);
        Assert.DoesNotContain(nameof(UserSession.IpAddress), properties);
        Assert.DoesNotContain(nameof(UserSession.SecurityStampAtIssue), properties);
        Assert.DoesNotContain("Cookie", properties);
        Assert.DoesNotContain("Token", properties);
    }

    [Fact]
    public void DeviceMetadata_SanitizesUserAgentAndRejectsInvalidIp()
    {
        var metadata = SessionDeviceMetadata.Create("Chrome/1\r\nInjected", "not-an-ip");

        Assert.DoesNotContain('\r', metadata.UserAgent!);
        Assert.DoesNotContain('\n', metadata.UserAgent!);
        Assert.Null(metadata.IpAddress);
        Assert.False(string.IsNullOrWhiteSpace(metadata.DeviceName));
    }

    [Fact]
    public void DeviceMetadata_NormalizesValidIpWithoutUsingItAsIdentity()
    {
        var metadata = SessionDeviceMetadata.Create(null, "2001:0db8::1");

        Assert.Equal("2001:db8::1", metadata.IpAddress);
        Assert.Equal("Browser session", metadata.DeviceName);
    }

    [Fact]
    public void SessionEntity_DoesNotContainCredentialOrCookieFields()
    {
        var properties = typeof(UserSession).GetProperties().Select(x => x.Name).ToArray();

        Assert.DoesNotContain(properties, x => x.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, x => x.Contains("ResetToken", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(properties, x => x.Contains("Cookie", StringComparison.OrdinalIgnoreCase));
    }
}
