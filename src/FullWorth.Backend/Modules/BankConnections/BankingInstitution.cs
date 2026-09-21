namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Ein Institut aus dem Enable-Banking-Katalog, lokal gehalten (#169).
///
/// Warum ueberhaupt lokal: <c>GET /api/banking/institutions</c> rief den ASPSP-Katalog bei jedem
/// Oeffnen des Bankdialogs live ab. Damit hing die Bankauswahl an der Erreichbarkeit und Latenz eines
/// Fremdsystems - fuer einen Katalog, der sich selten aendert, der falsche Preis. Nach #165 war der
/// Gesundheitszustand lokal, die eigentliche Liste aber weiterhin nicht; genau das sagt #169 auch.
///
/// Was hier Spalte ist und was nicht, ist eine bewusste Grenze. Land, Name, Gruppe, Logo,
/// Beta-Kennzeichen und PSU-Typen sind fachliche Angaben: danach wird gesucht, sortiert, gefiltert
/// und angezeigt. <see cref="AuthMethodsJson"/> ist es nicht - das sind Protokollangaben des
/// Anbieters (welche Anmeldeverfahren es gibt, welche Felder sie brauchen, welche verborgen sind),
/// verschachtelt, vom Anbieter definiert und in der Oberflaeche selbst nur durchgereicht. Dafuer ein
/// Schema zu erfinden hiesse, eine fremde Protokollform nachzubauen und bei jeder Anbieteraenderung
/// nachzuziehen. Das Issue verbietet rohes Provider-JSON als zweite unstrukturierte WAHRHEIT - die
/// fachlichen Felder stehen deshalb als Spalten da; durchgereicht wird nur, was keine ist.
/// </summary>
public sealed class BankingInstitution
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Country { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Die Unterscheidung, die der Anbieter selbst macht: dieselbe Bank kann als getrennter
    /// Privat- und Geschaeftseintrag kommen. Sortiert und verbunden gespeichert, damit aus derselben
    /// Antwort immer derselbe Schluessel entsteht - sonst legt eine andere Reihenfolge im Feed
    /// dieselbe Bank ein zweites Mal an.
    /// </summary>
    public string PsuTypesKey { get; set; } = string.Empty;

    /// <summary>Die PSU-Typen dieses Eintrags, in der Form, in der die Oberflaeche sie erwartet.</summary>
    public string PsuTypesJson { get; set; } = "[]";

    /// <summary>Bankengruppe, soweit der Anbieter eine nennt. Kann bei ihm Text oder Objekt sein.</summary>
    public string? GroupJson { get; set; }

    public string? LogoUrl { get; set; }

    public bool Beta { get; set; }

    /// <summary>Protokollangaben des Anbieters - siehe die Begruendung am Typ.</summary>
    public string AuthMethodsJson { get; set; } = "[]";

    /// <summary>
    /// Wann dieses Institut zuletzt im Feed stand. Zusammen mit <see cref="IsActive"/> die Antwort
    /// auf "zuletzt gesehen / entfernt" aus dem Issue.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Falsch, sobald der Anbieter das Institut nicht mehr meldet. Bewusst stillgelegt statt
    /// geloescht: eine bestehende Verbindung zeigt weiterhin auf diesen Namen, und eine Bank, die der
    /// Anbieter fuer einen Durchlauf vergisst, soll nicht aus der Geschichte verschwinden.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Der Zustand der Katalogaktualisierung - einmal je Land, weil der Anbieter je Land abgefragt wird
/// (<c>GET /aspsps?country=XX</c>), anders als beim Gesundheitsfeed aus #165.
///
/// Dieselbe Regel wie dort: ein fehlgeschlagener Versuch ueberschreibt den letzten erfolgreichen
/// Stand nicht. Ein Katalog, der bei jedem Anbieterausfall leer waere, haette denselben Fehler wie
/// der Live-Abruf davor.
/// </summary>
public sealed class BankingInstitutionRefresh
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Country { get; set; } = string.Empty;

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? LastSuccessfulAt { get; set; }

    /// <summary>Ein bereinigter Schluessel, nie eine Anbieterantwort im Rohzustand.</summary>
    public string? LastError { get; set; }
}
