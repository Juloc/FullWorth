# 0.88.0

Der Vermögensverlauf reicht wieder so weit zurück, wie es Buchungen gibt, und lässt sich lesen.

## Neu

- **Achsen im Vermögensverlauf.** Die Kurve trägt jetzt Eurowerte an den Hilfslinien und Monate oder Jahre darunter. Ab zwei Jahren zeigt sie den letzten Stand jedes Monats statt Tageswerte.
- **„Max" beginnt am ersten gemessenen Tag.** Bisher begann es bei der ersten Buchung, und die Kurve belegte nur ein Fünftel der Breite.
- **Der zuletzt gewählte Zeitraum wird gemerkt.** Beim nächsten Öffnen der Seite gilt er wieder.
- **„Woraus dein Vermögen besteht".** Die Karte „Reihen im Zeitverlauf" ist jetzt eine gestapelte Fläche aus Konten, Investments, Immobilien, Edelmetallen, Altersvorsorge und sonstigen Sachwerten, je Monat. Schulden und Immobilien-Eigenkapital stehen nicht mehr im Stapel: Schulden sind eine Last und stehen oben bei den Kennzahlen, das Eigenkapital wäre dieselbe Immobilie ein zweites Mal.

## Geändert

- **Die Vorschau ist standardmäßig aus.** Sie lag auf derselben Skala wie das Gemessene; mit einer Sparrate unter null reichte sie auf −379 000 € und machte aus der echten Kurve einen flachen Strich. Wer sie schon eingestellt hat, behält seine Wahl. Eine eigene Seite für die Prognose ist in Planung.

## Behoben

- **Verlauf fehlte vor dem ersten Wechselkurs.** Ein Konto in Fremdwährung, das an einem Tag noch leer war, machte jeden Tag davor „unbekannt", weil für die Währung noch kein Kurs vorlag. Null Einheiten brauchen keinen Kurs. Ein Betrag ungleich null ohne Kurs bleibt unbekannt, nie 1:1.
- **Kurse reichen bis zur ersten Buchung zurück.** Der Kursabruf holte nur 400 Tage nach. Er geht jetzt bis zur ersten Buchung, höchstens bis zum ersten EZB-Kurs (4. Januar 1999). Der erste Lauf nach dem Update lädt die Kurse einmal nach.
