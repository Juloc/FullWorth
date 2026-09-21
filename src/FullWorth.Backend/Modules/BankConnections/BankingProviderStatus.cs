namespace FullWorth.Backend.Modules.BankConnections;

/// <summary>
/// Der zuletzt bekannte Gesundheitszustand eines Instituts bei Enable Banking (#165).
///
/// Warum das ueberhaupt in der Datenbank liegt: bis hierher holte
/// <c>GET /api/banking/provider-status</c> den Zustand bei jedem Aufruf live aus dem
/// Enable-Banking-Control-Panel - inklusive Token-Erneuerung und einem zweiten Anlauf bei 401. Der
/// Bankdialog wartete darauf, und wenn das Control Panel langsam oder aus war, war die Bankauswahl
/// langsam oder unbenutzbar. Eine Gesundheitsangabe darf aber nicht darueber entscheiden, ob man eine
/// Bank ueberhaupt auswaehlen kann.
///
/// Der Zustand selbst ist NICHT nutzerspezifisch: das Control Panel liefert denselben Feed fuer die
/// ganze Installation, nur die Zugangsdaten zum Abruf gehoeren einem Nutzer. Deshalb gibt es hier
/// keine Nutzer- und keine Space-Spalte - zwei Nutzer derselben Installation lesen dieselbe Zeile.
/// </summary>
public sealed class BankingProviderStatus
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>ISO-Laendercode, immer zweistellig und gross.</summary>
    public string Country { get; set; } = string.Empty;

    public string Brand { get; set; } = string.Empty;

    /// <summary>Privat- oder Geschaeftskunde - dasselbe Institut kann je Typ anders dastehen.</summary>
    public string PsuType { get; set; } = string.Empty;

    /// <summary>Wie der Anbieter es nennt. Bewusst nicht in eine eigene Aufzaehlung gezwungen:
    /// lernt der Feed einen neuen Zustand, soll er durchkommen und nicht in "unbekannt" fallen.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Der Zustand der Aktualisierung selbst - einmal pro Installation, nicht pro Institut.
///
/// Der Abruf holt den ganzen Feed in einem Zug (<c>/api/get_today_stats</c> kennt keinen
/// Laenderfilter, gefiltert wird erst danach), also gibt es genau einen Zeitpunkt und genau einen
/// Fehlerzustand fuer alle Zeilen. Sie je Institut zu wiederholen waere dieselbe Angabe hundertfach.
///
/// Drei Zeitpunkte statt einem, und das ist der Punkt: ein fehlgeschlagener Versuch darf den letzten
/// erfolgreichen Stand nicht ueberschreiben. Sonst wuerde ein kurzer Ausfall des Control Panels aus
/// "alle Banken erreichbar" ein "Zustand unbekannt" machen, obwohl sich an den Banken nichts geaendert
/// hat. <see cref="LastAttemptAt"/> sagt, wann es zuletzt versucht wurde,
/// <see cref="LastSuccessfulAt"/>, wie alt die Angaben wirklich sind.
/// </summary>
public sealed class BankingProviderStatusRefresh
{
    public const string InstanceScopeKey = "instance";

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Genau eine Zeile, durch einen eindeutigen Index erzwungen statt angenommen.</summary>
    public string ScopeKey { get; set; } = InstanceScopeKey;

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? LastSuccessfulAt { get; set; }

    /// <summary>
    /// Ein bereinigter Grund, nie eine Anbieterantwort im Rohzustand: hier landet ein kurzer,
    /// bekannter Schluessel wie <c>control_panel_login_expired</c>. Token, Kopfzeilen und
    /// Antwortkoerper des Control Panels haben in der Datenbank nichts zu suchen.
    /// </summary>
    public string? LastError { get; set; }
}
