// Der Einstieg der Seite "account-detail" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert.
//
// Diese Seite hat kein eigenes page.js: die Kontodetails werden von pages/accounts/page.js
// gezeichnet, weil sie dieselben Daten und dieselben Dialoge benutzen wie die Liste. Nur das Markup
// und das Stylesheet gehoeren ihr allein - und der Weg zurueck fuehrt in die Liste, weshalb die
// Seitenleiste weiterhin "Konten" markiert (NavigationCatalog.SubPages).

import { startShellPage } from '../../../app/shell.js';
import { renderAccountDetail } from '../page.js';

await startShellPage(async context => {
  await renderAccountDetail(context);
});
