# 0.86.0

Die Gehalt-Seite ist aufgeräumt, startet mit deinen echten Zahlen und hat einen einzigen Verlauf.

## Neu

- **„Mein Gehalt“ als Startseite.** Aus Abrechnungen und Verträgen: die Zusammensetzung des Monats (Grundgehalt, Firmenwagen-Vorteil, Steuern, Sozialabgaben, bAV, Auszahlung, dazu der Arbeitgeberanteil der bAV), eine Summenzeile der letzten zwölf Monate mit Quelle und der Bonus erklärt (halbes Monatsgehalt im Juni und November = 13. Gehalt, sonst Prämie), darunter „Verdienst gegen Preise“ und die Wohnkosten.
- **Ein Verlauf statt drei Reitern.** Abrechnungen, Verträge und Verlauf sind eine Zeitachse: je Jahr Brutto, Bonus, Auszahlung im Monat, Veränderung zum Vorjahr, Teuerung und real; aufgeklappt die Monate mit Detail, PDF und Löschen. Gehaltsänderungen aus den Verträgen stehen als Ereignisse im Jahr. Vertragsdiagramm, Änderungsprotokoll und die alten Planungsstände aus dem Rechner stehen darunter eingeklappt; Planungsstände nur, wenn es welche gibt.
- **„Hinzufügen“ statt Karten am Seitenende.** Ein Knopf öffnet einen Dialog: Paperless (Suche einrichten, Funde prüfen, neu lesen), PDF hochladen, Abrechnung oder Vertragsstand von Hand. Die Prüfformulare öffnen sich dort.
- **Vier Gruppen statt elf Reiter:** Mein Gehalt, Verlauf, Planen (Rechner, Szenarien, Optimierer, Gehaltsgespräch), Mehr (Benefits, Dienstrad, weitere Einkünfte). Kopf mit Titel, Untertitel und Plakette und die Erklärtexte unter den Überschriften sind weg; „Profil speichern“ steht im Rechner.
- **Verdienst gegen Preise verständlicher.** Ein Satz sagt, was die Linien bedeuten, die Kennzahlen heißen „Dein Verdienst“, „Preise insgesamt“, „Real für dich“, dazu ein Satz im Klartext. Die Legende zeigt oben nur sichtbare Linien mit Herkunft (Statistik oder deine Daten), der Rest steht hinter „Weitere Linien“.
- **Auswahlfelder wie Buttons**, mit größerem Pfeil und genug Abstand; in Formularen sind Eingabe- und Auswahlfelder gleich hoch und gleich gerundet.
- **Der „Hinzufügen“-Dialog** hat einen Scrollbereich, einen festen Kopf mit Schließen-Knopf und zwei Ebenen: Lohnabrechnung oder Vertrag, dann Paperless, PDF oder von Hand.
- **Am Telefon steht von der Marke nur das Logo,** in der Kopfzeile neben dem Breadcrumb und der Suche.

## Behoben

- **Das Brutto sank scheinbar, wenn der Firmenwagen-Vorteil sank.** „Brutto-Gehalt“ ist jetzt Brutto ohne Bonus und ohne Firmenwagen-Vorteil (das Vertragsgehalt), und das Gehalt wird als Treppe gezeichnet statt als Schräge über fehlende Monate.
- Der Verlauf war leer, obwohl Abrechnungen da waren: er kannte nur gespeicherte Planungsstände.
