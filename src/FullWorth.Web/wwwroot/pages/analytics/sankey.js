// Das Flussdiagramm der Auswertung (#177).
//
// Der Endpunkt war fertig gebaut und hatte keinen Aufrufer — und schlimmer: sein Rumpf lief lange
// gar nicht, weil eine Middleware die Route vor der Zuordnung abfing. Die Middleware ist weg, die
// Antwort kommt jetzt aus dem Handler, und hier ist endlich jemand, der sie zeigt.
//
// Gezeichnet wird von Hand als SVG. Ein Sankey im allgemeinen Fall ist eine Layoutaufgabe, dieser
// hier ist keine: der Server liefert genau drei Spalten — Einnahmen, „verfügbar", und was davon
// wohin gegangen ist. Eine Bibliothek dafür wäre ein Framework für ein Rechteck und eine Kurve.
//
// Zwei Dinge aus der Antwort sind keine Zierde:
//
//   incomplete  Es fehlte ein Wechselkurs. Dann ist die Summe unvollständig, und das muss dastehen —
//               ein stillschweigendes 1:1 wäre eine erfundene Zahl (Geldregel).
//   reconciles  Der Server rechnet selbst nach, ob Zufluss und Abfluss aufgehen. Tun sie es nicht,
//               zeigt das Bild etwas anderes als die Wahrheit, und das gehört gesagt statt gemalt.

const de = () => (localStorage.getItem('finance.language')
  || (navigator.language || 'de')).startsWith('de');
const t = (german, english) => (de() ? german : english);

const WIDTH = 720;
const NODE_WIDTH = 14;
const GAP = 10;
const MIN_HEIGHT = 2;

/**
 * Zeichnet das Flussdiagramm in das gegebene Element.
 *
 * @param {object} ctx     Der Seitenkontext (für money/esc).
 * @param {Element} host   Wohin.
 * @param {object} data    Die Antwort von /api/analytics/sankey.
 */
export function renderSankey(ctx, host, data) {
  if (!host) return;
  const nodes = Array.isArray(data?.nodes) ? data.nodes : [];
  const links = Array.isArray(data?.links) ? data.links : [];

  if (!links.length) {
    host.innerHTML = `<p class="row-sub">${ctx.esc(t('Für diesen Zeitraum gibt es keine Flüsse.', 'No flows in this period.'))}</p>`;
    return;
  }

  const currency = data.currency || 'EUR';
  const columns = layout(nodes, links);
  const height = Math.max(...columns.map(column => column.height), 1);
  const scale = height > 0 ? 320 / height : 1;

  const ribbons = links.map(link => ribbon(columns, link, scale)).filter(Boolean).join('');
  const boxes = columns.flatMap(column => column.items.map(item => box(ctx, item, scale, currency))).join('');

  const notes = [];
  if (data.incomplete) {
    notes.push(note(ctx, 'warn', t(
      'Unvollständig: für mindestens einen Betrag fehlt ein Wechselkurs. Er fehlt in der Summe.',
      'Incomplete: at least one amount has no exchange rate. It is missing from the total.')));
  }
  if (data.reconciles === false) {
    notes.push(note(ctx, 'warn', t(
      'Zu- und Abfluss gehen nicht auf. Das Bild zeigt die Teile, nicht die Summe.',
      'Inflow and outflow do not add up. The picture shows the parts, not the total.')));
  }

  const viewHeight = Math.round(320 + 40);
  host.innerHTML = `<svg class="an-sankey" viewBox="0 0 ${WIDTH} ${viewHeight}" role="img" `
    + `aria-label="${ctx.esc(t('Flussdiagramm der Einnahmen und Ausgaben', 'Flow of income and spending'))}">`
    + `<g class="an-sankey-links">${ribbons}</g><g class="an-sankey-nodes">${boxes}</g></svg>`
    + notes.join('');
}

/** Drei Spalten: woher, „verfügbar", wohin. Die Höhe eines Knotens ist sein Durchfluss. */
function layout(nodes, links) {
  const columnOf = id => (id === 'available' ? 1 : id === 'income' || id === 'deficit' ? 0 : 2);
  const flow = new Map();
  for (const link of links) {
    flow.set(link.source, (flow.get(link.source) || 0) + Number(link.value || 0));
    flow.set(link.target, (flow.get(link.target) || 0) + Number(link.value || 0));
  }
  // "available" steht auf beiden Seiten jeder Kante und wuerde sich sonst selbst verdoppeln.
  if (flow.has('available')) flow.set('available', flow.get('available') / 2);

  const columns = [0, 1, 2].map(index => ({ index, items: [], height: 0 }));
  for (const node of nodes) {
    const column = columns[columnOf(node.id)];
    const value = Math.max(0, Number(flow.get(node.id) || 0));
    column.items.push({ ...node, value, column: column.index });
  }
  for (const column of columns) {
    let offset = 0;
    for (const item of column.items) {
      item.offset = offset;
      offset += item.value + GAP;
    }
    column.height = Math.max(0, offset - GAP);
  }
  return columns;
}

const xOf = column => (column === 0 ? 0 : column === 1 ? (WIDTH - NODE_WIDTH) / 2 : WIDTH - NODE_WIDTH);

function find(columns, id) {
  for (const column of columns) {
    const item = column.items.find(entry => entry.id === id);
    if (item) return item;
  }
  return null;
}

/**
 * Ein Band von Kante zu Kante. Die Anteile werden je Knoten fortlaufend gestapelt, damit zwei Bänder
 * aus demselben Knoten nicht übereinander liegen.
 */
function ribbon(columns, link, scale) {
  const from = find(columns, link.source);
  const to = find(columns, link.target);
  if (!from || !to) return '';

  const value = Math.max(0, Number(link.value || 0));
  const thickness = Math.max(MIN_HEIGHT, value * scale);

  from.out = (from.out || 0);
  to.in = (to.in || 0);
  const y1 = from.offset * scale + from.out;
  const y2 = to.offset * scale + to.in;
  from.out += thickness;
  to.in += thickness;

  const x1 = xOf(from.column) + NODE_WIDTH;
  const x2 = xOf(to.column);
  const mid = (x1 + x2) / 2;
  const path = `M${x1} ${y1} C${mid} ${y1} ${mid} ${y2} ${x2} ${y2} `
    + `L${x2} ${y2 + thickness} C${mid} ${y2 + thickness} ${mid} ${y1 + thickness} ${x1} ${y1 + thickness} Z`;
  return `<path d="${path}" class="an-sankey-link an-sankey-link-${kind(link.target)}"></path>`;
}

/** Fehlbetrag und Rest bekommen eine eigene Rolle - die anderen Bänder teilen sich eine. */
function kind(target) {
  if (target === 'remaining') return 'remaining';
  if (target === 'available') return 'in';
  return 'out';
}

function box(ctx, item, scale, currency) {
  const height = Math.max(MIN_HEIGHT, item.value * scale);
  const x = xOf(item.column);
  const y = item.offset * scale;
  const anchor = item.column === 2 ? 'end' : 'start';
  const labelX = item.column === 2 ? x - 6 : x + NODE_WIDTH + 6;
  const label = `${item.name} · ${ctx.money(item.value, currency)}`;
  return `<rect x="${x}" y="${y}" width="${NODE_WIDTH}" height="${height}" rx="2" `
    + `class="an-sankey-node an-sankey-node-${ctx.esc(item.id.replace(/[^a-z0-9-]/gi, ''))}"></rect>`
    + `<text x="${labelX}" y="${y + height / 2}" text-anchor="${anchor}" dominant-baseline="middle" `
    + `class="an-sankey-label">${ctx.esc(label)}</text>`;
}

function note(ctx, tone, text) {
  return `<p class="row-sub an-sankey-note an-sankey-note-${tone}">${ctx.esc(text)}</p>`;
}
