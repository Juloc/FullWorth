// Der Dialog "Hinzufügen" (Index.cshtml, #verlauf-add): was in den Verlauf kommt, kommt hierüber - es gibt keine Karte
// mehr am Ende der Seite. Zwei Ebenen: erst WAS (Lohnabrechnung oder Vertrag), dann WIE (Paperless, PDF, von Hand).
// Das Markup steht in der Seite, die Felder tragen ihre alten Ids; dieses Modul schaltet nur, welcher Bereich zu sehen
// ist, und öffnet und schließt den Dialog. extended.js und contracts.js rufen openAdd, wenn es etwas zu prüfen gibt.
const dialog = () => document.getElementById('verlauf-add');

// Welche Ebene ein Bereich hat: 'contract' und 'contract-paperless' gehören zum Vertrag, alles andere zur Lohnabrechnung.
const whatOf = panel => panel.startsWith('contract') ? 'contract' : 'payslip';

function showPanel(name) {
  const root = dialog(); if (!root) return;
  const what = whatOf(name);
  root.querySelectorAll('[data-va-panel]').forEach(panel => { panel.hidden = panel.dataset.vaPanel !== name; });
  root.querySelectorAll('[data-va-what]').forEach(tab => tab.setAttribute('aria-selected', String(tab.dataset.vaWhat === what)));
  root.querySelectorAll('[data-va-tab]').forEach(tab => {
    tab.hidden = tab.dataset.vaFor !== what;
    tab.setAttribute('aria-selected', String(tab.dataset.vaTab === name));
  });
}

/** Öffnet den Dialog im gewünschten Bereich: 'paperless' | 'upload' | 'payslip' | 'contract-paperless' | 'contract'. */
export function openAdd(panel = 'paperless') {
  const root = dialog(); if (!root) return;
  showPanel(panel);
  if (!root.open) root.showModal();
  document.dispatchEvent(new CustomEvent('fw-add-opened', { detail: { panel } }));
}

export function closeAdd() {
  const root = dialog();
  if (root?.open) root.close();
}

const root = dialog();
if (root) {
  root.addEventListener('click', event => {
    const what = event.target.closest('[data-va-what]');
    if (what) { showPanel(what.dataset.vaWhat === 'contract' ? 'contract-paperless' : 'paperless'); return; }
    const tab = event.target.closest('[data-va-tab]');
    if (tab) { showPanel(tab.dataset.vaTab); return; }
    if (event.target.closest('[data-close]') || event.target === root) closeAdd();
  });
  document.getElementById('verlauf-add-open')?.addEventListener('click', () => openAdd('paperless'));
  showPanel('paperless');
}
