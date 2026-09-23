// Der Einstieg der Buchungsseite (#154 Phase B).
//
// Das ist alles, was eine echte Seite zusätzlich braucht: die Hülle starten und zeichnen. Sprache,
// Rechte, Raum, Navigation, Topbar und Sperre erledigt `startShellPage` — für jede Seite gleich.
// `page.js` selbst hat sich dafür nicht geändert; es bekommt denselben Kontext wie vorher aus der
// alten Hülle, nur dass ihn jetzt die Seite erzeugt statt app.js für alle.
//
// Dass es diese Datei gibt und nicht nur page.js, hat einen Grund: page.js trennt `bind` und
// `render`, weil ein Neuzeichnen nicht neu binden darf. Der Einstieg ist die Stelle, die das weiß.

import { startShellPage } from '../../app/shell.js';
import { bindTransactions, renderTransactions } from './page.js';

let bound = false;

await startShellPage(async context => {
  if (!bound) {
    bindTransactions(context);
    bound = true;
  }
  await renderTransactions(context);
});
