// Der Einstieg der Buchungsseite (#154 Phase B).
//
// Das ist alles, was eine echte Seite zusätzlich braucht: den gemeinsamen Kontext erzeugen, einmal
// binden, einmal zeichnen. `page.js` selbst hat sich dafür nicht geändert — es bekommt denselben
// Kontext wie vorher aus der Hülle, nur dass ihn jetzt die Seite erzeugt statt app.js für alle.
//
// Dass es diese Datei gibt und nicht nur page.js, hat einen Grund: page.js exportiert `render` und
// `bind` getrennt, weil die Hülle beides zu verschiedenen Zeitpunkten braucht. Diese Trennung bleibt
// nützlich (ein Neuzeichnen darf nicht neu binden), und der Einstieg ist die Stelle, die sie kennt.

import { startPage } from '../../app/page-context.js';
import { bindTransactions, renderTransactions } from './page.js';

let bound = false;

await startPage(context => {
  if (!bound) {
    bindTransactions(context);
    bound = true;
  }
  return renderTransactions(context);
});
