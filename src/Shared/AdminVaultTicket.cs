using System.Security.Cryptography;
using System.Text;

namespace FullWorth.Shared;

/// <summary>
/// A one-minute, single-purpose proof that the BFF has already done the step-up, so the finance
/// backend may decrypt one of the caller's own stored credentials.
///
/// Signed with a key <b>derived</b> from the backend internal key rather than with the internal key
/// itself. The derivation is the point: the internal key already opens the internal-context path, and
/// reusing it here would quietly turn it into a read-anything key — anyone who ever learned it for
/// one purpose would hold the other. HKDF with its own info string keeps the two apart even though
/// there is one file on disk.
///
/// The ticket carries the finance user it was issued for. The backend reads only rows owned by that
/// user, so a ticket cannot become a way to read somebody else's bank PIN — which is the one thing
/// the vault's scope says it must never do.
/// </summary>
public static class AdminVaultTicket
{
    /// <summary>
    /// Short on purpose. It is minted immediately before the call it authorises, so a minute is
    /// generous; anything longer is a window for a ticket that leaked into a log to be replayed.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    public const string HeaderName = "X-FullWorth-Admin-Vault-Ticket";

    private const string Version = "v1";
    private static ReadOnlySpan<byte> DerivationInfo => "fullworth-admin-vault-ticket"u8;

    /// <summary>
    /// <c>v1.&lt;financeUserId&gt;.&lt;expiryUnixSeconds&gt;.&lt;signature&gt;</c>. Everything the
    /// backend needs is in the ticket, so validating it costs no lookup and no shared state.
    /// </summary>
    public static string Issue(string internalKey, Guid financeUserId, DateTimeOffset now)
    {
        var expires = now.Add(Lifetime).ToUnixTimeSeconds();
        var payload = $"{Version}.{financeUserId:N}.{expires}";
        return $"{payload}.{Sign(internalKey, payload)}";
    }

    public static bool TryValidate(
        string? internalKey, string? ticket, DateTimeOffset now, out Guid financeUserId)
    {
        financeUserId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(internalKey) || string.IsNullOrWhiteSpace(ticket)) return false;

        var parts = ticket.Split('.');
        if (parts.Length != 4 || parts[0] != Version) return false;
        if (!Guid.TryParseExact(parts[1], "N", out var user)) return false;
        if (!long.TryParse(parts[2], out var expires)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expires) <= now) return false;

        var expected = Sign(internalKey, $"{parts[0]}.{parts[1]}.{parts[2]}");

        // Fixed-time: the signature is the only thing standing between a caller with the ingest key
        // and every credential this user stored, and a byte-by-byte comparison leaks how far a forged
        // one got.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[3])))
            return false;

        financeUserId = user;
        return true;
    }

    private static string Sign(string internalKey, string payload)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(internalKey),
            outputLength: 32,
            info: DerivationInfo.ToArray());

        return Base64Url(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>Base64 without padding or slashes, so the ticket survives a header untouched.</summary>
    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
