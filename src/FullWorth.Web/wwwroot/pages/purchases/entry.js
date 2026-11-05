// Der Einstieg der Seite "purchases" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.

import { startShellPage } from '../../app/shell.js';
import { renderPurchases, bindPurchases, chooseReceipt, openPurchaseFromHash } from './page.js';

// Die Hauptaktion der Aktionsleiste gehoert der Seite; der Kontext steht erst fest, wenn gezeichnet wird.
let pageContext;
let bound = false;

await startShellPage(async context => {
  pageContext = context;
  if (!bound) {
    bindPurchases(context);
    bound = true;
  }
  await renderPurchases(context);
  await openPurchaseFromHash();
}, { primaryAction: () => ['purchases.scan', () => chooseReceipt(pageContext), 'scan'] });
