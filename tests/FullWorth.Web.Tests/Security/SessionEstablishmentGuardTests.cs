using System.IO;

namespace FullWorth.Web.Tests.Security;

/// <summary>
/// A release must never contain a code path that establishes a signed-in session without verifying a
/// credential. Convenience bypasses ("dev login", "sign in as first user", impersonation helpers) are the
/// classic way one slips in: they are easy to gate behind an environment flag and easy to mis-deploy.
///
/// Both the low-level cookie sign-in AND the reusable session-sign-in service are therefore pinned to a
/// reviewed set of files. A new caller fails this test on purpose — including one that reuses an existing
/// allowlisted service, which a guard on the low-level call alone would miss. Widening an allowlist must be
/// a deliberate, reviewed act.
/// </summary>
public sealed class SessionEstablishmentGuardTests
{
    // Only these may call the cookie sign-in directly: the coordinator owns the password/2FA/recovery
    // flows, and the passkey flow builds its session after the WebAuthn assertion is verified.
    private static readonly string[] CookieSignInAllowlist =
    [
        Path.Combine("src", "FullWorth.Web", "Modules", "Auth", "AuthSessionCoordinator.cs"),
        Path.Combine("src", "FullWorth.Web", "Modules", "Passkeys", "PasskeyUserAccess.cs")
    ];

    // Only these may touch the reusable session-sign-in service: its definition, its DI registration, and
    // the passkey endpoints that perform the credential check before calling it.
    private static readonly string[] SessionServiceAllowlist =
    [
        Path.Combine("src", "FullWorth.Web", "Modules", "Passkeys", "PasskeyUserAccess.cs"),
        Path.Combine("src", "FullWorth.Web", "Modules", "Passkeys", "PasskeyRegistration.cs"),
        Path.Combine("src", "FullWorth.Web", "Modules", "Passkeys", "PasskeyEndpoints.cs")
    ];

    [Fact]
    public void OnlyReviewedFilesEstablishACookieSession() =>
        AssertMarkerConfinedTo("SignInWithClaimsAsync", CookieSignInAllowlist);

    [Fact]
    public void OnlyReviewedFilesUseTheReusableSessionSignInService() =>
        AssertMarkerConfinedTo("PasskeySessionSignInService", SessionServiceAllowlist);

    private static void AssertMarkerConfinedTo(string marker, string[] allowlist)
    {
        var root = Root();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildArtifact(path))
            .Where(path => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .Where(relative => !allowlist.Contains(relative, StringComparer.OrdinalIgnoreCase))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"'{marker}' establishes a signed-in session and may only appear in reviewed sign-in files. " +
            $"Unexpected: {string.Join(", ", offenders)}. If a new sign-in path is genuinely required, have it " +
            "reviewed and add it to the allowlist deliberately — never to make a test pass.");
    }

    private static bool IsBuildArtifact(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FullWorth.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
