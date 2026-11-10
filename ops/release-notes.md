# 0.83.0

FullWorth kann dich jetzt benachrichtigen, wenn neue Buchungen eingehen.

## Neu

- **Neue Buchungen als Push-Mitteilung.** Nach jedem Bankabruf bekommst du **eine** Mitteilung für alles, was neu ist: eine einzelne Buchung
  als „Neue Buchung“, mehrere gebündelt als „5 neue Buchungen“ mit den ersten drei und „+ 2 weitere“. Das Antippen öffnet die Buchungen.
  Die Bank meldet Buchungen nicht von sich aus, FullWorth holt sie ab (alle 15 Minuten, im Hintergrund höchstens alle 6 Stunden): die
  Mitteilung kommt, wenn FullWorth sie abholt, nicht in der Sekunde der Zahlung.
- **Beträge in Mitteilungen einstellbar.** Standard: aus, es steht nur der Händler da, denn einen Sperrbildschirm liest jeder mit.
  Schalter „Beträge in Mitteilungen anzeigen“ auf der Seite Benachrichtigungen.
- **Testbenachrichtigung.** Auf der Seite Benachrichtigungen schickt ein Knopf eine Probe an deine eigenen Geräte.
- Neuer Typ „Neue Buchungen“ in der Liste der Benachrichtigungstypen, einzeln abschaltbar.

## Was nicht gemeldet wird

- Der erste Abruf eines neuen Kontos (das ist ein Import der Vergangenheit) und Buchungen, die älter als eine Woche sind.
- Umbuchungen zwischen deinen eigenen Konten und Buchungen, die du ausgeblendet hast.
- Eine Buchung, die eine schon gemeldete Vormerkung ersetzt: dieselbe Zahlung wird nur einmal gemeldet.
- Nur die Besitzer eines Kontos werden benachrichtigt, nicht jedes Mitglied des Bereichs.

## Wichtig

- **Push läuft ohne Einrichtung.** Die Installation erzeugt das nötige Schlüsselpaar beim ersten Bedarf selbst und legt es verschlüsselt in
  der Datenbank ab: nichts in einer Compose-Datei, nichts zu kopieren. Wer ein eigenes Paar mitbringen will, setzt wie bisher
  `Push__VapidPublicKey` und `Push__VapidPrivateKey`; das gilt dann vorrangig.
- Danach auf der Seite Benachrichtigungen einmal **„Push aktivieren“** tippen (pro Gerät) und die Browser-Abfrage erlauben. Auf dem iPhone
  geht Push nur, wenn FullWorth zum Home-Bildschirm hinzugefügt ist (iOS 16.4 oder neuer).
- Eine Datenbank-Änderung (eine neue kleine Tabelle), sonst keine Konfiguration.
