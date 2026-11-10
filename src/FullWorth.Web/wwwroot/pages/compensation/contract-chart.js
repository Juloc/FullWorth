// "Brutto laut Vertrag": die Treppe der bestätigten Vertragsstände seit Vertragsbeginn, flächig leicht
// gefüllt, an jeder Änderung ein Marker mit Betrag, Datum und Prozent direkt am Diagramm - und die
// Punkte der Abrechnungen darüber: grün, wenn das laufende Brutto dem Vertrag entspricht, orange, wenn nicht.
//
// Inline-SVG wie die anderen Diagramme der App, in der Breite des Rahmens gezeichnet (nicht skaliert), damit
// die Schrift auf dem Handy dieselbe Größe hat wie am Schreibtisch. Alle Farben sind Tokens.
import { esc, euro, euro2 } from './shared.js';

const MONTH = Array.from({ length: 12 }, (_, i) => new Intl.DateTimeFormat('de-DE', { month: 'short' }).format(new Date(2021, i, 1)).replace('.', ''));
const MONTH_LONG = Array.from({ length: 12 }, (_, i) => new Intl.DateTimeFormat('de-DE', { month: 'long' }).format(new Date(2021, i, 1)));
const DAY = 86400000;
const num = value => Number(value) || 0;
const day = iso => new Date(`${String(iso).slice(0, 10)}T12:00:00`).getTime();
const dmy = iso => new Intl.DateTimeFormat('de-DE', { day: '2-digit', month: '2-digit', year: 'numeric' }).format(new Date(day(iso)));

// Das laufende Brutto einer Abrechnung: ohne Bonus und ohne den Sachbezug des Dienstwagens, denn beides steht
// nicht im Vertragsgehalt (derselbe Vergleich wie im Backend, ContractReconciliation).
const running = p => num(p.grossPay) - num(p.bonus) - num(p.companyCarTaxableBenefit);

function niceStep(span) {
  for (const step of [50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000, 20000]) if (span / step <= 5) return step;
  return 50000;
}

/**
 * states: [{ id, state: { validFrom, grossMonthly } }], payslips: [{ payslip: { period, grossPay, bonus, ... } }].
 * Zeichnet in host und bindet die Anzeige beim Überfahren (Maus) oder Tippen (Touch).
 */
export function drawContractChart(host, { states, payslips }) {
  const steps = states.map(s => ({ t: day(s.state.validFrom), gross: num(s.state.grossMonthly), id: s.id, state: s.state }))
    .sort((a, b) => a.t - b.t);
  if (!steps.length) { host.innerHTML = '<p class="extended-note">Noch kein Vertragsstand. Das Diagramm erscheint mit dem ersten bestätigten Stand.</p>'; return; }

  const width = Math.max(280, Math.floor(host.clientWidth));
  const compact = width < 520;
  const height = width < 400 ? 270 : 330;
  const left = compact ? 46 : 56, right = compact ? 14 : 20, top = 44, bottom = 34;
  const plotW = width - left - right, plotH = height - top - bottom;

  const points = payslips.map(item => ({ t: day(item.payslip.period), gross: running(item.payslip), period: item.payslip.period }))
    .sort((a, b) => a.t - b.t);
  const t0 = Math.min(steps[0].t, points[0]?.t ?? Infinity) - 10 * DAY;
  const now = Date.now();
  const t1 = Math.max(now, steps[steps.length - 1].t, points[points.length - 1]?.t ?? 0) + 20 * DAY;
  const x = t => left + (t - t0) / (t1 - t0) * plotW;

  const values = [...steps.map(s => s.gross), ...points.map(p => p.gross)].filter(v => v > 0);
  const lo = Math.min(...values), hi = Math.max(...values);
  const step = niceStep(Math.max(hi - lo, hi * 0.12));
  // Die Achse beginnt unter dem niedrigsten Wert, nicht bei null: ein Sprung von 5.000 auf 5.400 € wäre sonst
  // ein Strich. Die untere Beschriftung nennt den Wert ausdrücklich, damit niemand eine Null liest.
  const yMin = Math.max(0, Math.floor((lo - step * 0.6) / step) * step);
  const yMax = Math.ceil((hi + step * 0.5) / step) * step;
  const y = v => top + plotH - (v - yMin) / (yMax - yMin) * plotH;

  const levelAt = t => { let level = null; for (const s of steps) if (s.t <= t) level = s; return level; };

  // Treppe: waagerecht bis zum nächsten Stand, dort senkrecht.
  let line = `M${x(t0 + 10 * DAY).toFixed(1)} ${y(steps[0].gross).toFixed(1)}`;
  steps.forEach((s, i) => {
    const next = steps[i + 1];
    if (i > 0) line += `V${y(s.gross).toFixed(1)}`;
    line += `H${(next ? x(next.t) : x(t1 - 20 * DAY)).toFixed(1)}`;
  });
  const first = x(t0 + 10 * DAY), base = y(yMin);
  const area = `${line}V${base.toFixed(1)}H${first.toFixed(1)}Z`;

  // y-Achse
  let grid = '';
  for (let v = yMin; v <= yMax; v += step)
    grid += `<line class="cc-grid" x1="${left}" x2="${width - right}" y1="${y(v).toFixed(1)}" y2="${y(v).toFixed(1)}"/><text class="cc-axis" x="${left - 8}" y="${(y(v) + 3.5).toFixed(1)}" text-anchor="end">${v.toLocaleString('de-DE')}</text>`;

  // x-Achse: bei kurzer Zeitspanne die Quartalsanfänge (der erste und jeder Januar mit Jahr), sonst die Jahre.
  const months = (t1 - t0) / DAY / 30.4;
  const ticks = [];
  for (let d = new Date(new Date(t0).getFullYear(), new Date(t0).getMonth() + 1, 1); d.getTime() < t1; d = new Date(d.getFullYear(), d.getMonth() + 1, 1)) {
    const m = d.getMonth();
    if (months > 30) { if (m === 0) ticks.push({ t: d.getTime(), label: String(d.getFullYear()) }); }
    else if (m % 3 === 0) ticks.push({ t: d.getTime(), label: m === 0 || !ticks.length ? `${MONTH[m]} ${String(d.getFullYear()).slice(2)}` : MONTH[m] });
  }
  const axis = ticks.map(tick => `<line class="cc-tick" x1="${x(tick.t).toFixed(1)}" x2="${x(tick.t).toFixed(1)}" y1="${top + plotH}" y2="${top + plotH + 5}"/><text class="cc-axis" x="${x(tick.t).toFixed(1)}" y="${height - 10}" text-anchor="middle">${tick.label}</text>`).join('');

  // Punkte der Abrechnungen
  const dots = points.map(p => {
    const level = levelAt(p.t), off = !level || Math.abs(p.gross - level.gross) > 1;
    return `<circle class="cc-dot ${off ? 'is-off' : 'is-ok'}" cx="${x(p.t).toFixed(1)}" cy="${y(p.gross).toFixed(1)}" r="3.2"/>`;
  }).join('');

  // Beschriftung der Stufen: Betrag, Prozent, Datum - über der Stufe; eine Absenkung unter ihr. Liegt ein Schild
  // zu nah am vorigen, rückt es eine Zeile nach oben beziehungsweise unten, statt es zu überdecken.
  let labels = '', markers = '', lastRight = -Infinity, lastOffset = 0;
  steps.forEach((s, i) => {
    const prev = steps[i - 1], cx = x(s.t), cy = y(s.gross);
    // Ein Stand ohne anderes Gehalt (Dienstwagen, Urlaub, ...) bekommt nur den Marker: eine zweite Beschriftung
    // mit demselben Betrag und "+0 %" waere Rauschen. Seine Einzelheiten stehen im Protokoll und im Tooltip.
    if (prev && s.gross === prev.gross) { markers += `<circle class="cc-marker is-quiet" cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="4"/>`; return; }
    const percent = prev ? (s.gross - prev.gross) / prev.gross * 100 : null;
    const down = prev && s.gross < prev.gross;
    const amount = `${euro.format(s.gross)}`, pct = percent === null ? '' : ` ${percent >= 0 ? '+' : '−'}${Math.abs(percent).toLocaleString('de-DE', { maximumFractionDigits: 1 })} %`;
    const w = (amount.length + pct.length) * 7 + 8;
    const anchorEnd = cx + w > width - right;
    const startX = anchorEnd ? cx - 8 : cx + 8;
    const overlap = startX < lastRight + 6 && !anchorEnd;
    const offset = overlap ? (lastOffset === 0 ? 1 : 0) : 0;
    const ty = down ? cy + 20 + offset * 28 : cy - 22 - offset * 28;
    labels += `<g class="cc-label"><text class="cc-amount" x="${startX.toFixed(1)}" y="${ty.toFixed(1)}" text-anchor="${anchorEnd ? 'end' : 'start'}">${amount}<tspan class="${percent === null ? '' : percent >= 0 ? 'cc-up' : 'cc-down'}">${esc(pct)}</tspan></text>`
      + `<text class="cc-date" x="${startX.toFixed(1)}" y="${(ty + 13).toFixed(1)}" text-anchor="${anchorEnd ? 'end' : 'start'}">${i === 0 ? 'ab' : s.state.kind === 'addendum' ? 'Nachtrag ab' : 'ab'} ${dmy(s.state.validFrom)}</text></g>`;
    lastRight = anchorEnd ? lastRight : startX + w; lastOffset = offset;
    markers += `<circle class="cc-marker" cx="${cx.toFixed(1)}" cy="${cy.toFixed(1)}" r="5.5"/>`;
  });

  host.innerHTML = `<svg class="cc-chart" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-label="Brutto laut Vertrag: Stufen mit Betrag und Datum, dazu die Abrechnungen">`
    + `<defs><linearGradient id="cc-fill" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" class="cc-fill-top"/><stop offset="100%" class="cc-fill-bottom"/></linearGradient></defs>`
    + `${grid}${axis}<path class="cc-area" d="${area}"/><path class="cc-line" d="${line}"/>${dots}${markers}${labels}`
    + `<line class="cc-guide" x1="0" x2="0" y1="${top}" y2="${top + plotH}" hidden/><rect class="cc-hit" x="${left}" y="${top - 20}" width="${plotW}" height="${plotH + 20}"/></svg>`;

  bindTip(host, { steps, points, levelAt, x, t0, t1, left, plotW, top, plotH, width });
}

function bindTip(host, geo) {
  const svg = host.querySelector('svg'), hit = svg.querySelector('.cc-hit'), guide = svg.querySelector('.cc-guide');
  const wrap = host.closest('.cc-wrap'), tip = wrap.querySelector('.cc-tip');
  const show = event => {
    const rect = svg.getBoundingClientRect();
    const px = event.clientX - rect.left;
    const t = geo.t0 + (px - geo.left) / geo.plotW * (geo.t1 - geo.t0);
    // Das nächste Monatsende mit Abrechnung, sonst der Monat unter dem Zeiger.
    const near = geo.points.reduce((best, p) => (!best || Math.abs(p.t - t) < Math.abs(best.t - t) ? p : best), null);
    const point = near && Math.abs(near.t - t) < 20 * DAY ? near : null;
    const at = point ? point.t : t;
    const level = geo.levelAt(at);
    const date = new Date(at);
    const rows = [];
    rows.push(`<strong>${MONTH_LONG[date.getMonth()]} ${date.getFullYear()}</strong>`);
    rows.push(`<div class="cc-tip-row"><span><i class="cc-key-line"></i>Vertrag</span><span>${level ? euro2.format(level.gross) : '—'}</span></div>`);
    if (point) {
      const diff = level ? point.gross - level.gross : null;
      rows.push(`<div class="cc-tip-row"><span><i class="cc-key-dot ${diff !== null && Math.abs(diff) > 1 ? 'is-off' : 'is-ok'}"></i>Abrechnung</span><span>${euro2.format(point.gross)}</span></div>`);
      if (diff !== null && Math.abs(diff) > 1) rows.push(`<div class="cc-tip-row cc-tip-foot"><span>Abweichung</span><span>${diff > 0 ? '+' : '−'}${euro2.format(Math.abs(diff))}</span></div>`);
    } else rows.push('<div class="cc-tip-row cc-tip-foot"><span>Abrechnung</span><span>—</span></div>');
    tip.innerHTML = rows.join('');
    tip.hidden = false;
    guide.removeAttribute('hidden');
    guide.setAttribute('x1', px.toFixed(1)); guide.setAttribute('x2', px.toFixed(1));
    const w = tip.offsetWidth, room = wrap.clientWidth;
    let left = px + 14;
    if (left + w > room - 4) left = px - w - 14;
    tip.style.left = `${Math.max(4, Math.min(left, room - w - 4))}px`;
    tip.style.top = `${geo.top}px`;
  };
  const hide = () => { tip.hidden = true; guide.setAttribute('hidden', ''); };
  hit.addEventListener('pointermove', show);
  hit.addEventListener('pointerdown', show);
  hit.addEventListener('pointerleave', hide);
}

