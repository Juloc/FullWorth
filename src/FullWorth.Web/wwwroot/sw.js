// FullWorth service worker.
// Strategy: cache ONLY the static app shell (css/js/locales/manifest/icon). Everything dynamic or
// sensitive — the finance API (/api), the BFF proxy (/bff), auth (/auth), share inbox and connector
// flows — is ALWAYS fetched from the network and NEVER cached, so no financial data lives in the offline cache.
// Bump VERSION to ship a new shell; old caches are purged on activate.

const VERSION = 'v135';
const SHELL_CACHE = `fullworth-shell-${VERSION}`;

// Static, non-sensitive assets safe to precache. No API/BFF/auth paths appear here.
const APP_SHELL = [
  // Everything below is imported by the shell itself, so an offline cold start needs all of it. The
  // fetch handler is network-first with a cache fallback, which hides a gap here while online and
  // only fails once there is no connection - PwaOfflineShellCoverageTests walks the real import
  // graph so the list cannot fall behind again.
  '/security/secure-fetch.js',
  '/components/money.js',
  '/components/empty.js',
  '/components/balance-meaning.js',
  // The Gehalt page is its own HTML page rather than an SPA view, so the shell import graph above
  // does not reach it. It is precached because the payroll engine is pure client-side maths and
  // genuinely works offline. Auth, admin, intelligence and passkeys are deliberately NOT here: every
  // one of them needs the server to do anything, so caching them would only fake availability.
  '/security/browser-fetch.js',
  '/styles/mobile-polish.css',
  '/pages/dashboard/page.js',
  '/pages/dashboard/page.css',
  '/app/lock.js',
  '/components/privacy.js',
  '/pages/transactions/category-picker.js',
  '/components/combobox.js',
  '/components/icons.js',
  '/components/list-position.js',
  '/components/chart-scrubber.js',
  '/components/topbar-metrics.js',
  '/pages/settings/access-setup.js',
  '/pages/audit/page.js',
  '/pages/audit/page.css',
  '/pages/categories/page.js',
  '/pages/collections/page.js',
  '/pages/collections/page.css',
  '/pages/categories/page.css',
  '/pages/networth/loans.js',
  '/pages/merchants/page.js',
  '/pages/merchants/page.css',
  '/pages/notifications/page.js',
  '/pages/notifications/page.css',
  '/pages/rules/page.js',
  '/pages/rules/page.css',
  '/pages/settings/sharing.js',
  '/push/push.js',
  '/passkeys/passkeys.js',
  '/passkeys/base64url.js',
  '/styles/tokens.css',
  '/styles/reset.css',
  '/styles/shell.css',
  '/pages/insights/page.css',
  '/styles/components.css',
  '/styles/app.css',
  '/styles/responsive.css',
  '/styles/appearance.css',
  '/styles/design-depth.css',
  '/styles/dialogs.css',
  '/app.js',
  '/core/api.js',
  '/core/html.js',
  '/core/state.js',
  '/core/router.js',
  '/core/feature-registry.js',
  '/core/i18n.js',
  '/core/services.js',
  '/core/navigation.js',
  '/core/event-bus.js',
  '/components/toast.js',
  '/app/global-search.js',
  '/components/buttons.js',
  '/components/confirm.js',
  '/components/dialog.js',
  '/components/form-dialog.js',
  '/components/password-toggle.js',
  '/features/ux-kit.js',
  '/pages/budgets/page.js',
  '/pages/budgets/page.css',
  '/pages/contracts/page.js',
  '/pages/contracts/page.css',
  '/pages/insights/page.js',
  '/pages/tax/page.js',
  '/pages/tax/review-extra.js',
  '/pages/tax/page.css',
  '/pages/pension/page.js',
  '/pages/pension/documents.js',
  '/pages/pension/projection.js',
  '/pages/networth/preview.js',
  '/pages/pension/page.css',
  '/pages/analytics/page.js',
  '/pages/analytics/page.css',
  '/features/data-completeness.js',
  '/pages/transactions/page.js',
  '/pages/transactions/page.css',
  '/pages/networth/page.js',
  '/pages/networth/page.css',
  '/pages/networth/real-estate.js',
  '/pages/networth/real-estate-core.js',
  '/pages/networth/real-estate-operations.js',
  '/pages/networth/real-estate-advanced.js',
  '/pages/networth/specialized-assets.js',
  '/pages/networth/specialized-assets-extra.js',
  '/pages/networth/investment-consolidation.js',
  '/features/wealth-portability.js',
  '/pages/networth/investment-performance-ui.js',
  '/pages/accounts/page.js',
  '/pages/settings/page.js',
  '/pages/settings/page.css',
  '/pages/accounts/presentation.js',
  '/pages/accounts/page.css',
  '/pages/purchases/page.js',
  '/pages/purchases/page.css',
  '/pages/purchases/gpt-normal.js',
  '/pages/purchases/receipt-imports.js',
  '/pages/purchases/receipt-import-batch-details.js',
  '/pages/purchases/receipt-scan-set.js',
  '/pages/purchases/articles-workspace.js',
  '/pages/purchases/articles-advanced.js',
  '/pages/purchases/articles-advanced-actions.js',
  '/pages/purchases/discount-actions.js',
  '/pages/purchases/price-insights.js',
  '/pages/purchases/advanced-insights.js',
  '/pages/purchases/receipt-source-review.js',
  '/pages/coach/page.js',
  '/pages/coach/page.css',
  
  '/components/accessibility-release.js',
  '/app/motion.js',
  '/app/appearance.js',
  '/app/boot.js',
  '/app/menu.js',
  '/pages/settings/intelligence/page.js',
  '/pages/settings/intelligence/page.css',
  '/pages/settings/intelligence/cloud.js',
  '/pages/settings/intelligence/brand-packs.js',
  '/pages/settings/intelligence/jobs.js',
  '/pages/settings/import/page.js',
  '/pages/settings/import/page.css',
  '/pages/settings/import/broker-pdf/page.js',
  '/pages/settings/import/finanzguru/xlsx/page.js',
  '/pages/settings/import/finanzguru/xlsx/page.css',
  '/pages/compensation/page.js',
  '/pages/compensation/page.css',
  '/pages/compensation/shared.js',
  '/pages/compensation/extended.js',
  '/pages/compensation/history.js',
  '/pages/compensation/other-income.js',
  '/pages/compensation/benchmarks.js',
  '/pages/settings/security/passkeys/page.js',
  '/pages/settings/security/passkeys/page.css',
  '/pages/admin/page.js',
  '/pages/admin/instance-settings.js',
  '/pages/admin/vault.js',
  '/pages/admin/page.css',
  '/app/nav-state.js',
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
