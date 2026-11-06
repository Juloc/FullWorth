# 0.82.2

Die Belegkamera löst jetzt von selbst aus - beim Einzelfoto und beim langen Bon.

## Behoben

- **Einzelfoto: die Kamera wartete ewig auf ein „ruhiges“ Bild.** Sie hat die Bewegung auf voller Analysebreite gemessen; Sensorrauschen und das
  Zittern einer Hand, die ruhig zu halten meint, lagen bei einem Beleg voller Schrift dauernd über der Schwelle. Nachgestellt mit einem
  verrauschten, zitternden Kamerabild blieb die Anzeige zwölf Sekunden bei „Ruhig halten …“, ohne dass ein Foto kam. Jetzt wird die
  Bewegung auf einem Viertel der Auflösung gemessen, drei ruhige Bilder genügen statt fünf, und die Grenzen geben nach, je länger ein
  erkannter Beleg wartet: nach etwa zwei Sekunden gelten sie dreimal so weit.
- **Langer Bon: nach dem ersten Stück kam nichts mehr.** Die Kamera hat die Naht zum zuletzt aufgenommenen Stück gesucht; die liegt gleich
  nach einer Aufnahme dort, wo eine Suche auf echten, verrauschten Bildern unsicher bleibt - es kam nie ein zweites Stück. Jetzt misst sie
  die Fahrt am Bon entlang von Bild zu Bild und nimmt das nächste Stück auf, wenn knapp die Hälfte neu ist. Die Naht prüft weiter erst das
  Zusammensetzen, und eine Naht, die nicht sicher ist, bleibt getrennt wie bisher.
- Ein einzelnes Bild, in dem die Randerkennung die Unterkante verpasst, beendete bisher den ganzen langen Bon („Das Ende des Bons ist
  aufgenommen“). Das Ende gilt jetzt erst, wenn die Unterkante etwa eine Sekunde lang zu sehen ist.
- Das Ergebnis des Zusammensetzens („5 Stücke, 4 von 4 Nähten sicher …“) stand nur einen Augenblick da, dann überschrieb es die Anleitung.

## Wichtig

- Beides nur mit einem künstlichen, verrauschten und zitternden Kamerabild geprüft, nicht an einem echten Handy: ein langer Bon mit 100 bis
  260 px/s Fahrt ergab 3 bis 6 Stücke, die Nähte waren jeweils sicher. Löst die Kamera bei dir weiter schlecht aus, sag Bescheid; der
  Auslöser von Hand funktioniert immer.
- Keine Datenbank-Änderung, keine Konfiguration nötig.
