// Der Einstieg der Seite "settings" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert: es bekommt denselben Kontext wie vorher aus der
// alten Huelle, nur dass ihn jetzt die Seite erzeugt statt app.js fuer alle.
//
// Diese Seite bekommt zwei Dinge hineingereicht, die ihr nicht gehoeren: den Zugangs-Assistenten
// und die Bankeinstellungen. Beide stellte bisher app.js her, weil sie auch anderswo gebraucht
// wurden; jetzt stellt sie die Seite her, die sie noch benutzt. Der Assistent braucht dafuer den
// Kontext, also entsteht er beim ersten Zeichnen und nicht schon beim Laden des Moduls.

import { startShellPage } from '../../app/shell.js';
import { renderSettings, bindSettings } from './page.js';
import { createAccessSetup } from '../../features/access-setup.js';
import { openBankingSetup, renderBankingSettings } from '../../features/bank-connections.js';

let accessSetup;
let bound = false;

await startShellPage(async context => {
  if (!bound) {
    accessSetup = createAccessSetup(context, (status, options) => openBankingSetup(context, status, options));
    bindSettings(context);
    bound = true;
  }
  await renderSettings(context, { accessSetup, renderBankingSettings });
});
