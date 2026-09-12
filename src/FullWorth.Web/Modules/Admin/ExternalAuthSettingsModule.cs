using FullWorth.Web.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Web.Modules.Admin;

/// <summary>
/// Which external sign-in providers this installation offers, stored rather than deployed.
///
/// Google and Apple used to be six lines in the deploy stack's compose file, threaded through six
/// environment variables. That is the wrong place for them twice over: an operator had to edit YAML and
/// restart the whole stack to turn on a login button, and a compose file ended up carrying credentials
/// that say nothing about how the containers are wired.
///
/// Configuration still wins, so anyone who prefers environment variables keeps them — this only adds a
/// way that does not need a restart.
///
/// The two secrets are encrypted at rest with the app's data-protection key, the same way the Enable
/// Banking private key and the AI credentials already are. They are never sent back to a browser: the
/// admin surface answers "configured" plus a short fingerprint, never the value.
/// </summary>
public sealed class ExternalAuthSettings
{
    /// <summary>There is exactly one row. The scope key makes that explicit and unique-indexable.</summary>
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScopeKey { get; set; } = InstanceScopeKey;

    public string GoogleClientId { get; set; } = string.Empty;
    public string GoogleClientSecretProtected { get; set; } = string.Empty;

    public string AppleServiceId { get; set; } = string.Empty;
    public string AppleTeamId { get; set; } = string.Empty;
    public string ApplePrivateKeyId { get; set; } = string.Empty;
    public string ApplePrivateKeyProtected { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// What the admin surface shows. Ids are not secrets and are shown; the two secrets are reported only as
/// "is one stored", because a value that has been saved has no reason to travel back to a browser.
/// </summary>
public sealed record ExternalAuthSettingsView(
    string GoogleClientId,
    bool GoogleClientSecretStored,
    string AppleServiceId,
    string AppleTeamId,
    string ApplePrivateKeyId,
    bool ApplePrivateKeyStored);

/// <summary>
/// What the admin surface sends. A null secret means "leave the stored one alone" — otherwise saving the
/// client id would silently wipe the secret every time, since the form never has it to send back.
/// </summary>
public sealed record ExternalAuthSettingsWrite(
    string? GoogleClientId,
    string? GoogleClientSecret,
    string? AppleServiceId,
    string? AppleTeamId,
    string? ApplePrivateKeyId,
    string? ApplePrivateKey);

/// <summary>The resolved values a provider needs, or null when this installation has none.</summary>
public sealed record GoogleCredentials(string ClientId, string ClientSecret);

public sealed record AppleCredentials(string ServiceId, string TeamId, string PrivateKeyId, string PrivateKey);

public sealed class ExternalAuthSettingsStore(AuthDbContext db, IDataProtectionProvider protection)
{
    // Named, not derived from the type: the purpose string is part of the ciphertext, so renaming the
    // class must never quietly make every stored secret undecryptable.
    private IDataProtector Protector => protection.CreateProtector("FullWorth.Web.ExternalAuthSettings.v1");

    public async Task<ExternalAuthSettings?> GetAsync(CancellationToken ct) =>
        await db.Set<ExternalAuthSettings>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ScopeKey == ExternalAuthSettings.InstanceScopeKey, ct);

    public async Task<ExternalAuthSettingsView> GetViewAsync(CancellationToken ct)
    {
        var row = await GetAsync(ct);
        return new ExternalAuthSettingsView(
            row?.GoogleClientId ?? string.Empty,
            !string.IsNullOrEmpty(row?.GoogleClientSecretProtected),
            row?.AppleServiceId ?? string.Empty,
            row?.AppleTeamId ?? string.Empty,
            row?.ApplePrivateKeyId ?? string.Empty,
            !string.IsNullOrEmpty(row?.ApplePrivateKeyProtected));
    }

    public async Task<ExternalAuthSettingsView> SetAsync(ExternalAuthSettingsWrite request, CancellationToken ct)
    {
        var row = await db.Set<ExternalAuthSettings>()
            .SingleOrDefaultAsync(x => x.ScopeKey == ExternalAuthSettings.InstanceScopeKey, ct);
        if (row is null)
        {
            row = new ExternalAuthSettings { ScopeKey = ExternalAuthSettings.InstanceScopeKey };
            db.Add(row);
        }

        row.GoogleClientId = Trim(request.GoogleClientId, 200);
        row.AppleServiceId = Trim(request.AppleServiceId, 200);
        row.AppleTeamId = Trim(request.AppleTeamId, 64);
        row.ApplePrivateKeyId = Trim(request.ApplePrivateKeyId, 64);

        // Null leaves the stored secret alone; an empty string is an explicit "remove it".
        if (request.GoogleClientSecret is not null)
            row.GoogleClientSecretProtected = ProtectOrClear(request.GoogleClientSecret);
        if (request.ApplePrivateKey is not null)
            row.ApplePrivateKeyProtected = ProtectOrClear(request.ApplePrivateKey);

        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return await GetViewAsync(ct);
    }

    /// <summary>Stored credentials, or null when this installation has not configured the provider.</summary>
    public async Task<GoogleCredentials?> GetGoogleAsync(CancellationToken ct)
    {
        var row = await GetAsync(ct);
        var secret = Unprotect(row?.GoogleClientSecretProtected);
        return string.IsNullOrWhiteSpace(row?.GoogleClientId) || secret is null
            ? null
            : new GoogleCredentials(row.GoogleClientId, secret);
    }

    /// <inheritdoc cref="GetGoogleAsync"/>
    public async Task<AppleCredentials?> GetAppleAsync(CancellationToken ct)
    {
        var row = await GetAsync(ct);
        var key = Unprotect(row?.ApplePrivateKeyProtected);
        return row is null
            || string.IsNullOrWhiteSpace(row.AppleServiceId)
            || string.IsNullOrWhiteSpace(row.AppleTeamId)
            || string.IsNullOrWhiteSpace(row.ApplePrivateKeyId)
            || key is null
            ? null
            : new AppleCredentials(row.AppleServiceId, row.AppleTeamId, row.ApplePrivateKeyId, key);
    }

    private string ProtectOrClear(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Protector.Protect(value.Trim());

    /// <summary>
    /// Null rather than an exception when the ciphertext cannot be read. That happens for one real
    /// reason — the data-protection key ring was lost — and the honest consequence is "this provider is
    /// not configured", not a login page that returns 500 for everybody.
    /// </summary>
    private string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return null;
        try { return Protector.Unprotect(protectedValue); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private static string Trim(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
