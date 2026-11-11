// "Verlauf": die eine Zeitachse für Abrechnungen, Verträge und (wenn es welche gibt) gespeicherte Planungsstände.
// Jahr für Jahr, neueste zuerst: die Zeile eines Jahres fasst es zusammen (Brutto, Bonus, Auszahlung im Monat, die
// Veränderung zum Vorjahr, die Teuerung im selben Zeitraum und was real übrig bleibt) und klappt zu seinen Monaten auf;
// die Gehaltsänderungen aus den Verträgen stehen als Ereignisse im Jahr, mit ihrem PDF. Alles kommt aus den
// bestätigten Abrechnungen und Vertragsständen - hinzugefügt wird über den Dialog (add-dialog.js).
import { esc, euro, spaceId, api } from './shared.js';
import { setOverviewData, renderYear } from './payslip-overview.js';
import { openImportPdf } from './paperless-finds.js';
import { openAdd } from './add-dialog.js';

const num = value => Number(value) || 0;
const pct = value => `${value >= 0 ? '+' : '−'}${Math.abs(value).toLocaleString('de-DE', { maximumFractionDigits: 1 })} %`;
const average = (list, pick) => list.length ? list.reduce((sum, item) => sum + pick(item), 0) / list.length : null;

// Was die Abrechnungen und Funde (extended.js) melden und die Jahre nicht selbst wissen: die offenen Funde aus Paperless
// und die Dinge, die ein Knopf im Jahr auslösen darf.
let context = { finds: [], searching: false, actions: {} };
let data = null;
let openYear = null;
let timer = null;

/** Zeichnet die Zeitachse neu; die Daten holt sie selbst. */
export async function renderVerlauf() {
  const host = document.getElementById('verlauf-years');
  const space = spaceId();
  if (!host || !space) return;
  const [payslips, contracts, inflation] = await Promise.all([
    api(`api/compensation/payslips?fullWorthSpaceId=${space}`),
    api(`api/compensation/contracts?fullWorthSpaceId=${space}`),
    api('api/compensation/inflation/series').catch(() => null)]);
  data = { payslips: payslips || [], contracts: contracts || { states: [], events: [], hints: [] }, inflation };
  draw(host);
}

function yearRows() {
  // Je Monat die zuletzt gespeicherte Abrechnung, je Jahr deren Monate.
  const years = new Map();
  for (const item of data.payslips) {
    const p = item.payslip, year = Number(p.period.slice(0, 4)), month = Number(p.period.slice(5, 7));
    const months = years.get(year) || new Map();
    if (!months.has(month)) months.set(month, p);
    years.set(year, months);
  }
  // Die Teuerung eines Jahres ist der Durchschnitt des Verbraucherpreisindex über die Monate, für die es Abrechnungen gibt -
  // so ist sie mit dem Durchschnitt der Auszahlungen vergleichbar, auch in einem Teiljahr.
  const { inflation } = data;
  const first = inflation ? Number(inflation.firstMonth.slice(0, 4)) * 12 + Number(inflation.firstMonth.slice(5, 7)) - 1 : 0;
  const cpi = inflation?.series.find(s => s.key === 'gesamt');
  const cpiOf = (year, months) => {
    const values = [...months.keys()].map(m => cpi?.values[year * 12 + m - 1 - first]).filter(v => v != null);
    return values.length ? values.reduce((a, b) => a + b, 0) / values.length : null;
  };
  const rows = [...years].sort((a, b) => a[0] - b[0]).map(([year, months]) => {
    const list = [...months.values()], regular = list.filter(p => !num(p.bonus));
    return {
      year, count: list.length,
      gross: list.reduce((sum, p) => sum + num(p.grossPay), 0), bonus: list.reduce((sum, p) => sum + num(p.bonus), 0),
      paid: average(regular.length ? regular : list, p => num(p.payout)), cpi: cpiOf(year, months)
    };
  });
  rows.forEach((row, i) => {
    const before = rows[i - 1];
    row.change = before?.paid ? (row.paid / before.paid - 1) * 100 : null;
    row.inflation = before?.cpi && row.cpi ? (row.cpi / before.cpi - 1) * 100 : null;
    row.real = row.change !== null && row.inflation !== null ? ((1 + row.change / 100) / (1 + row.inflation / 100) - 1) * 100 : null;
  });
  return rows;
}

const cell = (label, value, tone = '') => `<span class="vl-cell"><small>${label}</small><b${tone ? ` class="${tone}"` : ''}>${value}</b></span>`;
const tone = value => value === null ? '' : value >= 0 ? 'positive' : 'negative';

function eventHtml(event) {
  const lead = event.changed.find(change => change.label.toLowerCase().includes('brutto')) || event.changed[0];
  const detail = lead ? `${esc(lead.label)}${lead.old == null ? '' : ` ${esc(lead.old)} →`} <b>${lead.new == null ? 'entfällt' : esc(lead.new)}</b>${lead.percent == null ? '' : ` (${pct(lead.percent)})`}` : '';
  const when = new Date(`${event.validFrom}T12:00:00`);
  return `<li class="vl-event"><span class="vl-ev-date">${String(when.getDate()).padStart(2, '0')}.${String(when.getMonth() + 1).padStart(2, '0')}.</span>`
    + `<span class="vl-ev-main"><strong>${esc(event.title)}</strong><small>${detail}</small></span>`
    + (event.document ? `<button type="button" class="btn btn-quiet" data-open-pdf="${esc(event.document.importId)}">PDF</button>` : '')
    + '</li>';
}

function draw(host) {
  const rows = yearRows().reverse();
  const events = data.contracts.events || [];
  const years = [...new Set([...rows.map(r => r.year), ...events.map(e => Number(e.validFrom.slice(0, 4)))])].sort((a, b) => b - a);
  if (!years.length) {
    host.innerHTML = '<p class="extended-note vl-empty">Noch nichts erfasst. Mit „Hinzufügen“ holst du Abrechnungen und Verträge aus Paperless, lädst PDFs hoch oder erfasst sie von Hand.</p>';
    return;
  }
  if (!years.includes(openYear)) openYear = null;
  host.innerHTML = years.map(year => {
    const row = rows.find(r => r.year === year);
    const eventList = events.filter(e => e.validFrom.startsWith(String(year))).sort((a, b) => b.validFrom.localeCompare(a.validFrom));
    const head = row
      ? `<summary class="vl-summary"><span class="vl-y">${year}</span>`
        + cell('Brutto', euro.format(row.gross)) + cell('Bonus', row.bonus ? euro.format(row.bonus) : '—')
        + cell('Ausgezahlt / Monat', euro.format(row.paid))
        + cell('zum Vorjahr', row.change === null ? '—' : pct(row.change), tone(row.change))
        + cell('Teuerung', row.inflation === null ? '—' : pct(row.inflation))
        + cell('real', row.real === null ? '—' : pct(row.real), tone(row.real)) + '</summary>'
      : '';
    return `<section class="vl-year">${row
      ? `<details data-vl-year="${year}"${year === openYear ? ' open' : ''}>${head}<div class="vl-body"></div></details>`
      : `<div class="vl-summary vl-plain"><span class="vl-y">${year}</span><small>keine Abrechnungen</small></div>`}`
      + (eventList.length ? `<ul class="vl-events">${eventList.map(eventHtml).join('')}</ul>` : '') + '</section>';
  }).join('');

  if (openYear) paintYear(host, openYear);
}

// Ein Jahr zeichnet seine Monate erst, wenn es aufgeht: es gibt immer nur eines, und das Diagramm darin hat feste Ids.
function paintYear(host, year) {
  const details = host.querySelector(`details[data-vl-year="${year}"]`);
  if (!details) return;
  setOverviewData({
    payslips: data.payslips, finds: context.finds, contract: data.contracts, searching: context.searching,
    actions: { upload: () => openAdd('upload'), ...context.actions }
  });
  renderYear(details.querySelector('.vl-body'), year);
}

function schedule() {
  clearTimeout(timer);
  timer = setTimeout(() => { if (isActive()) renderVerlauf().catch(error => console.error(error)); }, 60);
}

const isActive = () => !!document.getElementById('tab-verlauf')?.classList.contains('active');

const host = document.getElementById('verlauf-years');
if (host) {
  host.addEventListener('toggle', event => {
    const details = event.target.closest?.('details[data-vl-year]');
    if (!details?.open) return;
    const year = Number(details.dataset.vlYear);
    host.querySelectorAll('details[data-vl-year][open]').forEach(other => { if (other !== details) other.open = false; });
    openYear = year;
    paintYear(host, year);
  }, true);
  host.addEventListener('click', event => {
    const pdf = event.target.closest('[data-open-pdf]');
    if (pdf) openImportPdf(pdf.dataset.openPdf);
  });
  document.addEventListener('fw-gehalt-changed', event => { context = { ...context, ...(event.detail || {}) }; schedule(); });
  document.addEventListener('fw-contracts-changed', schedule);
  document.addEventListener('fw-gehalt-open', () => renderVerlauf().catch(error => console.error(error)));
  // Die Knöpfe der Startseite ("zu den Lohnabrechnungen") meinen seit der Zusammenlegung den Verlauf.
  document.addEventListener('click', event => {
    if (event.target.closest('[data-open-tab="payslips"],[data-open-tab="contracts"]'))
      document.querySelector('.comp-groups [data-group-tab="verlauf"]')?.click();
  });
}

