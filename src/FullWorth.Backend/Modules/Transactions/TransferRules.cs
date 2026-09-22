using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Transactions;

/// <summary>
/// Was FullWorth aus einer bestaetigten Umbuchung gelernt hat (#146).
///
/// Bis hierher gab es kein Gedaechtnis: <see cref="TransferDetectionService"/> leitete bei jedem Lauf
/// alles neu aus Betrag, Drei-Tage-Fenster und Kontokennung ab, und eine Bestaetigung des Benutzers
/// hinterliess nichts, was ein spaeterer Lauf haette lesen koennen. Solange ein Fall in die Mechanik
/// passt, faellt das nicht auf - er wird ja jedes Mal wieder gefunden. Es faellt genau dort auf, wo
/// sie nicht greift: eine Gegenbuchung vier Tage spaeter, ein Betrag, der wegen einer Gebuehr nicht
/// exakt entgegengesetzt ist, oder gar keine Gegenbuchung, weil das Zielkonto nicht in FullWorth
/// gefuehrt wird.
///
/// Eine Regel ist deshalb bewusst schmal: sie sagt nur, dass DIESE Gegenpartei auf DIESEM Konto in
/// DIESER Richtung eine Umbuchung ist, und wohin sie geht. Sie ersetzt keine Erkennung, sie ergaenzt
/// sie um das, was der Benutzer bereits entschieden hat.
///
/// <see cref="TargetAccountId"/> ist NULL, wenn das Gegenkonto bewusst nicht in FullWorth gefuehrt
/// wird - das externe Sparkonto, das Bargeld, das Verrechnungskonto. Das ist kein Sonderfall, sondern
/// die zweite Haelfte des Issues: eine solche Buchung bleibt fachlich eine Umbuchung und darf in
/// keiner Auswertung als Konsumausgabe erscheinen.
/// </summary>
[Table("TransferRules")]
[Index(nameof(FullWorthSpaceId), nameof(AccountId), nameof(NormalizedCounterparty), nameof(Direction), IsUnique = true)]
public sealed class TransferRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }

    /// <summary>Das Konto, auf dem die Buchung steht.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Die Gegenpartei, normalisiert wie ueberall sonst.</summary>
    [MaxLength(320)] public string NormalizedCounterparty { get; set; } = string.Empty;

    /// <summary><c>income</c> oder <c>expense</c> - dieselbe Gegenpartei kann beides sein.</summary>
    [MaxLength(16)] public string Direction { get; set; } = string.Empty;

    /// <summary>Das FullWorth-Gegenkonto, oder NULL fuer "extern, kein Gegenkonto".</summary>
    public Guid? TargetAccountId { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Eine Regel, wie die Oberflaeche sie zeigt - mit den Namen statt der Kennungen.</summary>
public sealed record TransferRuleView(
    Guid Id,
    Guid AccountId,
    string AccountName,
    string NormalizedCounterparty,
    string Direction,
    Guid? TargetAccountId,
    string? TargetAccountName,
    DateTimeOffset UpdatedAt);
