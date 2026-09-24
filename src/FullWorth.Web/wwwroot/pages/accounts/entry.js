// Der Einstieg der Seite "accounts" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.

import { startShellPage } from '../../app/shell.js';
import { renderAccounts, bindAccounts, openAddAccount } from './page.js';
// bindAccounts nimmt einen zweiten Weg entgegen: "Bankverbindung oeffnen" aus der Kontoliste. Der
// fuehrt in einen Dialog dieser Seite, nicht auf die Bankverbindungs-Seite - deshalb wird er hier
// hineingereicht und nicht zu einer Navigation.
import { openBankConnection } from '../settings/bank-connections/page.js';

// Die Hauptaktion der Topbar gehoert der Seite. Der Kontext steht erst fest, wenn
// gezeichnet wird - der Knopf wird aber vorher beschriftet, also merkt ihn sich der
// Einstieg und der Klick liest ihn spaeter.
let pageContext;

let bound = false;

await startShellPage(async context => {
  pageContext = context;
  if (!bound) {
    bindAccounts(context, () => openBankConnection(context));
    bound = true;
  }
  await renderAccounts(context);
}, { primaryAction: () => ['accounts.add', () => openAddAccount(pageContext)] });
