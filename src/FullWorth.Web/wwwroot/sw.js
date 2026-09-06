// FullWorth service worker.
// Strategy: cache ONLY the static app shell (css/js/locales/manifest/icon). Everything dynamic or
// sensitive — the finance API (/api), the BFF proxy (/bff), auth (/auth), share inbox and connector
// flows — is ALWAYS fetched from the network and NEVER cached, so no financial data lives in the offline cache.
// Bump VERSION to ship a new shell; old caches are purged on activate.

const VERSION = 'v68';
const SHELL_CACHE = `fullworth-shell-${VERSION}`;

// Static, non-sensitive assets safe to precache. No API/BFF/auth paths appear here.
const APP_SHELL = [
  '/app.css',
  '/appearance.css',
  '/parity-completion.css',
  '/design-depth.css',
  '/dialogs.css',
  '/app.js',
  '/ui/ux-kit.js',
  '/features/contracts.js',
  '/features/analytics.js',
  '/features/transactions.js',
  '/features/networth.js',
  '/features/wealth-assets.css',
  '/features/wealth-real-estate.js',
  '/features/wealth-real-estate-core.js',
  '/features/wealth-real-estate-operations.js',
  '/features/wealth-real-estate-advanced.js',
  '/features/wealth-real-estate.css',
  '/features/wealth-real-estate-operations.css',
  '/features/wealth-real-estate-advanced.css',
  '/features/wealth-specialized-assets.js',
  '/features/wealth-specialized-assets-extra.js',
  '/features/wealth-specialized-assets.css',
  '/features/wealth-investment-consolidation.js',
  '/features/wealth-investment-consolidation.css',
  '/features/wealth-portability.js',
  '/features/investment-performance-ui.js',
  '/investment-performance.css',
  '/features/accounts-ux.js',
  '/features/accounts-ux.css',
  '/features/purchases.js',
  '/features/purchases-gpt-normal.js',
  '/features/receipt-imports.js',
  '/features/receipt-import-batch-details.js',
  '/features/receipt-imports.css',
  '/features/receipt-scan-local-builder.js',
  '/features/receipt-scan-local-builder.css',
  '/features/receipt-scan-ai.js',
  '/features/receipt-scan-ai.css',
  '/features/purchase-articles-workspace.js',
  '/features/purchase-articles-workspace.css',
  '/features/purchase-articles-advanced-installer.js',
  '/features/purchase-articles-advanced-actions.js',
  '/features/purchase-discount-actions.js',
  '/features/purchase-price-insights.js',
  '/features/purchase-price-insights.css',
  '/features/purchase-advanced-insights.js',
  '/features/purchase-advanced-insights.css',
  '/features/purchase-receipt-source-review.js',
  '/features/purchases-gpt-test.js',
  '/features/purchases-gpt-test.css',
  '/features/coach-shell.js',
  '/features/transaction-review-controls.js',
  '/features/coach.css',
  '/ui/accessibility-release.js',
  '/ui/motion.js',
  '/ui/appearance.js',
  '/theme-init.js',
  '/pwa/standalone-init.js',
  '/manifest.json',
  '/pwa/icon.svg',
  '/pwa/icon-192.png',
  '/pwa/icon-512.png',
  '/pwa/apple-touch-icon-180.png',
  '/fonts/BarlowCondensed-400.woff2',
  '/fonts/BarlowCondensed-500.woff2',
  '/fonts/BarlowCondensed-600.woff2',
  '/locales/de.json',
  '/locales/en.json',
];

function isSensitive(url) {
  return url.pathname.startsWith('/api')
    || url.pathname.startsWith('/bff')
    || url.pathname.startsWith('/auth')
    || url.pathname.startsWith('/share')
    || url.pathname.startsWith('/connect');
}

function isStaticAsset(url) {
  return /\.(css|js|mjs|json|svg|png|woff2?)$/i.test(url.pathname);
}

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(SHELL_CACHE).then((cache) => cache.addAll(APP_SHELL)));
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((key) => key !== SHELL_CACHE).map((key) => caches.delete(key))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  if (request.method !== 'GET') return;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return;
  if (isSensitive(url)) return;
  if (!isStaticAsset(url)) return;
  event.respondWith(
    caches.open(SHELL_CACHE).then((cache) =>
      cache.match(request).then((cached) => {
        const network = fetch(request)
          .then((response) => {
            if (response && response.ok) cache.put(request, response.clone());
            return response;
          })
          .catch(() => cached);
        return cached || network;
      })
    )
  );
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
