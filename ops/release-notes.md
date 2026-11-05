# 0.80.0

Die erste Version in der neuen Zählung: Statt `1.3.0-alpha.N` gibt es jetzt `0.x.y`. FullWorth ist weiter Alpha (darum
die 0), aber jede Version hat eine Nummer, die man lesen kann, und **`:latest` zeigt auf die jeweils neueste**. Die bisherigen
Alphas (bis `1.3.0-alpha.79`) bleiben in GHCR; wer eine davon festgesteckt hat, ändert nichts. Wer auf `0.80.0` wechselt,
ändert die Version im Compose-File - die Daten und die Verschlüsselungs-Schlüssel bleiben, die eine neue Tabelle
(`PaperlessUploads`) wird beim Start angelegt.

## Neu

- **Belegkamera** (Käufe → „Mit Kamera"): Livebild, Randerkennung, automatische Aufnahme, wenn das Bild ruhig und scharf ist, die
  Aufnahme wird gerade gerechnet.
- **Langer Bon**: am Bon entlangfahren, die Kamera nimmt Stücke auf und setzt sie an sicher gefundenen Nähten zu einem Bild zusammen. Wo
  eine Naht nicht sicher ist, bleibt der Bon dort getrennt (eine falsche Naht würde Zeilen doppeln oder verschlucken). Das Ergebnis wird in
  lesbar große Seiten ohne Überlappung geteilt.
- **Fotos aus der Kamera-App** werden beim Hinzufügen ebenfalls begradigt. „Zuschneiden" zieht den Rand von Hand, „Original" nimmt das
  Foto, wie es ist.
- **Belege in Paperless ablegen** (Beleg-Dialog → „In Paperless ablegen"): nur nach Bestätigung, Fotos als ein PDF, mit Titel, Datum und,
  wenn es ihn dort gibt, dem Korrespondenten des Händlers. FullWorth erkennt das Dokument beim späteren Import wieder und legt keinen
  zweiten Kauf an.
- Die Erkennung des Bons wurde neu aufgebaut (drei Quellen statt eines hellen Gebiets, gemessen an künstlichen Testbildern: 90 % sauber
  gefunden statt 54 %). Wo sie unsicher ist, zeigt die Kamera keinen Rahmen statt eines falschen.

## Wichtig

- **Auf einem echten Handy ist nichts davon getestet.** Die Schwellen stammen von künstlichen Bildern. Das Ablegen in Paperless ist nur gegen einen
  Fake-Server geprüft: ob deine Instanz Titel, Datum und Korrespondent so annimmt, zeigt der erste echte Upload. Der Paperless-Token braucht
  das Recht, Dokumente hinzuzufügen.
- Ein weißer Beleg auf weißem Tisch wird nicht erkannt; dann bleibt der Auslöser von Hand.
- Bei Schatten über dem Papier kann der automatische Zuschnitt einen Teil des Bons abschneiden (in 2 von 130 Testbildern). Das begradigte
  Foto wird vor der Analyse gezeigt, „Original" ist ein Tipp.
- Auf iPhone und iPad hat die Web-Kamera kein Licht und keine Fokussteuerung; die Kamera-App des Geräts liefert dort die bessere Optik.

## Technik

- Release-Ablauf: `ops/release-request.txt` (Version) und `ops/release-notes.md` (diese Notizen) werden zusammen nach `main` gepusht; die Notizen
  müssen mit der Version überschrieben sein, sonst wird nichts getaggt. Stabile Versionen (ohne `-rc`/`-alpha`) setzen `latest`, `0` und `0.80`.
- Messwerkzeug für die Kamera: `ops/receipt-vision-bench` (Szenen-Generator, Messungen, Schnelltest `check.mjs`).
