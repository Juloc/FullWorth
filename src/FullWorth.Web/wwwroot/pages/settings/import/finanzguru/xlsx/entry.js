// Der Einstieg der Seite "finanzguru-xlsx" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich.
//
// page.js exportiert nichts: das Modul baut seine Seite beim Laden selbst auf. Genau so hing es
// auch in der alten Huelle drin (ein blosses import ohne Namen), und daran aendert der Umzug
// nichts - nur dass es jetzt geladen wird, wenn diese Adresse aufgerufen wird, und nicht mehr
// bei jedem Start der Anwendung.

import { startShellPage } from '../../../../../app/shell.js';
import './page.js';

await startShellPage(() => {});
