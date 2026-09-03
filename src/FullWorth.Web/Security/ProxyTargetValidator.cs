namespace FullWorth.Web.Security;

/// <summary>
/// Builds and validates the upstream target for the BFF proxy. The catch-all route value is
/// attacker-controlled: without validation an absolute ("http://caddy:2019/…"), scheme-relative
/// ("//host/…"), backslash- or encoding-mangled path would escape the configured BaseAddress and
/// carry the internal service keys to a foreign host (SSRF + secret leak). Every proxied request
/// must pass this gate BEFORE any internal header is attached; a rejected request produces no
/// outbound traffic at all.
/// </summary>
public static class ProxyTargetValidator
{
    /// <summary>
    /// Composes <paramref name="path"/> + <paramref name="queryString"/> against
    /// <paramref name="baseAddress"/> and accepts the result only when scheme, host and port equal
    /// the base exactly, no userinfo/fragment is present, and the normalized absolute path starts
    /// with one of <paramref name="allowedPathPrefixes"/>.
    /// </summary>
    public static bool TryBuildTarget(
        Uri baseAddress,
        string? path,
        string queryString,
        IReadOnlyList<string> allowedPathPrefixes,
        out Uri? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Backslashes (raw or percent-encoded, any decoding depth) are only ever used to confuse
        // URI parsers — no legitimate API path contains them.
        if (ContainsForbiddenSequence(path)) return false;

        Uri composed;
        try
        {
            // RFC 3986 reference resolution: an absolute or scheme-/protocol-relative reference
            // REPLACES the base — the origin comparison below is what actually catches those.
            composed = new Uri(baseAddress, path.TrimStart('/') + queryString);
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (!IsSameOrigin(composed, baseAddress)) return false;

        // Dot segments were normalized during composition, so a traversal like "api/../admin"
        // resolves to "/admin" here and fails the prefix check.
        var absolutePath = composed.AbsolutePath;
        if (ContainsForbiddenSequence(absolutePath)) return false;

        var allowed = false;
        foreach (var prefix in allowedPathPrefixes)
        {
            if (absolutePath.StartsWith(prefix, StringComparison.Ordinal) ||
                string.Equals(absolutePath, prefix.TrimEnd('/'), StringComparison.Ordinal))
            {
                allowed = true;
                break;
            }
        }
        if (!allowed) return false;

        target = composed;
        return true;
    }

    /// <summary>
    /// True when <paramref name="uri"/> is absolute and matches <paramref name="expectedBase"/> in
    /// scheme, host and port, carrying neither userinfo nor a fragment. Used as the second,
    /// independent gate inside the outbound handlers right before internal keys are attached.
    /// </summary>
    public static bool IsSameOrigin(Uri? uri, Uri expectedBase)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        return string.Equals(uri.Scheme, expectedBase.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, expectedBase.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == expectedBase.Port
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool ContainsForbiddenSequence(string value) =>
        value.Contains('\\') ||
        value.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("%25", StringComparison.OrdinalIgnoreCase);
}
