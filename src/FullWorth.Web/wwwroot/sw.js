// FullWorth service worker.
//
// Er cacht genau eine Sache: die Hinweisseite ohne Verbindung (offline/). Scheitert das Laden einer
// Seite am Netz, antwortet er mit ihr statt mit der Fehlerseite des Browsers. Sonst tut er nichts -
// jede Anfrage geht ans Netz, wie ohne ihn, und nichts von der Anwendung landet in seinem Cache.
//
// Warum nicht mehr: eine Seite ist HTML, und das HTML der Anwendung cacht er nie - es traegt
// persoenliche Daten. Ohne ihr HTML oeffnet keine Seite offline, also haette ein Vorrat ihrer Module
// nichts gerettet. Bis #154 stand hier trotzdem ein Vorrat von ueber hundert Dateien, und der traf
// nicht einmal: die Seiten verlangen ihre Dateien seit MapStaticAssets unter der Adresse mit
// Fingerabdruck, der Vorrat hielt sie unter ihrem Namen. Auch Gehalt, das als Ausnahme galt, rechnet
// auf dem Server (api/compensation/calculate) und kann ohne Netz nichts.
//
// Fuer Geschwindigkeit sorgt der Browser-Cache: Dateien mit Fingerabdruck sind immutable.
// Push-Benachrichtigungen (unten) sind der andere Grund, dass es diesen Worker gibt.
//
// VERSION erhoehen, wenn sich die Hinweisseite aendert; alte Caches raeumt activate weg.

const VERSION = 'v155';
const OFFLINE_CACHE = `fullworth-offline-${VERSION}`;

// Die Hinweisseite und alles, was sie laedt. PwaOfflinePageTests prueft, dass die Liste genau das ist.
const OFFLINE_PAGE = '/offline/index.html';
const OFFLINE_ASSETS = [
  OFFLINE_PAGE,
  '/offline/offline.css',
  '/offline/offline.js',
  '/styles/tokens.css',
  '/styles/reset.css',
  '/styles/components.css',
  '/app/theme.js',
  '/pwa/icon.svg',
  // Die Schrift, die tokens.css per @font-face nennt.
  '/fonts/nunito-variable.woff2',
];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(OFFLINE_CACHE).then((cache) => cache.addAll(OFFLINE_ASSETS)));
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((key) => key !== OFFLINE_CACHE).map((key) => caches.delete(key))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.mode !== 'navigate') return;
  // Die Antwort des Netzes wird durchgereicht und nie abgelegt - auch eine 404 oder 500 ist eine
  // Antwort. Nur wenn es gar keine gibt, kommt die Hinweisseite, unter der Adresse, die geladen
  // werden sollte: "Erneut versuchen" ist dann ein schlichtes Neuladen.
  event.respondWith(fetch(request).catch(async () =>
    (await caches.match(OFFLINE_PAGE)) || Response.error()));
});

self.addEventListener('push', (event) => {
  let data = {};
  try { data = event.data ? event.data.json() : {}; } catch (e) { data = {}; }
  const title = data.title || 'FullWorth';
  event.waitUntil(self.registration.showNotification(title, {
    body: data.body || '',
    icon: '/pwa/icon.svg',
    data: { url: data.url || '/' },
  }));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const url = (event.notification.data && event.notification.data.url) || '/';
  event.waitUntil(self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((wins) => {
    for (const w of wins) { if ('focus' in w) return w.focus(); }
    if (self.clients.openWindow) return self.clients.openWindow(url);
  }));
});
