// Bilderkennung fuer die Belegkamera - ohne Bibliothek, ohne nachgeladenen Code.
//
// Alles hier arbeitet auf Pixelfeldern (Uint8Array / RGBA), nicht auf dem DOM: so laesst es sich mit
// Testbildern pruefen, und die Kamera ruft es nur mit einem kleinen Vorschaubild auf. Nur das
// Geraderechnen laeuft auf dem vollen Foto.
//
// Annahme, die jede Erkennung hier trifft: ein Beleg ist heller als das, worauf er liegt. Liegt er auf
// einem weissen Tisch, wird nichts erkannt - dann bleibt der Ausloeser von Hand, und nichts wird geraten.

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

/**
 * Sucht den Beleg im Bild: das groesste helle Gebiet, dessen Huelle auf vier Ecken reduziert wird.
 * Ergebnis: Ecken oben links, oben rechts, unten rechts, unten links in 0..1 - oder null, wenn nichts
 * Beleghaftes da ist (zu klein, zu unregelmaessig).
 */
export function findReceipt({ gray, width, height }) {
  const smooth = blur(gray, width, height);
  const threshold = otsu(smooth);
  const mask = new Uint8Array(width * height);
  for (let i = 0; i < mask.length; i++) mask[i] = smooth[i] > threshold ? 1 : 0;

  const region = largestRegion(mask, width, height);
  if (!region || region.size < width * height * 0.12) return null;

  const quad = reduceToQuad(convexHull(region.edge));
  if (!quad) return null;
  const quadArea = polygonArea(quad);
  // Ein Beleg fuellt sein Viereck fast ganz. Ein Gebiet, das es nicht tut, ist etwas anderes: ein
  // heller Fleck, ein Fenster, ein Tisch mit Reflexion.
  if (quadArea <= 0 || region.size / quadArea < 0.8) return null;

  // Die Maske reicht ueber den Papierrand hinaus (Weichzeichnen, Raster des Vorschaubildes): einen
  // Pixel nach innen, damit im geraden Ergebnis kein Streifen Tisch bleibt.
  const ordered = orderUpright(insetQuad(quad, 1));
  return {
    corners: ordered.map(p => ({ x: p.x / width, y: p.y / height })),
    coverage: quadArea / (width * height),
    // Der Beleg geht ueber den Bildrand hinaus: bei langen Bons der Normalfall, dann folgt ein
    // weiteres Foto mit Ueberlappung.
    continuesUp: region.minY <= 1,
    continuesDown: region.maxY >= height - 2
  };
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

// --- Bausteine ---------------------------------------------------------------------------------

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

/** Schwelle nach Otsu: trennt Hell von Dunkel so, dass die Streuung innerhalb der Gruppen minimal ist. */
function otsu(gray) {
  const hist = new Float64Array(256);
  for (let i = 0; i < gray.length; i++) hist[gray[i]]++;
  const total = gray.length;
  let sumAll = 0;
  for (let i = 0; i < 256; i++) sumAll += i * hist[i];
  let sumB = 0, weightB = 0, best = 0, threshold = 127;
  for (let t = 0; t < 256; t++) {
    weightB += hist[t];
    if (!weightB) continue;
    const weightF = total - weightB;
    if (!weightF) break;
    sumB += t * hist[t];
    const between = weightB * weightF * (sumB / weightB - (sumAll - sumB) / weightF) ** 2;
    if (between > best) { best = between; threshold = t; }
  }
  return threshold;
}

/** Das groesste zusammenhaengende helle Gebiet; liefert seine Groesse und je Zeile den linken und rechten Rand. */
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
      best = { size, edge, minY, maxY };
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

/** Jede Kante des Vierecks um `d` nach innen schieben und die Ecken neu schneiden. */
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

function polygonArea(poly) {
  let sum = 0;
  for (let i = 0; i < poly.length; i++) {
    const p = poly[i], q = poly[(i + 1) % poly.length];
    sum += p.x * q.y - q.x * p.y;
  }
  return Math.abs(sum) / 2;
}

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
