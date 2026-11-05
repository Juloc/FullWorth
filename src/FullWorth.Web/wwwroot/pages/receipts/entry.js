// Der Einstieg der Seite "receipts" (#154): Sprache, Rechte, Raum, Navigation und Topbar erledigt
// startShellPage; page.js bekommt den Kontext und zeichnet.

import { startShellPage } from '../../app/shell.js';
import { renderReceipts, bindReceipts } from './page.js';

let bound = false;

await startShellPage(async context => {
  if (!bound) {
    bindReceipts(context);
    bound = true;
  }
  await renderReceipts(context);
});
