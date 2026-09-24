// Der Einstieg der Seite "coach" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert.
//
// Diese Seite holt ihre Dienste selbst aus core/services.js, nimmt also keinen Kontext entgegen -
// deshalb steht hier renderCoach() und nicht renderCoach(context).

import { startShellPage } from '../../app/shell.js';
import { renderCoach, bindCoach } from './page.js';

let bound = false;

await startShellPage(async () => {
  if (!bound) {
    bindCoach();
    bound = true;
  }
  await renderCoach();
});
