using FullWorth.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Admin;

/// <summary>What the inventory says about one entry. Never a value.</summary>
public sealed record VaultEntryView(
    string Reference,
    string Group,
    string Label,
    string Description,
    bool Stored,
    bool RequiresFreshFactor,
    string? Hint);

public sealed record VaultInventoryView(
    IReadOnlyList<VaultEntryView> Entries,
    string Factor,
    bool Elevated,
    int RevealsLeft,
    DateTimeOffset? ElevatedUntil);

public sealed record VaultRevealResult(bool Found, string? Value, string? Error);

/// <summary>
/// Reads the secrets this host holds, once an <see cref="AdminElevation"/> says it may.
///
/// It runs in FullWorth.Web on purpose. Program.cs already loads every infrastructure secret into this
/// host's configuration, and the sign-in provider settings are a Web-local store with decrypting
/// methods of its own — so the infrastructure half needs no cross-process call at all.
///
/// Every reveal is recorded twice: a row in the audit table, and a line in the container log. The log
/// matters precisely because it survives whoever holds the database password, which is one of the
/// things this service can hand out.
/// </summary>
public sealed class AdminVaultService(
    AuthDbContext db,
    IConfiguration configuration,
    ExternalAuthSettingsStore externalAuth,
    AdminElevationService elevations,
    ILogger<AdminVaultService> logger,
    TimeProvider clock)
{
    public async Task<VaultInventoryView> InventoryAsync(
        Guid authUserId, Guid sessionId, bool twoFactorEnabled, CancellationToken ct)
    {
        var settings = await externalAuth.GetAsync(ct);

        var entries = new List<VaultEntryView>(AdminVaultCatalogue.All.Count);
        foreach (var descriptor in AdminVaultCatalogue.All)
        {
            var stored = descriptor.Source switch
            {
                VaultSource.Configuration =>
                    AdminVaultCatalogue.ResolveValue(descriptor, configuration) is not null,
                VaultSource.ExternalAuth => descriptor.Reference switch
                {
                    "signin.google-client-secret" =>
                        !string.IsNullOrWhiteSpace(settings?.GoogleClientSecretProtected),
                    "signin.apple-private-key" =>
                        !string.IsNullOrWhiteSpace(settings?.ApplePrivateKeyProtected),
                    _ => false
                },
                _ => false
            };

            entries.Add(new VaultEntryView(
                descriptor.Reference,
                descriptor.Group,
                descriptor.Label,
                descriptor.Description,
                stored,
                descriptor.RequiresFreshFactor,
                // A hint, not a fingerprint. Even four characters of a 32-byte key is four characters
                // an attacker does not have to guess, and it is shown to anyone who reaches this page.
                stored ? null : "nicht gesetzt"));
        }

        // consume: false - looking at the list is not looking at a secret.
        var elevation = await elevations.CurrentAsync(authUserId, sessionId, consume: false, ct);

        return new VaultInventoryView(
            entries,
            twoFactorEnabled ? "totp" : "password",
            elevation is not null,
            elevation is null ? 0 : AdminElevation.RevealBudget - elevation.RevealsUsed,
            elevation?.ExpiresAt);
    }

    /// <summary>
    /// Hands out exactly one value. One per call and no batch endpoint: a batch endpoint is a single
    /// request that exports the installation.
    /// </summary>
    public async Task<VaultRevealResult> RevealAsync(
        Guid authUserId, string actorEmail, string? reference, CancellationToken ct)
    {
        var descriptor = AdminVaultCatalogue.Find(reference);
        if (descriptor is null) return new(false, null, "unknown_reference");

        var value = descriptor.Source switch
        {
            VaultSource.Configuration => AdminVaultCatalogue.ResolveValue(descriptor, configuration),
            VaultSource.ExternalAuth => await ReadExternalAuthAsync(descriptor.Reference, ct),
            _ => null
        };

        // Audited whether or not there was anything there. "Tried to read the data encryption key" is
        // the interesting line, and it does not become less interesting because the key was absent.
        db.Add(new AdminAuditEvent
        {
            Id = Guid.NewGuid(),
            OccurredAt = clock.GetUtcNow(),
            ActorAuthUserId = authUserId,
            TargetAuthUserId = authUserId,
            Action = "vault.reveal:" + descriptor.Reference,
            Outcome = string.IsNullOrEmpty(value) ? "not_set" : "success"
        });
        await db.SaveChangesAsync(ct);

        // The reference, never the value, and never a prefix of it.
        logger.LogWarning(
            "Vault: {Actor} revealed {Reference} ({Found}).",
            actorEmail, descriptor.Reference,
            string.IsNullOrEmpty(value) ? "not set" : "value returned");

        return string.IsNullOrEmpty(value)
            ? new(false, null, "not_set")
            : new(true, value, null);
    }

    private async Task<string?> ReadExternalAuthAsync(string reference, CancellationToken ct) =>
        reference switch
        {
            "signin.google-client-secret" => (await externalAuth.GetGoogleAsync(ct))?.ClientSecret,
            "signin.apple-private-key" => (await externalAuth.GetAppleAsync(ct))?.PrivateKey,
            _ => null
        };
}
