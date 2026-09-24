// Der Einstieg der Seite "audit" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.

import { startShellPage } from '../../app/shell.js';
import { renderAudit, bindAudit } from './page.js';

let bound = false;

await startShellPage(async context => {
  if (!bound) {
    bindAudit(context);
    bound = true;
  }
  await renderAudit(context);
});
