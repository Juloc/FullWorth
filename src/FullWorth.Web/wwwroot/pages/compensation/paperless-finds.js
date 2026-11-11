// Die Karte "Aus Paperless": die Suche einrichten, die Funde einer Art zur Pruefung zeigen, einen Fund
// pruefen oder verwerfen. Gemeinsam fuer Lohnabrechnungen und Arbeitsvertraege - sie unterscheiden sich in
// der Art, den Typen, die vorgeschlagen werden, den Texten und darin, wie eine Zeile ihre gelesenen Werte zusammenfasst.
//
// Die Suche wird nicht getippt, sondern aus dem zusammengesetzt, was in Paperless wirklich liegt: erst die
// Dokumenttypen, dann - weil dieselbe Art bei Banken, Vermietern und Arbeitgebern vorkommt - von wem. Die
// Treffer samt ihren Tags stehen vor dem Speichern da; so sieht man, ob die eigene Ablage stimmt.
import { $, esc, attr, spaceId, api, json, bffUrl } from './shared.js';

const PAPERLESS = 'api/purchases/receipt-imports/paperless';
const IMPORTS = 'api/compensation/document-imports';
const PREVIEW_LIMIT = 500;

// Das Original liegt verschluesselt in FullWorth; der Browser holt es selbst, in einem neuen Tab.
export const openImportPdf = importId =>
  window.open(bffUrl(`${IMPORTS}/${importId}/document?fullWorthSpaceId=${spaceId()}`), '_blank', 'noopener');

const quoted = name => `"${String(name).replace(/"/g, '')}"`;
const names = (query, field) => [...String(query || '').matchAll(new RegExp(`${field}:"([^"]+)"`, 'g'))].map(m => m[1]);
const any = (field, list) => list.length === 1 ? `${field}:${quoted(list[0])}` : `(${list.map(n => `${field}:${quoted(n)}`).join(' OR ')})`;
export const buildQuery = (types, correspondents) =>
  [types.length ? any('type', types) : '', correspondents.length ? any('correspondent', correspondents) : ''].filter(Boolean).join(' AND ');

const year = created => (created && created >= '2000' ? created.slice(0, 4) : null);

/**
 * kind: 'payslip' | 'contract'. texts: { preset (Name), noun (z. B. "Lohnabrechnungen"), typePattern (welche Typen vorgeschlagen werden), empty }.
 * summarize(item) -> { title, detail } fuer einen ausgelesenen Fund. onReview(item), onDismiss(id) und onChange() melden nach aussen.
 * group(item) -> { key, signature } | null: Funde mit gleichem Schluessel UND gleicher Signatur sind dieselbe Abrechnung mehrfach in Paperless.
 */
export function createFindsCard({ kind, root, texts, summarize, group, onReview, onDismiss, onChange, isActive, onError }) {
  const state = { connected: false, preset: null, items: [], setup: null };
  let timer = null;

  async function load() {
    const space = spaceId(); if (!space) return;
    clearTimeout(timer);
    const [connection, presets, items] = await Promise.all([
      api(`${PAPERLESS}/connection?fullWorthSpaceId=${space}`),
      api(`${PAPERLESS}/presets?fullWorthSpaceId=${space}&kind=${kind}`),
      api(`${IMPORTS}?fullWorthSpaceId=${space}&kind=${kind}`)]);
    Object.assign(state, { connected: connection?.configured !== false && !!connection?.isEnabled, preset: (presets || [])[0] || null, items: items || [] });
    if (state.connected && !state.preset && !state.setup) await openSetup();
    render();
    onChange?.();
    // Gefundene Dokumente werden im Hintergrund gelesen: solange welche warten, sieht die Liste nach.
    if (state.items.some(item => item.status === 'pending'))
      timer = setTimeout(() => { if (isActive()) load().catch(onError); }, 5000);
  }

  // ---- Suche einrichten ----------------------------------------------------------------------

  async function openSetup() {
    const space = spaceId();
    const options = state.options ||= await api(`${PAPERLESS}/options?fullWorthSpaceId=${space}`);
    const byName = new Map(options.documentTypes.map(t => [t.name, t.id]));
    const saved = names(state.preset?.query, 'type').map(n => byName.get(n)).filter(Boolean);
    const suggested = options.documentTypes.filter(t => new RegExp(texts.typePattern, 'i').test(t.name)).map(t => t.id);
    state.setup = { options, types: new Set(saved.length ? saved : suggested), correspondents: null, docs: [], truncated: false, loading: false, error: '', savedCorrespondents: names(state.preset?.query, 'correspondent') };
    await refreshPreview();
  }

  async function refreshPreview() {
    const setup = state.setup;
    const typeNames = setup.options.documentTypes.filter(t => setup.types.has(t.id)).map(t => t.name);
    if (!typeNames.length) { Object.assign(setup, { docs: [], truncated: false }); return; }
    setup.loading = true;
    try {
      const result = await api(`${PAPERLESS}/preview?fullWorthSpaceId=${spaceId()}`, json('POST', { query: buildQuery(typeNames, []), limit: PREVIEW_LIMIT }));
      Object.assign(setup, { docs: result.documents || [], truncated: !!result.truncated, error: '' });
      // Vorgewaehlt: die Absender der gespeicherten Suche, sonst der mit den meisten Treffern - wer Abrechnungen
      // sammelt, hat in aller Regel einen Arbeitgeber, und jede weitere Gruppe waere erst einmal fremd.
      const groups = correspondentGroups(setup);
      const wanted = setup.savedCorrespondents.length
        ? groups.filter(g => setup.savedCorrespondents.includes(g.name)).map(g => g.id)
        : groups.slice(0, 1).map(g => g.id);
      setup.correspondents = new Set([...(setup.correspondents || wanted)].filter(id => groups.some(g => g.id === id)));
    } catch (error) { setup.error = error.message || 'Paperless antwortet nicht.'; }
    finally { setup.loading = false; }
  }

  function correspondentGroups(setup) {
    const nameOf = new Map(setup.options.correspondents.map(c => [c.id, c.name]));
    const tagOf = new Map(setup.options.tags.map(t => [t.id, t.name]));
    const groups = new Map();
    for (const doc of setup.docs) {
      const id = doc.correspondent ?? 0;
      const entry = groups.get(id) || { id, name: nameOf.get(id) || 'Ohne Absender', docs: [], tags: new Map() };
      entry.docs.push(doc);
      for (const tag of doc.tags || []) entry.tags.set(tagOf.get(tag), (entry.tags.get(tagOf.get(tag)) || 0) + 1);
      groups.set(id, entry);
    }
    return [...groups.values()].sort((a, b) => b.docs.length - a.docs.length);
  }

  function setupHtml() {
    const setup = state.setup, { options } = setup;
    const typeIds = [...new Set([...setup.types, ...options.documentTypes.filter(t => new RegExp(texts.typePattern, 'i').test(t.name)).map(t => t.id)])];
    const typeName = new Map(options.documentTypes.map(t => [t.id, t.name]));
    const chips = typeIds.map(id => `<label class="pf-row"><input type="checkbox" data-pf-type="${id}"${setup.types.has(id) ? ' checked' : ''}><span class="pf-row-main"><strong>${esc(typeName.get(id))}</strong></span></label>`).join('');
    const more = options.documentTypes.filter(t => !typeIds.includes(t.id)).map(t => `<option value="${t.id}">${esc(t.name)}</option>`).join('');
    const groups = setup.correspondents ? correspondentGroups(setup) : [];
    const picked = groups.filter(g => setup.correspondents.has(g.id));
    const count = picked.reduce((sum, g) => sum + g.docs.length, 0);
    const query = buildQuery(options.documentTypes.filter(t => setup.types.has(t.id)).map(t => t.name), picked.filter(g => g.id).map(g => g.name));

    const groupHtml = groups.map(g => {
      const years = [...new Set(g.docs.map(d => year(d.created)).filter(Boolean))].sort();
      const range = years.length ? (years.length > 1 ? `${years[0]}–${years[years.length - 1]}` : years[0]) : '';
      const tags = [...g.tags].sort((a, b) => b[1] - a[1]).slice(0, 4).map(([name]) => `<i>${esc(name)}</i>`).join('');
      return `<label class="pf-row"><input type="checkbox" data-pf-corr="${g.id}"${setup.correspondents.has(g.id) ? ' checked' : ''}>`
        + `<span class="pf-row-main"><strong>${esc(g.name)}</strong><small>${g.docs.length} Dokument${g.docs.length === 1 ? '' : 'e'}${range ? ` · ${range}` : ''}</small>`
        + `<small class="pf-example">${esc(g.docs[0].title)}</small>${tags ? `<span class="pf-tags">${tags}</span>` : ''}</span></label>`;
    }).join('');

    const docs = picked.flatMap(g => g.docs).sort((a, b) => (b.created || '').localeCompare(a.created || ''));
    const list = docs.slice(0, 80).map(d => `<li><span>${esc(d.title)}</span><small>${esc(setup.options.documentTypes.find(t => t.id === d.documentType)?.name || '')}</small></li>`).join('');

    return `<div class="pf-setup">
      <div class="pf-step"><span class="pf-step-title">1 · Dokumenttyp in Paperless</span><div class="pf-rows">${chips}</div><select data-pf-more aria-label="Weiteren Dokumenttyp hinzufügen"><option value="">Weiterer Typ …</option>${more}</select></div>
      ${setup.error ? `<p class="pf-error">${esc(setup.error)}</p>` : ''}
      ${groups.length ? `<div class="pf-step"><span class="pf-step-title">2 · Von wem?</span><div class="pf-rows">${groupHtml}</div>${setup.truncated ? `<p class="extended-note">Es gibt mehr als ${PREVIEW_LIMIT} Treffer, gezeigt werden die neuesten.</p>` : ''}</div>`
        : `<p class="extended-note">${setup.loading ? 'Paperless wird durchsucht …' : setup.types.size ? 'Zu diesen Typen gibt es in Paperless keine Dokumente.' : 'Wähle mindestens einen Dokumenttyp.'}</p>`}
      ${picked.length ? `<details class="pf-docs"><summary>${count} Treffer ansehen</summary><ul>${list}</ul>${docs.length > 80 ? `<p class="extended-note">… und ${docs.length - 80} weitere.</p>` : ''}</details>` : ''}
      <div class="pf-actions"><button class="btn btn-primary" type="button" data-pf-save ${picked.length ? '' : 'disabled'}>${count ? `${count} ${esc(texts.noun)} holen` : 'Suche speichern'}</button>${state.preset ? '<button class="btn btn-secondary" type="button" data-pf-cancel>Abbrechen</button>' : ''}</div>
      ${query ? `<p class="pf-query"><small>Suche in Paperless: <code>${esc(query)}</code></small></p>` : ''}
    </div>`;
  }

  async function saveSetup() {
    const setup = state.setup;
    const groups = correspondentGroups(setup).filter(g => setup.correspondents.has(g.id));
    const query = buildQuery(setup.options.documentTypes.filter(t => setup.types.has(t.id)).map(t => t.name), groups.filter(g => g.id).map(g => g.name));
    const body = { name: state.preset?.name || texts.preset, query, autoImport: state.preset?.autoImport ?? true, analyzeAutomatically: state.preset?.analyzeAutomatically ?? true, currency: state.preset?.currency, kind };
    const saved = state.preset
      ? await api(`${PAPERLESS}/presets/${state.preset.id}?fullWorthSpaceId=${spaceId()}`, json('PUT', body))
      : await api(`${PAPERLESS}/presets?fullWorthSpaceId=${spaceId()}`, json('POST', body));
    state.setup = null;
    await run(saved.id);
  }

  // ---- Funde -----------------------------------------------------------------------------------

  // Dieselbe Abrechnung mehrfach in Paperless (gescannt und digital, doppelt hochgeladen) ist EIN Eintrag
  // mit dem Hinweis, wie oft - bei gleichen Werten. Weichen die Werte ab, sind es verschiedene Fassungen und
  // bleiben einzeln stehen: welche gilt, entscheidet der Benutzer.
  function entries() {
    const result = [];
    const seen = new Map();
    for (const item of state.items) {
      const g = item.status === 'needs_review' && group ? group(item) : null;
      const id = g ? `${g.key}|${g.signature}` : null;
      if (id && seen.has(id)) { seen.get(id).copies.push(item); continue; }
      const entry = { item, copies: [], key: g?.key };
      if (id) seen.set(id, entry);
      result.push(entry);
    }
    const versions = new Map();
    for (const entry of result) if (entry.key) versions.set(entry.key, (versions.get(entry.key) || 0) + 1);
    result.forEach(entry => { entry.versions = versions.get(entry.key) || 1; });
    return result;
  }

  function render() {
    const el = $(root); if (!el) return;
    const { connected, preset, items } = state;
    if (!connected) { el.innerHTML = '<p class="extended-note">Paperless ist noch nicht verbunden. Die Verbindung richtest du unter <a href="/receipts">Belege</a> → Importieren → Paperless-ngx ein.</p>'; return; }
    if (state.setup) { el.innerHTML = setupHtml(); return; }
    const status = preset.lastError ? `Letzte Suche: ${esc(preset.lastError)}` : preset.lastCheckedAt ? `Zuletzt gesucht: ${new Date(preset.lastCheckedAt).toLocaleString('de-DE')}` : 'Noch nicht gesucht.';
    const open = items.some(item => item.status === 'needs_review' || item.status === 'failed');
    el.innerHTML = `<div class="payslip-row"><div><small>${status}</small><small class="pf-query">Suche: <code>${esc(preset.query)}</code></small></div><div>`
      + `<button type="button" class="btn btn-secondary" data-paperless-run>Jetzt suchen</button>`
      + (open ? '<button type="button" class="btn btn-secondary" data-paperless-reread title="Liest die offenen Funde noch einmal aus dem Original">Neu lesen</button>' : '')
      + '<button type="button" class="btn btn-secondary" data-paperless-setup>Suche ändern</button></div></div>'
      + (items.length ? entries().map(row).join('') : `<p class="extended-note">${esc(texts.empty)}</p>`);
  }

  function row({ item, copies, versions }) {
    const summary = item.status === 'needs_review' ? summarize(item) : { title: item.title, detail: esc(item.status === 'pending' ? 'Wird gelesen …' : item.error || 'Konnte nicht gelesen werden.') };
    const notes = [];
    if (copies.length) notes.push(`<small class="pf-copies">${copies.length + 1}× gleich in Paperless <button type="button" class="btn btn-quiet" data-paperless-dupes="${attr(item.id)}">Dubletten verwerfen</button></small>`);
    if (versions > 1) notes.push('<small class="pf-versions">Es gibt Fassungen mit anderen Werten – prüfe, welche gilt.</small>');
    const review = item.status === 'needs_review'
      ? `<button type="button" class="btn btn-secondary" data-paperless-review="${attr(item.id)}">Prüfen</button><button type="button" class="btn btn-secondary" data-open-pdf="${attr(item.id)}">PDF</button>` : '';
    return `<div class="payslip-row"><div><strong>${esc(summary.title)}</strong><small>${summary.detail}</small>${notes.join('')}</div><div>${review}<button type="button" class="btn btn-danger" title="Verwerfen" data-paperless-dismiss="${attr(item.id)}">×</button></div></div>`;
  }

  async function run(presetId = state.preset?.id) {
    await api(`${PAPERLESS}/presets/${presetId}/run?fullWorthSpaceId=${spaceId()}`, { method: 'POST' });
    await load();
  }

  async function dismiss(id) {
    await api(`${IMPORTS}/${id}/dismiss?fullWorthSpaceId=${spaceId()}`, { method: 'POST' });
    await load();
  }

  async function action(event) {
    const input = event.target.closest('[data-pf-type],[data-pf-corr],[data-pf-more]');
    if (input && event.type === 'change') {
      const setup = state.setup;
      if (input.dataset.pfType) setup.types[input.checked ? 'add' : 'delete'](Number(input.dataset.pfType));
      if (input.dataset.pfCorr) setup.correspondents[input.checked ? 'add' : 'delete'](Number(input.dataset.pfCorr));
      if (input.matches('[data-pf-more]') && input.value) setup.types.add(Number(input.value));
      if (!input.dataset.pfCorr) { setup.correspondents = null; await refreshPreview(); }
      render();
      return;
    }
    const button = event.target.closest('button'); if (!button || event.type !== 'click') return;
    if (button.dataset.openPdf) return openImportPdf(button.dataset.openPdf);
    if ('paperlessRun' in button.dataset) return run();
    if ('paperlessSetup' in button.dataset) { state.setup = null; await openSetup(); return render(); }
    if ('pfCancel' in button.dataset) { state.setup = null; return render(); }
    if ('pfSave' in button.dataset) { button.disabled = true; return saveSetup(); }
    if ('paperlessReread' in button.dataset) {
      await api(`${IMPORTS}/reread?fullWorthSpaceId=${spaceId()}&kind=${kind}`, { method: 'POST' });
      return load();
    }
    if (button.dataset.paperlessDupes) {
      const entry = entries().find(e => e.item.id === button.dataset.paperlessDupes);
      for (const copy of entry?.copies || []) { onDismiss?.(copy.id); await api(`${IMPORTS}/${copy.id}/dismiss?fullWorthSpaceId=${spaceId()}`, { method: 'POST' }); }
      return load();
    }
    if (button.dataset.paperlessReview) onReview(state.items.find(x => x.id === button.dataset.paperlessReview));
    if (button.dataset.paperlessDismiss) { const id = button.dataset.paperlessDismiss; onDismiss?.(id); await dismiss(id); }
  }

  const el = $(root);
  el?.addEventListener('click', event => action(event).catch(onError));
  el?.addEventListener('change', event => action(event).catch(onError));
  return { load, state, find: id => state.items.find(x => x.id === id) };
}
