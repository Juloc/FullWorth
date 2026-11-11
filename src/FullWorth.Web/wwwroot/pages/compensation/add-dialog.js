// Der Dialog "Hinzufügen" (Index.cshtml, #verlauf-add): Paperless, PDF hochladen, eine Abrechnung oder einen Vertragsstand
// von Hand erfassen. Alles, was in den Verlauf kommt, kommt hierüber - es gibt keine Karte mehr am Ende der Seite.
// Das Markup steht in der Seite, die Felder tragen ihre alten Ids; dieses Modul schaltet nur, welcher Bereich zu sehen
// ist, und öffnet und schließt den Dialog. extended.js und contracts.js rufen openAdd, wenn es etwas zu prüfen gibt.
const dialog = () => document.getElementById('verlauf-add');

function showPanel(name) {
  const root = dialog(); if (!root) return;
  root.querySelectorAll('[data-va-panel]').forEach(panel => { panel.hidden = panel.dataset.vaPanel !== name; });
  root.querySelectorAll('[data-va-tab]').forEach(tab => tab.setAttribute('aria-selected', String(tab.dataset.vaTab === name)));
}

/** Öffnet den Dialog im gewünschten Bereich: 'paperless' | 'upload' | 'payslip' | 'contract'. */
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
    const tab = event.target.closest('[data-va-tab]');
    if (tab) { showPanel(tab.dataset.vaTab); return; }
    if (event.target.closest('[data-close]') || event.target === root) closeAdd();
  });
  document.getElementById('verlauf-add-open')?.addEventListener('click', () => openAdd('paperless'));
  showPanel('paperless');
}
