// Bilderkennung fuer die Belegkamera - ohne Bibliothek, ohne nachgeladenen Code.
//
// Alles hier arbeitet auf Pixelfeldern (Uint8Array / RGBA), nicht auf dem DOM: so laesst es sich an
// kuenstlichen Fotos messen (ops/receipt-vision-bench), und die Kamera ruft es nur mit einem kleinen
// Vorschaubild auf. Nur das Geraderechnen laeuft auf dem vollen Foto.
//
// Den Bon im Bild zu finden geht in drei Schritten:
//   1. Kandidaten: Vierecke, die ein Blatt sein koennten. Sie kommen aus drei Quellen, weil jede
//      dort versagt, wo eine andere traegt:
//        helligkeit  das groesste helle Gebiet            (scheitert bei weissem Tisch, Schatten)
//        textur      das Gebiet voller Schrift             (scheitert auf gemustertem Tisch)
//        kanten      vier lange gerade Kanten, die ein Blatt umschliessen (scheitert bei kaum Kontrast)
//   2. Bewertung: jedes Viereck bekommt Zahlen - wie sehr seine Seiten echte Kanten sind, wie sehr sein
//      Inneres sich von der Umgebung unterscheidet, wie viel Schrift darin steht - und ein lineares
//      Modell macht eine Wertung daraus. Die Gewichte stehen in MODEL und kommen aus
//      ops/receipt-vision-bench (learn.mjs), nicht aus dem Bauch.
//   3. Nachschaerfen: jede Seite des Siegers rutscht auf die staerkste Kante in ihrer Naehe.
//
// Annahme, die alles hier trifft: ein Bon ist ein Rechteck aus hellem Papier mit Schrift. Wo das nicht
// stimmt oder nichts Beleghaftes da ist, kommt null - dann bleibt der Ausloeser von Hand, und nichts
// wird geraten.

export const SOURCES = ['helligkeit', 'textur', 'kanten'];

/** RGBA -> Graubild (Luma), verkleinert auf hoechstens `targetWidth` Pixel Breite (Mittelwert je Block). */
export function toGray(rgba, width, height, targetWidth = width) {
  const step = Math.max(1, Math.floor(width / targetWidth));
  const w = Math.floor(width / step);
  const h = Math.floor(height / step);
  const gray = new Uint8Array(w * h);
  const area = step * step;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      let sum = 0;
      for (let dy = 0; dy < step; dy++) {
        let i = ((y * step + dy) * width + x * step) * 4;
        for (let dx = 0; dx < step; dx++, i += 4) sum += rgba[i] * 0.299 + rgba[i + 1] * 0.587 + rgba[i + 2] * 0.114;
      }
      gray[y * w + x] = sum / area;
    }
  }
  return { gray, width: w, height: h };
}

/**
 * Schaerfe als Varianz des Laplace-Filters. Unscharf bedeutet: wenige harte Kanten, kleine Varianz.
 * Der Wert ist nur zwischen Bildern derselben Szene vergleichbar - ein Beleg voller Text hat immer mehr
 * davon als ein leerer. Deshalb gibt es keinen festen Grenzwert, sondern einen Vergleich mit den
 * Bildern davor (siehe Kamera).
 */
export function sharpness(gray, width, height, rect = { x0: 0, y0: 0, x1: width, y1: height }) {
  let sum = 0, sumSq = 0, n = 0;
  for (let y = Math.max(1, rect.y0); y < Math.min(height - 1, rect.y1); y++) {
    for (let x = Math.max(1, rect.x0); x < Math.min(width - 1, rect.x1); x++) {
      const i = y * width + x;
      const v = 4 * gray[i] - gray[i - 1] - gray[i + 1] - gray[i - width] - gray[i + width];
      sum += v;
      sumSq += v * v;
      n++;
    }
  }
  if (!n) return 0;
  const mean = sum / n;
  return sumSq / n - mean * mean;
}

/** Mittlere Abweichung zweier gleich grosser Graubilder (0-255): wie sehr sich das Bild bewegt hat. */
export function motion(a, b) {
  if (!a || !b || a.length !== b.length) return 255;
  let sum = 0;
  for (let i = 0; i < a.length; i++) sum += Math.abs(a[i] - b[i]);
  return sum / a.length;
}

// ------------------------------------------------------------------ Erkennung

/**
 * Die Gewichte der Bewertung und die Mindestwertung, angelernt mit ops/receipt-vision-bench/learn.mjs an
 * kuenstlichen Fotos (Seeds 101-122) und geprueft an anderen (1001-1012). Lieber keine Antwort als eine
 * falsche: die Mindestwertung ist dort gesetzt, wo auf den Pruefbildern ohne Bon nichts mehr erkannt wird.
 */
export const MODEL = {
  threshold: 0.1,
  bias: -20.064,
  weights: { edgeMean: 1.197, edgeMin: 5.56, contrast: 7.399, texture: 2.074, area: 3.891, borders: -0.299, known: 2.903 }
};

/**
 * Sucht den Beleg im Bild. Ergebnis: Ecken oben links, oben rechts, unten rechts, unten links in 0..1,
 * die Bewertung, und ob der Beleg oben/unten ueber den Bildrand hinausgeht (bei langen Bons der
 * Normalfall) - oder null, wenn nichts Beleghaftes da ist. `sources` waehlt die Kandidatenquellen.
 */
export function findReceipt({ gray, width, height }, { sources = SOURCES, model = MODEL } = {}) {
  const found = bestCandidate(candidatePool(analyse(gray, width, height), sources), model);
  return found ? finish(found, width, height) : null;
}

/** Wie `findReceipt`, aber mit allen bewerteten Kandidaten - fuer das Anlernen der Gewichte. */
export function rateCandidates({ gray, width, height }, { sources = SOURCES } = {}) {
  const field = analyse(gray, width, height);
  return candidatePool(field, sources).map(candidate => rateRaw(field, candidate)).filter(Boolean);
}

function candidatePool(field, sources) {
  const pool = [];
  if (sources.includes('helligkeit')) pool.push(...brightCandidates(field));
  if (sources.includes('textur')) pool.push(...textureCandidates(field));
  if (sources.includes('kanten')) pool.push(...edgeCandidates(field));
  return pool.map(candidate => ({ ...candidate, field }));
}

function bestCandidate(pool, model) {
  let best = null;
  for (const candidate of pool) {
    const rated = rateRaw(candidate.field, candidate);
    if (!rated) continue;
    rated.score = scoreOf(rated.features, model);
    if (!best || rated.score > best.score) best = rated;
  }
  return best && best.score >= model.threshold ? best : null;
}

function finish(best, width, height) {
  const snapped = snap(best.field, best);
  const ordered = orderUpright(snapped);
  return {
    corners: ordered.map(p => ({ x: p.x / width, y: p.y / height })),
    coverage: best.area,
    score: best.score,
    source: best.source,
    // der Beleg geht weiter, wenn eine seiner Seiten auf dem Bildrand liegt
    continuesUp: best.border.top,
    continuesDown: best.border.bottom
  };
}

export function scoreOf(features, model = MODEL) {
  let z = model.bias;
  for (const key of Object.keys(model.weights)) z += model.weights[key] * features[key];
  return 1 / (1 + Math.exp(-z));
}

/**
 * Die Ecken des Stuecks, das beim Zusammensetzen eines langen Bons gerade gerechnet wird (Pixel, TL TR BR BL).
 *
 * Geht der Bon oben oder unten ueber den Bildrand hinaus, liegt dort keine Papierkante, sondern der
 * Bildrand - und der verlaeuft nicht rechtwinklig zu den Seiten des Bons, sobald das Handy leicht
 * gedreht ist. Wuerde man ihn zur oberen Kante machen, wuerde jedes Stueck anders verschert. Stattdessen
 * wird dort eine Linie rechtwinklig zu den Seiten gelegt, durch die weiter innen liegende Ecke
 * (es geht ein kleines Dreieck Bild verloren, die Ueberlappung deckt es ab). Eine echte Papierkante
 * bleibt, wie sie ist.
 */
export function stripCorners(quad, continuesUp, continuesDown) {
  const [tl, tr, br, bl] = quad;
  const unit = (a, b) => { const l = Math.hypot(b.x - a.x, b.y - a.y) || 1; return { x: (b.x - a.x) / l, y: (b.y - a.y) / l }; };
  const dl = unit(tl, bl), dr = unit(tr, br);
  const dm = Math.hypot(dl.x + dr.x, dl.y + dr.y) || 1;
  const d = { x: (dl.x + dr.x) / dm, y: (dl.y + dr.y) / dm };
  const along = p => p.x * d.x + p.y * d.y;
  const onLine = (a, b, t) => {
    // der Punkt der Geraden a-b, dessen Abstand entlang d gleich t ist
    const den = along({ x: b.x - a.x, y: b.y - a.y });
    return Math.abs(den) < 1e-9 ? a : { x: a.x + (b.x - a.x) * (t - along(a)) / den, y: a.y + (b.y - a.y) * (t - along(a)) / den };
  };
  const top = continuesUp ? Math.max(along(tl), along(tr)) : null;
  const bottom = continuesDown ? Math.min(along(bl), along(br)) : null;
  if (top !== null && bottom !== null && top >= bottom) return quad;
  return [
    top === null ? tl : onLine(tl, bl, top),
    top === null ? tr : onLine(tr, br, top),
    bottom === null ? br : onLine(tr, br, bottom),
    bottom === null ? bl : onLine(tl, bl, bottom)
  ];
}

/** Groesse des geraden Ergebnisbildes aus den Ecken in Pixeln; die lange Seite hoechstens `maxLong`. */
export function outputSize(corners, maxLong = 3200) {
  const [tl, tr, br, bl] = corners;
  let w = Math.max(dist(tl, tr), dist(bl, br));
  let h = Math.max(dist(tl, bl), dist(tr, br));
  const scale = Math.min(1, maxLong / Math.max(w, h));
  w *= scale; h *= scale;
  return { width: Math.max(1, Math.round(w)), height: Math.max(1, Math.round(h)) };
}

/** Das Viereck `corners` (Pixel, TL TR BR BL) des Quellbildes als gerades Rechteck `outW` x `outH`. */
export function warp(src, srcW, srcH, corners, outW, outH) {
  const m = squareToQuad(corners);
  const out = new Uint8ClampedArray(outW * outH * 4);
  for (let y = 0; y < outH; y++) {
    const v = (y + 0.5) / outH;
    for (let x = 0; x < outW; x++) {
      const u = (x + 0.5) / outW;
      const d = m.g * u + m.h * v + 1;
      const sx = (m.a * u + m.b * v + m.c) / d - 0.5;
      const sy = (m.d * u + m.e * v + m.f) / d - 0.5;
      const x0 = Math.min(srcW - 1, Math.max(0, Math.floor(sx)));
      const y0 = Math.min(srcH - 1, Math.max(0, Math.floor(sy)));
      const x1 = Math.min(srcW - 1, x0 + 1);
      const y1 = Math.min(srcH - 1, y0 + 1);
      const fx = Math.min(1, Math.max(0, sx - x0));
      const fy = Math.min(1, Math.max(0, sy - y0));
      const o = (y * outW + x) * 4;
      for (let c = 0; c < 3; c++) {
        const top = src[(y0 * srcW + x0) * 4 + c] * (1 - fx) + src[(y0 * srcW + x1) * 4 + c] * fx;
        const bottom = src[(y1 * srcW + x0) * 4 + c] * (1 - fx) + src[(y1 * srcW + x1) * 4 + c] * fx;
        out[o + c] = top * (1 - fy) + bottom * fy;
      }
      out[o + 3] = 255;
    }
  }
  return out;
}

// ------------------------------------------------------------------ gemeinsame Messwerte

/** Was alle Quellen und die Bewertung aus dem Bild brauchen, einmal berechnet. */
function analyse(gray, width, height) {
  const smooth = blur(gray, width, height);
  const gx = new Float32Array(width * height), gy = new Float32Array(width * height), mag = new Float32Array(width * height);
  for (let y = 1; y < height - 1; y++) {
    for (let x = 1; x < width - 1; x++) {
      const i = y * width + x;
      gx[i] = (smooth[i - width + 1] + 2 * smooth[i + 1] + smooth[i + width + 1]) - (smooth[i - width - 1] + 2 * smooth[i - 1] + smooth[i + width - 1]);
      gy[i] = (smooth[i + width - 1] + 2 * smooth[i + width] + smooth[i + width + 1]) - (smooth[i - width - 1] + 2 * smooth[i - width] + smooth[i - width + 1]);
      mag[i] = Math.hypot(gx[i], gy[i]);
    }
  }
  // Schriftdichte: wie viel Kante pro Flaeche (Mittelwert ueber 7x7)
  const texture = boxMean(mag, width, height, 3);
  return { gray, smooth, gx, gy, mag, texture, width, height };
}

function blur(gray, width, height) {
  const out = new Uint8Array(gray.length);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      let sum = 0, n = 0;
      for (let dy = -1; dy <= 1; dy++) {
        const yy = y + dy;
        if (yy < 0 || yy >= height) continue;
        for (let dx = -1; dx <= 1; dx++) {
          const xx = x + dx;
          if (xx < 0 || xx >= width) continue;
          sum += gray[yy * width + xx];
          n++;
        }
      }
      out[y * width + x] = sum / n;
    }
  }
  return out;
}

function boxMean(src, width, height, r) {
  const tmp = new Float32Array(src.length), out = new Float32Array(src.length);
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      let sum = 0, n = 0;
      for (let dx = -r; dx <= r; dx++) { const xx = x + dx; if (xx >= 0 && xx < width) { sum += src[y * width + xx]; n++; } }
      tmp[y * width + x] = sum / n;
    }
  }
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      let sum = 0, n = 0;
      for (let dy = -r; dy <= r; dy++) { const yy = y + dy; if (yy >= 0 && yy < height) { sum += tmp[yy * width + x]; n++; } }
      out[y * width + x] = sum / n;
    }
  }
  return out;
}

function bilinear(img, width, height, x, y) {
  const x0 = Math.min(width - 2, Math.max(0, Math.floor(x))), y0 = Math.min(height - 2, Math.max(0, Math.floor(y)));
  const fx = Math.min(1, Math.max(0, x - x0)), fy = Math.min(1, Math.max(0, y - y0));
  const i = y0 * width + x0;
  return (img[i] * (1 - fx) + img[i + 1] * fx) * (1 - fy) + (img[i + width] * (1 - fx) + img[i + width + 1] * fx) * fy;
}

/** Schwelle nach Otsu: trennt Hell von Dunkel so, dass die Streuung innerhalb der Gruppen minimal ist. */
function otsu(values, levels = 256) {
  const hist = new Float64Array(levels);
  let max = 0;
  for (let i = 0; i < values.length; i++) if (values[i] > max) max = values[i];
  const scale = (levels - 1) / Math.max(1e-9, max);
  for (let i = 0; i < values.length; i++) hist[Math.round(values[i] * scale)]++;
  const total = values.length;
  let sumAll = 0;
  for (let i = 0; i < levels; i++) sumAll += i * hist[i];
  let sumB = 0, weightB = 0, best = 0, threshold = levels / 2;
  for (let t = 0; t < levels; t++) {
    weightB += hist[t];
    if (!weightB) continue;
    const weightF = total - weightB;
    if (!weightF) break;
    sumB += t * hist[t];
    const between = weightB * weightF * (sumB / weightB - (sumAll - sumB) / weightF) ** 2;
    if (between > best) { best = between; threshold = t; }
  }
  return threshold / scale;
}

// ------------------------------------------------------------------ Quelle 1 und 2: Gebiete

function brightCandidates({ smooth, width, height }) {
  const threshold = otsu(smooth);
  const mask = new Uint8Array(width * height);
  for (let i = 0; i < mask.length; i++) mask[i] = smooth[i] > threshold ? 1 : 0;
  return fromMask(mask, width, height, 'helligkeit', 0);
}

function textureCandidates({ texture, width, height }) {
  const threshold = otsu(texture);
  const mask = new Uint8Array(width * height);
  for (let i = 0; i < mask.length; i++) mask[i] = texture[i] > threshold ? 1 : 0;
  // Die Schrift fuellt das Papier nicht bis an den Rand: den Rand des Blattes dazugeben
  return fromMask(mask, width, height, 'textur', 0.07);
}

/** Das groesste Gebiet einer Maske als Viereck; `grow` vergroessert es um diesen Anteil seiner Breite. */
function fromMask(mask, width, height, source, grow) {
  const region = largestRegion(mask, width, height);
  if (!region || region.size < width * height * 0.01) return [];
  let quad = reduceToQuad(convexHull(region.edge));
  if (!quad) return [];
  if (grow > 0) {
    const shortSide = Math.min(dist(quad[0], quad[1]), dist(quad[1], quad[2]));
    quad = insetQuad(quad, -grow * shortSide);
  }
  return [{ corners: quad, source }];
}

/** Das groesste zusammenhaengende Gebiet; liefert seine Groesse und je Zeile den linken und rechten Rand. */
function largestRegion(mask, width, height) {
  const seen = new Uint8Array(mask.length);
  const stack = new Int32Array(mask.length);
  let best = null;
  for (let start = 0; start < mask.length; start++) {
    if (!mask[start] || seen[start]) continue;
    let top = 0, size = 0, minY = height, maxY = 0;
    const left = new Int32Array(height).fill(width);
    const right = new Int32Array(height).fill(-1);
    stack[top++] = start;
    seen[start] = 1;
    while (top) {
      const i = stack[--top];
      const x = i % width, y = (i - x) / width;
      size++;
      if (x < left[y]) left[y] = x;
      if (x > right[y]) right[y] = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
      if (x > 0 && mask[i - 1] && !seen[i - 1]) { seen[i - 1] = 1; stack[top++] = i - 1; }
      if (x < width - 1 && mask[i + 1] && !seen[i + 1]) { seen[i + 1] = 1; stack[top++] = i + 1; }
      if (y > 0 && mask[i - width] && !seen[i - width]) { seen[i - width] = 1; stack[top++] = i - width; }
      if (y < height - 1 && mask[i + width] && !seen[i + width]) { seen[i + width] = 1; stack[top++] = i + width; }
    }
    if (!best || size > best.size) {
      const edge = [];
      for (let y = minY; y <= maxY; y++) {
        if (right[y] < 0) continue;
        edge.push({ x: left[y], y }, { x: right[y] + 1, y }, { x: left[y], y: y + 1 }, { x: right[y] + 1, y: y + 1 });
      }
      best = { size, edge };
    }
  }
  return best;
}

function convexHull(points) {
  const pts = [...points].sort((a, b) => a.x - b.x || a.y - b.y);
  const cross = (o, a, b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
  const lower = [];
  for (const p of pts) {
    while (lower.length >= 2 && cross(lower[lower.length - 2], lower[lower.length - 1], p) <= 0) lower.pop();
    lower.push(p);
  }
  const upper = [];
  for (let i = pts.length - 1; i >= 0; i--) {
    const p = pts[i];
    while (upper.length >= 2 && cross(upper[upper.length - 2], upper[upper.length - 1], p) <= 0) upper.pop();
    upper.push(p);
  }
  lower.pop(); upper.pop();
  return lower.concat(upper);
}

/**
 * Reduziert eine konvexe Huelle auf vier Ecken, ohne in sie hineinzuschneiden: die Kante, deren
 * Entfernung die kleinste Flaeche hinzufuegt, wird durch den Schnittpunkt ihrer beiden Nachbarkanten
 * ersetzt. Das Ergebnis umschliesst die Huelle - ein Viereck, das den Beleg abschneidet, waere
 * schlimmer als eines mit einem schmalen Rand.
 */
function reduceToQuad(hull) {
  let poly = hull;
  if (poly.length < 4) return null;
  while (poly.length > 4) {
    let bestIndex = -1, bestArea = Infinity, bestPoint = null;
    const n = poly.length;
    for (let i = 0; i < n; i++) {
      const a = poly[(i - 1 + n) % n], b = poly[i], c = poly[(i + 1) % n], d = poly[(i + 2) % n];
      const p = intersect(a, b, c, d);
      // Nur ein Schnittpunkt hinter der Kante zaehlt: laufen die Nachbarkanten auseinander, liegt er auf
      // der anderen Seite, und das Viereck wuerde sich verdrehen.
      if (!p || p.t <= 1) continue;
      const added = Math.abs((b.x - p.x) * (c.y - p.y) - (b.y - p.y) * (c.x - p.x)) / 2;
      if (added < bestArea) { bestArea = added; bestIndex = i; bestPoint = p; }
    }
    if (bestIndex < 0) return null;
    const next = [];
    for (let k = 0; k < n; k++) {
      if (k === bestIndex) next.push(bestPoint);
      else if (k !== (bestIndex + 1) % n) next.push(poly[k]);
    }
    poly = next;
  }
  return poly;
}

/** Jede Kante des Vierecks um `d` nach innen schieben (negativ: nach aussen) und die Ecken neu schneiden. */
function insetQuad(quad, d) {
  const cx = quad.reduce((s, p) => s + p.x, 0) / 4;
  const cy = quad.reduce((s, p) => s + p.y, 0) / 4;
  const lines = quad.map((a, i) => {
    const b = quad[(i + 1) % 4];
    const len = Math.hypot(b.x - a.x, b.y - a.y) || 1;
    let nx = -(b.y - a.y) / len, ny = (b.x - a.x) / len;
    if ((cx - a.x) * nx + (cy - a.y) * ny < 0) { nx = -nx; ny = -ny; }
    return { a: { x: a.x + nx * d, y: a.y + ny * d }, b: { x: b.x + nx * d, y: b.y + ny * d } };
  });
  const shifted = quad.map((_, i) => {
    const previous = lines[(i + 3) % 4], current = lines[i];
    return intersect(previous.a, previous.b, current.a, current.b);
  });
  // Parallele Kanten (Entartung) schneiden sich nicht: dann bleibt das Viereck, wie es war.
  return shifted.every(Boolean) ? shifted.map(({ x, y }) => ({ x, y })) : quad;
}

function intersect(a, b, c, d) {
  const d1x = b.x - a.x, d1y = b.y - a.y, d2x = d.x - c.x, d2y = d.y - c.y;
  const den = d1x * d2y - d1y * d2x;
  if (Math.abs(den) < 1e-9) return null;
  const t = ((c.x - a.x) * d2y - (c.y - a.y) * d2x) / den;
  return { x: a.x + t * d1x, y: a.y + t * d1y, t };
}

// ------------------------------------------------------------------ Quelle 3: Kanten

const DEGREE = Math.PI / 180;

/**
 * Vier lange gerade Kanten, die ein Blatt umschliessen: Hough-Transformation ueber die Kantenpixel (die
 * Richtung des Gradienten sagt, welche Seite die hellere ist), jede Gerade genau nachgezogen, jede
 * Vierergruppe, deren Normalen sich im rechten Winkel drehen, ein Kandidat. Die Bildraender sind
 * Geraden ohne eigene Kante: ein Bon, der ueber den Rand hinausgeht, hat dort seine Seite.
 */
function edgeCandidates({ gx, gy, mag, width, height }) {
  const lines = findLines(gx, gy, mag, width, height);
  for (const border of borderLines(width, height)) lines.push(border);
  return quadsFromLines(lines, width, height);
}

function findLines(gx, gy, mag, width, height) {
  let max = 0;
  for (let i = 0; i < mag.length; i++) if (mag[i] > max) max = mag[i];
  // Schwelle: die staerksten sieben Prozent, aber nicht Rauschen
  const hist = new Uint32Array(256);
  for (let i = 0; i < mag.length; i++) hist[Math.min(255, Math.floor(mag[i] / Math.max(1, max) * 255))]++;
  let acc = 0, cut = 255;
  for (let v = 255; v >= 0; v--) { acc += hist[v]; if (acc > mag.length * 0.07) { cut = v; break; } }
  const threshold = Math.max(36, cut / 255 * max);

  // Kantenpixel: dort, wo der Gradient laengs seiner Richtung sein Maximum hat
  const edge = [];
  for (let y = 1; y < height - 1; y++) {
    for (let x = 1; x < width - 1; x++) {
      const i = y * width + x, m = mag[i];
      if (m < threshold) continue;
      const ox = Math.round(gx[i] / m), oy = Math.round(gy[i] / m);
      if (m < mag[i + oy * width + ox] || m < mag[i - oy * width - ox]) continue;
      edge.push({ x, y, angle: (Math.atan2(gy[i], gx[i]) / DEGREE + 360) % 360 });
    }
  }

  const diagonal = Math.ceil(Math.hypot(width, height));
  const rhoBins = 2 * diagonal + 1;
  const votes = new Float32Array(360 * rhoBins);
  const cosT = new Float32Array(360), sinT = new Float32Array(360);
  for (let t = 0; t < 360; t++) { cosT[t] = Math.cos(t * DEGREE); sinT[t] = Math.sin(t * DEGREE); }
  for (const p of edge) {
    const centre = Math.round(p.angle) % 360;
    for (let d = -2; d <= 2; d++) {
      const t = (centre + d + 360) % 360;
      votes[t * rhoBins + Math.round(p.x * cosT[t] + p.y * sinT[t]) + diagonal] += Math.abs(d) === 2 ? 0.5 : 1;
    }
  }

  // Spitzen: lokale Maxima; eine Seite ist mindestens ein Zehntel des kleineren Bildmasses lang
  const minVotes = Math.min(width, height) / 10;
  const peaks = [];
  for (let t = 0; t < 360; t++) {
    for (let r = 4; r < rhoBins - 4; r++) {
      const v = votes[t * rhoBins + r];
      if (v < minVotes) continue;
      let top = true;
      for (let dt = -4; dt <= 4 && top; dt++) {
        const tt = (t + dt + 360) % 360;
        for (let dr = -4; dr <= 4; dr++) { if ((dt || dr) && votes[tt * rhoBins + r + dr] > v) { top = false; break; } }
      }
      if (top) peaks.push({ theta: t, rho: r - diagonal, votes: v });
    }
  }
  peaks.sort((a, b) => b.votes - a.votes);

  const lines = [];
  for (const peak of peaks.slice(0, 26)) {
    const line = refine(edge, peak);
    if (!line) continue;
    if (lines.some(other => Math.abs(angleDiff(other.theta, line.theta)) < 6 && Math.abs(other.rho - line.rho) < 4)) continue;
    lines.push(line);
  }
  return lines;
}

/** Die Gerade aus den Kantenpixeln einer Spitze: Hauptachse ihrer Punkte (genauer als ein Stimmbehaelter). */
function refine(edge, peak) {
  const c = Math.cos(peak.theta * DEGREE), s = Math.sin(peak.theta * DEGREE);
  const inliers = [];
  for (const p of edge) {
    if (Math.abs(p.x * c + p.y * s - peak.rho) < 2.2 && Math.abs(angleDiff(p.angle, peak.theta)) < 15) inliers.push(p);
  }
  if (inliers.length < 8) return null;
  let mx = 0, my = 0;
  for (const p of inliers) { mx += p.x; my += p.y; }
  mx /= inliers.length; my /= inliers.length;
  let sxx = 0, sxy = 0, syy = 0;
  for (const p of inliers) { const dx = p.x - mx, dy = p.y - my; sxx += dx * dx; sxy += dx * dy; syy += dy * dy; }
  const angle = 0.5 * Math.atan2(2 * sxy, sxx - syy);
  let nx = -Math.sin(angle), ny = Math.cos(angle);
  if (nx * c + ny * s < 0) { nx = -nx; ny = -ny; }
  return { theta: (Math.atan2(ny, nx) / DEGREE + 360) % 360, rho: nx * mx + ny * my, nx, ny, border: null };
}

function angleDiff(a, b) { return ((a - b + 540) % 360) - 180; }

function borderLines(width, height) {
  const make = (nx, ny, rho, border) => ({ theta: (Math.atan2(ny, nx) / DEGREE + 360) % 360, rho, nx, ny, border });
  return [make(1, 0, 0.5, 'left'), make(0, 1, 0.5, 'top'), make(-1, 0, -(width - 1.5), 'right'), make(0, -1, -(height - 1.5), 'bottom')];
}

function intersectLines(a, b) {
  const det = a.nx * b.ny - a.ny * b.nx;
  if (Math.abs(det) < 1e-6) return null;
  return { x: (a.rho * b.ny - b.rho * a.ny) / det, y: (a.nx * b.rho - b.nx * a.rho) / det };
}

/** Jede Vierergruppe, deren nach innen zeigende Normalen sich ungefaehr im rechten Winkel im Uhrzeigersinn drehen. */
function quadsFromLines(lines, width, height) {
  const sorted = [...lines].sort((a, b) => a.theta - b.theta);
  const pool = [];
  const n = sorted.length;
  const turn = (a, b) => ((b.theta - a.theta) + 360) % 360;
  const ok = d => d >= 40 && d <= 140;
  for (let i = 0; i < n; i++) for (let j = i + 1; j < n; j++) {
    if (!ok(turn(sorted[i], sorted[j]))) continue;
    for (let k = j + 1; k < n; k++) {
      if (!ok(turn(sorted[j], sorted[k]))) continue;
      for (let l = k + 1; l < n; l++) {
        if (!ok(turn(sorted[k], sorted[l])) || !ok(turn(sorted[l], sorted[i]))) continue;
        const four = [sorted[i], sorted[j], sorted[k], sorted[l]];
        // Ohne mindestens zwei echte Kanten ist es nur der Bildrand
        if (four.filter(x => x.border).length > 2) continue;
        const corners = [intersectLines(four[3], four[0]), intersectLines(four[0], four[1]), intersectLines(four[1], four[2]), intersectLines(four[2], four[3])];
        if (corners.some(p => !p || Math.abs(p.x - width / 2) > width * 1.6 || Math.abs(p.y - height / 2) > height * 1.6)) continue;
        if (!isConvex(corners)) continue;
        pool.push({ corners, source: 'kanten' });
      }
    }
  }
  return pool;
}

function isConvex(c) {
  let sign = 0;
  for (let i = 0; i < 4; i++) {
    const a = c[i], b = c[(i + 1) % 4], d = c[(i + 2) % 4];
    const z = (b.x - a.x) * (d.y - b.y) - (b.y - a.y) * (d.x - b.x);
    if (Math.abs(z) < 1e-6) return false;
    if (sign && Math.sign(z) !== sign) return false;
    sign = Math.sign(z);
  }
  return true;
}

// ------------------------------------------------------------------ Bewertung

function insidePolygon(poly, x, y) {
  let c = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const a = poly[i], b = poly[j];
    if ((a.y > y) !== (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) c = !c;
  }
  return c;
}

/** Seite k eines Vierecks mit ihrer nach innen zeigenden Einheitsnormale. */
function side(corners, k, cx, cy) {
  const a = corners[k], b = corners[(k + 1) % 4];
  const length = Math.hypot(b.x - a.x, b.y - a.y) || 1;
  let nx = -(b.y - a.y) / length, ny = (b.x - a.x) / length;
  if ((cx - a.x) * nx + (cy - a.y) * ny < 0) { nx = -nx; ny = -ny; }
  return { a, b, length, nx, ny };
}

/**
 * Die Zahlen, nach denen ein Viereck beurteilt wird:
 *   edgeMean/edgeMin  wie viel der Seiten (im Mittel, an der schwaechsten) wirklich eine Kante ist
 *   contrast          wie viel heller sein Inneres als seine Umgebung ist
 *   texture           wie viel mehr Schrift innen als aussen steht
 *   area, borders, known  Groesse, Seiten auf dem Bildrand, Anteil der Seiten, die sich pruefen liessen
 * Seiten auf dem Bildrand zaehlen bei den Kanten nicht (dort ist nichts zu sehen).
 */
function rateRaw(field, candidate) {
  const { smooth, texture, width, height } = field;
  const corners = candidate.corners;
  const cx = corners.reduce((s, p) => s + p.x, 0) / 4, cy = corners.reduce((s, p) => s + p.y, 0) / 4;

  const supports = [];
  let knownTotal = 0;
  const border = { top: false, right: false, bottom: false, left: false };
  for (let k = 0; k < 4; k++) {
    const { a, b, length, nx, ny } = side(corners, k, cx, cy);
    if (length < 4) return null;
    const steps = Math.max(6, Math.floor(length / 2));
    let known = 0, sum = 0;
    for (let t = 0; t < steps; t++) {
      const x = a.x + (b.x - a.x) * (t + 0.5) / steps, y = a.y + (b.y - a.y) * (t + 0.5) / steps;
      if (x < 3 || y < 3 || x > width - 4 || y > height - 4) continue;
      const step = bilinear(smooth, width, height, x + nx * 2.5, y + ny * 2.5) - bilinear(smooth, width, height, x - nx * 2.5, y - ny * 2.5);
      sum += Math.min(1, Math.abs(step) / 16);
      known++;
    }
    knownTotal += known / steps;
    if (known / steps > 0.3) supports.push(sum / known);
    else {
      // eine Seite fast ganz am Bildrand: der Bon geht dort weiter
      const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
      if (my < 4 && Math.abs(a.y - b.y) < 6) border.top = true;
      else if (my > height - 5 && Math.abs(a.y - b.y) < 6) border.bottom = true;
      else if (mx < 4 && Math.abs(a.x - b.x) < 6) border.left = true;
      else if (mx > width - 5 && Math.abs(a.x - b.x) < 6) border.right = true;
    }
  }
  if (supports.length < 2) return null;
  const edgeMean = supports.reduce((s, v) => s + v, 0) / supports.length;
  const edgeMin = Math.min(...supports);

  // Inneres: ein Raster ueber das Bild
  let insideSum = 0, textureIn = 0, insideCount = 0;
  const grid = 28;
  for (let gy = 0; gy < grid; gy++) {
    for (let gx = 0; gx < grid; gx++) {
      const x = (gx + 0.5) / grid * width, y = (gy + 0.5) / grid * height;
      if (insidePolygon(corners, x, y)) {
        insideSum += bilinear(smooth, width, height, x, y);
        textureIn += bilinear(texture, width, height, x, y);
        insideCount++;
      }
    }
  }
  const area = insideCount / (grid * grid);
  if (area < 0.012 || area > 0.97) return null;

  // Umgebung: knapp ausserhalb der Seiten
  let outSum = 0, textureOut = 0, outCount = 0;
  for (let k = 0; k < 4; k++) {
    const { a, b, nx, ny } = side(corners, k, cx, cy);
    for (let t = 1; t < 8; t++) {
      const x = a.x + (b.x - a.x) * t / 8 - nx * 6, y = a.y + (b.y - a.y) * t / 8 - ny * 6;
      if (x < 1 || y < 1 || x > width - 2 || y > height - 2 || insidePolygon(corners, x, y)) continue;
      outSum += bilinear(smooth, width, height, x, y);
      textureOut += bilinear(texture, width, height, x, y);
      outCount++;
    }
  }
  const brightnessIn = insideSum / insideCount, textureMeanIn = textureIn / insideCount;
  const brightnessOut = outCount ? outSum / outCount : brightnessIn - 20;
  const textureMeanOut = outCount ? textureOut / outCount : textureMeanIn * 0.5;
  const contrast = Math.min(1, Math.max(-0.5, (brightnessIn - brightnessOut) / 255 / 0.2));
  const textureContrast = (textureMeanIn - textureMeanOut) / (textureMeanIn + textureMeanOut + 1);

  const features = { edgeMean, edgeMin, contrast, texture: textureContrast, area: Math.min(1, area / 0.05), borders: 4 - supports.length, known: knownTotal / 4 };
  return { corners, area, border, source: candidate.source, features, field };
}

// ------------------------------------------------------------------ Nachschaerfen

/**
 * Jede Seite rutscht auf die staerkste Kante in ihrer Naehe (bis zu vier Pixel des Analysebildes):
 * die Quellen liefern den Rand auf ein, zwei Pixel genau, und das sind bei einem schmalen Bon schnell
 * zehn Prozent seiner Breite. Seiten am Bildrand bleiben, wo sie sind.
 */
function snap(field, rated) {
  const { smooth, width, height } = field;
  const corners = rated.corners;
  const cx = corners.reduce((s, p) => s + p.x, 0) / 4, cy = corners.reduce((s, p) => s + p.y, 0) / 4;
  const lines = [];
  for (let k = 0; k < 4; k++) {
    const { a, b, length, nx, ny } = side(corners, k, cx, cy);
    const steps = Math.max(8, Math.floor(length / 2));
    let bestOffset = 0, bestStrength = -1, strengthAtZero = 0, usable = 0;
    for (let offset = -4; offset <= 4; offset += 0.5) {
      let sum = 0, count = 0;
      for (let t = 1; t < steps; t++) {
        const x = a.x + (b.x - a.x) * t / steps + nx * offset, y = a.y + (b.y - a.y) * t / steps + ny * offset;
        if (x < 3 || y < 3 || x > width - 4 || y > height - 4) continue;
        sum += Math.abs(bilinear(smooth, width, height, x + nx * 1.5, y + ny * 1.5) - bilinear(smooth, width, height, x - nx * 1.5, y - ny * 1.5));
        count++;
      }
      if (count < steps * 0.3) continue;
      usable++;
      const strength = sum / count;
      if (offset === 0) strengthAtZero = strength;
      if (strength > bestStrength) { bestStrength = strength; bestOffset = offset; }
    }
    // nur verschieben, wenn die Kante dort deutlich staerker ist als an der alten Stelle
    const shift = usable && bestStrength > strengthAtZero * 1.15 ? bestOffset : 0;
    lines.push({ a: { x: a.x + nx * shift, y: a.y + ny * shift }, b: { x: b.x + nx * shift, y: b.y + ny * shift } });
  }
  const moved = corners.map((_, i) => intersect(lines[(i + 3) % 4].a, lines[(i + 3) % 4].b, lines[i].a, lines[i].b));
  return moved.every(Boolean) ? moved.map(({ x, y }) => ({ x, y })) : corners;
}

// ------------------------------------------------------------------ Geraderechnen

/**
 * Ecken so ordnen, dass der Beleg aufrecht steht: die Kante, die am ehesten waagerecht liegt und in
 * der oberen Bildhaelfte des Vierecks, ist die obere. Das haelt die Ausrichtung des Handys fest und
 * dreht nicht, wenn der Beleg schraeg liegt.
 */
function orderUpright(quad) {
  const cx = quad.reduce((s, p) => s + p.x, 0) / 4;
  const cy = quad.reduce((s, p) => s + p.y, 0) / 4;
  // Im Uhrzeigersinn (Bildschirm: y nach unten) sortieren.
  const clockwise = [...quad].sort((p, q) => Math.atan2(p.y - cy, p.x - cx) - Math.atan2(q.y - cy, q.x - cx));
  let best = 0, bestScore = Infinity;
  for (let i = 0; i < 4; i++) {
    const a = clockwise[i], b = clockwise[(i + 1) % 4];
    if ((a.y + b.y) / 2 >= cy) continue;
    const score = Math.abs(Math.atan2(b.y - a.y, b.x - a.x));
    if (score < bestScore) { bestScore = score; best = i; }
  }
  return [0, 1, 2, 3].map(k => clockwise[(best + k) % 4]);
}

function dist(p, q) { return Math.hypot(p.x - q.x, p.y - q.y); }

/** Abbildung des Einheitsquadrats auf ein Viereck (Heckbert): ergibt die Koeffizienten der Projektion. */
function squareToQuad([p0, p1, p2, p3]) {
  const dx1 = p1.x - p2.x, dx2 = p3.x - p2.x, dx3 = p0.x - p1.x + p2.x - p3.x;
  const dy1 = p1.y - p2.y, dy2 = p3.y - p2.y, dy3 = p0.y - p1.y + p2.y - p3.y;
  if (Math.abs(dx3) < 1e-9 && Math.abs(dy3) < 1e-9)
    return { a: p1.x - p0.x, b: p2.x - p1.x, c: p0.x, d: p1.y - p0.y, e: p2.y - p1.y, f: p0.y, g: 0, h: 0 };
  const den = dx1 * dy2 - dx2 * dy1;
  const g = (dx3 * dy2 - dx2 * dy3) / den;
  const h = (dx1 * dy3 - dx3 * dy1) / den;
  return {
    a: p1.x - p0.x + g * p1.x, b: p3.x - p0.x + h * p3.x, c: p0.x,
    d: p1.y - p0.y + g * p1.y, e: p3.y - p0.y + h * p3.y, f: p0.y, g, h
  };
}
