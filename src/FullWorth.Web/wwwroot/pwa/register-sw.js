// Registers the FullWorth service worker. External file (not inline) so it complies with the strict
// Content-Security-Policy set by UseFinanceSecurityHeaders.
if ('serviceWorker' in navigator) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => { /* PWA is progressive enhancement */ });
  });
}
