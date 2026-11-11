# 0.85.0

Gehalt gegen Preise, eine Paperless-Suche, die zu deiner Ablage passt, ein Beleg-Prüfen, das wie ein Beleg aussieht, Produkte umbenennen und eine kompaktere Handy-Ansicht.

## Neu

- **Gehalt gegen Preise.** Im Reiter „Lohnabrechnungen“ liegen Netto oder Brutto, die allgemeine Teuerung, Lebensmittel, Miete, Energie und Benzin (Destatis, eine Quelle, bis September 2026) und die Preise deiner eigenen Einkäufe übereinander, alle am selben Monat bei 100. Dazu steht in Klartext, was dein Gehalt real gewachsen ist und was das im Monat in Euro bedeutet. Ein Bonus fällt durch eine Glättung (Median aus drei Monaten) nicht als Gehaltssprung auf.
- **Preisindex aus deinen eigenen Belegen.** Belege aus Paperless haben keine Produktzuordnung, die bisherige persönliche Teuerung kam deshalb nie zu einer Zahl. Der neue Index vergleicht gleiche Artikel im selben Geschäft von Quartal zu Quartal, getrennt für Lebensmittel, Kraftstoff, Haushalt/Drogerie und Gesundheit. Er zählt nur bestätigte Belege und nur, wo genug Artikel vergleichbar sind.
- **Paperless-Suche aus deiner echten Ablage.** Statt ein Textfeld zu füllen, wählst du Dokumenttyp und Absender aus dem, was in Paperless liegt, und siehst die Treffer mit ihren Tags, bevor etwas gespeichert wird. So landen keine Bank-Entgeltaufstellungen oder Abrechnungen anderer Personen unter deinen Lohnabrechnungen. Dieselbe Abrechnung, die mehrfach in Paperless liegt, ist ein Eintrag; „Neu lesen“ liest offene Funde noch einmal aus dem Original.
- **Beleg prüfen als Beleg.** Die Prüfansicht zeigt den Bon wie einen Kassenbon, daneben (am Handy per Umschalter „Bon | Original | Geteilt“) das Original mit Zoom. Du gehst die ganze Liste durch: „Bestätigen & weiter“, zurück, überspringen, Pfeiltasten, Wischen. Positionen bearbeitest du direkt, am Handy in einem Blatt mit großen Feldern. Die Summenprüfung steht dauernd sichtbar. PDF-Belege werden dafür seitenweise als Bild geliefert.
- **Produkte umbenennen.** „Brokolo 1kg“ lässt sich zu „Brokkoli“ korrigieren, direkt an der Zeile oder am Produkt. Der alte Text wird als Alias gemerkt, ein späterer Import von „Brokolo 1kg“ landet dann gleich bei „Brokkoli“; auf Wunsch werden gleiche Zeilen mit umbenannt.
- **Kompaktere Handy-Ansicht.** Engere Abstände und Schriften, flache Karten, kleinere Zeilen; Seiten sind bis zu ein Viertel kürzer. Auf dem Desktop ändert sich nichts.

## Behoben

- **Lohnabrechnungen lasen die Jahressumme statt des Monats.** Auf vielen Abrechnungen steht hinter jedem Etikett „Monat  Jahr“; gelesen wurde die letzte Zahl, also aus 3.567 € Brutto im Februar wurden 11.134 €. Jetzt gilt der Monat. Arbeitgeber-Pauschalen und die Tabelle der Beitragsgrundlagen werden ignoriert, „Zahlbetrag“ und die Kürzel der Lohnarten werden erkannt. Ein digitales PDF wird über seine Textebene gelesen, nicht über Texterkennung.
- **Vertrags-Nachträge lieferten weder Gehalt noch Datum.** „Grundgehaltserhöhung auf brutto 3.240.- Euro“ (auch „3.100,-“ und mit Zeilenumbruch) wird jetzt gelesen.
- „Nach Produkt“ fasst nicht zugeordnete Zeilen unter ihrem eigenen Namen zusammen statt unter „Unmatched product“.

## Wichtig

- **Nach dem Update in „Lohnabrechnungen“ und „Arbeitsverträge“ auf „Neu lesen“ tippen:** bereits gelesene, noch unbestätigte Funde haben die alten, falschen Werte.
- Aufgenommen werden nur Abrechnungen, die du bestätigst. PDFs mit mehreren Monaten (zum Beispiel „Mai bis Juli“) werden mit einem Monat gelesen; prüfe die Monate.
- Eine Datenbank-Änderung gibt es nicht; ein neuer Endpunkt liefert PDF-Seiten als Bild.
- Nicht mit echten Belegen und echten Abrechnungen erprobt, nur mit Beispieldaten und gegen die echten Paperless-Texte gelesen.
