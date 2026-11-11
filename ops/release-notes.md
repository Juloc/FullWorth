# 0.84.0

Gehalt wird aus deinen Dokumenten gespeist: Lohnabrechnungen und Arbeitsverträge kommen aus Paperless, mit dem Original-PDF dabei. Dazu kommt die Korrektur der Push-Anzeige.

## Neu

- **Lohnabrechnungen aus Paperless.** Wie bei den Kassenbons: ein Preset mit Art „Lohnabrechnung“ holt neue Dokumente regelmäßig ab und legt sie in der Prüfliste ab. Erkannte Werte (Brutto, Netto, Steuern, Sozialabgaben, Bonus …) prüfst und bestätigst du, erst dann wird gespeichert. Das Original-PDF wird verschlüsselt in FullWorth abgelegt und bleibt aus der Abrechnung heraus öffenbar.
- **Monats- und Jahresansicht.** Reiter „Lohnabrechnungen“ mit Jahreswahl, Kennzahlen (Brutto, Netto, Nettoquote, Abrechnungen), einem Diagramm „Wohin das Brutto geht“ und der Monatsliste mit Änderung zum Vormonat und dem Detail je Monat. Fehlende Monate sind sichtbar. Hinweise sagen, was auffällt: ein Brutto-Sprung, ein Netto, das trotz gleichem Brutto sinkt (mit der Abzugsposition als Ursache).
- **Arbeitsverträge und Änderungsprotokoll.** Preset-Art „Arbeitsvertrag“ für Verträge und Nachträge, gleiche Prüfung und Bestätigung. Das Protokoll zeigt jede Änderung mit Datum, PDF und nur den geänderten Feldern (alt durchgestrichen, neu daneben, beim Gehalt mit Prozent); der Rest ist eingeklappt.
- **Abgleich Vertrag und Abrechnung.** Das Diagramm „Brutto laut Vertrag“ legt die Abrechnungen als Punkte auf die Vertragsstufen. Weicht das Brutto ab, steht ein Hinweis da; hält sich die Abweichung, kommt „Änderung erkannt“ mit einem vorausgefüllten Formular.

## Behoben

- **Push zeigte „aktiv“, obwohl nichts ankam, und ließ sich nicht abschalten.** Die Seite las „aktiv“ allein aus der Browser-Berechtigung, die auch nach einer fehlgeschlagenen Anmeldung bestehen bleibt. Jetzt gilt Push nur als aktiv, wenn Berechtigung, eine zum Server passende Anmeldung und der Servereintrag da sind. Eine veraltete Anmeldung wird erkannt und beim erneuten Aktivieren ersetzt. Abschalten geht in jedem Zustand, auch ohne Verbindung zum Server, und zeigt, was nicht geklappt hat.
- **Die Testbenachrichtigung meldete „gesendet“, egal was passierte.** Sie meldet jetzt einen Fehler, wenn kein Push-Dienst die Nachricht angenommen hat.
- Der Name eines Paperless-Presets ist je Benutzer eindeutig statt je Bereich: zwei Mitglieder mit gleich benannten Presets bekamen beim Speichern einen Fehler.

## Wichtig

- Gehaltsdaten, Abrechnungs- und Vertrags-PDFs sieht nur, wer sie importiert hat, nicht jedes Mitglied des Bereichs. Die Kontolöschung entfernt sie mit.
- Zwei Datenbank-Änderungen (neue Tabellen für Funde und Vertragsstände, eine neue Spalte „Art“ bei den Presets), sonst keine Konfiguration. Die PDFs liegen unter `/data/pension/compensation`, kein neues Volume nötig.
- Noch nicht mit echtem Paperless, echten Abrechnungen und echten Verträgen erprobt: die Auslesung liefert Vorschläge, die du bestätigst.
- Presets für Gehalt und Verträge lassen sich in der Oberfläche noch nicht löschen.
