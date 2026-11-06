# 0.82.0

Die Buchungsliste steht jetzt richtigrum und zeigt jede Zahlung nur noch einmal.

## Geändert

- **Buchungen sind nach Zahltag sortiert.** Vorgemerkte Buchungen stehen nicht mehr als eigener Block über „Heute“, sondern an dem
  Tag, an dem bezahlt wurde, und tragen dort die Markierung „Vorgemerkt“. Die Liste läuft von oben nach unten: Erwartet (Prognose),
  Heute, Gestern, davor. Der Kopf „Vorgemerkt“ ist weggefallen.
- **Vorgemerkte Buchungen verschwinden, sobald die Bank gebucht hat.** Bisher blieb die Vormerkung neben der Buchung stehen, weil die
  Bank sie weiter als vorgemerkt meldet; jede Zahlung stand doppelt, und der Buchungstag stimmte nicht mit dem Zahltag überein. Jetzt
  ersetzt die Buchung ihre Vormerkung, wenn **alles** zutrifft: gleicher Betrag auf den Cent, gleiche Währung, gleicher Händler (erstes
  Wort des Namens), Buchung ein Tag vor bis zehn Tage nach dem Zahltag, und die Buchung wurde nicht schon vor der Vormerkung gesehen.
  Eine Buchung ersetzt höchstens eine Vormerkung: zwei gleiche Zahlungen am selben Tag brauchen zwei Buchungen. Im Zweifel bleibt ein
  Doppeltes stehen, statt dass eine echte Zahlung verschwindet.
- Eine Vormerkung mit Notiz, manueller Kategorie, verknüpftem Beleg, Umbuchung oder Erstattung bleibt unangetastet.
  Die Banken-Wiederholung der ersetzten Vormerkung wird erkannt und nicht neu angelegt.

## Behoben

- **Die Tagesleiste mit „Heute“ und dem Tagesstand war unsichtbar**: sie klebte unter dem Topbar statt darunter. Die Tagesköpfe
  klebten gar nicht. Beides sitzt jetzt in drei Stufen übereinander (Topbar, Tagesleiste, Tageskopf), und der Sprung auf „Heute“ landet
  unter allem, was klebt, statt die erste Zeile des Tages dahinter zu verstecken.

## Wichtig

- Beim ersten Start schreibt eine Datenbank-Änderung die Buchungstabelle einmal neu (Sortierschlüssel nach Zahltag). Das dauert bei
  vielen Buchungen einen Moment; die Anwendung ist währenddessen noch nicht erreichbar.
- Bereits vorhandene Doppelte verschwinden beim **nächsten Bankabruf** jedes Kontos, nicht sofort beim Update.
- Eine neue Tabelle merkt sich ersetzte Vormerkungen (90 Tage), damit die Wiederholung der Bank sie nicht zurückbringt.
