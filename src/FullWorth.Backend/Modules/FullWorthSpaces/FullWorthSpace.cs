namespace FullWorth.Backend.Modules.FullWorthSpaces;

public sealed class FullWorthSpace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string BaseCurrency { get; set; } = "EUR";

    /// <summary>
    /// In welcher Sprache die Standardkategorien angelegt wurden - und wann. Das Datum ist der
    /// eigentliche Punkt: solange es fehlt, saet der Start die fehlenden Standardschluessel nach,
    /// danach nie wieder. Ohne diesen Marker kam eine geloeschte Standardkategorie beim naechsten
    /// Neustart zurueck, und zwar in der Sprache, in der einmal gesaet wurde (#117).
    /// </summary>
    public string? DefaultCategoryLanguage { get; set; }
    public DateTimeOffset? DefaultCategoriesSeededAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<FullWorthSpaceMember> Members { get; set; } = [];
}

public sealed class FullWorthSpaceMember
{
    public Guid FullWorthSpaceId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = FullWorthSpaceRoles.Member;
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    public FullWorthSpace FullWorthSpace { get; set; } = null!;
}

public static class FullWorthSpaceRoles
{
    public const string Owner = "owner";
    public const string Member = "member";

    public static bool IsValid(string? role) => role is Owner or Member;
}
