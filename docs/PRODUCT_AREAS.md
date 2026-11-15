# Produktteile: Ziel, Begriffe, Verknüpfungen

Stand: Entwurf nach der Fragerunde vom 2026-11-12. **Entschieden** heißt: Julian hat es so gesagt. **Vorschlag** heißt: ein Entwurf zum Prüfen, bis er bestätigt oder geändert ist. Dieses Dokument ergänzt `PRODUCT_DECISIONS.md` (die technischen und rechtlichen Grundentscheidungen für 1.0); es beantwortet die Frage, wozu jeder Teil des Produkts da ist und was er anzeigt.

Wer an einem Produktteil arbeitet, liest zuerst dessen Abschnitt hier. Passt eine Änderung nicht zum Ziel, ändert sich zuerst dieses Dokument, dann der Code.

## Hauptziel (entschieden)

FullWorth soll vier Dinge leisten. Jeder Produktteil dient mindestens einem davon, sonst gehört er nicht ins Produkt:

1. **Überblick**: auf einen Blick sehen, wo das Geld steht, was hereinkommt und was bleibt.
2. **Verhandeln und entscheiden**: mit Zahlen argumentieren (Gehaltsgespräch, Vertrag wechseln oder kündigen, Kauf, Kredit).
3. **Sparen und optimieren**: sehen, wo Geld unnötig weggeht (Preise, Abos, Einkäufe, Steuern).
4. **Vorsorgen und planen**: Prognosen über Jahre (Rente, Vermögen, Kredite).

## Begriffe (entschieden)

Ein Wort bedeutet überall dasselbe. Ein neuer Begriff steht erst hier, dann im Code und in der Oberfläche.

| Begriff | Bedeutung |
|---|---|
| **Netto** | Das, was auf dem Konto angekommen ist (die Auszahlung). Überall, wo nur „Netto“ steht. |
| **Netto-Verdienst** | Das Netto laut Abrechnung, vor bAV-Eigenbeitrag, Firmenwagen-Verrechnung und Zuzahlungen. Kommt nur in der Aufschlüsselung vor und heißt dort immer so. |
| **Brutto** | Das Brutto laut Abrechnung. Im Gehaltsgespräch zählt das Brutto. |
| **Brutto und Netto** | Im Gehalt stehen beide immer nebeneinander. |
| **13. Gehalt** | Urlaubs- und Weihnachtsgeld: je ein halbes Monatsgehalt im Juni und im November. |
| **Bonus** | Alles, was über das 13. Gehalt hinausgeht. Wird nie mit dem 13. Gehalt zusammengezählt, sondern einzeln gezeigt. |
| **Firmenwagen-Vorteil** | Geldwerter Vorteil, kein Geld. Zählt standardmäßig nicht zum Verdienst und lässt sich zuschalten. |

Regel für Aufschlüsselungen: Die Zahl steht vorn und heißt, wie oben. Was sie zusammensetzt, steht eine Ebene tiefer. Dieselbe Zahl bekommt nie an einer anderen Stelle einen anderen Namen.

## Produktteile

Je Teil: **Ziel**, **zeigt**, **verknüpft mit**, **offen**. Alles hier ist ein Vorschlag, bis es bestätigt ist, außer den Verknüpfungen unten.

### Überblick: Start, Für dich, Coach
- Ziel: in zehn Sekunden wissen, wie es steht und was als Nächstes zu tun ist.
- Zeigt: das Netto des Monats, was fest abgeht, was frei bleibt, Auffälligkeiten.
- Verknüpft mit: allen anderen Teilen, ohne selbst Daten zu besitzen.
- Offen: welche drei Zahlen oben stehen müssen.

### Konten und Buchungen
- Ziel: ein vollständiges, richtiges Bild der Kontostände und Bewegungen.
- Zeigt: Saldo je Konto, Buchungen mit Kategorie, Umbuchungen getrennt.
- Verknüpft mit: Belege, Abrechnungen, Verträge (über die Buchung).
- Offen: wie fehlende oder doppelte Buchungen auffallen.

### Käufe, Belege, Händler
- Ziel: wissen, was wirklich gekauft wurde und was es kostet, über Zeit und Händler vergleichbar.
- Zeigt: Positionen mit Packung, Preis je kg, l oder Stück, Kategorie aus dem Supermarkt-Baum, eigene Preisentwicklung.
- Verknüpft mit: Buchung (jeder Beleg gehört zu einer Buchung), Budgets, Gehalt (Verdienst gegen Preise).
- Offen: wann ein Beleg als geprüft gilt, wie Händlernamen zusammenlaufen.

### Gehalt
- Ziel: im Gehaltsgespräch mit Zahlen auftreten und jederzeit wissen, was das Gehalt real wert ist.
- Zeigt: Brutto und Netto nebeneinander, 13. Gehalt und Bonus getrennt, Vertragsstände, Verdienst gegen Preise ab frei wählbarem Monat, Erhöhungen als Schnellwahl.
- Verknüpft mit: Bankeingang (Abrechnung), Vertrag (Stände), Käufe (eigene Preise).
- Offen: die Ausgangslage fürs Gehaltsgespräch (seit wann keine Erhöhung, Kaufkraft), ein klares Ergebnis („X € brutto nötig“), die Netto-Wirkung eines Wunschgehalts.

### Verträge und Abos
- Ziel: nichts läuft unbemerkt weiter, jede Erhöhung fällt auf.
- Zeigt: wiederkehrende Zahlungen, Laufzeit, Kündigungsfrist, Preisänderungen.
- Verknüpft mit: Abbuchung auf dem Konto.
- Offen: wie ein Vertrag seine Abbuchung findet.

### Budgets
- Ziel: wissen, was in einem Zeitraum noch übrig ist.
- Zeigt: Rahmen je Kategorie (mit Unterkategorien), Verbrauch, Übertrag.
- Verknüpft mit: Buchungen, Belegpositionen.

### Altersvorsorge, Vermögen, Steuern
- Ziel: langfristig planen (Rente, Vermögen) und Steuern nicht verschenken.
- Zeigt: Prognosen mit offenen Annahmen, Nettovermögen, Steuerübersicht.
- Offen: welche Prognose wirklich gebraucht wird.

### Analyse
- Ziel: Fragen beantworten, die der Überblick nicht beantwortet („wohin ging das Geld?“).

## Verknüpfungen (entschieden: diese vier zuerst und sauber)

| Verknüpfung | Soll |
|---|---|
| **Abrechnung ↔ Bankeingang** | Jede Gehaltsabrechnung findet ihre Überweisung. Weicht der Betrag von der Auszahlung ab, sieht man es an beiden Stellen. |
| **Beleg ↔ Banktransaktion** | Jeder Beleg gehört zu einer Buchung. Die Positionen laufen in Kategorien und Budgets ein. |
| **Vertrag ↔ Abbuchung** | Jeder Vertrag und jedes Abo findet seine wiederkehrende Zahlung. Eine Preiserhöhung fällt auf. |
| **Gehalt ↔ Preise** | Verdienst gegen die eigenen Preise aus den Belegen, in das Gehaltsgespräch übernommen. |

Für jede Verknüpfung ist festzulegen: wer sie anlegt (automatisch, vorgeschlagen, von Hand), wo man sie von beiden Seiten sieht und was bei einer Abweichung passiert. Das steht je Verknüpfung im Issue des Produktteils.

## Arbeitsweise

- Die Arbeit je Produktteil steht in dessen Issue (Gehalt #206, Käufe und Belege #202, Konten #195, Buchungen #196, Kategorien #197, Verträge #200, Budgets #199, Altersvorsorge #207, Steuern #208, Dashboard #209). Es gibt keine zweite Aufgabenliste.
- Jede Änderung nennt ihren Produktteil und das Ziel, dem sie dient.
- Unklare oder unvollständige Wünsche werden vor der Umsetzung als Frage zurückgegeben, nicht geraten.

## Nächste Fragerunde

1. Überblick: Welche drei Zahlen müssen oben stehen?
2. Gehalt: Was muss das Gehaltsgespräch am Ende ausgeben, damit es genügt?
3. Käufe: Wann gilt ein Beleg als geprüft, und was darf automatisch bestätigt werden?
4. Verträge: Welche Verträge und Abos sind dir wichtig, und woher kommen sie (Bank, Paperless, von Hand)?
5. Abrechnung ↔ Bankeingang: Wie viel Abweichung ist normal, und was soll bei mehr passieren?
6. Vorsorge und Vermögen: Welche Prognose brauchst du wirklich?
7. Design: Was darf nie auf einer Seite fehlen, und was soll nie darauf stehen?
