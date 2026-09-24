// Der Einstieg der Startseite (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.
//
// Diese Seite traegt zwei Dinge, die es nur hier gibt, weil sie unter "/" liegt:
//
//  - Die Rueckkehr von der Bank. BankingApplication leitet nach dem Verbinden auf
//    "/?bankConnected=…" bzw. "/?bankError=…" - also hierher, obwohl die Nachricht zu den Konten
//    gehoert. Sie wird deshalb dorthin weitergereicht, statt sie hier anzuzeigen und den Benutzer
//    danach selbst suchen zu lassen.
//  - Den Assistenten nach der Registrierung. Er lief bisher in app.js, weil man nach dem
//    Registrieren hier landet.

import { startShellPage } from '../../app/shell.js';
import { renderDashboard, bindDashboard, toggleDashboardEdit } from './page.js';
import { renderDashboardInsights } from '../insights/page.js';
import { createAccessSetup } from '../settings/access-setup.js';
import { openBankingSetup } from '../settings/bank-connections/page.js';

// Bevor irgendetwas gezeichnet wird: die Antwort der Bank gehoert auf die Kontenseite. Ein Umweg
// ueber das erste Bild hier waere ein sichtbares Aufblitzen der Startseite.
const params = new URLSearchParams(location.search);
if (params.has('bankConnected') || params.has('bankError')) {
  location.replace('/accounts' + location.search);
}

// Die Hauptaktion der Topbar gehoert der Seite. Der Kontext steht erst fest, wenn
// gezeichnet wird - der Knopf wird aber vorher beschriftet, also merkt ihn sich der
// Einstieg und der Klick liest ihn spaeter.
let pageContext;

let bound = false;

await startShellPage(async context => {
  pageContext = context;
  if (!bound) {
    bindDashboard(context);
    bound = true;
  }
  await Promise.all([renderDashboard(context), renderDashboardInsights(context)]);

  await createAccessSetup(context, (status, options) => openBankingSetup(context, status, options))
    .maybeOpenRegistrationOnboarding();
}, { primaryAction: () => ['dashboard.edit', () => toggleDashboardEdit(pageContext), 'edit'] });
