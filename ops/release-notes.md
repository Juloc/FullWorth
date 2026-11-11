# 0.85.2

Lohnabrechnungen mit Bonus werden richtig gelesen, der Dienstwagen-Sachbezug stimmt mit dem Vertrag überein, und gespeicherte Abrechnungen lassen sich korrigieren.

## Behoben

- **Bonus-Monate hatten falsche Abzüge.** In Monaten mit Einmalzahlung hat jede Abzugszeile drei Spalten (laufender Bezug, Monat gesamt, Jahr). Gelesen wird jetzt die mittlere, deren Summe die Abrechnung als gesetzliche Abzüge ausweist.
- **Dienstwagen-Sachbezug.** Gelesen wird die Zeile „Fahrzeug Nutzung“; brutto minus sie ist genau das Vertragsgehalt. Vorher fehlten die Fahrtkosten-Anteile, und jeder Monat sah wie eine Abweichung vom Vertrag aus („Änderung erkannt … kein Nachtrag“). bAV-Beiträge kommen aus den Summenzeilen.
- **Gehalt-gegen-Preise-Linie zackig.** Ein Bonus-Monat neben einer Lücke ließ die Linie springen. Geglättet wird jetzt über die Monate ringsum, bei nur zwei Werten gilt der kleinere.

## Neu

- **Abrechnung korrigieren.** Die Werte einer gespeicherten Abrechnung lassen sich an Ort und Stelle ändern (`PUT /api/compensation/payslips/{id}`); das angehängte PDF bleibt dran.
