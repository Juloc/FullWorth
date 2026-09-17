namespace FullWorth.Backend.Modules.Reconciliation;

/// <summary>Was mit dem Snapshot-Trade einer Position zu tun ist, nachdem Buchungen sie mit-erklaeren.</summary>
public enum SnapshotRestAction
{
    /// <summary>Der Snapshot-Trade bleibt, aber nur mit dem Rest, den die Buchungen nicht erklaeren.</summary>
    SetRemainder,

    /// <summary>Die Buchungen erklaeren den gemeldeten Bestand vollstaendig - der Snapshot-Trade entfaellt.</summary>
    Delete
}

/// <summary>Ergebnis von <see cref="SnapshotRestRule.Evaluate"/>.</summary>
public sealed record SnapshotRestResult(SnapshotRestAction Action, decimal Remainder, bool Overexplained);

/// <summary>
/// Verhindert doppelt gezaehlte Stuecke, wenn ein aus Buchungen erkannter Kauf UND der von FinTS
/// gemeldete Gesamtbestand fuer dasselbe Wertpapier nebeneinander stehen wuerden.
///
/// FinTS/MT535 meldet nur den HEUTIGEN Gesamtbestand (kein Kaufdatum, keine Historie) - genau dafuer
/// legt <c>FinTsInvestmentSnapshotStore</c> pro Wertpapier eine <c>security_transfer_in</c>-Zeile an,
/// die bei jeder Synchronisation ueberschrieben wird. Erkennt <c>SecuritiesBookingMatcher</c> aus einer
/// Kontobuchung zusaetzlich einen "buy"-Handel fuer dasselbe Wertpapier, zaehlen beide Zeilen
/// zusammen - der Bestand waere doppelt so gross wie tatsaechlich gehalten.
///
/// Die Regel ist deshalb an ZWEI Stellen anzuwenden (<c>FinTsInvestmentSnapshotStore.UpsertPositionAsync</c>
/// und <c>SecuritiesBookingStore.ApplyAsync</c>), und beide muessen sie IDENTISCH anwenden - daher eine
/// einzige, reine Funktion statt zweier Kopien, die auseinanderlaufen koennten.
///
/// Der "Rest" ist zugleich die Sicherheitsregel des Eigentuemers fuer automatisches Uebernehmen: Erklaeren
/// die Buchungen den gemeldeten Bestand genau (oder mehr), war die Zuordnung richtig - das ist dieselbe
/// Pruefung, nur global statt je Buchung.
/// </summary>
public static class SnapshotRestRule
{
    /// <param name="snapshotQuantity">Die von der Bank fuer dieses Wertpapier gemeldete Gesamtstueckzahl.</param>
    /// <param name="otherTradesQuantity">
    /// Summe der Stueckzahl aller ANDEREN Zugaenge (<c>buy</c>, <c>security_transfer_in</c>) desselben
    /// Wertpapiers in diesem Depot - alles ausser dem Snapshot-Trade selbst.
    /// </param>
    public static SnapshotRestResult Evaluate(decimal snapshotQuantity, decimal otherTradesQuantity)
    {
        var rest = snapshotQuantity - otherTradesQuantity;

        // Rest > 0: die Buchungen erklaeren nur einen Teil - der Snapshot traegt den Rest, damit die
        // Summe weiter dem von der Bank gemeldeten Bestand entspricht.
        if (rest > 0m) return new SnapshotRestResult(SnapshotRestAction.SetRemainder, rest, false);

        // Rest <= 0: die Buchungen erklaeren den Bestand vollstaendig (oder mehr). Eine Zeile mit
        // Quantity<=0 verletzt TR_InvestmentTrades_ValidateLedger und waere ohnehin sinnlos - der
        // Snapshot-Trade entfaellt. Rest < 0 heisst zusaetzlich: mehr wurde als Kauf erkannt, als die
        // Bank heute haelt - vermutlich ein Verkauf, den dieser Slice nicht erkennt. Das wird nie
        // stillschweigend verschluckt, sondern als Ueberdeckung gemeldet.
        return new SnapshotRestResult(SnapshotRestAction.Delete, 0m, rest < 0m);
    }
}
