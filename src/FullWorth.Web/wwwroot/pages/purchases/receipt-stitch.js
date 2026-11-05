// Einen langen Bon aus mehreren Aufnahmen zu EINEM Bild zusammensetzen.
//
// Die Streifen sind schon gerade gerechnet (Seiten senkrecht, alle gleich breit); es bleibt eine
// senkrechte Verschiebung zu finden. Sie ergibt sich daraus, wo die untere Kante des einen Streifens
// und die obere des naechsten gleich aussehen (normierte Kreuzkorrelation ueber die Zeilen).
//
// Eine falsche Naht ist hier schlimmer als keine: sie verdoppelt oder verschluckt Zeilen, und bei
// Geldbetraegen sieht man das dem Bild nicht an. Deshalb gilt eine Naht nur, wenn sie
//   - gut passt (die Korrelation ist hoch) UND
//   - eindeutig ist (keine zweite, fast gleich gute Stelle: Trennstriche und leere Zeilen sehen
//     an vielen Stellen gleich aus).
// Wo eine Naht nicht gilt, bleibt der Bon dort getrennt; `stitchStrips` liefert dann mehrere Stuecke,
// die der Aufrufer als einzelne Fotos weitergibt.

/** Mindestwerte, ab denen eine Naht gilt (an kuenstlichen Bons eingestellt, siehe ops/receipt-vision-bench). */
export const SEAM = { score: 0.6, margin: 0.1 };

const REDUCE = 4;
const MIN_OVERLAP = 0.25;
// Hoechstes Seitenverhaeltnis (Hoehe zu Breite) einer Seite, wenn ein langes Bild geteilt wird
const PAGE_MAX_ASPECT = 1.6;
const MAX_SEGMENT_HEIGHT = 9000;

/**
 * Setzt die Streifen von oben nach unten zusammen. Jeder Streifen: { rgba, width, height }, alle gleich breit.
 * Ergebnis: { segments: [{ rgba, width, height, from, to }], seams: [{ overlap, score, margin, ok }] };
 * `seams[i]` ist die Naht zwischen Streifen i und i + 1.
 */
export function stitchStrips(strips, seamLimits = SEAM) {
  const seams = [];
  for (let i = 0; i + 1 < strips.length; i++) {
    const seam = alignPair(strips[i], strips[i + 1]);
    seam.ok = seam.score >= seamLimits.score && seam.margin >= seamLimits.margin;
    seams.push(seam);
  }

  // Zusammenhaengende Gruppen: dort, wo eine Naht nicht gilt, beginnt ein neues Stueck
  const groups = [];
  let current = [0];
  for (let i = 0; i < seams.length; i++) {
    if (seams[i].ok) current.push(i + 1);
    else { groups.push(current); current = [i + 1]; }
  }
  groups.push(current);

  const segments = [];
  for (const group of groups) {
    let piece = [];
    let height = 0;
    const flush = () => {
      if (!piece.length) return;
      segments.push(compose(strips, seams, piece));
      piece = []; height = 0;
    };
    for (const index of group) {
      const add = piece.length ? strips[index].height - seams[index - 1].overlap : strips[index].height;
      if (piece.length && height + add > MAX_SEGMENT_HEIGHT) flush();
      height += piece.length ? add : strips[index].height;
      piece.push(index);
    }
    flush();
  }
  return { segments, seams };
}

/**
 * Teilt ein zusammengesetztes langes Bild in Seiten, die sich lesen lassen: ohne Ueberlappung und nur an
 * Zeilen, auf denen (fast) nichts steht.
 *
 * Warum nicht als ein Bild lassen: ein 1000 x 5000 Pixel hohes Bild wird fuer die Analyse auf eine Seitenlaenge
 * von rund 2000 verkleinert - die Schrift hat danach nur noch ein Viertel ihrer Pixel. Seiten im Verhaeltnis
 * 1 : 1,6 behalten sie. Weil die Naht schon bewiesen ist, braucht es keine Ueberlappung mehr, die erst wieder
 * entfernt werden muesste: nichts wird doppelt, nichts fehlt.
 */
export function splitIntoPages(segment, maxAspect = PAGE_MAX_ASPECT) {
  const { rgba, width, height } = segment;
  const maxRows = Math.round(width * maxAspect);
  if (height <= maxRows * 1.15) return [segment];

  const ink = inkPerRow(rgba, width, height);
  const pages = [];
  let top = 0;
  while (height - top > maxRows * 1.15) {
    const target = top + maxRows, window = Math.round(maxRows * 0.25);
    let cut = target, best = Infinity;
    for (let row = target - window; row <= target; row++) {
      // ein Fenster von fuenf Zeilen: eine ruhige Zeile allein koennte der Zwischenraum innerhalb einer Schriftzeile sein
      let sum = 0;
      for (let d = -2; d <= 2; d++) sum += ink[Math.min(height - 1, Math.max(0, row + d))];
      if (sum < best || (sum === best && Math.abs(row - target) < Math.abs(cut - target))) { best = sum; cut = row; }
    }
    pages.push(crop(rgba, width, top, cut));
    top = cut;
  }
  pages.push(crop(rgba, width, top, height));
  return pages;
}

/** Anteil dunkler Bildpunkte je Zeile (dunkel: deutlich unter der typischen Papierhelligkeit). */
function inkPerRow(rgba, width, height) {
  const sample = [];
  for (let i = 0; i < width * height; i += 97) sample.push(rgba[i * 4 + 1]);
  sample.sort((a, b) => a - b);
  const paper = sample[Math.floor(sample.length * 0.8)];
  const dark = paper * 0.62;
  const ink = new Float32Array(height);
  for (let y = 0; y < height; y++) {
    let count = 0;
    for (let x = 0; x < width; x += 2) if (rgba[(y * width + x) * 4 + 1] < dark) count++;
    ink[y] = count;
  }
  return ink;
}

function crop(rgba, width, from, to) {
  return { rgba: rgba.slice(from * width * 4, to * width * 4), width, height: to - from };
}

/** Wo liegt der obere Rand von `lower` in `upper`? Ergebnis: Ueberlappung in Zeilen, Korrelation, Abstand zur zweitbesten Stelle. */
export function alignPair(upper, lower) {
  const a = reduced(upper), b = reduced(lower);
  // Die Kamera loest erst aus, wenn sich das Bild deutlich weiterbewegt hat, nimmt aber auch nicht mehr
  // als zwei Drittel des alten Bildes noch einmal auf. Kuerzere Ueberlappungen gelten nicht: ein
  // einzelner Trennstrich sieht an jeder Stelle gleich aus und passt "perfekt" an die falsche.
  const maxOverlap = Math.floor(Math.min(a.height, b.height) * 0.92);
  const minOverlap = Math.max(5, Math.floor(Math.min(a.height, b.height) * MIN_OVERLAP));
  if (maxOverlap <= minOverlap) return { overlap: 0, score: 0, margin: 0, ok: false };

  const scores = new Float32Array(maxOverlap + 1).fill(-1);
  for (let o = minOverlap; o <= maxOverlap; o++) scores[o] = correlation(a, b, o);

  let best = minOverlap;
  for (let o = minOverlap; o <= maxOverlap; o++) if (scores[o] > scores[best]) best = o;
  let second = -1;
  const exclusion = Math.max(3, Math.round(a.height * 0.02));
  for (let o = minOverlap; o <= maxOverlap; o++) if (Math.abs(o - best) > exclusion && scores[o] > second) second = scores[o];

  // Auf dem vollen Bild genauer: hoechstens eine Stufe der Verkleinerung hin und her
  const fine = refineOverlap(upper, lower, best * REDUCE);
  return { overlap: fine.overlap, score: scores[best], margin: scores[best] - second, fineScore: fine.score };
}

// ------------------------------------------------------------------ Bausteine

/** Graubild, um `REDUCE` verkleinert (Mittelwert je Block). */
function reduced(strip) {
  const w = Math.floor(strip.width / REDUCE), h = Math.floor(strip.height / REDUCE);
  const g = new Float32Array(w * h);
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      let sum = 0;
      for (let dy = 0; dy < REDUCE; dy++) {
        let i = ((y * REDUCE + dy) * strip.width + x * REDUCE) * 4;
        for (let dx = 0; dx < REDUCE; dx++, i += 4) sum += strip.rgba[i] * 0.299 + strip.rgba[i + 1] * 0.587 + strip.rgba[i + 2] * 0.114;
      }
      g[y * w + x] = sum / (REDUCE * REDUCE);
    }
  }
  return { g, width: w, height: h };
}

/** Normierte Kreuzkorrelation der unteren `o` Zeilen von a mit den oberen `o` Zeilen von b (-1..1). */
function correlation(a, b, o) {
  const w = Math.min(a.width, b.width);
  const startA = (a.height - o) * a.width;
  let sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0, n = 0;
  for (let y = 0; y < o; y++) {
    const ra = startA + y * a.width, rb = y * b.width;
    for (let x = 0; x < w; x += 2) {
      const va = a.g[ra + x], vb = b.g[rb + x];
      sa += va; sb += vb; saa += va * va; sbb += vb * vb; sab += va * vb; n++;
    }
  }
  const varA = saa - sa * sa / n, varB = sbb - sb * sb / n;
  // Eine leere Flaeche (kein Text) hat keine Streuung und beweist nichts
  if (varA < n * 4 || varB < n * 4) return -1;
  return (sab - sa * sb / n) / Math.sqrt(varA * varB);
}

/** Dieselbe Suche auf dem vollen Bild, aber nur um `guess` herum. */
function refineOverlap(upper, lower, guess) {
  const ga = grayFull(upper), gb = grayFull(lower);
  let best = guess, bestScore = -2;
  for (let o = Math.max(2, guess - REDUCE * 2); o <= Math.min(Math.min(upper.height, lower.height) - 1, guess + REDUCE * 2); o++) {
    const score = fullCorrelation(ga, gb, o);
    if (score > bestScore) { bestScore = score; best = o; }
  }
  return { overlap: best, score: bestScore };
}

function grayFull(strip) {
  const g = new Float32Array(strip.width * strip.height);
  for (let i = 0; i < g.length; i++) g[i] = strip.rgba[i * 4] * 0.299 + strip.rgba[i * 4 + 1] * 0.587 + strip.rgba[i * 4 + 2] * 0.114;
  return { g, width: strip.width, height: strip.height };
}

function fullCorrelation(a, b, o) {
  const w = Math.min(a.width, b.width);
  const startA = (a.height - o) * a.width;
  let sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0, n = 0;
  for (let y = 0; y < o; y += 2) {
    const ra = startA + y * a.width, rb = y * b.width;
    for (let x = 0; x < w; x += 3) {
      const va = a.g[ra + x], vb = b.g[rb + x];
      sa += va; sb += vb; saa += va * va; sbb += vb * vb; sab += va * vb; n++;
    }
  }
  const varA = saa - sa * sa / n, varB = sbb - sb * sb / n;
  if (varA < n * 4 || varB < n * 4) return -1;
  return (sab - sa * sb / n) / Math.sqrt(varA * varB);
}

/**
 * Die Streifen `indexes` zu einem Bild. In der Ueberlappung wird dort geschnitten, wo beide Streifen
 * sich am wenigsten unterscheiden - nicht ueberblendet, denn zwei leicht verschobene Zeilen uebereinander
 * ergaeben Doppelschrift. Helligkeit und Kontrast der Streifen werden vorher angeglichen.
 */
function compose(strips, seams, indexes) {
  const width = strips[indexes[0]].width;
  let total = strips[indexes[0]].height;
  const offsets = [0];
  for (let k = 1; k < indexes.length; k++) {
    const overlap = seams[indexes[k] - 1].overlap;
    offsets.push(offsets[k - 1] + strips[indexes[k - 1]].height - overlap);
    total = offsets[k] + strips[indexes[k]].height;
  }
  const rgba = new Uint8ClampedArray(width * total * 4);
  rgba.set(strips[indexes[0]].rgba.subarray(0, width * strips[indexes[0]].height * 4));
  let filled = strips[indexes[0]].height; // Zeilen des Ergebnisses, die schon vergeben sind

  for (let k = 1; k < indexes.length; k++) {
    const upper = strips[indexes[k - 1]], lower = strips[indexes[k]];
    const overlap = Math.min(seams[indexes[k] - 1].overlap, upper.height, lower.height);
    const gain = exposure(upper, lower, overlap);
    const cut = seamRow(upper, lower, overlap, gain);
    // Zeile `cut` der Ueberlappung (von oben gezaehlt) ist die erste, die aus dem unteren Streifen kommt
    const firstLowerRow = cut;
    const resultStart = offsets[k] + firstLowerRow;
    for (let y = firstLowerRow; y < lower.height; y++) {
      const destRow = offsets[k] + y;
      if (destRow >= total) break;
      for (let x = 0; x < width; x++) {
        const s = (y * width + x) * 4, d = (destRow * width + x) * 4;
        rgba[d] = lower.rgba[s] * gain.a + gain.b;
        rgba[d + 1] = lower.rgba[s + 1] * gain.a + gain.b;
        rgba[d + 2] = lower.rgba[s + 2] * gain.a + gain.b;
        rgba[d + 3] = 255;
      }
    }
    filled = offsets[k] + lower.height;
    void resultStart;
  }
  return { rgba, width, height: total, from: indexes[0], to: indexes[indexes.length - 1], filled };
}

/** Helligkeit des unteren Streifens an den oberen angleichen: Faktor a und Versatz b aus Mittelwert und Streuung der Ueberlappung. */
function exposure(upper, lower, overlap) {
  const stats = (strip, from, to) => {
    let s = 0, ss = 0, n = 0;
    for (let y = from; y < to; y += 2) for (let x = 0; x < strip.width; x += 4) {
      const i = (y * strip.width + x) * 4, v = strip.rgba[i] * 0.299 + strip.rgba[i + 1] * 0.587 + strip.rgba[i + 2] * 0.114;
      s += v; ss += v * v; n++;
    }
    const mean = s / n;
    return { mean, std: Math.sqrt(Math.max(1, ss / n - mean * mean)) };
  };
  const u = stats(upper, upper.height - overlap, upper.height), l = stats(lower, 0, overlap);
  const a = Math.min(1.25, Math.max(0.8, u.std / l.std));
  return { a, b: u.mean - l.mean * a };
}

/** Die Zeile der Ueberlappung, an der beide Streifen am besten uebereinstimmen (Summe der Abweichungen in einem Fenster). */
function seamRow(upper, lower, overlap, gain) {
  const width = upper.width, window = 3;
  let best = Math.floor(overlap / 2), bestCost = Infinity;
  const margin = Math.max(window, Math.floor(overlap * 0.15));
  for (let row = margin; row < overlap - margin; row++) {
    let cost = 0;
    for (let dy = -window; dy <= window; dy++) {
      const yu = upper.height - overlap + row + dy, yl = row + dy;
      for (let x = 0; x < width; x += 3) {
        const i = (yu * width + x) * 4, j = (yl * width + x) * 4;
        cost += Math.abs(upper.rgba[i + 1] - (lower.rgba[j + 1] * gain.a + gain.b));
      }
    }
    if (cost < bestCost) { bestCost = cost; best = row; }
  }
  return best;
}
