using System.Security.Cryptography;
using System.Text;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// Stable privacy-safe entity keys for benchmark dimensions. These keys intentionally do not expose
/// legacy composite registry identifiers or local ids.
/// </summary>
public static class CloudBenchmarkEntityKeys
{
    public static string ForMerchant(string canonicalMerchantKey)
    {
        if (string.IsNullOrWhiteSpace(canonicalMerchantKey))
            throw new ArgumentException("Canonical merchant key is required.", nameof(canonicalMerchantKey));

        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"fullworth:merchant-benchmark-entity:v1:{canonicalMerchantKey}")))
            .ToLowerInvariant();
        return $"merchant.{hash[..32]}";
    }
}
