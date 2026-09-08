// Primary navigation for the standalone Gehalt page (/compensation.html).
//
// The Gehalt tool is its own page rather than an SPA view, which previously left it with a one-off "back"
// arrow and no navigation. This module renders the app's real sidebar and mobile bottom bar here so the
// primary nav is always visible, and only dialogs ever cover it on mobile.
//
// The items stay <button> elements on purpose: styles/shell.css and styles/responsive.css target
// ".sidebar button" and "#bottom-nav button", so reusing the element keeps the shared desktop AND mobile
// styling (including the 56px mobile touch targets) instead of re-inventing it. Navigation is a real page
// load because the SPA routes on paths (core/router.js maps a view to "/<view>").
const SIDEBAR_ITEMS = `
      <button data-view="dashboard" class="active"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="8" height="8" rx="2"/><rect x="13" y="3" width="8" height="8" rx="2"/><rect x="3" y="13" width="8" height="8" rx="2"/><rect x="13" y="13" width="8" height="8" rx="2"/></svg><span data-i18n="nav.dashboard">Übersicht</span></button>
      <button data-view="accounts"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M3 9.5 12 4l9 5.5"/><path d="M5 10v7m4.5-7v7m5-7v7m4.5-7v7"/><path d="M3 20h18"/></svg><span data-i18n="nav.accounts">Konten</span></button>
      <button data-view="transactions"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 4v13m0 0-3-3m3 3 3-3"/><path d="M17 20V7m0 0-3 3m3-3 3 3"/></svg><span data-i18n="nav.transactions">Buchungen</span></button>
      <button data-view="contracts"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3h7l4 4v14H7Z"/><path d="M14 3v4h4"/><path d="M10 12h5m-5 4h5"/></svg><span data-i18n="nav.contracts">Verträge</span></button>
      <button data-view="analytics"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20V10m5.5 10V4m5.5 16v-8m5 8V7"/></svg><span data-i18n="nav.analytics">Analyse</span></button>
      <button data-view="networth"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m2 17 6-6 4 4 10-10"/><path d="M16 5h6v6"/></svg><span data-i18n="nav.networth">Vermögen</span></button>
      <button data-view="purchases"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 3h12v18l-2-1.5L14 21l-2-1.5L10 21l-2-1.5L6 21Z"/><path d="M9 8h6m-6 4h6"/></svg><span data-i18n="nav.purchases">Käufe</span></button>
      <button data-view="tax"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 4h14v16H5Z"/><path d="M8 8h8M8 12h2m4 0h2M8 16h2m4 0h2"/></svg><span data-i18n="nav.tax">Steuern</span></button>
      <button data-view="budgets"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="8.5"/><path d="M12 3.5V12l6 4"/></svg><span data-i18n="nav.budgets">Budgets</span></button>
      <button type="button" data-compensation-link title="Gehalt & Benefits" aria-label="Gehalt & Benefits"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 18V8m5 10V5m5 13v-7m5 7V9"/><path d="M3 21h18"/></svg><span>Gehalt &amp; Benefits</span></button>
      <div class="nav-sep" role="separator"></div>
      <button data-view="categories"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 6h6l2 2h8v10H4Z"/></svg><span data-i18n="nav.categories">Kategorien</span></button>
      <button data-view="rules"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M4 12h16M4 17h16"/><circle cx="9" cy="7" r="2"/><circle cx="15" cy="12" r="2"/><circle cx="7" cy="17" r="2"/></svg><span data-i18n="nav.rules">Regeln</span></button>
      <button data-view="notifications"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9a6 6 0 0 1 12 0c0 5 2 6 2 6H4s2-1 2-6"/><path d="M10 20a2 2 0 0 0 4 0"/></svg><span data-i18n="nav.notifications">Benachrichtigungen</span></button>
`;

const BOTTOM_ITEMS = `
  <button data-view="dashboard" class="active"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="8" height="8" rx="2"/><rect x="13" y="3" width="8" height="8" rx="2"/><rect x="3" y="13" width="8" height="8" rx="2"/><rect x="13" y="13" width="8" height="8" rx="2"/></svg><span data-i18n="nav.dashboard">Übersicht</span></button>
  <button data-view="contracts"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 3h7l4 4v14H7Z"/><path d="M14 3v4h4"/><path d="M10 12h5m-5 4h5"/></svg><span data-i18n="nav.contracts">Verträge</span></button>
  <button data-view="analytics"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20V10m5.5 10V4m5.5 16v-8m5 8V7"/></svg><span data-i18n="nav.analytics">Analyse</span></button>
  <button data-view="networth"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="m2 17 6-6 4 4 10-10"/><path d="M16 5h6v6"/></svg><span data-i18n="nav.networth">Vermögen</span></button>
  <button id="bottom-more" type="button"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="5" cy="12" r="1.6"/><circle cx="12" cy="12" r="1.6"/><circle cx="19" cy="12" r="1.6"/></svg><span data-i18n="nav.more">Mehr</span></button>
`;

render(document.querySelector('[data-app-sidebar]'), SIDEBAR_ITEMS, true);
render(document.querySelector('[data-app-bottom-nav]'), BOTTOM_ITEMS, false);

function render(host, markup, withBrand) {
  if (!host) return;
  const brand = withBrand
    ? '<div class="brand"><img class="brand-logo" src="/branding/fullworth-logo.svg" alt="" width="26" height="26">'
      + '<span class="brand-name"><strong>FullWorth</strong><span class="brand-beta">Alpha</span></span></div>'
    : '';
  host.innerHTML = brand + (withBrand ? `<nav id="nav" aria-label="Sections">${markup}</nav>` : markup);

  host.querySelectorAll('button[data-view]').forEach(button => {
    const view = button.getAttribute('data-view');
    button.addEventListener('click', () => window.location.assign(view ? `/${view}` : '/'));
  });

  // This page IS the Gehalt view, so its entry reads as "you are here" and does not navigate.
  host.querySelectorAll('[data-compensation-link], [data-compensation-mobile]').forEach(button => {
    button.classList.add('active');
    button.setAttribute('aria-current', 'page');
    button.addEventListener('click', () => window.location.assign('/compensation.html'));
  });

  // The mobile "more" sheet is opened by the app shell's own JS, which this standalone page does not load.
  // Send the user into the app rather than leaving a button that does nothing.
  host.querySelectorAll('#bottom-more').forEach(button => {
    button.addEventListener('click', () => window.location.assign('/'));
  });
}
