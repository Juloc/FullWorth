// "Sonstige regelmäßige Einkünfte" — own tab on the Gehalt page.
//
// This is a THIRD track next to salary and employer benefits: a Halbwaisenrente, Unterhalt, Mieteinnahmen …
// The backend never feeds these records into the salary calculation, so nothing here may appear inside the
// Arbeitgeber-Gesamtpaket. The tab therefore lives next to (not inside) Rechner/Benefits and says so.
//
// Every record carries its own "counts toward the personally available income" flag, which decides whether
// the Verlauf chart adds it to „Persönlich verfügbar“. The flag is a plain visible checkbox per row.
//
// Row markup/reading lives in features/compensation-shared.js (addOtherIncomeRow / readOtherIncomeRow),
// same as the benefit and one-off rows — this file is only the tab, the API calls and the summary.
import { confirmMessage } from '../ui/confirm.js';
import {
  $, $$, euro, euro2, esc, fmtDate, localIsoDate, spaceId, api, json, notify,
  loadOtherIncomeTypes, fillOtherIncomeTypeList, otherIncomeTypeOptions, otherIncomeName,
  otherIncomeActiveOn, otherIncomeAmountsOn, addOtherIncomeRow, readOtherIncomeRow
} from './compensation-shared.js';

const state = { entries: [], loaded: false };

init();

function init() {
  const tabs = $('.comp-tabs');
  const toast = $('#comp-error');
  if (!tabs || !toast) return;

  // Right after Benefits: the income tracks stay next to each other instead of at the far end of the bar.
  const anchor = tabs.querySelector('[data-tab="benefits"]') || tabs.lastElementChild;
  anchor.insertAdjacentHTML('afterend', '<button data-income-tab="other-income" type="button">Weitere Einkünfte</button>');
  toast.insertAdjacentHTML('beforebegin', markup());

  $$('[data-income-tab]').forEach(button => button.addEventListener('click', () => openTab()));
  $('#income-add').addEventListener('click', () => addRow());
  $('#space-select').addEventListener('change', () => {
    state.loaded = false;
    if ($('#tab-other-income')?.classList.contains('active')) load().catch(fail);
  });

  // One delegated handler for every row: the rows are re-rendered after each save.
  $('#tab-other-income').addEventListener('click', event => {
    const row = event.target.closest('.income-row');
    if (!row) return;
    if (event.target.closest('[data-income-save]')) { save(row).catch(fail); return; }
    if (event.target.closest('[data-income-delete]')) remove(row).catch(fail);
  });
}

function markup() { return `
<section id="tab-other-income" class="comp-tab">
  <article class="panel comp-card income-intro">
    <div class="panel-head">
      <div>
        <h2>Sonstige regelmäßige Einkünfte</h2>
        <p>Regelmäßige Einkünfte, die nicht vom Arbeitgeber kommen — zum Beispiel eine Halbwaisenrente, Unterhalt oder Mieteinnahmen.</p>
      </div>
      <button id="income-add" class="btn btn-secondary" type="button">Einkunft hinzufügen</button>
    </div>
    <p class="income-separate">Getrennt von Gehalt und Benefits: diese Beträge gehen in keine Gehaltsberechnung ein und sind <strong>nicht Teil des Arbeitgeber-Gesamtpakets</strong>. Sie erscheinen ausschließlich als eigene Linie im Verlauf.</p>
  </article>
  <div id="income-summary" class="metric-grid income-summary"></div>
  <article class="panel comp-card">
    <div class="panel-head">
      <div>
        <h2>Erfasste Einkünfte</h2>
        <p>Jede Zeile wird einzeln gespeichert. „Zählt zum persönlich verfügbaren Einkommen“ entscheidet, ob die Einkunft im Verlauf zu „Persönlich verfügbar“ addiert wird.</p>
      </div>
    </div>
    <div id="other-income-list" class="income-list"></div>
    <div id="income-empty" class="income-empty" hidden>Noch keine sonstigen Einkünfte erfasst.</div>
    <datalist id="other-income-types"></datalist>
  </article>
</section>`; }

async function openTab() {
  $$('.comp-tabs button').forEach(button => button.classList.toggle('active', button.dataset.incomeTab === 'other-income'));
  $$('.comp-tab').forEach(tab => tab.classList.toggle('active', tab.id === 'tab-other-income'));
  await load().catch(fail);
}

async function load() {
  const space = spaceId();
  if (!space) return;
  fillOtherIncomeTypeList(await loadOtherIncomeTypes());
  state.entries = await api(`api/compensation/other-income?fullWorthSpaceId=${encodeURIComponent(space)}`) || [];
  state.loaded = true;
  render();
}

function render() {
  const list = $('#other-income-list');
  const options = otherIncomeTypeOptions();
  list.innerHTML = '';
  const sorted = [...state.entries].sort((a, b) =>
    String(b.validFrom).localeCompare(String(a.validFrom)) || otherIncomeName(a, options).localeCompare(otherIncomeName(b, options), 'de'));
  sorted.forEach(entry => {
    const row = addOtherIncomeRow(entry, options);
    if (row) row.classList.toggle('income-inactive', !otherIncomeActiveOn(entry));
  });
  $('#income-empty').hidden = sorted.length > 0;
  renderSummary();
}

function renderSummary() {
  const today = localIsoDate();
  const totals = otherIncomeAmountsOn(state.entries, today);
  const notCounted = totals.monthlyTotal - totals.monthlyCounted;
  $('#income-summary').innerHTML = `
    <article class="metric"><span>Aktuell aktiv</span><strong>${totals.activeCount}</strong><small>von ${state.entries.length} erfassten Einkünften · Stand ${esc(fmtDate(today))}</small></article>
    <article class="metric"><span>Summe pro Monat</span><strong>${euro2.format(totals.monthlyTotal)}</strong><small>${euro.format(totals.annualTotal)} pro Jahr</small></article>
    <article class="metric"><span>Persönlich verfügbar</span><strong>${euro2.format(totals.monthlyCounted)}</strong><small>${euro.format(totals.annualCounted)} pro Jahr · nur angerechnete Einkünfte</small></article>
    <article class="metric"><span>Nicht angerechnet</span><strong>${euro2.format(notCounted)}</strong><small>Einkünfte ohne den Haken „persönlich verfügbar“</small></article>`;
}

function addRow() {
  const row = addOtherIncomeRow({}, otherIncomeTypeOptions());
  $('#income-empty').hidden = true;
  row?.querySelector('[data-income="type"]')?.focus();
}

function validate(body) {
  if (!body.type) throw new Error('Bitte eine Art der Einkunft angeben.');
  if (!body.validFrom) throw new Error('Bitte ein „Gültig ab“-Datum angeben.');
  if (body.validTo && body.validTo < body.validFrom) throw new Error('„Gültig bis“ darf nicht vor „Gültig ab“ liegen.');
  if (!(body.monthlyAmount > 0)) throw new Error('Bitte einen Betrag pro Monat größer als 0 angeben.');
  return body;
}

async function save(row) {
  const space = spaceId();
  if (!space) throw new Error('Kein Finanzbereich ausgewählt.');
  const body = validate(readOtherIncomeRow(row));
  const id = row.dataset.incomeId;
  const query = `fullWorthSpaceId=${encodeURIComponent(space)}`;
  if (id) await api(`api/compensation/other-income/${id}?${query}`, json('PUT', body));
  else await api(`api/compensation/other-income?${query}`, json('POST', body));
  await load();
  notify(id ? 'Einkunft aktualisiert.' : 'Einkunft gespeichert.');
}

async function remove(row) {
  const id = row.dataset.incomeId;
  // A row that was never saved is just discarded — no round trip, no confirmation.
  if (!id) { row.remove(); $('#income-empty').hidden = !!$$('.income-row').length; renderSummary(); return; }
  const entry = state.entries.find(item => item.id === id);
  const name = otherIncomeName(entry || {}, otherIncomeTypeOptions());
  if (!await confirmMessage({
    message: `„${name}“ wirklich löschen?`, title: 'Einkunft löschen',
    confirmLabel: 'Löschen', cancelLabel: 'Abbrechen', destructive: true
  })) return;
  await api(`api/compensation/other-income/${id}?fullWorthSpaceId=${encodeURIComponent(spaceId())}`, { method: 'DELETE' });
  await load();
  notify('Einkunft gelöscht.');
}

function fail(error) { console.error(error); notify(error?.message || 'Unbekannter Fehler.'); }
