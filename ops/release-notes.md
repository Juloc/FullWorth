# 0.81.0

Bons werden jetzt, wo es geht, **ohne KI** gelesen, die Belege haben eine eigene Seite mit Warteschlange, Verlauf und
„Zu prüfen“, Käufe und Belege sind Tabellen, und die Oberfläche hat eine neue Kopfleiste und eine Aktionsleiste je Seite.
Wer `latest` zieht, bekommt diese Version.

## Bons lesen – ohne KI, wo es geht

- **Digitale PDFs werden aus ihrem exakten Text gelesen**, ohne OCR und ohne KI. Der Leser prüft die Rechnung des Bons selbst
  (Positionen − Rabatte + Pfand = Summe). Nur wenn sie aufgeht, gilt das Ergebnis als sicher.
- **Scans und Fotos gehen zur KI** (auch Paperless-PDFs mit OCR-Textschicht, denn dieser Text ist nur OCR). Ist die KI nicht
  erreichbar, liest Tesseract: zwei Seitenlayouts bei 300 dpi plus die Textschicht, gewählt wird die Lesung, deren Summen aufgehen.
  Eine einzelne verlesene Ziffer wird nur korrigiert, wenn genau eine Korrektur die Rechnung aufgehen lässt; das Ergebnis bleibt
  dann auf „zu prüfen“.
- **Zwei Spuren in der Warteschlange:** Eine schnelle Spur liest digitale PDFs in Millisekunden, eine KI-Spur arbeitet die Scans
  streng nacheinander ab. Ein digitaler Beleg wartet nicht mehr hinter einem langsamen KI-Scan.
- **Jeder Beleg merkt sich, wie er gelesen wurde:** Leser, Version seiner Regeln (`text/2`, `tesseract/2`, `codex/<Modell>`) und
  der Grund (`digital_pdf`, `scan`, `digital_unverified`, `ai_unavailable`). Ändert sich der Leser, lassen sich die Belege einer
  älteren Version gezielt wiederfinden und neu lesen.
- Ein sauber gelesener digitaler Beleg wird automatisch bestätigt, wenn `ReceiptImports__AutoConfirm` an ist.

Gemessen an 28 echten Bons (Soll: das KI-Ergebnis): Mit Textschicht und Tesseract sind 13 sicher erkannt, mit RapidOCR im Labor 16,
jeweils ohne einen falschen Treffer unter den als sicher eingestuften. Der Rest wird nicht geraten, sondern bleibt zur Prüfung.
Unscharfe Scans und Sonderformate (Tankstelle, Schnellrestaurant, Gutschriften) bleiben bei der KI.

## Neue Seite „Belege“

Warteschlange, Verlauf, Zu prüfen und Fehlgeschlagen, mit Zahlen an den Reitern. Die Warteschlange zeigt live, welcher Beleg gerade
gelesen wird, von wem und wie weit er ist. In „Zu prüfen“ lassen sich Belege einzeln oder gesammelt bestätigen (jeder durch dieselbe
Summenprüfung wie sonst), fehlgeschlagene Belege erneut versuchen. Die Liste lädt beim Scrollen nach.

## Käufe

- Die Liste ist eine Tabelle mit klebender Kopfzeile und Gesamt-Auswahl, vom Server seitenweise geladen. Filter: Quelle und
  „Zu prüfen“ / „Noch nicht verknüpft“, mit Zählern vom Server.
- Mehrere unbestätigte Käufe lassen sich gemeinsam bestätigen. Der Kauf-Dialog hat einen Bestätigen-Knopf.
- **Entfallen:** der Summenstreifen (Gesamtausgaben, Anzahl) und das Händler-Ranking unter der Liste. Sie wurden im Browser über die
  komplette Liste gerechnet und brauchen eine Zusammenfassung vom Server, um wiederzukommen.

## Oberfläche

- Jede Seite zeigt einen Brotkrumenpfad mit Suchfeld statt Titel und Unterzeile.
- Die **Aktionsleiste** gehört der Seite: Hauptaktion (z. B. „Beleg scannen“, „Beleg hochladen“), weitere Aktionen (Import, Kamera,
  Amazon) und die Aktionen einer Auswahl stehen in einer Leiste am unteren Rand der Seite.
- Eine einheitliche Checkbox; die Buchungsliste führt ihre Auswahl-Aktionen ebenfalls in der Aktionsleiste.
- **Entfernt:** der Privatmodus-Schalter und das ⋯-Menü in der Kopfleiste. „Beim Start im Privatmodus starten“ bleibt eine Einstellung.
  Der schwebende Coach-Knopf ist vorerst ausgeblendet; der Coach bleibt über das Menü und „Coach fragen“ erreichbar.

## Für die Aktualisierung

- Beim Start laufen **zwei Datenbank-Migrationen** automatisch (`ReaderVersion`/`Reason` und `Lane` an `ReceiptScanJobs`). Sie
  fügen nur Spalten hinzu. Vorher wie immer die Volumes sichern.
- `ReceiptExtraction:Provider` steht jetzt standardmäßig auf `tesseract` (vorher `none`): Ohne KI liest FullWorth lokal. Wer das nicht
  will, setzt `none`.
- Neue Endpunkte: `GET /api/purchases/receipt-scan/jobs?scope=queue|history|review|failed`, `GET /api/purchases/receipt-scan/counts`,
  `GET /api/purchases?scope=&limit=&offset=` und `GET /api/purchases/counts`. Der Parameter `includeCompleted` der Jobliste entfällt.
- Keine Änderung an Konfiguration oder Volumes nötig.

## Wichtig

- Die Browser-Layouttests laufen nur in der CI; die Oberfläche wurde zusätzlich im UI-Harness auf Desktop, Tablet und Handy angesehen,
  nicht auf einem echten Gerät.
- Die KI-Spur ist bewusst seriell. Wie viele parallele Aufrufe die Codex-Bridge verträgt, ist nicht gemessen.
- Der Foto-Scan-Dialog und der Paperless-Import liegen noch auf der Käufe-Seite, nicht auf der Belege-Seite.
