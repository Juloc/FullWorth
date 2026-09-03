// Registers the FullWorth service worker. External file (not inline) so it complies with the strict
// Content-Security-Policy set by UseFinanceSecurityHeaders.
if ('serviceWorker' in navigator) {
  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => { /* PWA is progressive enhancement */ });
  });
}

// Small shell extensions are loaded here because this file is present on every authenticated app
// page and is already allowed by the CSP. The Coach modules own their own DOM and degrade safely if
// they cannot be loaded; they never bypass the existing BFF for finance data.
function loadCoachExtension() {
  if (!document.querySelector('link[data-fullworth-coach]')) {
    const stylesheet = document.createElement('link');
    stylesheet.rel = 'stylesheet';
    stylesheet.href = '/features/coach.css';
    stylesheet.dataset.fullworthCoach = '1';
    document.head.appendChild(stylesheet);
  }
  import('/features/coach-shell.js').catch(() => { /* optional shell extension */ });
  import('/features/transaction-review-controls.js').catch(() => { /* optional transaction review extension */ });
}

if (document.readyState === 'loading')
  document.addEventListener('DOMContentLoaded', loadCoachExtension, { once: true });
else
  loadCoachExtension();
