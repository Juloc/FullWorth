# 0.85.5

Heiz- und Nebenkosten aus deinen Abrechnungen, neben den Preisen der Statistik.

## Neu

- **Heiz- und Nebenkosten erfassen.** Unter „Lohnabrechnungen“ gibt es die Karte „Heiz- und Nebenkosten“: Beträge aus Heizkosten-, Wasser-, Nebenkosten- und Hausgeldabrechnungen mit ihrem Zeitraum (und Notiz), mit Löschen. Sie gehören dir und werden bei der Kontolöschung entfernt.
- **Linien in „Gehalt gegen Preise“.** Je Kostenart eine Linie in Euro je Monat („Deine Wohnkosten“), an die passende Statistiklinie angelegt (Heizung gegen Haushaltsenergie, Strom gegen Strom) und mit ihr zusammen gezeigt. Eine Linie entsteht erst, wenn für ein Jahr mindestens elf Monate erfasst sind: ein Wintersegment neben einem Sommersegment wäre eine Heizsaison-Schwankung und keine Preisentwicklung. Gas und Strom der Statistik sind in der Legende wählbar.

## Wichtig

- Eine Datenbank-Änderung (neue Tabelle „HousingCostEntries“), sonst nichts.

## Behoben

- Das Gehaltsgespräch füllt sich mit 13 statt 14 Monatsgehältern, wenn eine einmalige Prämie dabei war: gezählt werden nur Einmalzahlungen von etwa einem halben Monatsgehalt.
