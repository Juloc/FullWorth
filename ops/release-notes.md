# 0.81.1

Ein Fehlerkorrektur-Release für 0.81.0: Die Kamera startet jetzt wirklich, und Beleg-Funktionen gehören nur noch auf die Belege-Seite.

## Behoben

- **Die Kamera ging nie.** Der Server hat sie selbst verboten: Der Header `Permissions-Policy` stand auf `camera=()`, der Browser lehnte die Kamera
  ab, bevor er überhaupt gefragt hat. Außerdem sperrte die Content-Security-Policy `blob:`-Bilder, also die Foto-Vorschauen. Jetzt gilt
  `camera=(self)` (nur FullWorth selbst darf die Kamera nutzen) und `img-src` erlaubt `blob:`. **Nach dem Update fragt der Browser einmal
  nach der Kamera-Berechtigung.**
- **Beleg-Funktionen standen noch bei den Käufen.** „Beleg scannen“, „Mit Kamera“ und „Belege importieren“ sitzen jetzt in der Aktionsleiste der
  **Belege**-Seite; Foto-Scan-Dialog, Kamera, Import und ihre Styles sind dorthin umgezogen. Die Käufe-Seite hat nur noch die Kaufliste und
  „Amazon verbinden“, ihr erster Reiter heißt „Käufe“ statt „Belege“.
- Ein fertiger oder neu angelegter Beleg lädt die Liste auf der Belege-Seite neu, statt die ganze Seite neu zu laden.
- Der alte Einzel-Datei-Upload, den nichts mehr erreichte, ist entfernt.

## Wichtig

- Die Kamera ist **nur ohne Gerät geprüft** (kein Header verbietet sie mehr, der Dialog öffnet sich). Ob sie auf deinem Handy startet, zeigt der erste echte Versuch;
  auf iPhone und iPad hat die Web-Kamera kein Licht und keine Fokussteuerung.
- Keine Datenbank-Änderung, keine Konfiguration nötig.
