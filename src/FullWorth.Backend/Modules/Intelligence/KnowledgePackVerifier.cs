using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Modules.Merchants;

namespace FullWorth.Backend.Modules.Intelligence;

public sealed record VerifiedKnowledgePack(
    KnowledgePackManifest Manifest,
    KnowledgePackPayload Payload,
    byte[] RawPayload,
    IReadOnlyList<OfficialMerchantMapping> MerchantMappings);

public sealed class KnowledgePackVerificationException(string errorCode, string? message = null, Exception? inner = null)
    : Exception(message ?? errorCode, inner)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// Cryptographic trust boundary for FullWorth Knowledge Packs. No payload is considered trusted until
/// the exact downloaded bytes match the manifest SHA-256 and the RSA-PSS/SHA-256 signature verifies
/// against the configured official FullWorth signing public key.
/// </summary>
public sealed class KnowledgePackVerifier(IConfiguration configuration)
{
    public const string SupportedSignatureAlgorithm = "RSA-PSS-SHA256";

    public VerifiedKnowledgePack Verify(KnowledgePackManifest manifest, byte[] rawPayload)
    {
        if (rawPayload.Length is < 2 or > KnowledgePackPolicy.MaximumPackBytes)
            throw new KnowledgePackVerificationException("knowledge_pack_invalid_size");
        if (!string.Equals(manifest.SchemaVersion, KnowledgePackPolicy.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new KnowledgePackVerificationException("knowledge_pack_schema_unsupported");
        if (!string.Equals(manifest.SignatureAlgorithm, SupportedSignatureAlgorithm, StringComparison.OrdinalIgnoreCase))
            throw new KnowledgePackVerificationException("knowledge_pack_signature_algorithm_unsupported");

        var expectedHash = NormalizeSha256(manifest.ContentSha256);
        var actualHash = Convert.ToHexString(SHA256.HashData(rawPayload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                Convert.FromHexString(actualHash)))
            throw new KnowledgePackVerificationException("knowledge_pack_hash_mismatch");

        byte[] signature;
        try { signature = Convert.FromBase64String(manifest.SignatureBase64); }
        catch (FormatException ex)
        {
            throw new KnowledgePackVerificationException("knowledge_pack_signature_invalid", inner: ex);
        }

        try
        {
            using var rsa = RSA.Create();
            ImportOfficialPublicKey(rsa);
            if (!rsa.VerifyData(rawPayload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new KnowledgePackVerificationException("knowledge_pack_signature_invalid");
        }
        catch (KnowledgePackVerificationException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            throw new KnowledgePackVerificationException("knowledge_pack_signing_key_invalid", inner: ex);
        }

        KnowledgePackPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<KnowledgePackPayload>(rawPayload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("Empty payload.");
        }
        catch (JsonException ex)
        {
            throw new KnowledgePackVerificationException("knowledge_pack_payload_invalid_json", inner: ex);
        }

        ValidateEnvelope(manifest, payload);
        var mappings = ValidateAndProjectMappings(payload);
        return new VerifiedKnowledgePack(manifest, payload, rawPayload, mappings);
    }

    private void ImportOfficialPublicKey(RSA rsa)
    {
        var pem = configuration["FullWorthCloud:KnowledgePackPublicKeyPem"];
        if (!string.IsNullOrWhiteSpace(pem))
        {
            rsa.ImportFromPem(pem);
            return;
        }

        var base64 = configuration["FullWorthCloud:KnowledgePackPublicKeyBase64"];
        if (string.IsNullOrWhiteSpace(base64))
            throw new KnowledgePackVerificationException("knowledge_pack_signing_key_unavailable");
        var der = Convert.FromBase64String(base64.Trim());
        rsa.ImportSubjectPublicKeyInfo(der, out var bytesRead);
        if (bytesRead != der.Length)
            throw new CryptographicException("Trailing bytes after SubjectPublicKeyInfo.");
    }

    private static void ValidateEnvelope(KnowledgePackManifest manifest, KnowledgePackPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.PackId) ||
            !string.Equals(manifest.PackId, payload.PackId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, payload.Version, StringComparison.Ordinal) ||
            !string.Equals(manifest.SchemaVersion, payload.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(NormalizeRegion(manifest.Region), NormalizeRegion(payload.Region), StringComparison.Ordinal))
            throw new KnowledgePackVerificationException("knowledge_pack_manifest_payload_mismatch");

        if (payload.Merchants is null || payload.Merchants.Count > KnowledgePackPolicy.MaximumMerchantMappings)
            throw new KnowledgePackVerificationException("knowledge_pack_mapping_limit_exceeded");
    }

    private static IReadOnlyList<OfficialMerchantMapping> ValidateAndProjectMappings(KnowledgePackPayload payload)
    {
        var result = new List<OfficialMerchantMapping>(payload.Merchants.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in payload.Merchants)
        {
            var alias = MerchantNormalization.Normalize(source.AliasKey);
            var canonicalName = source.CanonicalName?.Trim();
            var canonicalKey = MerchantNormalization.Normalize(source.CanonicalMerchantKey);
            var direction = NormalizeDirection(source.Direction);
            var country = NormalizeCountry(source.Country);
            var category = NormalizeNullable(source.CategoryKey, 180);
            var domain = NormalizeNullable(source.Domain, 255)?.ToLowerInvariant();
            var logoKey = NormalizeNullable(source.LogoKey, 180);

            if (alias is null || alias.Length > 300 || canonicalKey is null || canonicalKey.Length > 180 ||
                string.IsNullOrWhiteSpace(canonicalName) || canonicalName.Length > 240 ||
                source.Confidence is < 0m or > 1m)
                throw new KnowledgePackVerificationException("knowledge_pack_mapping_invalid");

            var uniqueKey = $"{alias}\u001f{direction}\u001f{country ?? string.Empty}";
            if (!unique.Add(uniqueKey))
                throw new KnowledgePackVerificationException("knowledge_pack_mapping_duplicate");

            result.Add(new OfficialMerchantMapping
            {
                PackId = payload.PackId,
                PackVersion = payload.Version,
                AliasKey = alias,
                Direction = direction,
                CanonicalMerchantKey = canonicalKey,
                CanonicalName = canonicalName,
                CategoryKey = category,
                Country = country,
                Confidence = source.Confidence,
                Domain = domain,
                LogoKey = logoKey
            });
        }
        return result;
    }

    private static string NormalizeSha256(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.StartsWith("sha256:", StringComparison.Ordinal)) normalized = normalized[7..];
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new KnowledgePackVerificationException("knowledge_pack_hash_invalid");
        return normalized;
    }

    private static string NormalizeDirection(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "any" : value.Trim().ToLowerInvariant();
        return normalized is "any" or "income" or "expense"
            ? normalized
            : throw new KnowledgePackVerificationException("knowledge_pack_mapping_invalid_direction");
    }

    private static string NormalizeRegion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "GLOBAL" : value.Trim().ToUpperInvariant();

    private static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length is < 2 or > 8)
            throw new KnowledgePackVerificationException("knowledge_pack_mapping_invalid_country");
        return normalized;
    }

    private static string? NormalizeNullable(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw new KnowledgePackVerificationException("knowledge_pack_mapping_invalid");
        return normalized;
    }
}
