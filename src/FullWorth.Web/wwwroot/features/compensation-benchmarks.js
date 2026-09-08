// Marktvergleich — the curated German salary reference dataset next to your own gross.
//
// It lives in the "Gehaltsgespräch" tab because that is what it is for: a reference number you can take
// into a salary talk. Nothing here is a measured value. Every figure of the dataset carries a quality
// marker and a source label, and the dataset ships its own disclaimer — this view only renders THAT text
// (`disclaimer`, `qualityLabel`, `qualityExplanation`, `confidence`, `derivationNotes`, `sourceLabel`) and
// never phrases its own claim about accuracy.
//
// Endpoints (read-only, no space id needed):
//   GET /api/compensation/benchmarks?profession=&bundesland=&year=&experience=&annualGross=
//   GET /api/compensation/benchmarks/series?profession=&bundesland=&experience=
//   GET /api/compensation/benchmarks/metadata
import {
  $, $$, euro, pct, esc, attr, val as value, num as number, setVal as set,
  signedEuro0 as signedMoney, api, notify, readProfile
} from './compensation-shared.js';

const STORE_KEY = 'fullworth.compensation.benchmark';
const GROUP_LABELS = {
  it: 'IT', engineering: 'Ingenieurwesen', trade: 'Handwerk & Technik',
  commercial: 'Kaufmännische Berufe', 'health-social': 'Gesundheit & Soziales',
  'retail-logistics': 'Handel & Logistik'
};
const BASIS_LABELS = { 'annual-gross-fulltime': 'Jahresbrutto, Vollzeit' };
const CONFIDENCE_LABELS = { medium: 'mittel', low: 'niedrig', 'very-low': 'sehr niedrig' };

const bstate = { metadata: null, filled: false };

initBenchmarks();

function initBenchmarks() {
  const host = $('#tab-negotiation');
  if (!host) return;
  host.insertAdjacentHTML('beforeend', benchmarkMarkup());
  $('#bench-run').addEventListener('click', () => run().catch(fail));
  $('#bench-from-calculator').addEventListener('click', () => { fillGrossFromCalculator(); run().catch(fail); });
  // The negotiation tab is opened by the calculator module's own handler; this only adds the first load.
  $$('.comp-tabs button[data-tab="negotiation"]').forEach(button =>
    button.addEventListener('click', () => { if (!bstate.filled) run().catch(fail); }));
}

function benchmarkMarkup() { return `
<article class="panel comp-card bench-card">
  <div class="panel-head">
    <div>
      <h2>Marktvergleich <span class="bench-badge bench-badge-estimate">Schätzwerte</span></h2>
      <p id="bench-disclaimer" class="bench-disclaimer">Referenzdaten werden geladen …</p>
    </div>
  </div>
  <div class="form-grid bench-filters">
    <label>Beruf<select id="bench-profession"></select></label>
    <label>Bundesland<select id="bench-state"></select></label>
    <label>Jahr<select id="bench-year"></select></label>
    <label>Erfahrungsstufe<select id="bench-experience"></select></label>
    <label>Dein Jahresbrutto<input id="bench-gross" type="number" min="0" step="500"><small class="field-help">Vorbelegt mit dem Jahresbrutto aus dem Rechner (ohne Bonus).</small></label>
  </div>
  <div class="bench-actions">
    <button id="bench-run" class="btn btn-primary" type="button">Vergleichen</button>
    <button id="bench-from-calculator" class="btn btn-secondary" type="button">Brutto aus Rechner übernehmen</button>
  </div>
</article>
<div id="bench-result" class="bench-result"></div>`; }

async function loadMetadata() {
  if (bstate.metadata) return bstate.metadata;
  bstate.metadata = await api('api/compensation/benchmarks/metadata');
  return bstate.metadata;
}

function fillGrossFromCalculator() {
  const profile = readProfile();
  set('bench-gross', Math.max(0, Math.round(Number(profile.annualGross) || 0)));
}

async function fillFilters() {
  const meta = await loadMetadata();
  $('#bench-disclaimer').textContent = `${meta.disclaimer} Stand ${meta.dataAsOf}.`;
  const saved = readSaved();

  const groups = new Map();
  (meta.professions || []).forEach(profession => {
    const key = profession.group || 'sonstige';
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(profession);
  });
  $('#bench-profession').innerHTML = [...groups.entries()].map(([key, list]) =>
    `<optgroup label="${attr(GROUP_LABELS[key] || key)}">${list.map(profession =>
      `<option value="${attr(profession.key)}">${esc(profession.name)}</option>`).join('')}</optgroup>`).join('');

  $('#bench-state').innerHTML = (meta.states || []).map(state =>
    `<option value="${attr(state.key)}">${esc(state.name)}</option>`).join('');

  $('#bench-year').innerHTML = [...(meta.years || [])].reverse().map(year =>
    `<option value="${year}">${year}${(meta.projectedYears || []).includes(year) ? ' (projizierter Index)' : ''}</option>`).join('');

  $('#bench-experience').innerHTML = (meta.experienceBands || []).map(band =>
    `<option value="${attr(band.key)}">${esc(band.label)}</option>`).join('');

  // Last choice wins, then the calculator's Bundesland (same two-letter keys), then the dataset defaults.
  selectOption('#bench-profession', saved.profession);
  selectOption('#bench-state', saved.state || value('state-code'));
  selectOption('#bench-year', saved.year || (meta.years || []).at(-1));
  selectOption('#bench-experience', saved.experience || '3-5');
  if (!number('bench-gross')) fillGrossFromCalculator();
  bstate.filled = true;
}

function selectOption(selector, wanted) {
  const select = $(selector);
  if (!select || wanted === undefined || wanted === null || wanted === '') return;
  const option = [...select.options].find(item => item.value === String(wanted));
  if (option) select.value = option.value;
}

function readSaved() {
  try { return JSON.parse(localStorage.getItem(STORE_KEY) || '{}') || {}; } catch { return {}; }
}

function storeSelection(selection) {
  try { localStorage.setItem(STORE_KEY, JSON.stringify(selection)); } catch { /* private mode: not critical */ }
}

async function run() {
  if (!bstate.filled) await fillFilters();
  const selection = {
    profession: value('bench-profession'),
    state: value('bench-state'),
    year: value('bench-year'),
    experience: value('bench-experience')
  };
  if (!selection.profession) return;
  storeSelection(selection);
  const gross = Math.max(0, Math.round(number('bench-gross')));
  const query = new URLSearchParams({
    profession: selection.profession,
    bundesland: selection.state,
    year: selection.year,
    experience: selection.experience
  });
  if (gross > 0) query.set('annualGross', String(gross));
  const [record, series] = await Promise.all([
    api(`api/compensation/benchmarks?${query}`),
    api(`api/compensation/benchmarks/series?${new URLSearchParams({
      profession: selection.profession, bundesland: selection.state, experience: selection.experience
    })}`).catch(() => null)
  ]);
  renderRecord(record, series);
}

function renderRecord(record, series) {
  const root = $('#bench-result');
  if (!record) { root.innerHTML = ''; return; }
  const comparison = record.comparison;
  root.innerHTML = `
<article class="panel comp-card bench-detail">
  <div class="panel-head">
    <div>
      <h2>${esc(record.professionName)} · ${esc(record.stateName)} · ${record.year}</h2>
      <p>${esc(record.experienceLabel)} · ${esc(BASIS_LABELS[record.basis] || record.basis)} · ${esc(record.currency)} · Stand ${esc(record.dataAsOf)}</p>
    </div>
  </div>
  <div class="bench-badges">
    <span class="bench-badge bench-badge-estimate">Schätzwert</span>
    <span class="bench-badge">${esc(record.qualityLabel)}</span>
    <span class="bench-badge">Konfidenz ${esc(CONFIDENCE_LABELS[record.confidence] || record.confidence)}</span>
    ${record.derived ? '<span class="bench-badge">abgeleiteter Wert</span>' : ''}
    ${record.measured ? '' : '<span class="bench-badge">kein Messwert</span>'}
    ${record.yearIsProjected ? '<span class="bench-badge">projizierter Lohnindex</span>' : ''}
  </div>
  <div class="metric-grid bench-metrics">
    <article class="metric bench-metric-median"><span>Referenz-Median (Schätzwert)</span><strong>${euro.format(record.annualGrossMedian)}</strong><small>${esc(record.qualityLabel)}</small></article>
    <article class="metric"><span>Unteres Viertel (P25)</span><strong>${euro.format(record.annualGrossP25)}</strong><small>abgeleitete Spannweite</small></article>
    <article class="metric"><span>Oberes Viertel (P75)</span><strong>${euro.format(record.annualGrossP75)}</strong><small>abgeleitete Spannweite</small></article>
    ${comparison ? `<article class="metric"><span>Dein Jahresbrutto</span><strong>${euro.format(comparison.annualGross)}</strong><small>${esc(comparison.positionLabel)}</small></article>` : ''}
  </div>
  ${comparison ? `<div class="bench-compare">
    <div class="bench-compare-row"><span>Abstand zum Median</span><strong class="${comparison.deltaToMedian >= 0 ? 'positive' : 'negative'}">${esc(signedMoney(comparison.deltaToMedian))}</strong></div>
    <div class="bench-compare-row"><span>Anteil am Median</span><strong>${esc(pct(comparison.percentOfMedian))}</strong></div>
    <div class="bench-compare-row"><span>Einordnung</span><strong>${esc(comparison.positionLabel)}</strong></div>
  </div>` : '<p class="bench-explain">Trage dein Jahresbrutto ein, um den Abstand zum Referenz-Median zu sehen.</p>'}
  <p class="bench-explain">${esc(record.qualityExplanation)}</p>
  <p class="bench-explain">${esc(record.quartileQualityExplanation)}</p>
  ${(record.derivationNotes || []).length ? `<div class="bench-notes"><h3>Wie dieser Wert entstanden ist</h3><ul>${record.derivationNotes.map(note => `<li>${esc(note)}</li>`).join('')}</ul></div>` : ''}
  <p class="bench-sources">Quellen: ${esc(record.sourceLabel)}</p>
  ${(record.sources || []).some(source => source.url) ? `<ul class="bench-source-links">${record.sources.filter(source => source.url).map(source =>
    `<li><a href="${attr(source.url)}" target="_blank" rel="noopener noreferrer">${esc(source.label)}</a>${source.note ? ` — ${esc(source.note)}` : ''}</li>`).join('')}</ul>` : ''}
  <p class="bench-disclaimer">${esc(record.disclaimer)}</p>
  ${seriesMarkup(series)}
  ${methodMarkup()}
</article>`;
}

function seriesMarkup(series) {
  const points = series?.points || [];
  if (points.length < 2) return '';
  return `<details class="bench-details"><summary>Jahresverlauf (geschätzt, ${esc(series.experienceLabel)}, ${esc(series.stateName)})</summary>
    <table class="bench-years"><thead><tr><th>Jahr</th><th>Median</th><th>P25</th><th>P75</th><th>Hinweis</th></tr></thead><tbody>${points.map(point =>
      `<tr><td>${point.year}</td><td>${euro.format(point.annualGrossMedian)}</td><td>${euro.format(point.annualGrossP25)}</td><td>${euro.format(point.annualGrossP75)}</td><td>${point.yearIsProjected ? 'projizierter Lohnindex' : (point.derived ? 'abgeleitet' : '')}</td></tr>`).join('')}</tbody></table>
  </details>`;
}

function methodMarkup() {
  const method = bstate.metadata?.method;
  if (!method) return '';
  return `<details class="bench-details"><summary>Methode und Grenzen des Datensatzes</summary>
    <p class="bench-explain">${esc(method.summary)}</p>
    <ol class="bench-notes-list">${(method.steps || []).map(step => `<li>${esc(step)}</li>`).join('')}</ol>
    <h3>Grenzen</h3>
    <ul class="bench-notes-list">${(method.limitations || []).map(limit => `<li>${esc(limit)}</li>`).join('')}</ul>
    <p class="bench-sources">Revision ${esc(method.revision)} · ${bstate.metadata.recordCount} Referenzwerte · Datensatz ${esc(bstate.metadata.datasetKey)}</p>
  </details>`;
}

function fail(error) { console.error(error); notify(error?.message || 'Unbekannter Fehler.'); }
