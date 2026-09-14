// Local UI audit harness. Serves the REAL wwwroot and stubs only the BFF, so every page and every
// dialog can be rendered and measured without a login - which is the point: verifying a layout must
// not require credentials.
//
//   node ops/ui-harness/server.mjs            # http://127.0.0.1:8095
//   node ops/ui-harness/server.mjs 8096       # different port
//
// It is a development tool and is never served by the app. Two things it deliberately does NOT do:
// hide when it cannot answer (see the X-Harness-Fallback header below), and keep its own copy of any
// page - the pages whose HTML is inlined in C# are read out of the source at startup.
import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { extname, join, normalize, resolve } from 'node:path';

const REPO_ROOT = resolve(import.meta.dirname, '..', '..');
const ROOT = process.argv[3] || join(REPO_ROOT, 'src', 'FullWorth.Web', 'wwwroot');
const PORT = Number(process.argv[2] || 8095);

const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8', '.webmanifest': 'application/manifest+json',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.webp': 'image/webp',
  '.ico': 'image/x-icon', '.woff2': 'font/woff2', '.woff': 'font/woff', '.map': 'application/json',
  // application/wasm is required for streaming instantiation; without it the .NET runtime falls back
  // to a slower path and Chrome logs a warning, which reads like a bundle problem when it is not.
  '.wasm': 'application/wasm', '.dat': 'application/octet-stream', '.blat': 'application/octet-stream'
};

// Injected before app.js so the fetch stub is installed before any module runs.
const INJECT = '<script src="/__fixtures.js"></script>\n  <script type="module" src="/app.js">';

// The PWA registration is skipped under the harness. Not because of anything in sw.js: the embedded
// browsers used to look at these pages do not allow service workers at all - registering ANY script,
// including a trivial correctly-served one, fails with "An unknown error occurred when fetching the
// script." while a plain fetch of the same URL returns 200. Chrome logs that itself, so the app's own
// .catch() cannot silence it, and a permanent error in the console makes the one rule this harness
// exists for - check the console after a frontend change - useless.
//
// Nur der Registrierungsaufruf wird ersetzt, der Rest der Datei bleibt. Sie lud einmal auch den
// Coach nach; seit der eine gewöhnliche Seite ist, registriert sie nur noch den Service Worker.
const SW_REGISTRATION = "navigator.serviceWorker.register('/sw.js')";
const SW_REGISTRATION_STUB =
  "Promise.reject(new Error('ui-harness: service workers are not available in this browser'))";

// Die Import-Seiten hielten ihr Markup einmal in C#-Stringliteralen, und diese Harness las die
// Literale samt ihrer Routen aus dem Quelltext - damit eine bearbeitete Seite hier auch bearbeitet
// ankam. Seit sie unter pages/settings/import/ liegen, ist das nicht mehr nötig: sie werden
// ausgeliefert wie jede andere Seite.

async function serveFile(res, path, injectInto) {
  const body = await readFile(path);
  const type = TYPES[extname(path).toLowerCase()] || 'application/octet-stream';
  if (injectInto) {
    let html = body.toString('utf8');
    html = html.replace('<script type="module" src="/app.js">', INJECT);
    res.writeHead(200, { 'content-type': type, 'cache-control': 'no-store' });
    return res.end(html);
  }
  res.writeHead(200, { 'content-type': type, 'cache-control': 'no-store' });
  res.end(body);
}

// The settings panel writes, so its fixture has to remember. Module scope, not per request.
const instanceSettings = [
  { key: 'EnableBanking:ApplicationName', section: 'enableBanking', label: 'Anwendungsname', hint: 'So heißt diese Installation gegenüber Enable Banking und den Banken.', kind: 'text', value: 'FullWorth', stored: false, source: 'default', readOnly: false },
  { key: 'EnableBanking:RedirectUrl', section: 'enableBanking', label: 'Rückleit-Adresse', hint: 'Abgeleitet aus der Adresse, unter der diese Installation erreicht wurde.', kind: 'url', value: 'https://fullworth.local/connect/enable-banking/callback', stored: false, source: 'default', readOnly: true },
  { key: 'FinTs:ProductId', section: 'banking', label: 'Produkt-ID', hint: 'Banken wie die ING verlangen für FinTS eine registrierte Produkt-ID.', kind: 'text', value: '', stored: false, source: 'default', readOnly: false },
  { key: 'Sync:IntervalMinutes', section: 'sync', label: 'Aufwachintervall (Minuten)', hint: 'Wie oft der Hintergrunddienst nachsieht.', kind: 'integer', value: '60', stored: true, source: 'stored', readOnly: false, minimum: 5, maximum: 1440 },
  { key: 'Registration:Enabled', section: 'registration', label: 'Registrierung offen', hint: 'Nach dem ersten Konto schließt sie sich automatisch.', kind: 'boolean', value: 'false', stored: false, source: 'environment', readOnly: true },
  { key: 'Logging:LogLevel:Default', section: 'logging', label: 'Protokollstufe', hint: 'Wirkt sofort, ohne Neustart.', kind: 'choice', value: 'Information', stored: false, source: 'default', readOnly: false, choices: ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'None'] }
];

createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://127.0.0.1');
    let path = decodeURIComponent(url.pathname);

    // Wer hier antwortet, sagt es. LayoutStabilityTests startet diese Harness auf einem festen Port
    // und wartete vorher nur darauf, dass IRGENDJEMAND dort antwortet - lief schon ein anderer
    // Server darauf (die Cloud-Harness stand auf demselben Port), band node nicht, und der Test maß
    // still die falsche Anwendung. Eine Messung, die das Falsche misst, ist schlimmer als keine.
    if (path === '/__harness') {
      res.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' });
      return res.end(JSON.stringify({ harness: 'fullworth', root: ROOT }));
    }
    if (path === '/__fixtures.js') return serveFile(res, join(import.meta.dirname, 'fixtures.js'), false);
    if (path === '/pwa/register-sw.js') {
      const source = await readFile(join(ROOT, 'pwa', 'register-sw.js'), 'utf8');
      if (!source.includes(SW_REGISTRATION))
        console.warn('[harness] register-sw.js changed: the service-worker stub no longer matches.');
      res.writeHead(200, { 'content-type': TYPES['.js'], 'cache-control': 'no-store' });
      return res.end(source.replace(SW_REGISTRATION, SW_REGISTRATION_STUB));
    }
    // secure-fetch.js requires a { token } payload before any write, so the harness must answer this
    // like the real host does or every POST fails before it reaches a fixture.
    if (path === '/auth/antiforgery') {
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ token: 'harness-token', headerName: 'X-CSRF-TOKEN' }));
    }

    // The stub answers in the browser, but anything that still reaches the server gets an empty 200
    // rather than a 401 - a 401 would make secure-fetch bounce to the login page.
    // Import fixtures, so the confirmation step can be walked end to end without a backend. One row is
    // deliberately invalid: the point of the review is that a bad row is shown WITH its reason and
    // cannot be selected.
    // Log every write body BEFORE anything can answer it. secure-fetch.js captures nativeFetch at
    // module load, so a page-side fetch override never sees these requests - the server log is the only
    // place the payload a page really sends can be read. This has to sit above the fixture map: put it
    // below and every logged path is exactly the one a fixture already returned, i.e. none of them.
    let writeBody = '';
    if (req.method !== 'GET' && req.method !== 'HEAD'
      && (path.startsWith('/bff/') || path.startsWith('/api/') || path.startsWith('/auth/'))) {
      for await (const chunk of req) writeBody += chunk;
      // The content type belongs in this line. A JSON body sent as text/plain is refused by ASP.NET
      // before the endpoint runs, and the only symptom on screen is a field that reloads empty - so
      // the header is the first thing worth seeing when a save "does nothing".
      console.log(
        `${req.method} ${path} [${req.headers['content-type'] || 'no content-type'}] ${writeBody.slice(0, 900)}`);
    }

    const JOB = '22222222-2222-2222-2222-222222222222';
    const IMPORT = {
      'fullworth-spaces': [{ id: '11111111-1111-1111-1111-111111111111', name: 'Haushalt', baseCurrency: 'EUR', role: 'owner', isDefault: true }],
      'accounts': [{ id: 'a1', displayName: 'Girokonto', institutionName: 'Sparkasse', ibanLast4: '2051', isActive: true, currency: 'EUR' },
                   { id: 'a2', displayName: 'Tagesgeld', institutionName: 'ING', ibanLast4: '5030', isActive: true, currency: 'EUR' }],
      'categories': [{ id: 'c1', name: 'Lebensmittel' }, { id: 'c2', name: 'Restaurant' }, { id: 'c3', name: 'Gehalt' }],
      'portfolios': [], 'securities': [], 'investment-import/jobs': [],
      'import-mapping/detect': { rowCount: 5, headers: ['Datum','Betrag','Empfänger','Verwendungszweck','Kategorie','Konto'],
        preview: [{Datum:'2026-09-08',Betrag:'-42,19',Empfänger:'REWE Markt GmbH',Verwendungszweck:'Einkauf',Kategorie:'Lebensmittel',Konto:'DE02…2051'}] },
      'import-mapping/upload': { jobId: JOB, sourceRows: 5, ready: 4, errors: 1 },
      'summary': { sourceAccounts: [{ source: 'DE02…2051', count: 5 }],
        sourceCategories: [{ source: 'Lebensmittel', count: 1 }, { source: 'Restaurant', count: 1 },
                           { source: 'Gehalt', count: 1 }, { source: 'Sonstige Ausgaben', count: 2 }] },
      // Two of the four valid rows are already booked, one of them found inside the file itself.
      'duplicate-preview': { candidates: [
        { id: 'k1', status: 'duplicate', reason: 'existing' },
        { id: 'k2', status: 'new', reason: null },
        { id: 'k3', status: 'new', reason: null },
        { id: 'k4', status: 'duplicate', reason: 'in_file' }
      ], duplicates: 2, unmapped: 0, fresh: 2 },
      'candidates': [
        { id: 'k1', sourceAccount: 'DE02…2051', bookingDate: '2026-09-08', amount: -42.19, currency: 'EUR', counterparty: 'REWE Markt GmbH', description: 'Einkauf', categoryText: 'Lebensmittel', duplicateStatus: 'new', validationStatus: 'ready', validationError: null },
        { id: 'k2', sourceAccount: 'DE02…2051', bookingDate: '2026-09-07', amount: -18.90, currency: 'EUR', counterparty: 'Trattoria da Enzo mit einem sehr langen Namen zum Umbruchtest', description: 'Abendessen', categoryText: 'Restaurant', duplicateStatus: 'new', validationStatus: 'ready', validationError: null },
        { id: 'k3', sourceAccount: 'DE02…2051', bookingDate: '2026-08-28', amount: 2810.44, currency: 'EUR', counterparty: 'Arbeitgeber AG', description: 'Gehalt August', categoryText: 'Gehalt', duplicateStatus: 'new', validationStatus: 'ready', validationError: null },
        { id: 'k4', sourceAccount: 'DE02…2051', bookingDate: '2026-08-20', amount: -9.99, currency: 'EUR', counterparty: 'Spotify', description: 'Abo', categoryText: null, duplicateStatus: 'new', validationStatus: 'ready', validationError: null },
        { id: 'k5', sourceAccount: 'DE02…2051', bookingDate: null, amount: 0, currency: 'EUR', counterparty: 'Zeile ohne Datum', description: '', categoryText: null, duplicateStatus: 'new', validationStatus: 'error', validationError: 'Kein gültiges Buchungsdatum in Spalte "Datum".' }
      ],
      // Must stay ABOVE 'import-jobs': the rollback path contains that substring too.
      'rollback': { jobId: JOB, removed: 3, kept: 1 },
      'commit': { imported: 3, duplicates: 0, total: 3 },
      'import-jobs': [
        { id: JOB, fileName: 'umsaetze.csv', adapterKey: 'mapped_csv', status: 'completed', sourceRowCount: 5, readyCount: 4, duplicateCount: 2, importedCount: 3, errorCount: 1, createdAt: '2026-09-09T08:12:00Z', completedAt: '2026-09-09T08:12:30Z', rolledBackAt: null, rollbackAvailable: true },
        { id: '33333333-3333-3333-3333-333333333333', fileName: 'alter-export.csv', adapterKey: 'mapped_csv', status: 'rolled_back', sourceRowCount: 2, readyCount: 2, duplicateCount: 0, importedCount: 2, errorCount: 0, createdAt: '2026-09-01T10:00:00Z', completedAt: '2026-09-01T10:00:20Z', rolledBackAt: '2026-09-02T10:00:00Z', rollbackAvailable: false }
      ]
    };
    // Only for BFF paths. Without this guard the substring match also caught real module requests -
    // /features/accounts.js contains "accounts", /features/categories.js contains "categories" - and
    // answered them with JSON, which the browser rejects as a module script. The import page happened
    // to survive because it loads a single module whose name matches no fixture key.
    if (path.startsWith('/bff/') || path.startsWith('/api/')) {
      for (const [key, value] of Object.entries(IMPORT)) {
        if (path.includes(key)) { res.writeHead(200, { 'content-type': 'application/json' }); return res.end(JSON.stringify(value)); }
      }
    }
    // The admin page talks to /auth/admin/* directly (not through the BFF), so the browser-side stub
    // never sees it. One user is an admin, one is disabled, one is pending deletion.
    if (path.startsWith('/auth/admin/')) {
      // Before anything is written, because that is where the real check sits. ASP.NET refuses a body
      // whose content type is not JSON before the endpoint runs at all, and a harness that accepted it
      // would happily serve the exact request that made the settings form look broken.
      if (req.method !== 'GET' && !String(req.headers['content-type'] || '').includes('application/json')) {
        res.writeHead(415, { 'content-type': 'application/json' });
        return res.end(JSON.stringify({ error: 'unsupported_media_type' }));
      }

      const users = [
        { id: 'u1111111-1111-1111-1111-111111111111', email: 'admin@fullworth.local', isAdmin: true, isDisabled: false, deletionRequestedAt: null, activeSessionCount: 2, twoFactorEnabled: true, createdAt: '2026-01-04T09:00:00Z', lastSessionSeenAt: '2026-09-09T07:45:00Z', deletionScheduledFor: null },
        { id: 'u2222222-2222-2222-2222-222222222222', email: 'eine.sehr.lange.mailadresse.zum.umbruchtest@beispiel-domain.de', isAdmin: false, isDisabled: true, deletionRequestedAt: null, activeSessionCount: 0, twoFactorEnabled: false, createdAt: '2026-03-12T12:00:00Z', lastSessionSeenAt: '2026-08-01T10:00:00Z', deletionScheduledFor: null },
        { id: 'u3333333-3333-3333-3333-333333333333', email: 'weg@beispiel.de', isAdmin: false, isDisabled: false, deletionRequestedAt: '2026-09-05T08:00:00Z', activeSessionCount: 1, twoFactorEnabled: false, createdAt: '2026-05-20T08:00:00Z', lastSessionSeenAt: '2026-09-04T18:20:00Z', deletionScheduledFor: '2026-09-12T08:00:00Z' }
      ];
      res.writeHead(200, { 'content-type': 'application/json' });

      // The two panels that are driven entirely by their server response. Without stubs they would
      // fall through to the user list below and render as an empty box, which is exactly the kind of
      // "looks fine, is broken" this harness exists to catch.
      if (path.startsWith('/auth/admin/instance-settings')) {
        if (req.method === 'PUT') {
          // Stored, not echoed. A stub that always answered with the same fixture would show an empty
          // field after a successful save and look exactly like the bug it is meant to catch.
          const body = JSON.parse(writeBody || '{}');
          const target = instanceSettings.find(setting => setting.key === body.key);
          if (target) {
            target.value = body.value || '';
            target.stored = Boolean(body.value);
            target.source = body.value ? 'stored' : 'default';
          }
        }
        return res.end(JSON.stringify(instanceSettings));
      }


      if (path === '/auth/admin/vault') {
        return res.end(JSON.stringify({
          factor: 'totp',
          elevated: false,
          revealsLeft: 0,
          elevatedUntil: null,
          entries: [
            { reference: 'infra.data-encryption-key', group: 'Infrastruktur', label: 'Datenschlüssel', description: 'Entschlüsselt jede verschlüsselte Spalte dieser Installation.', stored: true, requiresFreshFactor: true, hint: null },
            { reference: 'infra.banking-key', group: 'Infrastruktur', label: 'Banking-API-Schlüssel', description: 'Der Schlüssel zwischen Anwendung und Banking-Modul.', stored: true, requiresFreshFactor: false, hint: null },
            { reference: 'signin.google-client-secret', group: 'Anmeldeanbieter', label: 'Google Client Secret', description: 'Aus der Google Cloud Console, verschlüsselt gespeichert.', stored: false, requiresFreshFactor: false, hint: 'nicht gesetzt' },
            { reference: 'bank.fints.0000', group: 'Bankzugänge', label: 'FinTS-Zugang · ING', description: 'Anmeldename und PIN, mit denen sich diese Installation bei der Bank meldet.', stored: true, requiresFreshFactor: false, hint: null }
          ]
        }));
      }

      if (path.endsWith('/overview')) {
        return res.end(JSON.stringify({ users: 3, active: 2, disabled: 1, pendingDeletion: 1, failedDeletion: 0, admins: 1 }));
      }
      const detail = users.find(user => path.includes(user.id));
      if (detail) {
        return res.end(JSON.stringify({
          user: detail,
          sessions: [
            { deviceName: 'Pixel 8 Pro · Chrome', lastSeenAt: '2026-09-09T07:45:00Z', active: true },
            { deviceName: 'Windows · Edge', lastSeenAt: '2026-09-02T21:10:00Z', active: false }
          ]
        }));
      }
      return res.end(JSON.stringify({ items: users, total: users.length }));
    }
    if (path.startsWith('/bff/') || path.startsWith('/api/') || path.startsWith('/auth/')) {
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end('[]');
    }

    if (path === '/') path = '/index.html';
    if (path.endsWith('/')) path += 'index.html';
    const full = normalize(join(ROOT, path));
    if (!full.startsWith(normalize(ROOT))) { res.writeHead(403); return res.end('no'); }

    try {
      const info = await stat(full);
      if (info.isFile()) return serveFile(res, full, path === '/index.html');
    } catch { /* fall through to the SPA shell */ }

    // SPA fallback, and it announces itself. A silent fallback once made a perfectly reachable page
    // (the import centre, whose HTML lives in C#) look broken, and the harness itself was then used
    // as evidence that the product was broken. The header lets an audit tell "this is the SPA shell"
    // from "this is the real page" - never remove it.
    res.setHeader('X-Harness-Fallback', path);
    return serveFile(res, join(ROOT, 'index.html'), true);
  } catch (error) {
    res.writeHead(500, { 'content-type': 'text/plain' });
    res.end(String(error));
  }
}).listen(PORT, '127.0.0.1', () => console.log(`ui-harness on http://127.0.0.1:${PORT} serving ${ROOT}`));
