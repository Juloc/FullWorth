# 0.85.6

Die Gehaltslinie in „Gehalt gegen Preise“ ist mit echten Abrechnungen glatt.

## Behoben

- Bonus-Monate, Nachzahlungen und Korrekturläufe ließen die Linie ausreißen, wenn sie dicht beieinander lagen (November 2024). Monate mit erfasster Einmalzahlung zählen beim Glätten nicht mit, und der Median schaut drei Monate vor und zurück.
