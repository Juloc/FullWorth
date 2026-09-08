// Primary navigation for the standalone Gehalt page (/compensation.html).
//
// The Gehalt tool is its own page rather than an SPA view, which previously left it with a one-off "back"
// arrow and no navigation. This module renders the app's real sidebar and mobile bottom bar here so the
// primary nav is always visible, and only dialogs ever cover it on mobile.
//
// The item list below is kept in sync with the app shell (index.html) — it is markup, not logic, and the
// shared styling comes from styles/shell.css. The SPA routes on real paths (core/router.js maps a view to
// "/<view>"), so each entry is a plain link and needs no client-side router here.
const SIDEBAR_ITEMS = `
      <a data-nav-item data-view="dashboard" class="active"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="8" height="8" rx="2"/><rect x="13" y="3" width="8" height="8" rx="2"/><rect x="3" y="13" width="8" height="8" rx="2"/><rect x="13" y="13" width="8" height="8" rx="2"/></svg><span data-i18n="nav.dashboard">Übersicht</span></a>
      <a data-nav-item data-view="accounts"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 9.5 12 4l9 5.5"/><path d="M5 10v7m4.5-7v7m5-7v7m4.5-7v7"/><path d="M3 20h18"/></svg><span data-i18n="nav.accounts">Konten</span></a>
      <a data-nav-item data-view="transactions"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 4v13m0 0-3-3m3 3 3-3"/><path d="M17 20V7m0 0-3 3m3-3 3 3"/></svg><span data-i18n="nav.transactions">Buchungen</span></a>
      <a data-nav-item data-view="contracts"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3h7l4 4v14H7Z"/><path d="M14 3v4h4"/><path d="M10 12h5m-5 4h5"/></svg><span data-i18n="nav.contracts">Verträge</span></a>
      <a data-nav-item data-view="analytics"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20V10m5.5 10V4m5.5 16v-8m5 8V7"/></svg><span data-i18n="nav.analytics">Analyse</span></a>
      <a data-nav-item data-view="networth"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m2 17 6-6 4 4 10-10"/><path d="M16 5h6v6"/></svg><span data-i18n="nav.networth">Vermögen</span></a>
      <a data-nav-item data-view="purchases"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3h12v18l-2-1.5L14 21l-2-1.5L10 21l-2-1.5L6 21Z"/><path d="M9 8h6m-6 4h6"/></svg><span data-i18n="nav.purchases">Käufe</span></a>
      <a data-nav-item data-view="tax"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 4h14v16H5Z"/><path d="M8 8h8M8 12h2m4 0h2M8 16h2m4 0h2"/></svg><span data-i18n="nav.tax">Steuern</span></a>
      <a data-nav-item data-view="budgets"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="8.5"/><path d="M12 3.5V12l6 4"/></svg><span data-i18n="nav.budgets">Budgets</span></a>
      <a data-nav-item type="button" data-compensation-link title="Gehalt & Benefits" aria-label="Gehalt & Benefits"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 18V8m5 10V5m5 13v-7m5 7V9"/><path d="M3 21h18"/></svg><span>Gehalt &amp; Benefits</span></a>
      <div class="nav-sep" role="separator"></div>
      <a data-nav-item data-view="categories"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 6h6l2 2h8v10H4Z"/></svg><span data-i18n="nav.categories">Kategorien</span></a>
      <a data-nav-item data-view="rules"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16"/><circle cx="9" cy="7" r="2"/><circle cx="15" cy="12" r="2"/><circle cx="7" cy="17" r="2"/></svg><span data-i18n="nav.rules">Regeln</span></a>
      <a data-nav-item data-view="notifications"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9a6 6 0 0 1 12 0c0 5 2 6 2 6H4s2-1 2-6"/><path d="M10 20a2 2 0 0 0 4 0"/></svg><span data-i18n="nav.notifications">Benachrichtigungen</span></a>
`;

const BOTTOM_ITEMS = `
  <a data-nav-item data-view="dashboard" class="active"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="8" height="8" rx="2"/><rect x="13" y="3" width="8" height="8" rx="2"/><rect x="3" y="13" width="8" height="8" rx="2"/><rect x="13" y="13" width="8" height="8" rx="2"/></svg><span data-i18n="nav.dashboard">Übersicht</span></a>
  <a data-nav-item data-view="contracts"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3h7l4 4v14H7Z"/><path d="M14 3v4h4"/><path d="M10 12h5m-5 4h5"/></svg><span data-i18n="nav.contracts">Verträge</span></a>
  <a data-nav-item data-view="analytics"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20V10m5.5 10V4m5.5 16v-8m5 8V7"/></svg><span data-i18n="nav.analytics">Analyse</span></a>
  <a data-nav-item data-view="networth"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m2 17 6-6 4 4 10-10"/><path d="M16 5h6v6"/></svg><span data-i18n="nav.networth">Vermögen</span></a>
  <a data-nav-item id="bottom-more" type="button"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="19" cy="12" r="1.6"/></svg><span data-i18n="nav.more">Mehr</span></a>
`;

render(document.querySelector('[data-app-sidebar]'), SIDEBAR_ITEMS, true);
render(document.querySelector('[data-app-bottom-nav]'), BOTTOM_ITEMS, false);

function render(host, markup, withBrand) {
  if (!host) return;
  const brand = withBrand
    ? '<div class="brand"><img class="brand-logo" src="/branding/fullworth-logo.svg" alt="" width="26" height="26">'
      + '<span class="brand-name"><strong>FullWorth</strong><span class="brand-beta">Alpha</span></span></div>'
    : '';
  const nav = withBrand ? `<nav id="nav" aria-label="Sections">${markup}</nav>` : markup;
  host.innerHTML = brand + nav;

  // data-view is the SPA's in-page selector; on this standalone page it becomes a real link.
  host.querySelectorAll('[data-nav-item]').forEach(item => {
    const view = item.getAttribute('data-view');
    item.setAttribute('href', view ? `/${view}` : '/');
    item.removeAttribute('data-nav-item');
  });

  // Mark this page as the active entry so the Gehalt link reads as "you are here".
  host.querySelectorAll('[data-compensation-link], [data-compensation-mobile]').forEach(item => {
    item.setAttribute('href', '/compensation.html');
    item.setAttribute('aria-current', 'page');
    item.classList.add('active');
  });
}
