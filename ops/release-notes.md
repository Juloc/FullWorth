# 0.82.1

Kleine Korrektur zu 0.82.0.

## Behoben

- Eine Buchung ganz ohne Datum (zum Beispiel eine Kartenvormerkung, bei der die Bank noch gar kein Datum geliefert hat) machte mitten in der
  Liste eine eigene Gruppe „—“ auf und trennte damit zwei Tage („Gestern“, „—“, „Gestern“). Sie steht jetzt in der Gruppe, in die der
  Server sie einsortiert hat (Tag der ersten Beobachtung).

## Wichtig

- Nur Oberfläche, keine Datenbank-Änderung und keine Konfiguration nötig.
