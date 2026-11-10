using FullWorth.Backend.Data;
using FullWorth.Backend.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPush;

namespace FullWorth.Backend.Modules.Push;

/// <summary>Das VAPID-Schluesselpaar dieser Installation: der oeffentliche Teil geht an die Browser, der private signiert.</summary>
public sealed record VapidKeys(string PublicKey, string PrivateKey);

/// <summary>
/// Woher das Schluesselpaar fuer Web Push kommt - ohne dass jemand eines erzeugen und irgendwo ablegen muss.
///
/// Steht eines in der Konfiguration (<c>Push:VapidPublicKey</c> / <c>Push:VapidPrivateKey</c>), gilt es:
/// wer ein eigenes mitbringt, behaelt die Hand darauf. Sonst erzeugt die Installation beim ersten Bedarf
/// selbst eines und legt es in der Datenbank ab, den privaten Teil mit dem Datenschluessel verschluesselt
/// (wie jedes andere Geheimnis dort). So steht nichts davon in einer Compose-Datei, einem Repository oder
/// einem Chat, und es ueberlebt jede Aktualisierung - ein neues Paar wuerde alle Abos ungueltig machen,
/// weil der Browser sich an den oeffentlichen Teil bindet.
/// </summary>
public sealed class PushKeyStore(FullWorthDbContext db, IOptions<PushOptions> options, FieldCipher cipher)
{
    public async Task<VapidKeys> GetAsync(CancellationToken ct)
    {
        var configured = options.Value;
        if (configured.IsConfigured) return new VapidKeys(configured.VapidPublicKey, configured.VapidPrivateKey);

        var row = await FindAsync(ct);
        if (row is null)
        {
            var generated = VapidHelper.GenerateVapidKeys();
            row = new PushInstanceKey
            {
                PublicKey = generated.PublicKey,
                PrivateKeyProtected = cipher.Protect(generated.PrivateKey) ?? string.Empty
            };
            db.PushInstanceKeys.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Zwei Anfragen haben gleichzeitig eines erzeugt, der eindeutige Index hat die zweite
                // abgewiesen: dann gilt das der ersten.
                db.Entry(row).State = EntityState.Detached;
                row = await FindAsync(ct) ?? throw new InvalidOperationException("The push key could not be created.");
            }
        }

        return new VapidKeys(row.PublicKey, cipher.Unprotect(row.PrivateKeyProtected) ?? string.Empty);
    }

    private Task<PushInstanceKey?> FindAsync(CancellationToken ct) =>
        db.PushInstanceKeys.AsNoTracking().SingleOrDefaultAsync(x => x.ScopeKey == PushInstanceKey.InstanceScopeKey, ct);
}
