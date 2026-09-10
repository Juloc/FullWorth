using System.Security.Cryptography;
using System.Text;
using FullWorth.Backend.Security;

namespace FullWorth.Backend.Modules.Pension;

/// <summary>
/// Contract identity for the pension area: the same policy number at the same provider is the same
/// contract, so an annual statement for it adds a snapshot instead of creating a second contract.
///
/// The policy number itself is personal data and is stored encrypted, so the match cannot run on the
/// stored value. It runs on a keyed blind index built exactly like <c>AccountIdentifierLookup</c> does
/// for an IBAN: normalise, hash, then HMAC with the field key. Two instances of the same number
/// produce the same index; the index reveals nothing.
///
/// Normalisation is deliberately dumb — letters and digits, upper-cased. It must not encode any
/// provider's numbering scheme, because a rule for one provider is a wrong rule for the next one.
/// </summary>
public static class PensionIdentity
{
    /// <summary>The blind index a policy number is matched by, or null when there is no number.</summary>
    public static string? PolicyNumberLookup(string? policyNumber, FieldCipher cipher)
    {
        var normalized = NormalizePolicyNumber(policyNumber);
        if (normalized is null) return null;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return cipher.BlindIndex(digest);
    }

    /// <summary>The last four characters, so a list can tell two contracts apart without decrypting.</summary>
    public static string? PolicyNumberLast4(string? policyNumber)
    {
        var normalized = NormalizePolicyNumber(policyNumber);
        return normalized is { Length: >= 4 } ? normalized[^4..] : normalized;
    }

    /// <summary>Letters and digits only, upper-cased. Null for blank input.</summary>
    public static string? NormalizePolicyNumber(string? policyNumber)
    {
        if (string.IsNullOrWhiteSpace(policyNumber)) return null;
        var normalized = new string(policyNumber.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>
    /// The matching form of a provider name: letters and digits, lower-cased, with the legal form
    /// removed. Two documents write the same insurer differently, and a contract that gets a second
    /// row because one statement said "Allianz Lebensversicherungs-AG" and the next said "Allianz
    /// Lebensversicherung AG" is exactly the duplicate this area must not produce.
    ///
    /// The German genitive linking "s" ("Lebensversicherung<b>s</b>-AG") is dropped together with the
    /// legal form, because it only appears in front of one. It is dropped only after a legal form was
    /// actually removed and only from a stem long enough that a word ending in a real "s" survives.
    ///
    /// The suffix list is German company law, not a list of providers: nothing here knows any insurer.
    /// </summary>
    public static string ProviderKey(string? providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return string.Empty;
        var lowered = providerName.ToLowerInvariant();
        var letters = new string(lowered.Where(char.IsLetterOrDigit).ToArray());
        foreach (var suffix in LegalFormSuffixes)
        {
            if (letters.Length <= suffix.Length || !letters.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var stem = letters[..^suffix.Length];
            return stem.Length > 6 && stem.EndsWith('s') ? stem[..^1] : stem;
        }
        return letters;
    }

    /// <summary>Ordered longest-first, so "gmbhcokg" is not read as a plain "gmbh" with a tail.</summary>
    private static readonly string[] LegalFormSuffixes =
    [
        "aktiengesellschaft", "gmbhcokg", "gmbhco", "gmbh", "vvag", "kgaa", "ag", "se", "eg", "ev"
    ];

    /// <summary>An ISIN as printed: two country letters plus ten alphanumerics.</summary>
    public static string? NormalizeIsin(string? isin)
    {
        if (string.IsNullOrWhiteSpace(isin)) return null;
        var normalized = new string(isin.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    /// <summary>True for a syntactically valid ISIN. The check digit is not verified here.</summary>
    public static bool IsValidIsin(string? normalizedIsin) =>
        normalizedIsin is { Length: 12 }
        && char.IsAsciiLetterUpper(normalizedIsin[0])
        && char.IsAsciiLetterUpper(normalizedIsin[1])
        && normalizedIsin.Skip(2).All(char.IsAsciiLetterOrDigit);
}
