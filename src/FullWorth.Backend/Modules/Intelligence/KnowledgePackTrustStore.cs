using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Intelligence;

/// <summary>
/// The pack verification key this installation trusts, and the Cloud it trusts it for.
///
/// Obtaining this key used to be an operator task: a shell script copied it out of a Docker volume the
/// Cloud and the app stack shared. That works on exactly one host — the one running both — and for
/// anybody else it worked not at all. A stranger self-hosting FullWorth against the official Cloud could
/// enroll, submit and download packs, and then reject every single one as unverifiable, because there
/// was no way for them to obtain the key at all. The shipped <c>OfficialPublicKeyPem</c> that was meant
/// to cover that case has always been empty.
///
/// So the key is fetched from the Cloud and pinned here on first contact. That is a real trust decision
/// and worth naming: the fetch is authenticated only by TLS to the Cloud endpoint, which outside
/// Development is the official one and not configurable. Whoever controls that endpoint at the moment of
/// the first sync decides which key this installation pins. From then on the pin holds — a later
/// different key is recorded and refused, never silently adopted — so the exposure is one moment, not
/// every sync, and it is strictly better than the status quo of verifying nothing at all.
///
/// An operator who does not want to trust that moment still has the last word: an explicitly configured
/// <c>FullWorthCloud:KnowledgePackPublicKeyPem</c> / <c>…Path</c> / <c>…Base64</c> takes precedence over
/// anything pinned here.
/// </summary>
public sealed class KnowledgePackTrustedKey
{
    /// <summary>
    /// The Cloud origin this key was pinned for. A pin is only ever used for the endpoint it came from,
    /// so pointing an instance at a different Cloud cannot inherit the old Cloud's trust.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Algorithm { get; set; } = string.Empty;
    public string PublicKeyPem { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset PinnedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// A different key the Cloud offered after this one was pinned, kept for the operator to look at.
    /// Recorded, never applied: adopting it automatically would make the pin worth nothing.
    /// </summary>
    public string? OfferedFingerprint { get; set; }
    public DateTimeOffset? OfferedAt { get; set; }
}

public sealed record KnowledgePackTrustView(
    string Endpoint,
    string? Fingerprint,
    DateTimeOffset? PinnedAt,
    string Source,
    string? OfferedFingerprint,
    DateTimeOffset? OfferedAt);

public sealed class KnowledgePackTrustStore(
    IntelligenceDbContext db,
    IFullWorthCloudClient cloud,
    IConfiguration configuration,
    ILogger<KnowledgePackTrustStore> logger)
{
    /// <summary>
    /// The Cloud presented a key that is not the pinned one. Reported instead of a bare signature
    /// failure, because the two need different answers: a bad signature is a broken pack, a changed key
    /// is a decision for whoever runs this installation.
    /// </summary>
    public const string KeyChangedErrorCode = "knowledge_pack_public_key_changed";

    public const string ConfiguredSource = "configured";
    public const string PinnedSource = "pinned";
    public const string NoneSource = "none";

    /// <summary>The Cloud origin in force for this process, normalized so it compares as stored.</summary>
    public string Endpoint => Normalize(cloud.BaseUri);

    /// <summary>
    /// The key to verify with, or null. Configuration wins over the pin: an operator who states a key
    /// has made exactly the decision this store automates, and must not be overruled by it.
    /// </summary>
    public async Task<string?> ResolvePemAsync(CancellationToken ct) =>
        ConfiguredPem(configuration) ?? (await GetAsync(ct))?.PublicKeyPem;

    public async Task<KnowledgePackTrustedKey?> GetAsync(CancellationToken ct)
    {
        var endpoint = Endpoint;
        return await db.KnowledgePackTrustedKeys.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Endpoint == endpoint, ct);
    }

    public async Task<KnowledgePackTrustView> GetViewAsync(CancellationToken ct)
    {
        var pinned = await GetAsync(ct);
        var configured = ConfiguredPem(configuration);
        var source = configured is not null ? ConfiguredSource : pinned is not null ? PinnedSource : NoneSource;
        return new KnowledgePackTrustView(
            Endpoint,
            configured is not null ? FingerprintOf(configured) : pinned?.Fingerprint,
            pinned?.PinnedAt,
            source,
            pinned?.OfferedFingerprint,
            pinned?.OfferedAt);
    }

    /// <summary>
    /// Returns the key to verify with, fetching and pinning one from the Cloud if this installation has
    /// none yet. Never replaces a pin, and never throws: a Cloud that cannot be reached leaves the
    /// installation exactly as it was.
    /// </summary>
    public async Task<string?> EnsurePinnedAsync(string instanceCredential, CancellationToken ct)
    {
        if (ConfiguredPem(configuration) is { } configured) return configured;

        var endpoint = Endpoint;
        var existing = await db.KnowledgePackTrustedKeys.SingleOrDefaultAsync(x => x.Endpoint == endpoint, ct);
        if (existing is not null) return existing.PublicKeyPem;

        var offered = await FetchAsync(instanceCredential, ct);
        if (offered is null) return null;

        db.KnowledgePackTrustedKeys.Add(new KnowledgePackTrustedKey
        {
            Endpoint = endpoint,
            Algorithm = Trim(offered.Algorithm, 40),
            PublicKeyPem = offered.Pem,
            Fingerprint = Trim(offered.Fingerprint, 120),
            PinnedAt = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two sync passes raced for the first pin. The unique index decided; read the winner rather
            // than pinning a second key for the same endpoint.
            db.ChangeTracker.Clear();
            return (await GetAsync(ct))?.PublicKeyPem;
        }

        logger.LogInformation(
            "Pinned the knowledge-pack verification key {Fingerprint} for {Endpoint}.",
            offered.Fingerprint,
            endpoint);
        return offered.Pem;
    }

    /// <summary>
    /// Asks the Cloud which key it signs with now and records it when it differs from the pin. Called
    /// after a signature failure, which is the moment a rotated Cloud key looks exactly like a corrupt
    /// pack — the difference is worth telling an operator about.
    /// </summary>
    /// <returns>True when the Cloud offered a key other than the pinned one.</returns>
    public async Task<bool> NoteOfferedKeyAsync(string instanceCredential, CancellationToken ct)
    {
        var endpoint = Endpoint;
        var pinned = await db.KnowledgePackTrustedKeys.SingleOrDefaultAsync(x => x.Endpoint == endpoint, ct);
        if (pinned is null) return false;

        var offered = await FetchAsync(instanceCredential, ct);
        if (offered is null || string.Equals(offered.Fingerprint, pinned.Fingerprint, StringComparison.Ordinal))
            return false;

        pinned.OfferedFingerprint = Trim(offered.Fingerprint, 120);
        pinned.OfferedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        logger.LogWarning(
            "{Endpoint} now signs knowledge packs with {Offered}, not the pinned {Pinned}. Packs stay " +
            "unverified until an administrator accepts the new key.",
            endpoint,
            offered.Fingerprint,
            pinned.Fingerprint);
        return true;
    }

    /// <summary>
    /// Replaces the pin with the key the Cloud offers now. The one deliberate act in this store: a key
    /// rotation is a decision, so it is made by a person in the admin surface, never by a sync pass.
    /// </summary>
    public async Task<KnowledgePackTrustView> AcceptOfferedKeyAsync(string instanceCredential, CancellationToken ct)
    {
        var offered = await FetchAsync(instanceCredential, ct)
            ?? throw new InvalidOperationException("The Cloud did not offer a knowledge-pack verification key.");

        var endpoint = Endpoint;
        var pinned = await db.KnowledgePackTrustedKeys.SingleOrDefaultAsync(x => x.Endpoint == endpoint, ct);
        if (pinned is null)
        {
            pinned = new KnowledgePackTrustedKey { Endpoint = endpoint };
            db.KnowledgePackTrustedKeys.Add(pinned);
        }

        pinned.Algorithm = Trim(offered.Algorithm, 40);
        pinned.PublicKeyPem = offered.Pem;
        pinned.Fingerprint = Trim(offered.Fingerprint, 120);
        pinned.PinnedAt = DateTimeOffset.UtcNow;
        pinned.OfferedFingerprint = null;
        pinned.OfferedAt = null;
        await db.SaveChangesAsync(ct);

        db.ChangeTracker.Clear();
        return await GetViewAsync(ct);
    }

    private async Task<FullWorthCloudPublicKey?> FetchAsync(string instanceCredential, CancellationToken ct)
    {
        FullWorthCloudPublicKey? offered;
        try
        {
            offered = await cloud.GetKnowledgePackPublicKeyAsync(instanceCredential, ct);
        }
        catch (Exception ex) when (ex is FullWorthCloudException or HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Could not fetch the knowledge-pack verification key from {Endpoint}.", Endpoint);
            return null;
        }

        if (offered is null || string.IsNullOrWhiteSpace(offered.Pem)) return null;

        // Only a key that actually imports is worth storing. Anything else would be pinned once and then
        // fail every verification with "public key invalid" for the life of the installation.
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(offered.Pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            logger.LogWarning("{Endpoint} offered a knowledge-pack key that is not a usable PEM.", Endpoint);
            return null;
        }

        return offered;
    }

    /// <summary>
    /// An explicitly configured key, in the order the sync service has always read it. Kept here so the
    /// pin and the override cannot drift apart on which one wins.
    /// </summary>
    internal static string? ConfiguredPem(IConfiguration configuration)
    {
        var pem = configuration["FullWorthCloud:KnowledgePackPublicKeyPem"];
        if (!string.IsNullOrWhiteSpace(pem))
            return pem.Replace("\\n", Environment.NewLine, StringComparison.Ordinal);

        var path = configuration["FullWorthCloud:KnowledgePackPublicKeyPath"]?.Trim();
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable override: fall through rather than failing the instance outright.
            }
        }

        var encoded = configuration["FullWorthCloud:KnowledgePackPublicKeyBase64"];
        if (!string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
            }
            catch (FormatException)
            {
                // Malformed override: fall through.
            }
        }

        return null;
    }

    /// <summary>Same shape as the Cloud's own fingerprint, so the two are comparable by eye.</summary>
    internal static string? FingerprintOf(string pem)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return "sha256:" + Convert.ToHexString(
                SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Origin only, lower-cased, no trailing slash — the same Cloud must compare equal.</summary>
    internal static string Normalize(Uri baseUri) =>
        baseUri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
