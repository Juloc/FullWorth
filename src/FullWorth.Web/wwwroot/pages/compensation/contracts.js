// Der Reiter "Arbeitsverträge": die bestätigten Vertragsstände als Änderungsprotokoll (neueste zuerst, nur
// geänderte Felder, alt durchgestrichen und neu daneben), das Diagramm "Brutto laut Vertrag", Hinweise aus dem
// Abgleich mit den Abrechnungen - und darunter das Finden in Paperless und das Prüfen und Bestätigen.
import { confirmMessage } from '../../components/confirm.js';
import { spriteHref } from '../../components/sprite.js';
import { $, $$, esc, attr, euro2, spaceId, api, json, notify, val, setVal, checked, setChecked } from './shared.js';
import { drawContractChart } from './contract-chart.js';
import { createFindsCard, openImportPdf } from './paperless-finds.js';

const state = { overview: { states: [], events: [], hints: [] }, payslips: [] };
let reviewedImport = null;
const dmy = new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' });
const icon = name => `<svg viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref(name)}"></use></svg>`;
const BADGE_TONE = { Start: 'start', 'Gehaltserhöhung': 'raise', 'Gehaltsänderung': 'change', Arbeitszeit: 'info', Benefits: 'info' };

const finds = createFindsCard({
  kind: 'contract', root: '#contract-finds', isActive: () => $('#tab-contracts')?.classList.contains('active'), onError: showError,
  texts: { preset: 'Arbeitsverträge', noun: 'Verträge und Nachträge', typePattern: 'arbeitsvertrag|anstellungsvertrag|vertragsnachtrag|vertragsänderung', empty: 'Keine Verträge zur Prüfung.' },
  summarize: item => {
    const x = item.extraction;
    const kind = x.kind === 'addendum' ? 'Nachtrag' : x.kind === 'contract' ? 'Vertrag' : item.title;
    return { title: x.validFrom ? `${kind} ab ${dmy.format(new Date(`${x.validFrom}T12:00:00`))}` : kind, detail: `${x.grossMonthly == null ? '—' : euro2.format(x.grossMonthly)} brutto / Monat${x.weeklyHours == null ? '' : ` · ${x.weeklyHours} h`}` };
  },
  onReview: item => fillForm(item.extraction, item.id, item.title),
  onDismiss: id => { if (reviewedImport === id) resetForm(); }
});

init();

function init() {
  const tab = $('#tab-contracts');
  if (!tab) return;
  $('[data-extended-tab="contracts"]')?.addEventListener('click', () => loadAll().catch(showError));
  $('#space-select')?.addEventListener('change', () => { if (tab.classList.contains('active')) loadAll().catch(showError); });
  $('#ct-save').addEventListener('click', () => save().catch(showError));
  $('#ct-add-extra').addEventListener('click', () => addExtra());
  $('#ct-extras').addEventListener('click', event => event.target.closest('[data-remove-extra]')?.closest('.ct-extra').remove());
  $('#contract-log').addEventListener('click', event => logAction(event).catch(showError));
  $('#contract-hints').addEventListener('click', event => {
    const button = event.target.closest('[data-prefill]');
    if (button) prefill(Number(button.dataset.prefill));
  });
  new ResizeObserver(() => drawChart()).observe($('#contract-chart'));
}

async function loadAll() {
  const space = spaceId(); if (!space) return;
  const [overview, payslips] = await Promise.all([
    api(`api/compensation/contracts?fullWorthSpaceId=${space}`),
    api(`api/compensation/payslips?fullWorthSpaceId=${space}`),
    finds.load()]);
  state.overview = overview || state.overview;
  state.payslips = payslips || [];
  render();
}

function render() {
  const { hints } = state.overview;
  $('#contract-hints').innerHTML = hints.map((hint, index) => {
    const detected = hint.kind === 'change_detected';
    return `<div class="po-hint ${detected ? 'po-hint-info' : 'po-hint-warn'}">${icon(detected ? 'ui-info' : 'ui-alert')}<span>${esc(hint.message)}</span>${detected ? `<button type="button" class="btn btn-secondary" data-prefill="${index}">Stand anlegen</button>` : ''}</div>`;
  }).join('');
  drawChart();
  $('#contract-log').innerHTML = state.overview.events.length
    ? `<ol class="co-timeline">${state.overview.events.map(eventHtml).join('')}</ol>`
    : '<p class="extended-note">Noch keine Vertragsstände. Lass Verträge in Paperless suchen oder erfasse den ersten Stand unten von Hand.</p>';
}

const drawChart = () => { const host = $('#contract-chart'); if (host) drawContractChart(host, state.overview.states.length ? { states: state.overview.states.map(s => ({ id: s.id, state: s.state })), payslips: state.payslips } : { states: [], payslips: [] }); };

// ---- Änderungsprotokoll ----------------------------------------------------------------------

function eventHtml(event) {
  const when = new Date(`${event.validFrom}T12:00:00`);
  const doc = event.document;
  const changes = event.changed.map(change => `<div class="co-change"><dt>${esc(change.label)}</dt><dd>${change.old == null ? '' : `<s class="co-old">${esc(change.old)}</s><span class="co-arrow" aria-hidden="true">→</span>`}${change.new == null ? '<span class="co-gone">entfällt</span>' : `<strong class="co-value">${esc(change.new)}</strong>`}${change.old == null && event.badge !== 'Start' ? '<span class="co-new">neu</span>' : ''}${change.percent == null ? '' : `<span class="co-pct ${change.percent >= 0 ? 'is-up' : 'is-down'}">${change.percent >= 0 ? '+' : '−'}${Math.abs(change.percent).toLocaleString('de-DE', { maximumFractionDigits: 1 })} %</span>`}</dd></div>`).join('');
  const unchanged = event.unchanged.length
    ? `<details class="co-unchanged"><summary>${event.unchanged.length} unverändert</summary><dl>${event.unchanged.map(f => `<div class="co-change"><dt>${esc(f.label)}</dt><dd>${esc(f.value)}</dd></div>`).join('')}</dl></details>` : '';
  const source = `${doc ? `Paperless-Dokument ${doc.paperlessDocumentId}` : 'von Hand erfasst'} · bestätigt am ${dmy.format(new Date(event.confirmedAt))}${doc?.readConfidencePercent != null ? ` · Auslesesicherheit ${Number(doc.readConfidencePercent).toLocaleString('de-DE', { maximumFractionDigits: 0 })} %` : ''}`;
  return `<li class="co-event"><div class="co-date"><strong>${String(when.getDate()).padStart(2, '0')}.${String(when.getMonth() + 1).padStart(2, '0')}.</strong><small>${when.getFullYear()}</small></div>`
    + `<div class="co-body"><div class="co-head"><h3>${esc(event.title)}</h3><span class="po-badge co-badge co-badge-${BADGE_TONE[event.badge] || 'plain'}">${esc(event.badge)}</span></div>`
    + `<dl class="co-changes">${changes}</dl>${unchanged}`
    + `<div class="co-foot"><p class="po-foot">${esc(source)}</p><div class="co-actions">${doc ? `<button type="button" class="btn btn-secondary" data-open-pdf="${attr(doc.importId)}">PDF</button><button type="button" class="btn btn-secondary" data-open-url="${attr(doc.paperlessUrl)}">Paperless</button>` : ''}<button type="button" class="btn btn-danger" data-delete-state="${attr(event.id)}" title="Löschen" aria-label="Stand vom ${when.getFullYear()} löschen">×</button></div></div></div></li>`;
}

async function logAction(event) {
  const button = event.target.closest('button'); if (!button) return;
  if (button.dataset.openPdf) return openImportPdf(button.dataset.openPdf);
  if (button.dataset.openUrl) return window.open(button.dataset.openUrl, '_blank', 'noopener,noreferrer');
  if (button.dataset.deleteState) {
    if (!await confirmMessage({ message: 'Diesen Vertragsstand löschen? Das abgelegte PDF wird mitgelöscht.', title: 'Vertragsstand löschen', confirmLabel: 'Löschen', cancelLabel: 'Abbrechen', destructive: true })) return;
    await api(`api/compensation/contracts/${button.dataset.deleteState}?fullWorthSpaceId=${spaceId()}`, { method: 'DELETE' });
    await loadAll();
  }
}

// ---- Prüfen und bestätigen -------------------------------------------------------------------

const hintText = text => { $('#ct-review-hint').textContent = text; };

function fillForm(x, importId, title) {
  reviewedImport = importId;
  hintText(importId ? `Aus Paperless: „${title}“. Werte mit dem Original abgleichen, dann speichern.` : '');
  // Das Formular klappt auf, wenn es etwas zu pruefen gibt, und zu, wenn es geleert wird.
  $('#contract-manual').open = !!(importId || x.validFrom || x.grossMonthly != null);
  setVal('ct-from', x.validFrom || ''); setVal('ct-kind', x.kind || 'contract'); setVal('ct-employer', x.employer || '');
  setVal('ct-gross', x.grossMonthly); setVal('ct-hours', x.weeklyHours); setVal('ct-vacation', x.vacationDays);
  setChecked('ct-car', !!x.companyCar); setVal('ct-bav', x.bavEmployerShare); setVal('ct-notice', x.noticePeriod || ''); setVal('ct-position', x.position || '');
  $('#ct-extras').innerHTML = '';
  (x.extras || []).forEach(extra => addExtra(extra.label, extra.value));
  if (importId) $('#contract-manual').scrollIntoView({ block: 'start', behavior: 'smooth' });
}

const resetForm = () => fillForm({}, null);

function prefill(index) { suggest(state.overview.hints[index]); }

// Der Vorschlag aus dem Abgleich - auch von der Abrechnungsansicht aus (suggestState): der Reiter öffnet sich mit dem
// ausgefüllten Formular.
function suggest(hint) {
  fillForm({ validFrom: hint.suggestedValidFrom, kind: 'addendum', grossMonthly: hint.suggestedGross }, null);
  hintText('Vorschlag aus den Abrechnungen. Zum Beispiel Wochenstunden und Urlaubstage aus dem Vertrag ergänzen, dann speichern.');
  $('#contract-manual').scrollIntoView({ block: 'start', behavior: 'smooth' });
}

function addExtra(label = '', value = '') {
  $('#ct-extras').insertAdjacentHTML('beforeend', `<div class="ct-extra"><input data-extra-label placeholder="Feld" maxlength="80" value="${attr(label)}" aria-label="Name des Feldes"><input data-extra-value placeholder="Wert" maxlength="200" value="${attr(value)}" aria-label="Wert des Feldes"><button type="button" class="btn btn-secondary" data-remove-extra aria-label="Feld entfernen">×</button></div>`);
}

export async function suggestState(hint) {
  $('[data-extended-tab="contracts"]').click();
  await loadAll();
  suggest(hint);
}

const optional = id => val(id) === '' ? null : Number(val(id));

async function save() {
  const space = spaceId(); if (!space) throw new Error('Kein Finanzbereich ausgewählt.');
  if (!val('ct-from')) throw new Error('„Gültig ab“ fehlt.');
  if (!(Number(val('ct-gross')) > 0)) throw new Error('Das Bruttogehalt pro Monat fehlt.');
  const extras = $$('#ct-extras .ct-extra').map(row => ({ label: row.querySelector('[data-extra-label]').value.trim(), value: row.querySelector('[data-extra-value]').value.trim() })).filter(f => f.label && f.value);
  const payload = {
    validFrom: val('ct-from'), kind: val('ct-kind'), grossMonthly: Number(val('ct-gross')), employer: val('ct-employer') || null,
    weeklyHours: optional('ct-hours'), vacationDays: optional('ct-vacation'), companyCar: checked('ct-car'), bavEmployerShare: optional('ct-bav'),
    noticePeriod: val('ct-notice') || null, position: val('ct-position') || null, extras
  };
  await api(`api/compensation/contracts?fullWorthSpaceId=${space}${reviewedImport ? `&importId=${reviewedImport}` : ''}`, json('POST', payload));
  resetForm();
  notify('Vertragsstand gespeichert.');
  await loadAll();
}

function showError(error) { console.error(error); notify(error?.message || 'Unbekannter Fehler.'); }
