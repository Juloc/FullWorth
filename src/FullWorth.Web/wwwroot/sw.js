// FullWorth service worker.
// Strategy: cache ONLY the static app shell (css/js/locales/manifest/icon). Everything dynamic or
// sensitive — the finance API (/api), the BFF proxy (/bff), auth (/auth), share inbox and connector
// flows — is ALWAYS fetched from the network and NEVER cached, so no financial data lives in the offline cache.
// Bump VERSION to ship a new shell; old caches are purged on activate.

const VERSION = 'v102';
const SHELL_CACHE = `fullworth-shell-${VERSION}`;

// Static, non-sensitive assets safe to precache. No API/BFF/auth paths appear here.
const APP_SHELL = [
  // Everything below is imported by the shell itself, so an offline cold start needs all of it. The
  // fetch handler is network-first with a cache fallback, which hides a gap here while online and
  // only fails once there is no connection - PwaOfflineShellCoverageTests walks the real import
  // graph so the list cannot fall behind again.
  '/security/secure-fetch.js',
  '/ui/money.js',
  '/ui/balance-meaning.js',
  // The Gehalt page is its own HTML page rather than an SPA view, so the shell import graph above
  // does not reach it. It is precached because the payroll engine is pure client-side maths and
  // genuinely works offline. Auth, admin, intelligence and passkeys are deliberately NOT here: every
  // one of them needs the server to do anything, so caching them would only fake availability.
  '/compensation.html',
  '/compensation-history.css',
  '/compensation.css',
  '/features/compensation-benchmarks.js',
  '/features/compensation-extended.js',
  '/features/compensation-history.js',
  '/features/compensation-nav.js',
  '/features/compensation-other-income.js',
  '/features/compensation-shared.js',
  '/features/compensation.js',
  '/security/browser-fetch.js',
  '/styles/features/compensation-benchmarks.css',
  '/styles/features/compensation-other-income.css',
  '/ui/dashboard.js',
  '/ui/lock.js',
  '/ui/privacy.js',
  '/ui/category-picker.js',
  '/ui/chart-scrubber.js',
  '/ui/topbar-metrics.js',
  '/features/access-setup.js',
  '/features/audit.js',
  '/features/categories.js',
  '/features/loans.js',
  '/features/merchants.js',
  '/features/notifications.js',
  '/features/rules.js',
  '/features/sharing.js',
  '/push/push.js',
  '/passkeys/passkeys.js',
  '/passkeys/base64url.js',
  '/styles/tokens.css',
  '/styles/reset.css',
  '/styles/shell.css',
  '/styles/features/insights.css',
  '/styles/components.css',
  '/app.css',
  '/styles/responsive.css',
  '/appearance.css',
  '/design-depth.css',
  '/dialogs.css',
  '/app.js',
  '/core/api.js',
  '/core/state.js',
  '/core/router.js',
  '/core/feature-registry.js',
  '/core/i18n.js',
  '/core/services.js',
  '/core/navigation.js',
  '/core/event-bus.js',
  '/ui/toast.js',
  '/ui/global-search.js',
  '/ui/buttons.js',
  '/ui/confirm.js',
  '/ui/dialog.js',
  '/ui/ux-kit.js',
  '/features/budgets.js',
  '/features/contracts.js',
  '/styles/features/contracts-merge.css',
  '/features/insights.js',
  '/features/tax.js',
  '/features/tax-review-extra.js',
  '/styles/features/tax.css',
  '/styles/features/tax-review-extra.css',
  '/features/pension.js',
  '/styles/features/pension.css',
  '/features/analytics.js',
  '/features/data-completeness.js',
  '/features/transactions.js',
  '/features/networth.js',
  '/styles/features/wealth-assets.css',
  '/features/wealth-real-estate.js',
  '/features/wealth-real-estate-core.js',
  '/features/wealth-real-estate-operations.js',
  '/features/wealth-real-estate-advanced.js',
  '/styles/features/wealth-real-estate.css',
  '/styles/features/wealth-real-estate-operations.css',
  '/styles/features/wealth-real-estate-advanced.css',
  '/features/wealth-specialized-assets.js',
  '/features/wealth-specialized-assets-extra.js',
  '/styles/features/wealth-specialized-assets.css',
  '/features/wealth-investment-consolidation.js',
  '/styles/features/wealth-investment-consolidation.css',
  '/features/wealth-portability.js',
  '/features/investment-performance-ui.js',
  '/styles/features/investment-performance.css',
  '/features/accounts.js',
  '/features/settings.js',
  '/features/accounts-presentation.js',
  '/styles/features/accounts.css',
  '/features/purchases.js',
  '/features/purchases-gpt-normal.js',
  '/features/receipt-imports.js',
  '/features/receipt-import-batch-details.js',
  '/styles/features/receipt-imports.css',
  '/features/receipt-scan-set.js',
  '/styles/features/receipt-scan-set.css',
  '/features/purchase-articles-workspace.js',
  '/styles/features/purchase-articles-workspace.css',
  '/features/purchase-articles-advanced.js',
  '/features/purchase-articles-advanced-actions.js',
  '/features/purchase-discount-actions.js',
  '/features/purchase-price-insights.js',
  '/styles/features/purchase-price-insights.css',
  '/features/purchase-advanced-insights.js',
  '/styles/features/purchase-advanced-insights.css',
  '/features/purchase-receipt-source-review.js',
  '/features/purchases-gpt-test.js',
  '/styles/features/purchases-gpt-test.css',
  '/features/coach-shell.js',
  '/styles/features/coach.css',
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
    caches.open(SHELL_CACHE).then(async (cache) => {
      try {
        // Prefer the current deployment when online. Serving cached JS first can combine a fresh
        // index.html with stale modules after a release and crash the installed PWA.
        const response = await fetch(request);
        if (response && response.ok) await cache.put(request, response.clone());
        return response;
      } catch {
        const cached = await cache.match(request);
        if (cached) return cached;
        throw new Error(`FullWorth offline shell miss: ${url.pathname}`);
      }
    })
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
