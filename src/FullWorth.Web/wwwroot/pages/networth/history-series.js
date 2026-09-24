// Die Reihen des Vermögensverlaufs (#178).
//
// Die Kurve oben zeigt eine Zahl: das Nettovermögen. Gefordert waren acht Reihen — Konten,
// Investments, Immobilien, Edelmetalle, Altersvorsorge, sonstige Sachwerte, Schulden und
// Immobilien-Eigenkapital —, und vier davon gab es im Tageswert bis #178 gar nicht: sie steckten
// gemeinsam in einer Summe.
//
// Warum eine eigene Karte und nicht eine Auswahl in der Hero-Kurve: die trägt Vorschau, Scrubber und
// den Buchungsstreifen, und all das gilt für das Nettovermögen und für nichts sonst. Eine zweite
// Reihe dort hätte entweder die Vorschau mitgeschleppt (die es für Immobilien nicht gibt) oder sie
// abgeschaltet — beides ändert die Karte, um die es gar nicht geht.
//
// Warum die Legende schaltbar ist und nicht nur beschriftet: eine Immobilie steht bei 300 000 und
// das Edelmetall bei 4 700. Auf einer gemeinsamen Achse ist die kleinere Reihe ein Strich auf der
// Grundlinie — sichtbar, aber nicht lesbar. Abschalten lässt die Achse neu rechnen, und dann ist der
// Verlauf der kleinen Reihe wirklich zu sehen. Eine logarithmische Achse wäre die andere Antwort und
// die schlechtere: sie macht aus einer Verdopplung und einer Verzehnfachung optisch dasselbe.
//
// Und die Regel, die hier trägt: `null` heißt NICHT null Euro, sondern „an diesem Tag nicht
// festgehalten". Tage vor #178 kennen die Aufteilung nicht. Eine Linie bricht dort ab und beginnt
// danach neu, statt auf die Grundlinie zu fallen — ein Absturz auf 0 wäre eine Behauptung über
// Vermögen, das niemand gemessen hat.

const W = 720;
const H = 200;

const bilingual = (german, english) =>
  (document.documentElement.lang || '').startsWith('en') ? english : german;

/** Reihe, Feld in der Antwort und Farbtoken. Die Reihenfolge ist die der Legende. */
const SERIES = [
  { key: 'accounts', cat: 1, label: () => bilingual('Konten', 'Accounts') },
  { key: 'investments', cat: 2, label: () => bilingual('Investments', 'Investments') },
  { key: 'realEstateAssets', cat: 3, label: () => bilingual('Immobilien', 'Real estate') },
  { key: 'preciousMetalAssets', cat: 4, label: () => bilingual('Edelmetalle', 'Precious metals') },
  { key: 'pensionAssets', cat: 5, label: () => bilingual('Altersvorsorge', 'Pension') },
  { key: 'otherAssets', cat: 6, label: () => bilingual('Sonstige Sachwerte', 'Other assets') },
  { key: 'realEstateEquity', cat: 7, label: () => bilingual('Immobilien-Eigenkapital', 'Real-estate equity') },
  // Schulden sind eine Last, keine Position: gezeigt wird die Summe aus Darlehen und sonstigen
  // Verbindlichkeiten, positiv, denn "minus 200 000" neben "plus 300 000" liest sich als Vermögen.
  {
    key: 'debt', cat: 8, label: () => bilingual('Schulden', 'Debt'),
    derive: point => sum(point.loans, point.otherLiabilities)
  }
];

function sum(a, b) {
  if (a == null && b == null) return null;
  return (Number(a) || 0) + (Number(b) || 0);
}

function valueOf(series, point) {
  const raw = series.derive ? series.derive(point) : point[series.key];
  return raw == null || !Number.isFinite(Number(raw)) ? null : Number(raw);
}

/** Ob überhaupt ein Tag die Aufteilung trägt. Ohne das bleibt die Karte weg. */
export function hasSeriesHistory(history) {
  return (history || []).some(point =>
    point.realEstateAssets != null || point.preciousMetalAssets != null ||
    point.pensionAssets != null || point.otherAssets != null);
}

/** Die Reihen, für die es überhaupt einen Wert gibt - der Rest steht gar nicht erst in der Legende. */
function available(history) {
  const points = (history || []).filter(point => point?.date);
  return SERIES
    .map(series => ({ series, values: points.map(point => valueOf(series, point)) }))
    .filter(line => line.values.some(value => value != null));
}

/** Das Gerüst der Karte. Gezeichnet wird erst beim Binden, weil die Auswahl dort lebt. */
export function seriesChartMarkup(ctx, history, currency) {
  const lines = available(history);
  if (lines.length === 0 || (history || []).filter(point => point?.date).length < 2) return '';

  const legend = lines.map(line =>
    `<button type="button" class="nw-series-legend-item" data-series="${ctx.esc(line.series.key)}" aria-pressed="true">`
    + `<span class="cat-dot" data-cat="${line.series.cat}"></span>`
    + `<span>${ctx.esc(line.series.label())}</span>`
    + `<strong data-series-value></strong></button>`).join('');

  return `<div class="nw-series-chart"></div>`
    + `<div class="nw-series-legend">${legend}</div>`
    + `<p class="row-sub">${ctx.esc(bilingual(
      'Eine Lücke heißt: für diesen Tag wurde die Aufteilung nicht festgehalten - nicht null. '
        + 'Reihen lassen sich abschalten; die Achse rechnet dann neu.',
      'A gap means the split was not recorded for that day - not zero. '
        + 'Series can be switched off; the axis then rescales.'))}</p>`;
}

/**
 * Zeichnet und verdrahtet die Karte.
 *
 * @param {object} ctx      Seitenkontext (esc, money).
 * @param {Element} card    Die Karte aus {@link seriesChartMarkup}.
 * @param {Array} history   Die Antwort von api/wealth/history.
 * @param {string} currency Die Anzeigewährung.
 */
export function bindSeriesChart(ctx, card, history, currency) {
  if (!card) return;
  const host = card.querySelector('.nw-series-chart');
  const buttons = [...card.querySelectorAll('[data-series]')];
  if (!host || !buttons.length) return;

  const lines = available(history);
  const points = (history || []).filter(point => point?.date);
  const selected = new Set(lines.map(line => line.series.key));

  const draw = () => {
    const shown = lines.filter(line => selected.has(line.series.key));
    const all = shown.flatMap(line => line.values).filter(value => value != null);
    if (!all.length) { host.replaceChildren(); return; }

    // Die Achse folgt der Auswahl - das ist der ganze Zweck des Abschaltens. Die Null bleibt
    // trotzdem im Bild: eine Reihe, die zwischen 4 600 und 4 700 pendelt, sähe sonst nach einer
    // Verdopplung aus.
    const min = Math.min(0, ...all);
    const max = Math.max(...all);
    const span = max - min || 1;
    const x = index => points.length === 1 ? 0 : (index / (points.length - 1)) * W;
    const y = value => H - ((value - min) / span) * H;

    const pathOf = values => {
      const parts = [];
      let open = false;
      values.forEach((value, index) => {
        if (value == null) { open = false; return; }
        parts.push(`${open ? 'L' : 'M'}${x(index).toFixed(1)},${y(value).toFixed(1)}`);
        open = true;
      });
      return parts.join(' ');
    };

    host.innerHTML = `<svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" role="img" `
      + `aria-label="${ctx.esc(bilingual('Reihen des Vermögensverlaufs', 'Net-worth series over time'))}">`
      + shown.map(line =>
        `<path class="nw-series-line" data-cat="${line.series.cat}" d="${pathOf(line.values)}" `
        + `fill="none" stroke-width="2" vector-effect="non-scaling-stroke"></path>`).join('')
      + `</svg>`;
  };

  for (const line of lines) {
    const button = card.querySelector(`[data-series="${CSS.escape(line.series.key)}"]`);
    const last = [...line.values].reverse().find(value => value != null);
    const value = button?.querySelector('[data-series-value]');
    if (value) value.textContent = last == null ? '—' : ctx.money(last, currency);
    button?.addEventListener('click', () => {
      // Die letzte Reihe bleibt an: ein leeres Diagramm ist kein Zustand, den jemand wählt.
      if (selected.has(line.series.key) && selected.size === 1) return;
      if (selected.has(line.series.key)) selected.delete(line.series.key);
      else selected.add(line.series.key);
      button.setAttribute('aria-pressed', selected.has(line.series.key) ? 'true' : 'false');
      draw();
    });
  }

  draw();
}
