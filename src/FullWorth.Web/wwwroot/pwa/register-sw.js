// Registers the FullWorth service worker. External file (not inline) so it complies with the strict
// Content-Security-Policy set by UseFinanceSecurityHeaders.
//
// sw.js calls skipWaiting()+clients.claim() so a new deploy takes over as fast as possible - but that
// only decides which worker answers the NEXT network request. A tab or installed PWA left open across
// a deploy keeps running the JS it already loaded, against markup a newer sw.js may now shape
// differently (a page fetched fresh would get both from the same version; an old session gets old JS
// against whatever the new worker starts serving underneath it). controllerchange fires exactly when
// clients.claim() hands that open session to a new worker, so this is the one moment a stale session
// can reliably notice and reload itself onto a consistent version - the missing half of the same
// no-prompt, always-current design sw.js already chose.
if ('serviceWorker' in navigator) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => { /* PWA is progressive enhancement */ });
  });
  let reloaded = false;
  navigator.serviceWorker.addEventListener('controllerchange', () => {
    if (reloaded) return;
    reloaded = true;
    location.reload();
  });
}
