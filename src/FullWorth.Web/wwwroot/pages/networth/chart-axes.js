// Die Achsen der Vermoegenskurven: Zeitmarken unten, Eurowerte an den Hilfslinien, Monatsendstaende.
// Die Hero-Kurve und die Zusammensetzung darunter zeichnen beide damit - dieselbe Spanne bekommt so
// dieselben Marken, und eine Aenderung an der Beschriftung gilt fuer beide.

export const MS_PER_MONTH = 1000 * 60 * 60 * 24 * 30.4375;

// Ab zwei Jahren zeichnet die Kurve den letzten Stand jedes Monats: Tageswerte waeren ein Saegezahn aus
// Gehalt und Ausgaben, keine Entwicklung.
export const LONG_WINDOW_MS = 2 * 366 * 24 * 60 * 60 * 1000;

export function monthEnds(points) {
  const byMonth = new Map();
  for (const point of points) byMonth.set(String(point.date).slice(0, 7), point);
  return [...byMonth.values()];
}

// Der Abstand der Zeitmarken folgt der Spanne, damit es nie mehr als etwa sechs sind: Monate bis zu einem
// Jahr, dann Quartale, Halbjahre, Jahre. Sie stehen immer am Monatsersten, das bei der Schrittweite aufgeht.
function dateTicks(domain) {
  const months = (domain.end - domain.start) / MS_PER_MONTH;
  const step = months <= 4 ? 1 : months <= 14 ? 3 : months <= 30 ? 6 : months <= 72 ? 12 : 24;
  const first = new Date(domain.start);
  const ticks = [];
  for (let month = Math.ceil(first.getUTCMonth() / step) * step; ; month += step) {
    const time = Date.UTC(first.getUTCFullYear(), month, 1);
    if (time > domain.end) break;
    if (time >= domain.start) ticks.push(time);
  }
  return ticks;
}

/**
 * Die Zeitmarken einer Spanne.
 *
 * @param {{start: number, end: number}} domain Spanne in Millisekunden.
 * @param {(iso: string) => number} leftOf      Wo ein Tag steht, in Prozent der Breite.
 * @param {boolean} german                      Beschriftung auf Deutsch.
 */
export function timeTicks(domain, leftOf, german) {
  const yearly = (domain.end - domain.start) / MS_PER_MONTH > 30;
  const format = new Intl.DateTimeFormat(german ? 'de-DE' : 'en-GB', yearly
    ? { year: 'numeric', timeZone: 'UTC' }
    : { month: 'short', year: '2-digit', timeZone: 'UTC' });
  return dateTicks(domain).map(time => ({
    left: leftOf(new Date(time).toISOString().slice(0, 10)),
    label: format.format(new Date(time))
  }));
}

/** Zwei Zeilen: Eurowerte links ueber dem Diagramm, Zeitmarken darunter. */
export function axesMarkup({ yTicks, xTicks, esc, german, currency, showValues }) {
  const money = new Intl.NumberFormat(german ? 'de-DE' : 'en-GB', {
    style: 'currency', currency, notation: 'compact', maximumSignificantDigits: 3
  });
  const y = showValues ? yTicks.map(tick =>
    `<span style="top:${tick.top.toFixed(1)}%">${esc(money.format(tick.value))}</span>`).join('') : '';
  const x = xTicks.map(tick => {
    const edge = tick.left < 6 ? ' class="start"' : tick.left > 94 ? ' class="end"' : '';
    return `<span${edge} style="left:${tick.left.toFixed(1)}%">${esc(tick.label)}</span>`;
  }).join('');
  return `<div class="nw-chart-y" aria-hidden="true">${y}</div><div class="nw-chart-x" aria-hidden="true">${x}</div>`;
}
