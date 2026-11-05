// Der Einstieg der Seite "receipts" (#154): Sprache, Rechte, Raum, Navigation und Topbar erledigt
// startShellPage; page.js bekommt den Kontext und zeichnet.

import { startShellPage } from '../../app/shell.js';
import { renderReceipts, bindReceipts, chooseFiles } from './page.js';

// Die Hauptaktion der Topbar gehoert der Seite; der Kontext steht erst fest, wenn gezeichnet wird.
let pageContext;
let bound = false;

await startShellPage(async context => {
  pageContext = context;
  if (!bound) {
    bindReceipts(context);
    bound = true;
  }
  await renderReceipts(context);
}, { primaryAction: () => ['receipts.upload', () => chooseFiles(pageContext), 'upload'] });
