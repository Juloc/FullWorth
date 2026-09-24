// Der Einstieg der Seite "rules" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.

import { startShellPage } from '../../app/shell.js';
import { renderRules, bindRules, newRule } from './page.js';

// Die Hauptaktion der Topbar gehoert der Seite. Der Kontext steht erst fest, wenn
// gezeichnet wird - der Knopf wird aber vorher beschriftet, also merkt ihn sich der
// Einstieg und der Klick liest ihn spaeter.
let pageContext;

let bound = false;

await startShellPage(async context => {
  pageContext = context;
  if (!bound) {
    bindRules(context);
    bound = true;
  }
  await renderRules(context);

  // "Regel erstellen" aus dem Coach: die Absicht kommt als ?new=1 an, weil sie einen Seitenwechsel
  // ueberstehen muss. Danach faellt sie aus der Adresse, sonst oeffnete ein Neuladen sie wieder.
  if (new URLSearchParams(location.search).has('new')) {
    history.replaceState(null, '', location.pathname);
    newRule(context);
  }
}, { primaryAction: () => ['rules.new', () => newRule(pageContext)] });
