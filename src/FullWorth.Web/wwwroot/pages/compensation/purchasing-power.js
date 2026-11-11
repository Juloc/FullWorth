// "Gehalt gegen Preise": was das Gehalt seit der ersten Abrechnung gewachsen ist und was die Preise in der
// Zeit getan haben - die allgemeine Teuerung, einzelne Ausgaben des Grundbedarfs (Lebensmittel, Miete, Energie,
// Benzin) und, sobald bestaetigte Belege da sind, die Preise der eigenen Einkaeufe. Alle Linien beginnen am
// selben Monat bei 100, damit man sie nebeneinander lesen kann.
import { $, esc, euro, euro2, spaceId, api } from './shared.js';
import { monthsOf } from './housing-costs.js';

const MONTH = new Intl.DateTimeFormat('de-DE', { month: 'short', year: 'numeric' });
const monthName = index => MONTH.format(new Date(Math.floor(index / 12), index % 12, 1)).replace('.', '');
const num = value => Number(value) || 0;
const BASIS = { gross: 'Brutto', net: 'Netto', payout: 'Ausgezahlt' };
// Ausgangspunkt ist das Brutto der Abrechnung - mit Bonus, denn der gehoert zum Verdienst. Der Firmenwagen-Vorteil ist kein
// Geld und schwankt mit dem Auto (als das neue Auto 2025 weniger Vorteil brachte, sah es aus, als sinke das Gehalt); er ist
// deshalb standardmaessig draussen und laesst sich zuschalten. Netto und Ausgezahlt enthalten, was gezahlt wurde.
const BASIS_VALUE = {
  payout: p => num(p.payout),
  net: p => num(p.netPay),
  gross: p => num(p.grossPay) - (view.bonus ? 0 : num(p.bonus)) - (view.car ? 0 : num(p.companyCarTaxableBenefit))
};
const monthIndex = iso => Number(iso.slice(0, 4)) * 12 + Number(iso.slice(5, 7)) - 1;
const signed = value => `${value >= 0 ? '+' : '−'}${Math.abs(value).toLocaleString('de-DE', { maximumFractionDigits: 1 })} %`;

// Die Linien neben dem Gehalt. "own" kommen aus den eigenen Belegen, die anderen aus der amtlichen Statistik.
const LINES = [
  { key: 'gesamt', label: 'Alle Preise', color: 'var(--chart-inflation)', on: true },
  { key: 'nahrung', label: 'Lebensmittel', color: 'var(--cat-3)', on: true },
  { key: 'nettokaltmiete', label: 'Miete', color: 'var(--cat-2)' },
  { key: 'haushaltsenergie', label: 'Energie', color: 'var(--cat-4)' },
  { key: 'superbenzin', label: 'Benzin', color: 'var(--cat-5)' },
  { key: 'erdgas', label: 'Gas', color: 'var(--cat-8)' },
  { key: 'strom', label: 'Strom', color: 'var(--cat-7)' },
  { key: 'own', label: 'Alle meine Einkäufe', color: 'var(--cat-1)', own: true, on: true },
  { key: 'own:food', label: 'Meine Lebensmittel', color: 'var(--cat-7)', own: true },
  { key: 'own:fuel', label: 'Mein Kraftstoff', color: 'var(--cat-6)', own: true },
  // Aus den erfassten Abrechnungen (housing-costs.js): Euro je Monat, an die passende Statistiklinie angelegt.
  { key: 'home:heating', label: 'Meine Heizkosten', color: 'var(--cat-4)', own: true, group: 'home', stat: 'haushaltsenergie' },
  { key: 'home:electricity', label: 'Mein Strom', color: 'var(--cat-7)', own: true, group: 'home', stat: 'strom' },
  { key: 'home:water', label: 'Mein Wasser', color: 'var(--cat-2)', own: true, group: 'home', stat: 'gesamt' },
  { key: 'home:ancillary', label: 'Meine Nebenkosten', color: 'var(--cat-5)', own: true, group: 'home', stat: 'gesamt' }
];

const view = { basis: 'gross', bonus: true, car: false, window: 12, since: null, lines: new Set(LINES.filter(l => l.on).map(l => l.key)), paired: new Set() };
let data = null, loading = null, host = null, payslips = [], housing = [];

/** Die erfassten Wohnkosten (housing-costs.js); zeichnet neu, sobald sich etwas aendert. */
export function setHousingCosts(list) {
  housing = list || [];
  if (data && !data.failed && host?.isConnected) draw();
}

export function renderPurchasingPower(element, items) {
  host = element; payslips = items;
  if (!host) return;
  if (data) return draw();
  host.innerHTML = '<div class="panel-head"><div><h2>Verdienst gegen Preise</h2></div></div><div class="pp-placeholder"></div>';
  loading ||= load().then(result => { data = result; }).catch(() => { data = { failed: true }; });
  loading.then(draw);
}

async function load() {
  const space = spaceId();
  const [inflation, own] = await Promise.all([
    api('api/compensation/inflation/series'),
    // Ohne bestaetigte Belege ist die Antwort leer; ein Fehler dort darf die Statistik-Linien nicht kippen.
    api(`api/purchase-analytics/price-index?fullWorthSpaceId=${space}`).catch(() => null)]);
  return { inflation, own };
}

// ---- Modell ----------------------------------------------------------------------------------

// Je Monat die zuletzt gespeicherte Abrechnung. "Monat" zeigt jeden Monat, wie er war - mit Bonus ein Zacken im Juni und
// November. "Ø 12 Monate" ist der Durchschnitt der letzten zwoelf Monate: der Bonus verteilt sich, wie er verdient wurde,
// und Gehaltserhoehungen kommen dafuer erst nach und nach an. Fehlen Monate (nicht jede Abrechnung liegt vor), gilt dort der
// letzte Monat ohne Einmalzahlung; der erste Durchschnitt steht erst, wenn zwoelf Monate ab der ersten Abrechnung voll sind.
function salaryByMonth(basis) {
  const byMonth = new Map();
  for (const item of payslips) {
    const m = monthIndex(item.payslip.period);
    if (!byMonth.has(m)) byMonth.set(m, { value: BASIS_VALUE[basis](item.payslip), bonus: num(item.payslip.bonus) > 0 });
  }
  if (view.window === 1) return new Map([...byMonth].map(([m, entry]) => [m, entry.value]));
  const months = [...byMonth.keys()].sort((a, b) => a - b);
  const filled = new Map();
  let regular = null;
  for (let m = months[0]; m <= months[months.length - 1]; m++) {
    const entry = byMonth.get(m);
    if (entry) { filled.set(m, entry.value); if (!entry.bonus) regular = entry.value; }
    else if (regular !== null) filled.set(m, regular);
  }
  const average = new Map();
  for (const m of filled.keys()) {
    if (m - 11 < months[0]) continue;
    let sum = 0;
    for (let k = m - 11; k <= m; k++) sum += filled.get(k) ?? 0;
    average.set(m, sum / 12);
  }
  return average;
}

function buildModel() {
  const { inflation, own } = data;
  const first = monthIndex(`${inflation.firstMonth}-01`);
  const last = monthIndex(`${inflation.lastMonth}-01`);
  const salary = salaryByMonth(view.basis);
  const months = [...salary.keys()].sort((a, b) => a - b);
  const firstPay = months[0], lastPay = months[months.length - 1];
  const end = Math.min(lastPay, last);
  // Frueher als die Statistik beginnt, geht es nicht - und der Rest der Kette waere aus der Luft.
  const startOptions = [...new Set([Math.max(firstPay, first), ...[2024, 2025, 2026].map(y => y * 12).filter(m => m > Math.max(firstPay, first) && m < end)])];
  const start = startOptions.includes(view.since) ? view.since : startOptions[0];

  const at = (m, map) => map.get(m);
  const salaryAt = m => salary.has(m) ? salary.get(m) : null;
  // Gehalt: bei einem Startmonat ohne Abrechnung gilt die erste danach.
  const salaryStart = salaryAt(months.find(m => m >= start));

  const result = { start, end, startOptions, lines: [], salaryNow: salary.get(months.filter(m => m <= end).pop()), salaryStart };
  const cpi = key => {
    const series = inflation.series.find(s => s.key === key);
    if (!series) return null;
    const value = m => series.values[m - first] ?? null;
    return { value, base: value(start) };
  };

  result.lines.push({ key: 'salary', label: BASIS[view.basis], color: 'var(--accent)', main: true,
    points: months.filter(m => m >= start && m <= end).map(m => [m, salaryAt(m) / salaryStart * 100]) });

  for (const line of LINES.filter(l => !l.own)) {
    const series = cpi(line.key);
    if (!series?.base) continue;
    result.lines.push({ ...line, points: range(start, end).map(m => [m, series.value(m) / series.base * 100]).filter(p => Number.isFinite(p[1])) });
  }

  // Eigene Preise: je Quartal ein Wert (Mitte des Quartals). Sie beginnen, wo die Belege beginnen - dort werden
  // sie an die allgemeine Linie "angedockt", denn davor weiss man nichts ueber die eigenen Preise.
  const general = result.lines.find(l => l.key === 'gesamt');
  const own1 = (line, points) => {
    const placed = points.map(p => [monthIndex(p.start) + 1, p.index]).filter(([m]) => m >= start && m <= end);
    if (placed.length < 2 || !general) return;
    const anchor = general.points.find(p => p[0] >= placed[0][0]) || general.points[general.points.length - 1];
    const factor = anchor[1] / placed[0][1];
    result.lines.push({ ...line, points: placed.map(([m, v]) => [m, v * factor]), anchoredAt: placed[0][0], items: placed[placed.length - 1] });
  };
  if (own) {
    own1(LINES.find(l => l.key === 'own'), own.overall || []);
    for (const group of own.groups || []) {
      const line = LINES.find(l => l.key === `own:${group.key}`);
      if (line) own1(line, group.points);
    }
  }
  // Wohnkosten: je Kostenart und Jahr ein Punkt in der Jahresmitte, Euro je Monat - aber nur, wenn fuer das Jahr mindestens
  // elf Monate erfasst sind. Eine Abrechnung ueber vier Wintermonate neben einer ueber sieben Sommermonate waere sonst
  // eine Heizsaison-Schwankung und keine Preisentwicklung. Erst ab zwei Jahren ist es eine Linie.
  for (const line of LINES.filter(l => l.group === 'home')) {
    const years = new Map();
    for (const e of housing.filter(e => `home:${e.kind}` === line.key)) {
      const year = Number(e.periodFrom.slice(0, 4)), sum = years.get(year) || { amount: 0, months: 0 };
      years.set(year, { amount: sum.amount + e.amount, months: sum.months + monthsOf(e) });
    }
    const points = [...years].filter(([, v]) => v.months >= 11).map(([year, v]) => [year * 12 + 6, v.amount / v.months])
      .filter(([m]) => m >= start && m <= end).sort((a, b) => a[0] - b[0]);
    const base = result.lines.find(l => l.key === line.stat) || general;
    if (points.length < 2 || !base) continue;
    const anchor = base.points.find(p => p[0] >= points[0][0]) || base.points[base.points.length - 1];
    const factor = anchor[1] / points[0][1];
    result.lines.push({ ...line, points: points.map(([m, v]) => [m, v * factor]), anchoredAt: points[0][0] });
  }
  result.ownAvailable = result.lines.filter(l => l.own).length > 0;
  return result;
}

const range = (from, to) => Array.from({ length: to - from + 1 }, (_, i) => from + i);
const last = points => points[points.length - 1];

// ---- Zeichnen --------------------------------------------------------------------------------

// Die Bedienelemente: Basis, Zeitraum, bei Brutto die Zuschaltungen - und, wenn es mehrere Startpunkte gibt, "seit".
function controlsHtml(startOptions = [], start = null) {
  const tabs = (label, attr, entries, selected) => `<div class="po-years" role="tablist" aria-label="${label}">${entries.map(([key, name]) => `<button type="button" role="tab" class="po-year" ${attr}="${key}" aria-selected="${String(selected) === String(key)}">${name}</button>`).join('')}</div>`;
  const option = (key, name) => `<label class="pp-option"><input type="checkbox" data-pp-opt="${key}"${view[key] ? ' checked' : ''}>${name}</label>`;
  return `<div class="pp-controls">
      ${tabs('Verdienst', 'data-pp-basis', Object.entries(BASIS), view.basis)}
      ${tabs('Zeitraum', 'data-pp-window', [[12, 'Ø 12 Monate'], [1, 'Monat']], view.window)}
      ${startOptions.length > 1 ? `<select data-pp-since aria-label="Vergleich seit">${startOptions.map(m => `<option value="${m}"${m === start ? ' selected' : ''}>seit ${monthName(m)}</option>`).join('')}</select>` : ''}
    </div>
    ${view.basis === 'gross' ? `<div class="pp-options">${option('bonus', 'Bonus einrechnen')}${option('car', 'Firmenwagen-Vorteil einrechnen')}</div>` : ''}`;
}

function draw() {
  if (!host) return;
  if (!data || data.failed || !payslips.length) { host.innerHTML = '<div class="panel-head"><div><h2>Verdienst gegen Preise</h2></div></div>'; return; }
  if (salaryByMonth(view.basis).size < 2) {
    host.innerHTML = `<div class="panel-head pp-head"><div><h2>Verdienst gegen Preise</h2></div></div>${controlsHtml()}<p class="extended-note">${view.window === 12 ? 'Für den Durchschnitt über zwölf Monate braucht es Abrechnungen aus zwölf Monaten ab der ersten. Wähle „Monat“, um jeden Monat einzeln zu sehen.' : 'Dafür braucht es Abrechnungen aus mindestens zwei Monaten.'}</p>`;
    bind({ geometry: null });
    return;
  }
  const model = buildModel();
  // Wo es zu einem Thema beides gibt - die Statistik und die eigenen Preise -, werden beide gezeigt, sonst
  // sieht man nur eine Seite des Vergleichs. Einmal je Thema, sobald es die eigene Linie gibt; danach entscheidet der Benutzer.
  for (const [stat, own] of [['nahrung', 'own:food'], ['superbenzin', 'own:fuel'], ['haushaltsenergie', 'home:heating'], ['strom', 'home:electricity']])
    if (!view.paired.has(own) && model.lines.some(l => l.key === own)) { view.paired.add(own); view.lines.add(stat); view.lines.add(own); }
  const salary = model.lines[0], general = model.lines.find(l => l.key === 'gesamt');
  const salaryChange = last(salary.points)[1] - 100;
  const priceChange = general ? last(general.points)[1] - 100 : null;
  const real = priceChange === null ? null : ((1 + salaryChange / 100) / (1 + priceChange / 100) - 1) * 100;
  const power = priceChange === null ? null : model.salaryNow - model.salaryStart * (1 + priceChange / 100);
  const label = BASIS[view.basis];
  const descr = [label, view.basis === 'gross' ? (view.bonus ? 'mit Bonus' : 'ohne Bonus') : '', view.basis === 'gross' && view.car ? 'mit Firmenwagen-Vorteil' : '', view.window === 12 ? 'Ø 12 Monate' : 'je Monat'].filter(Boolean).join(', ');

  const legendRow = l => {
    const line = model.lines.find(x => x.key === l.key);
    return `<label class="pp-line${l.own ? ' is-own' : ''}" style="--pp-color:${l.color}"><input type="checkbox" data-pp-line="${l.key}"${view.lines.has(l.key) ? ' checked' : ''}><i></i><span>${esc(l.label)}<small>${l.own ? 'deine Daten' : 'Statistik'}</small></span><b>${signed(last(line.points)[1] - 100)}</b></label>`;
  };
  // Oben stehen die Linien, die im Diagramm zu sehen sind - mit Herkunft; alle anderen warten hinter "Weitere Linien".
  const available = LINES.filter(l => model.lines.some(x => x.key === l.key));
  const shownRows = available.filter(l => view.lines.has(l.key)).map(legendRow).join('');
  const restRows = available.filter(l => !view.lines.has(l.key)).map(legendRow).join('');
  const chips = `<div class="pp-legend-group">${shownRows}</div>${restRows ? `<details class="pp-more"><summary>Weitere Linien (${available.length - available.filter(l => view.lines.has(l.key)).length})</summary><div class="pp-legend-group">${restRows}</div></details>` : ''}`;

  host.innerHTML = `
    <div class="panel-head pp-head"><div><h2>Verdienst gegen Preise</h2></div></div>
    <p class="extended-note pp-hint">Verdienst: ${descr}. Alle Linien starten im ${monthName(model.start)} bei 100. Steigt deine schwarze Linie schneller als die Preise, kaufst du heute mehr als damals.</p>
    ${controlsHtml(model.startOptions, model.start)}
    <div class="po-kpis pp-kpis">
      <div><span>Dein Verdienst</span><strong>${signed(salaryChange)}</strong></div>
      ${priceChange === null ? '' : `<div><span>Preise insgesamt</span><strong>${signed(priceChange)}</strong></div><div><span>Real für dich</span><strong class="${real >= 0 ? 'positive' : 'negative'}">${signed(real)}</strong></div>`}
    </div>
    ${power === null ? '' : `<p class="extended-note pp-sentence">Von deinem Verdienst kaufst du heute ${Math.abs(real).toLocaleString('de-DE', { maximumFractionDigits: 1 })} % ${real >= 0 ? 'mehr' : 'weniger'} als im ${monthName(model.start)} - das sind rund ${euro.format(Math.abs(power))} im Monat.</p>`}
    <div class="pp-wrap"><div id="pp-chart"></div><div class="po-tip" hidden></div></div>
    <div class="pp-lines">${chips}</div>
    <p class="pp-foot">Preise: Destatis Verbraucherpreisindex Deutschland (${esc(data.inflation.base)}), Stand ${monthName(monthIndex(`${data.inflation.lastMonth}-01`))}.${data.own && model.ownAvailable ? ' „Meine Einkäufe“: Preisentwicklung derselben Artikel im selben Geschäft aus deinen bestätigten Belegen, je Quartal, ab dem ersten Quartal an die allgemeine Linie angelegt.' : ''}${data.own && !model.ownAvailable ? ' Deine eigenen Einkaufspreise erscheinen hier, sobald genug bestätigte Belege mit denselben Artikeln da sind.' : ''}${view.window === 12 ? ' Der Durchschnitt über zwölf Monate verteilt den Bonus, wie er verdient wurde; Monate ohne Abrechnung gelten mit dem letzten Monat ohne Einmalzahlung.' : ''}</p>`;

  drawChart(model);
  bind(model);
}

function niceStep(span) {
  for (const step of [5, 10, 20, 25, 50, 100]) if (span / step <= 6) return step;
  return 100;
}

function drawChart(model) {
  const el = $('#pp-chart'); if (!el) return;
  const width = Math.max(280, Math.floor(el.clientWidth));
  const compact = width < 520;
  const height = compact ? 250 : 300;
  const left = 36, right = 10, top = 14, bottom = 26;
  const plotW = width - left - right, plotH = height - top - bottom;
  const shown = model.lines.filter(l => l.main || view.lines.has(l.key));
  const values = shown.flatMap(l => l.points.map(p => p[1]));
  const step = niceStep(Math.max(...values) - Math.min(100, ...values));
  const yMin = Math.floor(Math.min(100, ...values) / step) * step, yMax = Math.ceil(Math.max(...values) / step) * step;
  const x = m => left + (m - model.start) / Math.max(1, model.end - model.start) * plotW;
  const y = v => top + plotH - (v - yMin) / (yMax - yMin) * plotH;

  let grid = '';
  for (let v = yMin; v <= yMax; v += step)
    grid += `<line class="po-grid-line" x1="${left}" x2="${width - right}" y1="${y(v).toFixed(1)}" y2="${y(v).toFixed(1)}"${v === 100 ? ' stroke-dasharray="4 4"' : ''}/><text class="po-axis" x="${left - 6}" y="${(y(v) + 3.5).toFixed(1)}" text-anchor="end">${v}</text>`;

  // Jahresmarken statt jedes Monats: ein Monat alle paar Pixel waere nur Rauschen.
  let axis = '';
  const stepMonths = compact ? 12 : 6;
  for (let m = Math.ceil(model.start / stepMonths) * stepMonths; m <= model.end; m += stepMonths)
    axis += `<text class="po-axis" x="${x(m).toFixed(1)}" y="${height - 7}" text-anchor="middle">${m % 12 === 0 ? Math.floor(m / 12) : monthName(m).split(' ')[0]}</text>`;

  // Das Gehalt ist eine Treppe: es gilt, bis die naechste Abrechnung etwas anderes zeigt. Eine schraege Linie ueber
  // fehlende Monate (nicht jede Abrechnung liegt vor) taeuschte einen allmaehlichen Anstieg vor.
  const path = (points, steps) => points.map((p, i) => `${i ? (steps ? `H${x(p[0]).toFixed(1)}V` : 'L') : 'M'}${steps && i ? '' : `${x(p[0]).toFixed(1)} `}${y(p[1]).toFixed(1)}`).join('');
  const lines = shown.slice().sort((a, b) => Number(!!a.main) - Number(!!b.main)).map(l =>
    `<path d="${path(l.points, l.main)}" fill="none" stroke="${l.color}" stroke-width="${l.main ? 3 : 2}" stroke-linecap="round" stroke-linejoin="round"${l.own ? ' stroke-dasharray="6 4"' : ''}/>`
    + (l.anchoredAt ? `<circle cx="${x(l.anchoredAt).toFixed(1)}" cy="${y(l.points[0][1]).toFixed(1)}" r="3.5" fill="${l.color}"/>` : '')
    + `<circle cx="${x(last(l.points)[0]).toFixed(1)}" cy="${y(last(l.points)[1]).toFixed(1)}" r="4" fill="${l.color}"/>`).join('');

  el.innerHTML = `<svg class="pp-chart" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="Gehalt und Preise seit ${monthName(model.start)}, Anfang gleich 100">${grid}${axis}${lines}<line class="pp-guide" hidden y1="${top}" y2="${top + plotH}"/><rect class="pp-hit" x="${left}" y="${top}" width="${plotW}" height="${plotH}" fill="transparent"/></svg>`;
  model.geometry = { left, top, plotW, plotH, x, shown };
}

function bind(model) {
  host.onchange = event => {
    const line = event.target.closest('[data-pp-line]');
    if (line) { line.checked ? view.lines.add(line.dataset.ppLine) : view.lines.delete(line.dataset.ppLine); return draw(); }
    const opt = event.target.closest('[data-pp-opt]');
    if (opt) { view[opt.dataset.ppOpt] = opt.checked; view.since = null; return draw(); }
    if (event.target.matches('[data-pp-since]')) { view.since = Number(event.target.value); draw(); }
  };
  host.onclick = event => {
    const basis = event.target.closest('[data-pp-basis]');
    if (basis) { view.basis = basis.dataset.ppBasis; view.since = null; return draw(); }
    const win = event.target.closest('[data-pp-window]');
    if (win) { view.window = Number(win.dataset.ppWindow); view.since = null; draw(); }
  };
  if (!model.geometry) return;

  const wrap = host.querySelector('.pp-wrap'), tip = wrap.querySelector('.po-tip'), svg = wrap.querySelector('svg'), guide = svg.querySelector('.pp-guide');
  const { left, top, plotW, x, shown } = model.geometry;
  const show = event => {
    const box = svg.getBoundingClientRect();
    const m = Math.round(model.start + Math.min(1, Math.max(0, (event.clientX - box.left - left) / plotW)) * (model.end - model.start));
    const rows = shown.map(l => {
      const nearest = l.points.reduce((best, p) => Math.abs(p[0] - m) < Math.abs(best[0] - m) ? p : best, l.points[0]);
      return Math.abs(nearest[0] - m) > 3 ? null : { l, v: nearest[1] };
    }).filter(Boolean);
    const salaryNow = rows.find(r => r.l.main);
    tip.innerHTML = `<strong>${monthName(m)}</strong>${rows.map(r => `<div class="po-tip-row"><span><i style="background:${r.l.color}"></i>${esc(r.l.label)}</span><span>${r.v.toLocaleString('de-DE', { maximumFractionDigits: 1 })}</span></div>`).join('')}${salaryNow ? `<div class="po-tip-row po-tip-foot"><span>${BASIS[view.basis]}</span><span>${euro2.format(salaryNow.v / 100 * model.salaryStart)}</span></div>` : ''}`;
    tip.hidden = false;
    guide.removeAttribute('hidden'); guide.setAttribute('x1', x(m).toFixed(1)); guide.setAttribute('x2', x(m).toFixed(1));
    const w = tip.offsetWidth, room = wrap.clientWidth;
    let at = x(m) + 14; if (at + w > room - 4) at = x(m) - w - 14;
    tip.style.left = `${Math.max(4, at)}px`; tip.style.top = `${top}px`;
  };
  const hide = () => { tip.hidden = true; guide.setAttribute('hidden', ''); };
  svg.addEventListener('pointermove', show);
  svg.addEventListener('pointerdown', show);
  svg.addEventListener('pointerleave', hide);
}

// Nur eine geaenderte Breite zeichnet neu: das Diagramm hat eine feste Hoehe, und jede Zeichnung ueber die Hoehe
// wuerde den Beobachter von vorn ausloesen.
let seenWidth = 0;
new ResizeObserver(() => {
  const width = $('#pp-chart')?.clientWidth || 0;
  if (!width || width === seenWidth || !data || data.failed || salaryByMonth(view.basis).size < 2) return;
  seenWidth = width;
  const model = buildModel(); drawChart(model); bind(model);
}).observe(document.documentElement);
