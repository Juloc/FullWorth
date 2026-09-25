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
import { openBankConnection } from '../../features/bank-connections.js';

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
  reportBankReturn(context);
}, { primaryAction: () => ['accounts.add', () => openAddAccount(pageContext)] });

// Die Rueckkehr von der Bank. BankingApplication leitet auf "/?bankConnected=…" bzw. "/?bankError=…"
// um; die Startseite reicht beides hierher weiter, weil die Nachricht zu den Konten gehoert und der
// Benutzer sie dort lesen soll, wo er gleich nachsieht.
//
// Bis #154 stand das in app.js: dort war der Ansichtswechsel nach "accounts" ein Schritt im selben
// Dokument, und der Hinweis ueberlebte ihn. Seit die Konten eine eigene Adresse haben, ueberlebt
// nichts einen Wechsel ausser dem, was in der Adresse steht - deshalb reisen die Werte mit.
function reportBankReturn(context) {
  const params = new URLSearchParams(location.search);
  const connected = params.get('bankConnected');
  const error = params.get('bankError');
  if (!connected && !error) return;

  history.replaceState(null, '', location.pathname);
  if (connected) {
    context.toast(context.get('accounts.connected').replace('{name}', () => connected), 6000);
    return;
  }

  const known = {
    access_denied: 'accounts.connectCancelled',
    app_invalid_callback: 'accounts.connectExpired',
    app_not_configured: 'accounts.notConfigured',
    app_missing_parameters: 'accounts.connectFailed',
    reauthorization_required: 'accounts.connectReauth'
  };
  context.toast(context.get(known[error] || 'accounts.connectFailed'), 8000);
}
