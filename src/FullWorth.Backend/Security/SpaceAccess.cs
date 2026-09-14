using FullWorth.Backend.Data;

namespace FullWorth.Backend.Security;

/// <summary>
/// Darf dieser Benutzer diesen Space sehen, darf er ihn ändern, und welche Konten darin gehören ihm?
///
/// Dieselben vier Fragen stellen 30 Dateien, zusammen 98 Mal — und sie stellten sie bis 2026-09-14 als
/// <c>RawSql.IsMemberAsync(db, …)</c>, also mit dem DbContext in der Hand. Genau deshalb hielt ein
/// Endpunkt den Kontext, der sonst gar nichts mit der Datenbank zu tun hat.
///
/// Das ist keine Fachabfrage, sondern eine Autorisierungsprüfung. Sie kommt jetzt injiziert, wie in der
/// Cloud <c>SpaceCapabilities</c>. Die Abfragen selbst sind unverändert; sie liegen weiterhin in
/// <see cref="RawSql"/>, weil auch Stores sie brauchen und ein Store keinen zweiten Dienst holen soll,
/// um eine Mitgliedschaft zu prüfen.
///
/// Siehe #113, Regel 2.
/// </summary>
public sealed class SpaceAccess(FullWorthDbContext db)
{
    /// <summary>Mitglied des Space — die schwächste Stufe, reicht zum Lesen.</summary>
    public Task<bool> IsMemberAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        RawSql.IsMemberAsync(db, userId, fullWorthSpaceId, ct);

    /// <summary>Eigentümer des Space — nötig für alles, was die Einrichtung verändert.</summary>
    public Task<bool> IsOwnerAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        RawSql.IsOwnerAsync(db, userId, fullWorthSpaceId, ct);

    /// <summary>Die Konten, die dieser Benutzer in diesem Space sehen darf.</summary>
    public Task<HashSet<Guid>> VisibleAccountIdsAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        RawSql.VisibleAccountIdsAsync(db, userId, fullWorthSpaceId, ct);

    /// <summary>Die Konten, die er auch ändern darf — eine echte Teilmenge der sichtbaren.</summary>
    public Task<HashSet<Guid>> WritableAccountIdsAsync(Guid userId, Guid fullWorthSpaceId, CancellationToken ct) =>
        RawSql.WritableAccountIdsAsync(db, userId, fullWorthSpaceId, ct);
}
