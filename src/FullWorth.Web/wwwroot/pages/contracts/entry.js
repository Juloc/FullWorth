// Der Einstieg der Seite "contracts" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.

import { emitAppEvent } from '../../core/event-bus.js';
import { startShellPage } from '../../app/shell.js';
import { renderContracts, bindContracts, newContract } from './page.js';

// Die Hauptaktion der Topbar gehoert der Seite. Der Kontext steht erst fest, wenn
// gezeichnet wird - der Knopf wird aber vorher beschriftet, also merkt ihn sich der
// Einstieg und der Klick liest ihn spaeter.
let pageContext;

let bound = false;

await startShellPage(async context => {
  pageContext = context;
  if (!bound) {
    bindContracts(context);
    bound = true;
  }
  await renderContracts(context);

  // "Vertrag oeffnen" aus den Hinweisen. Die Kennung kommt als ?open= an, weil sie einen
  // Seitenwechsel ueberstehen muss - frueher wurde dafuer nach dem Wechsel ein Ereignis geschickt,
  // was nur ging, solange beide Seiten dasselbe Dokument waren.
  //
  // Hier drin ist es weiterhin ein Ereignis: bindContracts hoert darauf, und die Detailansicht
  // oeffnet nur dieser eine Weg - sie wird nicht exportiert, und dafuer ist kein Grund.
  const open = new URLSearchParams(location.search).get('open');
  if (open) {
    history.replaceState(null, '', location.pathname);
    emitAppEvent('contract:open', { id: open });
  }
}, { primaryAction: () => ['contracts.new', () => newContract(pageContext)] });
