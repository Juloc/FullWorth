# 0.82.2

Die Belegkamera löst jetzt auch bei einer normal ruhigen Hand von selbst aus.

## Behoben

- **Die Kamera wartete ewig auf ein „ruhiges“ Bild.** Sie hat die Bewegung auf voller Analysebreite gemessen; Sensorrauschen und das
  Zittern einer Hand, die ruhig zu halten meint, lagen bei einem Beleg voller Schrift dauernd über der Schwelle. Nachgestellt mit einem
  verrauschten, leicht zitternden Kamerabild blieb die Anzeige zwölf Sekunden bei „Ruhig halten …“, ohne dass ein Foto kam. Jetzt wird die
  Bewegung auf einem Viertel der Auflösung gemessen (Rauschen mittelt sich weg), es genügen drei ruhige Bilder statt fünf, und die Grenzen
  geben nach, je länger ein erkannter Beleg wartet: nach etwa zwei Sekunden gelten sie dreimal so weit. Im selben Test löst die
  Kamera damit gleich nach der Anlaufzeit aus.
- Beim **Langen Bon** bleiben die Grenzen, wie sie waren.

## Wichtig

- Nur am Emulator mit künstlichem Rauschen geprüft, nicht an einem echten Handy. Löst sie bei dir weiter zu spät oder zu früh aus, sag
  Bescheid; der Auslöser von Hand funktioniert immer.
- Keine Datenbank-Änderung, keine Konfiguration nötig.
