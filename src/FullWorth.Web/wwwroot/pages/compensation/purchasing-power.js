// "Gehalt gegen Preise": was das Gehalt seit der ersten Abrechnung gewachsen ist und was die Preise in der
// Zeit getan haben - die allgemeine Teuerung, einzelne Ausgaben des Grundbedarfs (Lebensmittel, Miete, Energie,
// Benzin) und, sobald bestaetigte Belege da sind, die Preise der eigenen Einkaeufe. Alle Linien beginnen am
// selben Monat bei 100, damit man sie nebeneinander lesen kann.
import { $, esc, euro, euro2, spaceId, api } from './shared.js';

const MONTH = new Intl.DateTimeFormat('de-DE', { month: 'short', year: 'numeric' });
const monthName = index => MONTH.format(new Date(Math.floor(index / 12), index % 12, 1)).replace('.', '');
const num = value => Number(value) || 0;
const monthIndex = iso => Number(iso.slice(0, 4)) * 12 + Number(iso.slice(5, 7)) - 1;
const signed = value => `${value >= 0 ? '+' : '−'}${Math.abs(value).toLocaleString('de-DE', { maximumFractionDigits: 1 })} %`;

// Die Linien neben dem Gehalt. "own" kommen aus den eigenen Belegen, die anderen aus der amtlichen Statistik.
const LINES = [
  { key: 'gesamt', label: 'Allgemeine Teuerung', color: 'var(--chart-inflation)', on: true },
  { key: 'nahrung', label: 'Lebensmittel', color: 'var(--cat-3)', on: true },
  { key: 'nettokaltmiete', label: 'Miete', color: 'var(--cat-2)' },
  { key: 'haushaltsenergie', label: 'Energie', color: 'var(--cat-4)' },
  { key: 'superbenzin', label: 'Benzin', color: 'var(--cat-5)' },
  { key: 'own', label: 'Meine Einkäufe', color: 'var(--cat-1)', own: true, on: true },
  { key: 'own:food', label: 'Meine Lebensmittel', color: 'var(--cat-7)', own: true },
  { key: 'own:fuel', label: 'Mein Kraftstoff', color: 'var(--cat-6)', own: true }
];

const view = { basis: 'net', since: null, lines: new Set(LINES.filter(l => l.on).map(l => l.key)) };
let data = null, loading = null, host = null, payslips = [];

export function renderPurchasingPower(element, items) {
  host = element; payslips = items;
  if (!host) return;
  if (data) return draw();
  host.innerHTML = '<div class="panel-head"><div><h2>Gehalt gegen Preise</h2><p>Wird geladen …</p></div></div><div class="pp-placeholder"></div>';
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

// Je Monat die zuletzt gespeicherte Abrechnung, dann ein Glaetter: ein Bonus oder eine Nachzahlung in einem Monat
// soll nicht wie eine Gehaltserhoehung aussehen. Der Median der Monate im Umkreis von zwei Monaten laesst einen
// Ausreisser aus; fehlen Monate (nicht jede Abrechnung liegt vor), gilt bei nur zwei Werten der kleinere - ein
// einzelner hoher Wert ist eher eine Einmalzahlung als ein neues Gehalt.
function salaryByMonth(basis) {
  const byMonth = new Map();
  for (const item of payslips) {
    const m = monthIndex(item.payslip.period);
    if (!byMonth.has(m)) byMonth.set(m, num(basis === 'net' ? item.payslip.netPay : item.payslip.grossPay));
  }
  const smooth = new Map();
  for (const [m, value] of byMonth) {
    const around = [-2, -1, 0, 1, 2].map(d => byMonth.get(m + d)).filter(v => v > 0).sort((a, b) => a - b);
    // Bei gerader Zahl der untere Median: zwei hohe Monate nebeneinander (Bonus und eine Nachzahlung) sollen nicht gewinnen.
    smooth.set(m, around.length >= 3 ? around[Math.floor((around.length - 1) / 2)] : around.length === 2 ? around[0] : value);
  }
  return smooth;
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

  result.lines.push({ key: 'salary', label: view.basis === 'net' ? 'Netto' : 'Brutto', color: 'var(--accent)', main: true,
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
  result.ownAvailable = result.lines.filter(l => l.own).length > 0;
  return result;
}

const range = (from, to) => Array.from({ length: to - from + 1 }, (_, i) => from + i);
const last = points => points[points.length - 1];

// ---- Zeichnen --------------------------------------------------------------------------------

function draw() {
  if (!host) return;
  if (!data || data.failed || !payslips.length) { host.innerHTML = '<div class="panel-head"><div><h2>Gehalt gegen Preise</h2><p>Die Preisdaten konnten gerade nicht geladen werden.</p></div></div>'; return; }
  const model = buildModel();
  const salary = model.lines[0], general = model.lines.find(l => l.key === 'gesamt');
  if (!salary.points.length || salary.points.length < 2) {
    host.innerHTML = '<div class="panel-head"><div><h2>Gehalt gegen Preise</h2><p>Dafür braucht es Abrechnungen aus mindestens zwei Monaten.</p></div></div>';
    return;
  }

  const salaryChange = last(salary.points)[1] - 100;
  const priceChange = general ? last(general.points)[1] - 100 : null;
  const real = priceChange === null ? null : ((1 + salaryChange / 100) / (1 + priceChange / 100) - 1) * 100;
  const power = priceChange === null ? null : model.salaryNow - model.salaryStart * (1 + priceChange / 100);
  const label = view.basis === 'net' ? 'Netto' : 'Brutto';

  const chips = LINES.filter(l => model.lines.some(x => x.key === l.key)).map(l => {
    const line = model.lines.find(x => x.key === l.key);
    return `<label class="pp-line" style="--pp-color:${l.color}"><input type="checkbox" data-pp-line="${l.key}"${view.lines.has(l.key) ? ' checked' : ''}><i></i><span>${esc(l.label)}</span><b>${signed(last(line.points)[1] - 100)}</b></label>`;
  }).join('');

  host.innerHTML = `
    <div class="panel-head pp-head"><div><h2>Gehalt gegen Preise</h2><p>Alles beginnt im ${monthName(model.start)} bei 100. Liegt dein ${label} über den Preislinien, bleibt dir real mehr.</p></div></div>
    <div class="pp-controls">
      <div class="po-years" role="tablist" aria-label="Gehalt"><button type="button" role="tab" class="po-year" data-pp-basis="net" aria-selected="${view.basis === 'net'}">Netto</button><button type="button" role="tab" class="po-year" data-pp-basis="gross" aria-selected="${view.basis === 'gross'}">Brutto</button></div>
      ${model.startOptions.length > 1 ? `<select data-pp-since aria-label="Vergleich seit">${model.startOptions.map(m => `<option value="${m}"${m === model.start ? ' selected' : ''}>seit ${monthName(m)}</option>`).join('')}</select>` : ''}
    </div>
    <div class="po-kpis pp-kpis">
      <div><span>${label} seit ${monthName(model.start)}</span><strong>${signed(salaryChange)}</strong></div>
      ${priceChange === null ? '' : `<div><span>Preise allgemein</span><strong>${signed(priceChange)}</strong></div><div><span>Real</span><strong class="${real >= 0 ? 'positive' : 'negative'}">${signed(real)}</strong></div>`}
    </div>
    ${power === null ? '' : `<p class="extended-note pp-sentence">Das sind rund ${euro.format(Math.abs(power))} im Monat ${power >= 0 ? 'mehr' : 'weniger'} Kaufkraft als am Anfang.</p>`}
    <div class="pp-wrap"><div id="pp-chart"></div><div class="po-tip" hidden></div></div>
    <div class="pp-lines">${chips}</div>
    <p class="pp-foot">Preise: Destatis Verbraucherpreisindex Deutschland (${esc(data.inflation.base)}), Stand ${monthName(monthIndex(`${data.inflation.lastMonth}-01`))}.${data.own && model.ownAvailable ? ' „Meine Einkäufe“: Preisentwicklung derselben Artikel im selben Geschäft aus deinen bestätigten Belegen, je Quartal, ab dem ersten Quartal an die allgemeine Linie angelegt.' : ''}${data.own && !model.ownAvailable ? ' Deine eigenen Einkaufspreise erscheinen hier, sobald genug bestätigte Belege mit denselben Artikeln da sind.' : ''} Das Gehalt ist je Monat geglättet (Median aus den Monaten ringsum), damit ein Bonus keinen Sprung vortäuscht.</p>`;

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

  const path = points => points.map((p, i) => `${i ? 'L' : 'M'}${x(p[0]).toFixed(1)} ${y(p[1]).toFixed(1)}`).join('');
  const lines = shown.slice().sort((a, b) => Number(!!a.main) - Number(!!b.main)).map(l =>
    `<path d="${path(l.points)}" fill="none" stroke="${l.color}" stroke-width="${l.main ? 3 : 2}" stroke-linecap="round" stroke-linejoin="round"${l.own ? ' stroke-dasharray="1 0"' : ''}/>`
    + (l.anchoredAt ? `<circle cx="${x(l.anchoredAt).toFixed(1)}" cy="${y(l.points[0][1]).toFixed(1)}" r="3.5" fill="${l.color}"/>` : '')
    + `<circle cx="${x(last(l.points)[0]).toFixed(1)}" cy="${y(last(l.points)[1]).toFixed(1)}" r="4" fill="${l.color}"/>`).join('');

  el.innerHTML = `<svg class="pp-chart" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="Gehalt und Preise seit ${monthName(model.start)}, Anfang gleich 100">${grid}${axis}${lines}<line class="pp-guide" hidden y1="${top}" y2="${top + plotH}"/><rect class="pp-hit" x="${left}" y="${top}" width="${plotW}" height="${plotH}" fill="transparent"/></svg>`;
  model.geometry = { left, top, plotW, plotH, x, shown };
}

function bind(model) {
  host.onchange = event => {
    const line = event.target.closest('[data-pp-line]');
    if (line) { line.checked ? view.lines.add(line.dataset.ppLine) : view.lines.delete(line.dataset.ppLine); return draw(); }
    if (event.target.matches('[data-pp-since]')) { view.since = Number(event.target.value); draw(); }
  };
  host.onclick = event => {
    const basis = event.target.closest('[data-pp-basis]');
    if (basis) { view.basis = basis.dataset.ppBasis; draw(); }
  };

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
    tip.innerHTML = `<strong>${monthName(m)}</strong>${rows.map(r => `<div class="po-tip-row"><span><i style="background:${r.l.color}"></i>${esc(r.l.label)}</span><span>${r.v.toLocaleString('de-DE', { maximumFractionDigits: 1 })}</span></div>`).join('')}${salaryNow ? `<div class="po-tip-row po-tip-foot"><span>${view.basis === 'net' ? 'Netto' : 'Brutto'}</span><span>${euro2.format(salaryNow.v / 100 * model.salaryStart)}</span></div>` : ''}`;
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
  if (!width || width === seenWidth || !data || data.failed) return;
  seenWidth = width;
  const model = buildModel(); drawChart(model); bind(model);
}).observe(document.documentElement);
