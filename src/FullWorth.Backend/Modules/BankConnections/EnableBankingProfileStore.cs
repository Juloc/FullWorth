using System.Text.Json;
using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// One Bring-Your-Own Enable Banking application per FullWorth user. The RSA private key and optional
/// Control Panel refresh token are encrypted with FieldCipher and are only returned through the
/// ingest-key-protected internal banking API.
/// </summary>

public sealed class EnableBankingProfileStore(FullWorthDbContext db, FieldCipher cipher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EnableBankingProfileInternalDto?> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) return null;
        var entity = await db.EnableBankingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId, ct);
        return entity is null ? null : ToInternal(entity);
    }

    public async Task<EnableBankingProfileInternalDto?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) return null;
        var entity = await db.EnableBankingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return entity is null ? null : ToInternal(entity);
    }

    /// <summary>
    /// Wessen Control-Panel-Zugang der Hintergrunddienst fuer die Statusaktualisierung benutzen darf
    /// (#165).
    ///
    /// Der Gesundheitsfeed gilt fuer die ganze Installation, die Zugangsdaten dafuer gehoeren aber
    /// einem Nutzer - der Dienst braucht also einen. Bewusst nur DIE ID und bewusst keine Liste: wer
    /// Control-Panel-Zugang hat, ist nichts, was ein Maschinenpfad aufzaehlen muss, und das Token
    /// selbst verlaesst diesen Prozess ohnehin nie.
    ///
    /// Aeltestes Profil zuerst, damit die Wahl stabil ist: ein Dienst, der bei jedem Durchlauf einen
    /// anderen Zugang nimmt, verteilt seine Fehler ueber alle Nutzer statt sie an einem sichtbar zu
    /// machen.
    /// </summary>
    /// <summary>
    /// Wessen Enable-Banking-Profil der Katalogdienst benutzen darf (#169).
    ///
    /// Anderer Zugang als bei <see cref="FindControlPanelPrincipalAsync"/> und deshalb eine eigene
    /// Abfrage: der Institutionenkatalog kommt ueber die Anwendungs-Zugangsdaten des Profils
    /// (<c>/aspsps</c>), der Gesundheitsfeed ueber das Control-Panel-Token. Ein Haus kann das eine
    /// haben und das andere nicht - beides in eine Abfrage mit Schalter zu legen hiesse, dass ein
    /// fehlendes Control-Panel-Token auch den Katalog verhindert.
    /// </summary>
    public async Task<Guid?> FindEnableBankingPrincipalAsync(CancellationToken ct) =>
        await db.EnableBankingProfiles.AsNoTracking()
            .Where(profile => profile.Active
                && db.Users.Any(user => user.Id == profile.UserId && user.IsActive))
            .OrderBy(profile => profile.Id)
            .Select(profile => (Guid?)profile.UserId)
            .FirstOrDefaultAsync(ct);

    public async Task<Guid?> FindControlPanelPrincipalAsync(CancellationToken ct) =>
        await db.EnableBankingProfiles.AsNoTracking()
            .Where(profile => profile.Active
                && profile.ControlPanelRefreshToken != null
                && profile.ControlPanelRefreshToken != string.Empty
                && db.Users.Any(user => user.Id == profile.UserId && user.IsActive))
            .OrderBy(profile => profile.Id)
            .Select(profile => (Guid?)profile.UserId)
            .FirstOrDefaultAsync(ct);

    public async Task<EnableBankingProfileInternalDto> UpsertVerifiedAsync(EnableBankingProfileWrite request, CancellationToken ct)
    {
        if (request.UserId == Guid.Empty) throw new ArgumentException("UserId is required.");
        if (string.IsNullOrWhiteSpace(request.ApplicationId)) throw new ArgumentException("ApplicationId is required.");
        if (string.IsNullOrWhiteSpace(request.PrivateKeyPem)) throw new ArgumentException("PrivateKeyPem is required.");
        if (!await db.Users.AsNoTracking().AnyAsync(x => x.Id == request.UserId && x.IsActive, ct))
            throw new ArgumentException("User does not exist or is inactive.");

        var entity = await db.EnableBankingProfiles.SingleOrDefaultAsync(x => x.UserId == request.UserId, ct);
        if (entity is null)
        {
            entity = new EnableBankingProfile { UserId = request.UserId };
            db.EnableBankingProfiles.Add(entity);
        }

        entity.ApplicationId = request.ApplicationId.Trim();
        entity.PrivateKeyPem = cipher.Protect(request.PrivateKeyPem) ?? throw new InvalidOperationException("Failed to protect Enable Banking private key.");
        if (request.ControlPanelRefreshToken is not null)
            entity.ControlPanelRefreshToken = cipher.Protect(request.ControlPanelRefreshToken);
        entity.KeyFingerprint = request.KeyFingerprint;
        entity.Environment = request.Environment.ToUpperInvariant();
        entity.ApplicationName = request.ApplicationName;
        entity.Active = request.Active;
        entity.ServicesJson = JsonSerializer.Serialize(request.Services.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), JsonOptions);
        entity.RedirectUrlsJson = JsonSerializer.Serialize(request.RedirectUrls.Distinct(StringComparer.Ordinal).ToArray(), JsonOptions);
        entity.VerifiedAt = request.VerifiedAt;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        db.Entry(entity).State = EntityState.Detached;
        return ToInternal(entity);
    }

    public async Task<EnableBankingProfileDeleteResult> DeleteForUserAsync(Guid userId, CancellationToken ct)
    {
        var entity = await db.EnableBankingProfiles.SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (entity is null) return EnableBankingProfileDeleteResult.NotFound;

        var referenced = await db.BankConnections
            .Where(x => x.EnableBankingProfileId == entity.Id)
            .Select(x => new { x.Id, x.Status })
            .ToListAsync(ct);
        if (referenced.Any(x => !string.Equals(x.Status, "CLOSED", StringComparison.OrdinalIgnoreCase)))
            return EnableBankingProfileDeleteResult.InUse;

        // CLOSED connections may intentionally retain imported history. They no longer need provider
        // credentials, so detach them before deleting the encrypted BYO profile.
        if (referenced.Count > 0)
            await db.BankConnections
                .Where(x => x.EnableBankingProfileId == entity.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.EnableBankingProfileId, (Guid?)null), ct);

        db.EnableBankingProfiles.Remove(entity);
        await db.SaveChangesAsync(ct);
        return EnableBankingProfileDeleteResult.Deleted;
    }

    private EnableBankingProfileInternalDto ToInternal(EnableBankingProfile entity) => new(
        entity.Id,
        entity.UserId,
        entity.ApplicationId,
        cipher.Unprotect(entity.PrivateKeyPem) ?? string.Empty,
        entity.KeyFingerprint,
        entity.Environment,
        entity.ApplicationName,
        entity.Active,
        Deserialize(entity.ServicesJson),
        Deserialize(entity.RedirectUrlsJson),
        entity.VerifiedAt,
        entity.UpdatedAt,
        cipher.Unprotect(entity.ControlPanelRefreshToken));

    private static IReadOnlyList<string> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }
}
