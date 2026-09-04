using System.Security.Cryptography;
using System.Text.Json;
using FullWorth.Backend.Modules.Intelligence;
using Microsoft.Extensions.Configuration;

namespace FullWorth.Backend.Tests.Intelligence;

public sealed class KnowledgePackVerifierTests
{
    [Fact]
    public void Valid_signed_pack_is_verified_and_normalized()
    {
        using var rsa = RSA.Create(2048);
        var payload = Payload("1.0.0");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var verifier = CreateVerifier(rsa);
        var manifest = Manifest(rsa, payload, bytes);

        var verified = verifier.Verify(manifest, bytes);

        var mapping = Assert.Single(verified.MerchantMappings);
        Assert.Equal("AMZN MKTP DE", mapping.AliasKey);
        Assert.Equal("AMAZON", mapping.CanonicalMerchantKey);
        Assert.Equal("expense", mapping.Direction);
        Assert.Equal("DE", mapping.Country);
        Assert.Equal("shopping.online", mapping.CategoryKey);
    }

    [Fact]
    public void Tampered_payload_is_rejected_before_installation()
    {
        using var rsa = RSA.Create(2048);
        var payload = Payload("1.0.0");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var verifier = CreateVerifier(rsa);
        var manifest = Manifest(rsa, payload, bytes);
        var tampered = bytes.ToArray();
        tampered[^2] ^= 0x01;

        var ex = Assert.Throws<KnowledgePackVerificationException>(() => verifier.Verify(manifest, tampered));

        Assert.Equal("knowledge_pack_hash_mismatch", ex.ErrorCode);
    }

    [Fact]
    public void Wrong_signature_is_rejected_even_when_hash_matches()
    {
        using var rsa = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        var payload = Payload("1.0.0");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var verifier = CreateVerifier(rsa);
        var manifest = Manifest(attacker, payload, bytes);

        var ex = Assert.Throws<KnowledgePackVerificationException>(() => verifier.Verify(manifest, bytes));

        Assert.Equal("knowledge_pack_signature_invalid", ex.ErrorCode);
    }

    private static KnowledgePackPayload Payload(string version) => new(
        "merchant-de",
        version,
        KnowledgePackPolicy.CurrentSchemaVersion,
        "DE",
        [new KnowledgePackMerchantPayload(
            "amzn mktp de",
            "expense",
            "amazon",
            "Amazon",
            "shopping.online",
            "de",
            0.97m,
            "amazon.de",
            "amazon")]);

    private static KnowledgePackManifest Manifest(RSA signer, KnowledgePackPayload payload, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var signature = Convert.ToBase64String(signer.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return new(
            payload.PackId,
            payload.Version,
            payload.SchemaVersion,
            payload.Region,
            hash,
            KnowledgePackVerifier.SupportedSignatureAlgorithm,
            signature,
            null);
    }

    private static KnowledgePackVerifier CreateVerifier(RSA rsa)
    {
        var key = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FullWorthCloud:KnowledgePackPublicKeyBase64"] = key
            })
            .Build();
        return new KnowledgePackVerifier(configuration);
    }
}
