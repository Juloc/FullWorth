// Der Einstieg der Seite "intelligence" (#154).
//
// Sprache, Rechte, Raum, Navigation, Topbar und Sperre erledigt startShellPage - fuer jede Seite
// gleich. page.js selbst hat sich nicht geaendert.
//
// Diese Seite holt ihre Dienste selbst aus core/services.js, nimmt also keinen Kontext entgegen.

import { startShellPage } from '../../../app/shell.js';
import { renderIntelligence } from './page.js';

await startShellPage(() => renderIntelligence());
