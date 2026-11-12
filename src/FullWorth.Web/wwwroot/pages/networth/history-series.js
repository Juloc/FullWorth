// Woraus das Vermögen besteht, im Zeitverlauf (#178, #222).
//
// Die Kurve oben zeigt eine Zahl: das Nettovermögen. Diese Karte zeigt es in Teilen, als gestapelte
// Fläche mit dem letzten Stand jedes Monats: Konten, Investments, Immobilien, Edelmetalle,
// Altersvorsorge und sonstige Sachwerte. Die Höhe ist die Summe dieser Werte.
//
// Schulden und Immobilien-Eigenkapital stehen nicht im Stapel. Schulden sind eine Last, keine Schicht:
// "minus 200 000" über "plus 300 000" läse sich als Vermögen, und sie stehen oben bei den Kennzahlen.
// Das Eigenkapital ist Immobilie minus Darlehen, also dieselbe Immobilie ein zweites Mal.
//
// Warum eine eigene Karte und nicht eine Auswahl in der Hero-Kurve: die trägt den Buchungsstreifen und
// den Scrubber, und beides gilt für das Nettovermögen und für nichts sonst.
//
// Warum die Legende schaltbar ist: eine Immobilie steht bei 300 000 und das Edelmetall bei 4 700. Auf
// einer gemeinsamen Achse ist die kleinere Schicht ein Streifen an der Kante - sichtbar, aber nicht
// lesbar. Abschalten lässt die Achse neu rechnen.
//
// Und die Regel, die hier trägt: `null` heißt NICHT null Euro, sondern „in diesem Monat nicht
// festgehalten". Monate vor #178 kennen die Aufteilung nicht. Eine Schicht bricht dort ab und beginnt
// danach neu, statt auf die Grundlinie zu fallen - ein Absturz auf 0 wäre eine Behauptung über
// Vermögen, das niemand gemessen hat.

import { axesMarkup, monthEnds, timeTicks } from './chart-axes.js';

const W = 720;
const H = 200;

const german = () => !(document.documentElement.lang || '').toLowerCase().startsWith('en');
const bilingual = (de, en) => (german() ? de : en);

/** Schicht, Feld in der Antwort und Farbtoken. Die Reihenfolge ist die der Legende, unten nach oben. */
const SERIES = [
  { key: 'accounts', cat: 1, label: () => bilingual('Konten', 'Accounts') },
  { key: 'investments', cat: 2, label: () => bilingual('Investments', 'Investments') },
  { key: 'realEstateAssets', cat: 3, label: () => bilingual('Immobilien', 'Real estate') },
  { key: 'preciousMetalAssets', cat: 4, label: () => bilingual('Edelmetalle', 'Precious metals') },
  { key: 'pensionAssets', cat: 5, label: () => bilingual('Altersvorsorge', 'Pension') },
  { key: 'otherAssets', cat: 6, label: () => bilingual('Sonstige Sachwerte', 'Other assets') }
];

function valueOf(series, point) {
  const raw = point[series.key];
  return raw == null || !Number.isFinite(Number(raw)) ? null : Number(raw);
}

/** Ob überhaupt ein Tag die Aufteilung trägt. Ohne das bleibt die Karte weg. */
export function hasSeriesHistory(history) {
  return (history || []).some(point =>
    point.realEstateAssets != null || point.preciousMetalAssets != null ||
    point.pensionAssets != null || point.otherAssets != null);
}

/** Der letzte Stand jedes Monats, in dem der Tag überhaupt Daten hat. */
function monthly(history) {
  return monthEnds((history || []).filter(point => point?.date));
}

/** Die Schichten, für die es überhaupt einen Wert gibt - der Rest steht gar nicht erst in der Legende. */
function available(months) {
  return SERIES
    .map(series => ({ series, values: months.map(point => valueOf(series, point)) }))
    .filter(line => line.values.some(value => value != null));
}

/** Das Gerüst der Karte. Gezeichnet wird erst beim Binden, weil die Auswahl dort lebt. */
export function seriesChartMarkup(ctx, history, currency) {
  const months = monthly(history);
  const lines = available(months);
  if (lines.length === 0 || months.length < 2) return '';

  const legend = lines.map(line =>
    `<button type="button" class="nw-series-legend-item" data-series="${ctx.esc(line.series.key)}" aria-pressed="true">`
    + `<span class="cat-dot" data-cat="${line.series.cat}"></span>`
    + `<span>${ctx.esc(line.series.label())}</span>`
    + `<strong data-series-value></strong></button>`).join('');

  return `<div class="nw-series-chart"></div>`
    + `<div class="nw-series-legend">${legend}</div>`
    + `<p class="row-sub">${ctx.esc(bilingual(
      'Die Höhe ist die Summe der Vermögenswerte ohne Schulden. Fehlt eine Schicht in einem Monat, '
        + 'wurde sie dort nicht festgehalten - sie ist nicht null. Schichten lassen sich abschalten; '
        + 'die Achse rechnet dann neu.',
      'The height is the sum of the assets, without debt. If a layer is missing in a month, it was '
        + 'not recorded then - it is not zero. Layers can be switched off; the axis then rescales.'))}</p>`;
}

/**
 * Zeichnet und verdrahtet die Karte.
 *
 * @param {object} ctx      Seitenkontext (esc, money, isPrivate).
 * @param {Element} card    Die Karte aus {@link seriesChartMarkup}.
 * @param {Array} history   Die Antwort von api/wealth/history.
 * @param {string} currency Die Anzeigewährung.
 */
export function bindSeriesChart(ctx, card, history, currency) {
  if (!card) return;
  const host = card.querySelector('.nw-series-chart');
  const buttons = [...card.querySelectorAll('[data-series]')];
  if (!host || !buttons.length) return;

  const months = monthly(history);
  const lines = available(months);
  const selected = new Set(lines.map(line => line.series.key));
  const start = Date.parse(`${months[0].date.slice(0, 10)}T00:00:00Z`);
  const end = Date.parse(`${months.at(-1).date.slice(0, 10)}T00:00:00Z`);
  const x = index => (index / (months.length - 1)) * W;

  const draw = () => {
    const shown = lines.filter(line => selected.has(line.series.key));
    // Die Höhe des Stapels je Monat. Eine Schicht ohne Wert zählt dort nichts - und wird dort auch
    // nicht gezeichnet.
    const totals = months.map((_, index) =>
      shown.reduce((sum, line) => sum + (line.values[index] ?? 0), 0));
    const max = Math.max(...totals);
    if (!(max > 0)) { host.replaceChildren(); return; }
    // Die Null ist die Grundlinie: eine Fläche, die bei 4 600 beginnt, sähe sonst nach einer Verdopplung aus.
    const y = value => H - (value / max) * H;

    let base = months.map(() => 0);
    const areas = shown.map(line => {
      const lower = base;
      const tops = lower.map((below, index) => line.values[index] == null ? null : below + line.values[index]);
      base = tops.map((top, index) => top ?? lower[index]);
      return `<path class="nw-series-area" data-cat="${line.series.cat}" d="${areaPath(lower, tops, x, y)}"></path>`;
    });

    const xTicks = timeTicks({ start, end }, iso => ((Date.parse(`${iso}T00:00:00Z`) - start) / (end - start)) * 100, german());
    const yTicks = [0.25, 0.5, 0.75].map(fraction => ({ top: (1 - fraction) * 100, value: max * fraction }));
    const label = ctx.esc(bilingual('Zusammensetzung des Vermögens', 'Composition of wealth'));
    const axes = axesMarkup({ yTicks, xTicks, esc: ctx.esc, german: german(), currency, showValues: !ctx.isPrivate() });
    host.innerHTML = `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" role="img" aria-label="${label}">${areas.join('')}</svg>${axes}`;
  };

  for (const line of lines) {
    const button = card.querySelector(`[data-series="${CSS.escape(line.series.key)}"]`);
    const last = [...line.values].reverse().find(value => value != null);
    const value = button?.querySelector('[data-series-value]');
    if (value) value.textContent = last == null ? '—' : ctx.money(last, currency);
    button?.addEventListener('click', () => {
      // Die letzte Schicht bleibt an: ein leeres Diagramm ist kein Zustand, den jemand wählt.
      if (selected.has(line.series.key) && selected.size === 1) return;
      if (selected.has(line.series.key)) selected.delete(line.series.key);
      else selected.add(line.series.key);
      button.setAttribute('aria-pressed', selected.has(line.series.key) ? 'true' : 'false');
      draw();
    });
  }

  draw();
}

/**
 * Die Fläche einer Schicht zwischen ihrer Unterkante und ihrer Oberkante. Ein Monat ohne Wert (`null`
 * in `tops`) hebt den Stift: davor und danach entstehen zwei Flächen, keine Brücke darüber.
 */
function areaPath(lower, tops, x, y) {
  const parts = [];
  let run = [];
  const close = () => {
    if (run.length > 1) {
      const up = run.map(index => `${x(index).toFixed(1)},${y(tops[index]).toFixed(1)}`);
      const down = [...run].reverse().map(index => `${x(index).toFixed(1)},${y(lower[index]).toFixed(1)}`);
      parts.push(`M${up.join(' L')} L${down.join(' L')} Z`);
    }
    run = [];
  };
  tops.forEach((top, index) => { if (top == null) close(); else run.push(index); });
  close();
  return parts.join(' ');
}
