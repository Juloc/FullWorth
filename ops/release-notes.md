# 0.87.3

Nach einem Finanzguru-Import stehen Buchungen nicht mehr doppelt im Vermögensverlauf.

## Behoben

- **Doppelte Buchungen nach „Historie bestätigen".** Schreiben Bank und Finanzguru einen Händler verschieden („Kaufland" / „KAUFLAND.BIETIGHEIM.BIS/…"), entstand dieselbe Buchung zweimal auf dem Konto, und beide zählten für die Rückrechnung des Vermögens. Der Verlauf fiel dadurch um die Summe der Doppelten. Der Abgleich führt solche Paare jetzt zusammen: beim Bestätigen und Zuordnen vor dem Freischalten, und bei jedem Bankabruf – auch für Konten, die schon bestätigt sind. Die Bankzeile bleibt, die Kategorie aus Finanzguru wird übernommen.
- Ist eine Buchung auf beiden Seiten aufgeteilt, bleiben beide Zeilen bestehen; die Finanzguru-Zeile wird dann nicht für den Verlauf freigeschaltet.
