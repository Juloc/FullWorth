# 0.87.0

Packungsgröße an Belegpositionen und ein Kategorienbaum für den Supermarkt.

## Neu

- **Packung als eigenes Feld.** Eine Position kennt jetzt ihre Packungsgröße („500 g“, „10 Stück“, „2 × 100 g“) getrennt vom Namen. Im Beleg-Editor gibt es dafür „Packung (optional)“ mit Einheit; steht die Größe im Namen („Knoblauch 200 g“, „Eier, 10er“), liest FullWorth sie beim Speichern heraus. Die Zeile zeigt dann den Preis je kg, l oder Stück, und Vergleiche laufen über dieselbe Packung.
- **Bestand wird nachgezogen.** Beim Start liest eine Migration die Packung aus bestehenden Positionen: aus der Mengenangabe („500 g“) und aus dem Namensende. Der ursprüngliche Name bleibt in „Originaltext“.
- **Lebensmittel genauer aufteilen.** Auf der Kategorienseite steht, solange „Lebensmittel“ keine Unterkategorien hat, ein Angebot: Obst & Gemüse, Milch, Käse & Eier, Fleisch & Wurst, Fisch, Brot, Nudeln & Konserven, Öl & Gewürze, Frühstück, Tiefkühl, Vegetarisch & Vegan, Snacks, Süßigkeiten (mit Schokolade), Getränke, Kaffee & Tee und Alkohol (mit Bier, Wein, Spirituosen). Was du schon angelegt hast, wird einsortiert statt doppelt angelegt. Neue Spaces bekommen den Baum von Anfang an.
- **Positionen ordnen sich nach ihrem Namen ein.** Neu gelesene Belege landen mit „Hähnchenbrustfilet“ bei Fleisch & Wurst statt bei „Lebensmittel“, und die Positionen, die bisher nur „Lebensmittel“ waren, lassen sich auf Wunsch nachträglich einordnen. Genauere Einordnungen bleiben unangetastet.

## Geändert

- Der Preisindex vergleicht nur Artikel mit derselben Packung und rechnet Unterkategorien zu ihrem Grundbedarf.
- Mengeneinheiten „stk“ und „stk.“ gelten wie „Stück“.
