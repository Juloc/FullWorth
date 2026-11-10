// Monats- und Jahresansicht der bestätigten Lohnabrechnungen: Kennzahlen, Hinweise, das Diagramm
// "Wohin das Brutto geht" und darunter Monatsliste mit Detail. Alles wird aus den bestätigten
// Abrechnungen abgeleitet - was sich daraus nicht ergibt, steht nicht da.
import { $, esc, attr, euro, euro2, signedEuro } from './shared.js';
import { spriteHref } from '../../components/sprite.js';

const MONTH_LONG = Array.from({ length: 12 }, (_, i) => new Intl.DateTimeFormat('de-DE', { month: 'long' }).format(new Date(2021, i, 1)));
const MONTH_SHORT = Array.from({ length: 12 }, (_, i) => new Intl.DateTimeFormat('de-DE', { month: 'short' }).format(new Date(2021, i, 1)).replace('.', ''));
const SOURCES = { paperless: 'Paperless-Import', 'confirmed-ocr': 'Upload (Auslesung), bestätigt', manual: 'manuell erfasst' };

// Die Zeilen des Details. "kind" legt fest, wie die Änderung zum Vormonat gefärbt wird: bei Brutto und
// Netto ist mehr gut, bei Abzügen ist es nur eine Veränderung und bleibt neutral.
const DETAIL_ROWS = [
  ['grossPay', 'Brutto', 'income'],
  ['wageTax', 'Lohnsteuer', 'deduction'],
  ['solidaritySurcharge', 'Solidaritätszuschlag', 'deduction', true],
  ['churchTax', 'Kirchensteuer', 'deduction', true],
  ['pensionInsurance', 'Rentenversicherung', 'deduction'],
  ['healthInsurance', 'Krankenversicherung', 'deduction'],
  ['careInsurance', 'Pflegeversicherung', 'deduction'],
  ['unemploymentInsurance', 'Arbeitslosenversicherung', 'deduction'],
  ['bavEmployee', 'bAV Arbeitnehmer', 'deduction', true],
  ['companyCarTaxableBenefit', 'Firmenwagen Sachbezug', 'deduction', true],
  ['bonus', 'Bonus / Sonderzahlung', 'income', true],
  ['netPay', 'Netto', 'income']
];

const num = value => Number(value) || 0;
const tax = p => num(p.wageTax) + num(p.solidaritySurcharge) + num(p.churchTax);
const social = p => num(p.pensionInsurance) + num(p.unemploymentInsurance) + num(p.healthInsurance) + num(p.careInsurance);
// Was vom Brutto weder Netto, Steuer noch Sozialabgabe ist (bAV, Sachbezug, ...): ohne diesen Rest
// ergäben die Säulen nicht das Brutto, und die Lücke würde stillschweigend als "Netto" gelesen.
const other = p => Math.max(0, num(p.grossPay) - num(p.netPay) - tax(p) - social(p));
const monthOf = payslip => Number(String(payslip.period).slice(5, 7));
const yearOf = payslip => Number(String(payslip.period).slice(0, 4));
const pct = value => `${value >= 0 ? '+' : '−'}${Math.abs(value).toLocaleString('de-DE', { maximumFractionDigits: 1 })} %`;
const icon = name => `<svg viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref(name)}"></use></svg>`;

/**
 * Der Zustand der Ansicht. "payslips" sind die Einträge der API (Id, payslip, document), "finds" die
 * offenen Funde aus Paperless, "contract" die Vertragsstände und Hinweise aus dem Abgleich (Marker "Nachtrag" im
 * Diagramm, Hinweise darunter), "actions" die Dinge, die die Ansicht auslösen darf.
 */
const state = { payslips: [], finds: [], contract: { states: [], hints: [] }, searching: false, year: null, month: null, actions: null, model: null };
let chartObserver = null;

export function renderOverview(input) {
  Object.assign(state, input);
  const root = $('#payslip-overview');
  if (!root) return;

  if (!state.payslips.length) {
    root.innerHTML = '<div class="panel comp-card po-empty"><h2>Noch keine bestätigten Abrechnungen</h2><p class="extended-note">Lade eine Lohnabrechnung hoch oder lass sie in Paperless suchen. Hier erscheinen dann Jahresübersicht, Diagramm und Monatsliste.</p></div>';
    return;
  }

  const years = [...new Set(state.payslips.map(item => yearOf(item.payslip)))].sort((a, b) => b - a);
  if (!years.includes(state.year)) state.year = years[0];
  const model = state.model = buildYear(state.year);
  if (!model.months.some(m => m.month === state.month)) state.month = model.latest;

  root.innerHTML = `
    <div class="panel comp-card po-head">
      <div class="po-years" role="tablist" aria-label="Jahr">${years.map(y => `<button type="button" role="tab" class="po-year" aria-selected="${y === state.year}" data-po-year="${y}">${y}</button>`).join('')}</div>
      <div class="po-kpis">
        <div><span>Brutto bisher</span><strong>${euro.format(model.gross)}</strong></div>
        <div><span>Netto bisher</span><strong>${euro.format(model.net)}</strong></div>
        <div><span>Nettoquote</span><strong>${model.gross ? (model.net / model.gross * 100).toLocaleString('de-DE', { maximumFractionDigits: 1 }) + ' %' : '—'}</strong></div>
        <div><span>Abrechnungen</span><strong>${model.present} von ${model.expected}</strong></div>
      </div>
    </div>
    ${hintsHtml(model)}
    <div class="panel comp-card po-chart-card">
      <div class="panel-head"><div><h2>Wohin das Brutto geht</h2><p>Netto, Steuern und Sozialabgaben ergeben das Brutto des Monats.</p></div></div>
      <div class="po-legend">${legendHtml(model)}</div>
      <div class="po-chart-wrap"><div id="po-chart"></div><div class="po-tip" hidden></div></div>
    </div>
    <div class="po-grid">
      <div class="panel comp-card po-list-card"><div class="po-list-head" aria-hidden="true"><span>Monat</span><span class="po-h-gross">Brutto</span><span>Netto</span><span>Änderung</span><span></span></div><ol class="po-list">${listHtml(model)}</ol></div>
      <div class="panel comp-card po-side">${detailHtml(model, state.month)}</div>
    </div>`;

  bindOnce(root);
  drawChart(model);
  chartObserver?.disconnect();
  chartObserver = new ResizeObserver(() => drawChart(state.model));
  chartObserver.observe($('#po-chart'));
}

// ---- Modell ----------------------------------------------------------------------------------

function buildYear(year) {
  const all = state.payslips;
  const byMonth = new Map();
  // Die Liste kommt neueste zuerst; je Monat gilt die zuletzt gespeicherte Abrechnung.
  for (const item of all) if (yearOf(item.payslip) === year && !byMonth.has(monthOf(item.payslip))) byMonth.set(monthOf(item.payslip), item);
  const present = [...byMonth.keys()].sort((a, b) => a - b);
  const earliestYear = Math.min(...all.map(item => yearOf(item.payslip)));
  // Erwartet wird ab Januar - nur im ersten Jahr mit Daten ab dem ersten Monat, den es gibt (wer im Mai
  // anfängt, hat keine fehlenden Monate davor) - und bis zum letzten Monat mit Abrechnung: was danach
  // kommt, ist nicht "fehlend", sondern noch nicht da.
  const first = year === earliestYear ? present[0] : 1;
  const last = present[present.length - 1];

  const months = [];
  for (let m = first; m <= last; m++) months.push({ month: m, item: byMonth.get(m) || null });
  months.forEach((entry, i) => {
    entry.prev = entry.item ? previousPayslip(year, entry.month, byMonth) : null;
    entry.index = i;
  });

  const payslips = present.map(m => byMonth.get(m).payslip);
  return {
    year, months, byMonth, first, last, latest: last,
    present: present.length, expected: last - first + 1,
    gross: payslips.reduce((sum, p) => sum + num(p.grossPay), 0),
    net: payslips.reduce((sum, p) => sum + num(p.netPay), 0)
  };
}

// Der Vormonat: der unmittelbar vorangehende Kalendermonat, auch über den Jahreswechsel - aber nur, wenn es
// für ihn eine Abrechnung gibt. Ein fehlender Monat dazwischen macht den Vergleich nicht zu einem anderen.
function previousPayslip(year, month, byMonth) {
  if (month > 1) return byMonth.get(month - 1)?.payslip || null;
  return state.payslips.find(item => yearOf(item.payslip) === year - 1 && monthOf(item.payslip) === 12)?.payslip || null;
}

// ---- Hinweise --------------------------------------------------------------------------------

function hints(model) {
  const result = [];
  const find = month => state.finds.find(f => f.status === 'needs_review' && f.extraction?.period
    && Number(f.extraction.period.slice(0, 4)) === model.year && Number(f.extraction.period.slice(5, 7)) === month);

  const missing = model.months.filter(m => !m.item);
  for (const { month } of missing.slice(-3).reverse()) {
    const waiting = find(month);
    if (waiting) result.push({ tone: 'info', icon: 'ui-info', text: `${MONTH_LONG[month - 1]} wartet in der Prüfliste.`, action: 'review', id: waiting.id, label: 'Prüfen' });
    else result.push({
      tone: 'warn', icon: 'ui-alert', action: 'upload', label: 'Hochladen',
      text: `${MONTH_LONG[month - 1]} fehlt.${state.searching ? ' In Paperless keine Abrechnung gefunden.' : ''}`
    });
  }

  const pairs = model.months.filter(m => m.item && m.prev);
  for (const { month, item, prev } of pairs.slice().reverse()) {
    const p = item.payslip, gross = num(p.grossPay), before = num(prev.grossPay);
    if (!before) continue;
    const change = (gross - before) / before;
    const name = MONTH_LONG[month - 1];
    // Ein Bonus erklärt einen Sprung bereits - er wird im Diagramm benannt und hier nicht noch einmal gemeldet.
    if (Math.abs(change) >= 0.05 && Math.abs(num(p.bonus) - num(prev.bonus)) < 1)
      result.push({ tone: 'info', icon: 'ui-info', text: `Brutto im ${name} ${signedEuro(gross - before)} (${pct(change * 100)}) gegenüber dem Vormonat.` });
    else if (Math.abs(change) <= 0.01 && num(p.netPay) - num(prev.netPay) <= -10)
      result.push({ tone: 'warn', icon: 'ui-alert', text: `Netto im ${name} ${signedEuro(num(p.netPay) - num(prev.netPay))} bei gleichem Brutto${cause(p, prev)}` });
  }
  // Hinweise aus dem Abgleich mit den Vertragsständen: das Backend rechnet sie, hier werden sie nur gezeigt.
  state.contract.hints.forEach((hint, index) => result.push({
    tone: hint.kind === 'change_detected' ? 'info' : 'warn', icon: hint.kind === 'change_detected' ? 'ui-info' : 'ui-alert', text: hint.message,
    ...(hint.kind === 'change_detected' ? { action: 'contract', id: String(index), label: 'Stand anlegen' } : {})
  }));
  return result.slice(0, 5);
}

// Die Ursache steht nur da, wenn sie in den erfassten Abzugsfeldern zu finden ist.
function cause(p, prev) {
  const fields = DETAIL_ROWS.filter(row => row[2] === 'deduction');
  const best = fields.map(([key, label]) => ({ label, delta: num(p[key]) - num(prev[key]) })).sort((a, b) => b.delta - a.delta)[0];
  return best && best.delta >= 5 ? `: ${best.label} ${signedEuro(best.delta)}.` : '. Die erfassten Abzüge erklären es nicht.';
}

function hintsHtml(model) {
  const list = hints(model);
  if (!list.length) return '';
  return `<div class="po-hints">${list.map(h => `<div class="po-hint po-hint-${h.tone}">${icon(h.icon)}<span>${esc(h.text)}</span>${h.action ? `<button type="button" class="btn btn-secondary" data-po-hint="${h.action}"${h.id ? ` data-po-find="${attr(h.id)}"` : ''}>${h.label}</button>` : ''}</div>`).join('')}</div>`;
}

// ---- Diagramm --------------------------------------------------------------------------------

const SEGMENTS = [
  ['net', 'Netto', 'var(--cat-3)', p => num(p.netPay)],
  ['tax', 'Steuern', 'var(--cat-4)', tax],
  ['social', 'Sozialabgaben', 'var(--cat-1)', social],
  ['other', 'Sonstige Abzüge', 'var(--cat-6)', other]
];

function legendHtml(model) {
  const hasOther = model.months.some(m => m.item && other(m.item.payslip) > 0.5);
  return SEGMENTS.filter(s => s[0] !== 'other' || hasOther).map(s => `<span><i style="background:${s[2]}"></i>${s[1]}</span>`).join('');
}

function niceStep(max) {
  for (const step of [250, 500, 1000, 2000, 2500, 5000, 10000, 20000]) if (max / step <= 4) return step;
  return 50000;
}

function drawChart(model) {
  const host = $('#po-chart');
  if (!host) return;
  const width = Math.max(280, Math.floor(host.clientWidth));
  const compact = width < 520;
  const height = compact ? 230 : 280;
  const left = compact ? 40 : 46, right = 6, top = 24, bottom = 26;
  const plotW = width - left - right, plotH = height - top - bottom;

  const slots = model.months.filter(m => m.item);
  const max = Math.max(...slots.map(m => num(m.item.payslip.grossPay)), 1);
  const step = niceStep(max * 1.04);
  const yMax = Math.ceil(max * 1.04 / step) * step;
  const y = value => top + plotH - value / yMax * plotH;
  const slotW = plotW / 12;
  const barW = Math.min(22, slotW * 0.62);
  const typical = slots.reduce((sum, m) => sum + num(m.item.payslip.grossPay), 0) / slots.length;

  let grid = '';
  for (let v = 0; v <= yMax; v += step)
    grid += `<line class="po-grid-line" x1="${left}" x2="${width - right}" y1="${y(v).toFixed(1)}" y2="${y(v).toFixed(1)}"/><text class="po-axis" x="${left - 8}" y="${(y(v) + 3.5).toFixed(1)}" text-anchor="end">${v.toLocaleString('de-DE')}</text>`;

  let bars = '';
  for (let m = 1; m <= 12; m++) {
    const cx = left + slotW * (m - 0.5);
    const entry = model.months.find(x => x.month === m);
    const label = compact ? MONTH_SHORT[m - 1][0] : MONTH_SHORT[m - 1];
    const selected = m === state.month;
    bars += `<g class="po-col${selected ? ' is-selected' : ''}" data-po-month="${m}">`
      + `<rect class="po-col-hit" x="${(cx - slotW / 2).toFixed(1)}" y="${top}" width="${slotW.toFixed(1)}" height="${plotH}"/>`;
    // Ein Vertragsstand ab diesem Monat (nicht der erste): gestrichelte Linie hinter der Säule und ein Wort darüber.
    if (state.contract.states.some((c, i) => i > 0 && Number(c.state.validFrom.slice(0, 4)) === model.year && Number(c.state.validFrom.slice(5, 7)) === m))
      bars += `<line class="po-marker" x1="${cx.toFixed(1)}" x2="${cx.toFixed(1)}" y1="${top - 6}" y2="${top + plotH}"/><text class="po-marker-label" x="${cx.toFixed(1)}" y="${top - 10}" text-anchor="middle">Nachtrag</text>`;
    if (entry && !entry.item) {
      const h = typical * 0.55 / yMax * plotH;
      bars += `<rect class="po-gap" x="${(cx - barW / 2).toFixed(1)}" y="${(top + plotH - h).toFixed(1)}" width="${barW.toFixed(1)}" height="${h.toFixed(1)}" rx="4"/>`
        + `<text class="po-gap-mark" x="${cx.toFixed(1)}" y="${(top + plotH - h / 2 + 5).toFixed(1)}" text-anchor="middle">?</text>`;
    } else if (entry) {
      const p = entry.item.payslip;
      const parts = SEGMENTS.map(s => [s, s[3](p)]).filter(([, v]) => v > 0.005);
      let base = top + plotH;
      parts.forEach(([s, v], i) => {
        const h = v / yMax * plotH, topmost = i === parts.length - 1;
        base -= h;
        bars += topmost ? roundedTop(cx - barW / 2, base, barW, h, s[2]) : `<rect x="${(cx - barW / 2).toFixed(1)}" y="${base.toFixed(1)}" width="${barW.toFixed(1)}" height="${h.toFixed(1)}" fill="${s[2]}"/>`;
      });
      if (num(p.bonus) > 0) bars += `<text class="po-bonus" x="${cx.toFixed(1)}" y="${(base - 5).toFixed(1)}" text-anchor="middle">Bonus</text>`;
    }
    bars += `<text class="po-axis" x="${cx.toFixed(1)}" y="${height - 8}" text-anchor="middle">${esc(label)}</text></g>`;
  }

  host.innerHTML = `<svg class="po-chart" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="Brutto je Monat, aufgeteilt in Netto, Steuern und Sozialabgaben">${grid}${bars}</svg>`;
  bindTooltip(host, model, { left, slotW, top, plotH });
}

// Nur die oberste Fläche einer Säule ist oben abgerundet; eine zu flache Fläche bleibt ein Rechteck.
function roundedTop(x, y, w, h, fill) {
  const r = Math.min(4, w / 2, h);
  if (r < 1.5) return `<rect x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${w.toFixed(1)}" height="${h.toFixed(1)}" fill="${fill}"/>`;
  return `<path d="M${x.toFixed(1)} ${(y + h).toFixed(1)}V${(y + r).toFixed(1)}a${r} ${r} 0 0 1 ${r} ${-r}H${(x + w - r).toFixed(1)}a${r} ${r} 0 0 1 ${r} ${r}V${(y + h).toFixed(1)}Z" fill="${fill}"/>`;
}

// Der Hinweis liegt absolut im Rahmen des Diagramms: ihn zu zeigen, zu verschieben oder zu verstecken
// verschiebt nichts anderes auf der Seite. Maus: Hover. Touch: ein Tippen zeigt ihn und wählt den Monat.
function bindTooltip(host, model, geo) {
  const wrap = host.closest('.po-chart-wrap'), tip = wrap.querySelector('.po-tip');
  const show = month => {
    const entry = model.months.find(x => x.month === month);
    if (!entry?.item) { tip.hidden = true; return; }
    const p = entry.item.payslip;
    const rows = SEGMENTS.map(s => [s, s[3](p)]).filter(([s, v]) => v > 0.005 || s[0] !== 'other').filter(([s, v]) => s[0] !== 'other' || v > 0.5);
    tip.innerHTML = `<strong>${MONTH_LONG[month - 1]} ${model.year}</strong>`
      + rows.map(([s, v]) => `<div class="po-tip-row"><span><i style="background:${s[2]}"></i>${s[1]}</span><span>${euro2.format(v)}</span></div>`).join('')
      + `<div class="po-tip-row po-tip-foot"><span>Brutto</span><span>${euro2.format(num(p.grossPay))}</span></div>`;
    tip.hidden = false;
    const cx = geo.left + geo.slotW * (month - 0.5);
    const w = tip.offsetWidth, room = wrap.clientWidth;
    let x = cx + 18;
    if (x + w > room - 4) x = cx - w - 18;
    tip.style.left = `${Math.max(4, Math.min(x, room - w - 4))}px`;
    tip.style.top = `${geo.top}px`;
  };
  host.querySelectorAll('.po-col').forEach(col => {
    const month = Number(col.dataset.poMonth);
    col.addEventListener('mouseenter', () => show(month));
    col.addEventListener('pointerdown', () => show(month));
  });
  host.addEventListener('mouseleave', () => { tip.hidden = true; });
}

// ---- Liste und Detail ------------------------------------------------------------------------

const listHtml = model => model.months.slice().reverse().map(m => listItemHtml(model, m)).join('');

function listItemHtml(model, entry) {
  const selected = entry.month === state.month;
  const name = MONTH_LONG[entry.month - 1];
  if (!entry.item) {
    return `<li class="po-item${selected ? ' is-open' : ''}" data-po-item="${entry.month}"><div class="po-row po-row-missing" role="button" tabindex="0" data-po-row="${entry.month}"><span class="po-month"><strong>${name}</strong></span><span class="po-missing">fehlt</span><span class="po-act"><button type="button" class="btn btn-secondary po-icon" data-po-hint="upload" aria-label="Abrechnung für ${name} hochladen">${icon('ui-upload')}</button></span></div>${selected ? `<div class="po-inline">${detailHtml(model, entry.month)}</div>` : ''}</li>`;
  }
  const p = entry.item.payslip, prev = entry.prev;
  const change = prev ? num(p.netPay) - num(prev.netPay) : null;
  return `<li class="po-item${selected ? ' is-open' : ''}" data-po-item="${entry.month}"><div class="po-row" role="button" tabindex="0" aria-expanded="${selected}" data-po-row="${entry.month}">`
    + `<span class="po-month"><strong>${name}</strong><small>${euro.format(num(p.grossPay))} brutto</small></span>`
    + `<span class="po-gross">${euro.format(num(p.grossPay))}</span>`
    + `<span class="po-net">${euro.format(num(p.netPay))}</span>`
    + `<span class="po-change ${change === null || Math.abs(change) < 0.005 ? '' : change > 0 ? 'positive' : 'negative'}">${change === null ? '—' : Math.abs(change) < 0.005 ? '±0 €' : signedEuro(change)}</span>`
    + `<span class="po-act">${entry.item.document ? `<button type="button" class="btn btn-secondary po-icon" data-open-pdf="${attr(entry.item.document.importId)}" aria-label="PDF für ${name} öffnen">${icon('ui-document')}</button>` : ''}</span>`
    + `</div>${selected ? `<div class="po-inline">${detailHtml(model, entry.month)}</div>` : ''}</li>`;
}

function detailHtml(model, month) {
  const entry = model.months.find(x => x.month === month);
  if (!entry) return '';
  const name = `${MONTH_LONG[month - 1]} ${model.year}`;
  if (!entry.item) {
    return `<div class="po-detail"><div class="po-detail-head"><h3>${name}</h3><span class="po-badge po-badge-missing">fehlt</span></div><p class="extended-note">Für diesen Monat ist keine bestätigte Abrechnung gespeichert.</p><button type="button" class="btn btn-secondary" data-po-hint="upload">${icon('ui-upload')}Abrechnung hochladen</button></div>`;
  }
  const { item, prev } = entry, p = item.payslip, doc = item.document;
  const rows = DETAIL_ROWS.filter(([key, , , optional]) => !optional || num(p[key]) || num(prev?.[key]));
  const confirmed = new Intl.DateTimeFormat('de-DE').format(new Date(item.createdAt));
  const source = SOURCES[p.source] || esc(p.source);
  return `<div class="po-detail">`
    + `<div class="po-detail-head"><h3>${name}</h3><span class="po-badge">bestätigt</span>`
    + `<span class="po-detail-actions">${doc ? `<button type="button" class="btn btn-secondary" data-open-pdf="${attr(doc.importId)}">PDF</button><button type="button" class="btn btn-secondary" data-open-url="${attr(doc.paperlessUrl)}">Paperless</button>` : ''}<button type="button" class="btn btn-danger" data-delete-payslip="${attr(item.id)}" aria-label="Abrechnung ${name} löschen" title="Löschen">×</button></span></div>`
    + `<dl class="po-rows"><div class="po-detail-row po-detail-cols"><dt></dt><dd>Betrag</dd><dd>${prev ? 'Vormonat' : ''}</dd></div>${rows.map(([key, label, kind]) => {
      const delta = prev ? num(p[key]) - num(prev[key]) : null;
      const tone = delta === null || Math.abs(delta) < 0.005 ? '' : kind === 'income' ? (delta > 0 ? 'positive' : 'negative') : 'neutral';
      return `<div class="po-detail-row${key === 'grossPay' || key === 'netPay' ? ' is-total' : ''}"><dt>${label}</dt><dd>${euro2.format(num(p[key]))}</dd><dd class="po-delta ${tone}">${delta === null ? '' : Math.abs(delta) < 0.005 ? '±0' : signedEuro(delta)}</dd></div>`;
    }).join('')}</dl>`
    + `<p class="po-foot">Quelle: ${source} · bestätigt am ${confirmed}${doc?.readConfidencePercent != null ? ` · Auslesesicherheit ${Number(doc.readConfidencePercent).toLocaleString('de-DE', { maximumFractionDigits: 0 })} %` : ''}</p></div>`;
}

// ---- Bedienung -------------------------------------------------------------------------------

// Ein einziger Zuhörer an der Wurzel: die Liste, das Detail und das Diagramm werden bei jeder Auswahl neu
// gezeichnet, und Zuhörer an den einzelnen Elementen müssten jedes Mal neu gesetzt werden.
function bindOnce(root) {
  if (root.dataset.bound) return;
  root.dataset.bound = '1';
  root.addEventListener('click', event => {
    const year = event.target.closest('[data-po-year]');
    if (year) {
      state.year = Number(year.dataset.poYear);
      state.month = null;
      renderOverview({});
      return;
    }
    const button = event.target.closest('button');
    if (button) {
      if (button.dataset.openPdf) state.actions.openPdf(button.dataset.openPdf);
      if (button.dataset.openUrl) window.open(button.dataset.openUrl, '_blank', 'noopener,noreferrer');
      if (button.dataset.deletePayslip) state.actions.deletePayslip(button.dataset.deletePayslip);
      if (button.dataset.poHint === 'upload') state.actions.upload();
      if (button.dataset.poHint === 'review') state.actions.review(button.dataset.poFind);
      if (button.dataset.poHint === 'contract') state.actions.suggestContract(Number(button.dataset.poFind));
      return;
    }
    if (event.target.closest('a')) return;
    const target = event.target.closest('[data-po-row],.po-col');
    if (target) select(Number(target.dataset.poRow || target.dataset.poMonth), target.matches('[data-po-row]') ? target : null);
  });
  root.addEventListener('keydown', event => {
    const row = event.target.closest('[data-po-row]');
    if (row && event.target === row && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); select(Number(row.dataset.poRow), row); }
  });
}

// Wählt ein Monat, ohne die ganze Ansicht neu zu bauen: Liste, Detail und die Hervorhebung im Diagramm. Auf dem
// Handy klappt das Detail unter der Zeile auf; war vorher eine ANDERE Zeile oberhalb offen, rutscht die getippte
// Zeile beim Zuklappen nach oben - der Scroll gleicht das aus, damit sie unter dem Finger bleibt.
function select(month, row) {
  const before = row?.getBoundingClientRect().top;
  state.month = month;
  const root = $('#payslip-overview');
  root.querySelector('.po-list').innerHTML = listHtml(state.model);
  root.querySelector('.po-side').innerHTML = detailHtml(state.model, month);
  root.querySelectorAll('.po-col').forEach(col => col.classList.toggle('is-selected', Number(col.dataset.poMonth) === month));
  if (before !== undefined) {
    const after = root.querySelector(`[data-po-row="${month}"]`)?.getBoundingClientRect().top;
    if (after !== undefined && Math.abs(after - before) > 1) window.scrollBy(0, after - before);
  }
}
