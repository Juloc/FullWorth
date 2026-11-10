// Die Karte "Aus Paperless": die Suche einrichten, die Funde einer Art zur Pruefung zeigen, einen Fund
// pruefen oder verwerfen. Gemeinsam fuer Lohnabrechnungen und Arbeitsvertraege - sie unterscheiden sich in
// der Art, im Suchbegriff, in den Texten und darin, wie eine Zeile ihre gelesenen Werte zusammenfasst.
import { $, esc, attr, spaceId, api, json, bffUrl } from './shared.js';

const PAPERLESS = 'api/purchases/receipt-imports/paperless';

// Das Original liegt verschluesselt in FullWorth; der Browser holt es selbst, in einem neuen Tab.
export const openImportPdf = importId =>
  window.open(bffUrl(`api/compensation/document-imports/${importId}/document?fullWorthSpaceId=${spaceId()}`), '_blank', 'noopener');

/**
 * kind: 'payslip' | 'contract'. texts: { query (Vorgabe der Suche), preset (Name), search (Knopf), empty }.
 * summarize(item) -> { title, detail } fuer einen ausgelesenen Fund. onReview(item), onDismiss(id) und onChange() melden nach aussen.
 */
export function createFindsCard({ kind, root, texts, summarize, onReview, onDismiss, onChange, isActive, onError }) {
  const state = { connected: false, preset: null, items: [] };
  let timer = null;

  async function load() {
    const space = spaceId(); if (!space) return;
    clearTimeout(timer);
    const [connection, presets, items] = await Promise.all([
      api(`${PAPERLESS}/connection?fullWorthSpaceId=${space}`),
      api(`${PAPERLESS}/presets?fullWorthSpaceId=${space}&kind=${kind}`),
      api(`api/compensation/document-imports?fullWorthSpaceId=${space}&kind=${kind}`)]);
    Object.assign(state, { connected: connection?.configured !== false && !!connection?.isEnabled, preset: (presets || [])[0] || null, items: items || [] });
    render();
    onChange?.();
    // Gefundene Dokumente werden im Hintergrund gelesen: solange welche warten, sieht die Liste nach.
    if (state.items.some(item => item.status === 'pending'))
      timer = setTimeout(() => { if (isActive()) load().catch(onError); }, 5000);
  }

  function render() {
    const el = $(root); if (!el) return;
    const { connected, preset, items } = state;
    if (!connected) { el.innerHTML = '<p class="extended-note">Paperless ist noch nicht verbunden. Die Verbindung richtest du unter <a href="/receipts">Belege</a> → Importieren → Paperless-ngx ein.</p>'; return; }
    if (!preset) {
      el.innerHTML = `<div class="form-grid paperless-setup"><label>Suche in Paperless<input data-paperless-query value="${attr(texts.query)}" maxlength="4000"></label><p class="extended-note">Paperless-Suchsyntax, zum Beispiel <code>${esc(texts.query)}</code> oder <code>tag:Gehalt</code>. Neue Treffer werden regelmäßig geholt und hier zur Prüfung abgelegt.</p><button class="btn btn-primary" type="button" data-paperless-create>${esc(texts.search)}</button></div>`;
      return;
    }
    const status = preset.lastError ? `Letzte Suche: ${esc(preset.lastError)}` : preset.lastCheckedAt ? `Zuletzt gesucht: ${new Date(preset.lastCheckedAt).toLocaleString('de-DE')}` : 'Noch nicht gesucht.';
    el.innerHTML = `<div class="payslip-row"><div><small>${status}</small></div><div><button type="button" class="btn btn-secondary" data-paperless-run>Jetzt suchen</button></div></div>`
      + (items.length ? items.map(row).join('') : `<p class="extended-note">${esc(texts.empty)}</p>`);
  }

  function row(item) {
    const summary = item.status === 'needs_review' ? summarize(item) : { title: item.title, detail: item.status === 'pending' ? 'Wird gelesen …' : esc(item.error || 'Konnte nicht gelesen werden.') };
    const review = item.status === 'needs_review'
      ? `<button type="button" class="btn btn-secondary" data-paperless-review="${attr(item.id)}">Prüfen</button><button type="button" class="btn btn-secondary" data-open-pdf="${attr(item.id)}">PDF</button>` : '';
    return `<div class="payslip-row"><div><strong>${esc(summary.title)}</strong><small>${summary.detail}</small></div><div>${review}<button type="button" class="btn btn-danger" title="Verwerfen" data-paperless-dismiss="${attr(item.id)}">×</button></div></div>`;
  }

  async function run(presetId = state.preset?.id) {
    await api(`${PAPERLESS}/presets/${presetId}/run?fullWorthSpaceId=${spaceId()}`, { method: 'POST' });
    await load();
  }

  async function dismiss(id) {
    await api(`api/compensation/document-imports/${id}/dismiss?fullWorthSpaceId=${spaceId()}`, { method: 'POST' });
    await load();
  }

  async function action(event) {
    const button = event.target.closest('button'); if (!button) return;
    if (button.dataset.openPdf) return openImportPdf(button.dataset.openPdf);
    if ('paperlessRun' in button.dataset) return run();
    if ('paperlessCreate' in button.dataset) {
      const query = $(root).querySelector('[data-paperless-query]').value.trim();
      if (!query) throw new Error('Bitte eine Suche für Paperless angeben.');
      const preset = await api(`${PAPERLESS}/presets?fullWorthSpaceId=${spaceId()}`, json('POST', { name: texts.preset, query, autoImport: true, kind }));
      return run(preset.id);
    }
    if (button.dataset.paperlessReview) onReview(state.items.find(x => x.id === button.dataset.paperlessReview));
    if (button.dataset.paperlessDismiss) { const id = button.dataset.paperlessDismiss; onDismiss?.(id); await dismiss(id); }
  }

  $(root)?.addEventListener('click', event => action(event).catch(onError));
  return { load, state, find: id => state.items.find(x => x.id === id) };
}
