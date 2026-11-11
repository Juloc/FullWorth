# 0.85.1

Lohnabrechnungen und Verträge aus Paperless werden aus dem Text gelesen, den Paperless selbst erkannt hat.

## Behoben

- **Verlesene Zahlen bei Scans.** Die Originale in Paperless sind oft Scans. Eine zweite Erkennung derselben Datei verlas Ziffern (3.567,23 € Brutto wurden 3.867,23 €, ein Rentenbeitrag 1.270 € statt rund 308 €) und fand bei manchen Seiten weder Brutto noch Netto. Jetzt gilt der Text von Paperless, sofern er brauchbar ist; nur ohne ihn wird die Datei selbst gelesen.

## Wichtig

- In „Lohnabrechnungen“ und „Arbeitsverträge“ nach dem Update noch einmal auf „Neu lesen“ tippen. Bereits gelesene, unbestätigte Funde behalten sonst die verlesenen Werte.
