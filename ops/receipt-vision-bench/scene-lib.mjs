// Kuenstliche Fotos von Kassenbons mit bekannter Loesung - fuer die Messung der Belegkamera.
//
// Ein Bon liegt auf einem Tisch und wird mit einer Lochkamera aufgenommen (Brennweite, Neigung nach
// vorn/hinten und zur Seite, Drehung). Jedes Pixel wird rueckwaerts auf das Papier gerechnet; dort liefert
// eine Textur aus Zeilen mit Pseudo-Schrift die Farbe. Die vier Ecken des Papiers sind die Loesung.
//
// Eine Szene ist eine Funktion von (Kategorie, Seed) und laesst sich immer neu erzeugen. Die Zahlen
// sagen etwas darueber, was dieser Generator zeichnen kann - nicht ueber die Welt. Ein Foto, an dem die
// Erkennung in der Praxis scheitert, gehoert hier als neue Art Szene hinein.

export const CATEGORIES = [
  'easy', 'skew', 'rotate', 'crumple', 'lightdesk', 'pattern', 'shadow', 'glare',
  'occluded', 'clutter', 'small', 'cut', 'lowlight', 'blurry', 'nosheet'
];

export function mulberry32(seed) {
  let a = seed | 0;
  return () => {
    a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function hash3(a, b, c) {
  let h = (Math.imul(a | 0, 374761393) + Math.imul(b | 0, 668265263) + Math.imul(c | 0, 1274126177)) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}

function valueNoise(x, y, seed) {
  const xi = Math.floor(x), yi = Math.floor(y), fx = x - xi, fy = y - yi;
  const sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
  const top = hash3(xi, yi, seed) * (1 - sx) + hash3(xi + 1, yi, seed) * sx;
  const bottom = hash3(xi, yi + 1, seed) * (1 - sx) + hash3(xi + 1, yi + 1, seed) * sx;
  return top * (1 - sy) + bottom * sy;
}

// ------------------------------------------------------------------ der Bon

/** Ein Bon der Breite `wmm` und Laenge `lmm`: Zeilen aus Pseudo-Schrift, Trennstriche, Preise rechts. */
export function makeReceipt(seed, wmm, lmm) {
  const rnd = mulberry32(seed * 31 + 7);
  const pitch = 4.2, margin = 7, gw = 1.75;
  const count = Math.max(1, Math.floor((lmm - 2 * margin) / pitch));
  const lines = [];
  for (let i = 0; i < count; i++) {
    const r = rnd();
    const kind = i < 4 ? 'head' : r < 0.06 ? 'blank' : r < 0.13 ? 'sep' : r < 0.17 ? 'total' : 'item';
    const room = Math.floor((wmm - 20) / gw);
    lines.push({
      kind,
      chars: kind === 'head' ? 8 + Math.floor(rnd() * 10) : 6 + Math.floor(rnd() * (room - 6)),
      price: kind === 'item' || kind === 'total',
      gaps: 3 + Math.floor(rnd() * 4)
    });
  }
  const tone = 55 + rnd() * 45;

  function glyph(line, li, x, gy, x0, chars) {
    const local = (x - x0) / gw;
    if (local < 0 || local >= chars) return -1;
    const c = Math.floor(local);
    if (c % (line.gaps + 1) === line.gaps) return 0;
    const gx = local - c;
    if (gx < 0.12 || gx > 0.88) return 0;
    const cx = Math.floor((gx - 0.12) / 0.76 * 5), cy = Math.floor(gy * 7);
    return hash3(seed * 977 + li, c, cx * 8 + cy) < 0.52 ? 1 : 0;
  }

  return {
    wmm, lmm, pitch, margin, tone, lines, count,
    /** Tintenanteil (0 oder 1) an der Stelle (x, y) in mm vom linken oberen Papierrand. */
    ink(x, y) {
      const li = Math.floor((y - margin) / pitch);
      if (li < 0 || li >= count || x < 0 || x > wmm) return 0;
      const line = lines[li];
      if (line.kind === 'blank') return 0;
      const t = (y - margin) / pitch - li;
      if (line.kind === 'sep') return t > 0.42 && t < 0.58 && x > 3 && x < wmm - 3 && (Math.floor(x / 1.1) & 1) === 0 ? 1 : 0;
      if (t < 0.1 || t > 0.82) return 0;
      const gy = (t - 0.1) / 0.72;
      const left = line.kind === 'head' ? (wmm - line.chars * gw) / 2 : 3;
      let v = glyph(line, li, x, gy, left, line.chars);
      if (v < 0 && line.price) v = glyph(line, li + 5000, x, gy, wmm - 3 - 6 * gw, 6);
      return v < 0 ? 0 : v;
    }
  };
}

// ------------------------------------------------------------------ Kamera

function rotation(phi, psi, theta) {
  const cx = Math.cos(phi), sx = Math.sin(phi), cy = Math.cos(psi), sy = Math.sin(psi), cz = Math.cos(theta), sz = Math.sin(theta);
  // R = Rz * Ry * Rx; zurueckgegeben werden die Bilder der Papierachsen X und Y
  const apply = v => {
    let [x, y, z] = v;
    [y, z] = [y * cx - z * sx, y * sx + z * cx];
    [x, z] = [x * cy + z * sy, -x * sy + z * cy];
    [x, y] = [x * cz - y * sz, x * sz + y * cz];
    return [x, y, z];
  };
  return { r0: apply([1, 0, 0]), r1: apply([0, 1, 0]) };
}

const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

/** Kamera: Pixel <-> Papierpunkt (mm, vom Papiermittelpunkt aus). */
function camera(scene) {
  const { W, H, f, look, centerPx, s } = scene;
  const { r0, r1 } = rotation(scene.phi, scene.psi, scene.theta);
  const d = f / s;
  const rx0 = (centerPx.x - W / 2) / f, ry0 = (centerPx.y - H / 2) / f;
  const base = [r0[0] * look.x + r1[0] * look.y, r0[1] * look.x + r1[1] * look.y, r0[2] * look.x + r1[2] * look.y];
  const T = [d * rx0 - base[0], d * ry0 - base[1], d - base[2]];
  const rhs = [-T[0], -T[1], -T[2]];
  const project = (X, Y) => {
    const P = [r0[0] * X + r1[0] * Y + T[0], r0[1] * X + r1[1] * Y + T[1], r0[2] * X + r1[2] * Y + T[2]];
    return { x: f * P[0] / P[2] + W / 2, y: f * P[1] / P[2] + H / 2, z: P[2] };
  };
  const unproject = (x, y) => {
    const c = [-(x - W / 2) / f, -(y - H / 2) / f, -1];
    const bc = cross(r1, c);
    const det = dot(r0, bc);
    if (Math.abs(det) < 1e-12) return null;
    const lambda = dot(r0, cross(r1, rhs)) / det;
    if (lambda <= 0) return null;
    return { X: dot(rhs, bc) / det, Y: dot(r0, cross(rhs, c)) / det };
  };
  return { project, unproject };
}

// ------------------------------------------------------------------ Tisch

function background(kind, x, y, seed, tint) {
  let r, g, b;
  switch (kind) {
    case 'wood': {
      const grain = 0.78 + 0.22 * Math.sin(0.11 * y + 7 * valueNoise(x * 0.012, y * 0.12, seed) + 0.002 * x);
      r = 158 * grain; g = 112 * grain; b = 70 * grain; break;
    }
    case 'carpet': {
      const v = 96 + (hash3(x, y, seed) - 0.5) * 36 + valueNoise(x * 0.05, y * 0.05, seed) * 14;
      r = v * 0.95; g = v; b = v * 1.05; break;
    }
    case 'white': {
      const v = 240 - valueNoise(x * 0.01, y * 0.01, seed) * 9;
      r = v; g = v; b = v - 1; break;
    }
    case 'marble': {
      const vein = Math.abs(Math.sin(0.02 * x + 9 * valueNoise(x * 0.006, y * 0.006, seed)));
      const v = 206 + 18 * valueNoise(x * 0.02, y * 0.02, seed + 3) - 28 * (1 - Math.min(1, vein * 6));
      r = v; g = v; b = v + 3; break;
    }
    case 'gingham': {
      const a = ((Math.floor(x / 34) + Math.floor(y / 34)) & 1) ? 1 : 0;
      const v = a ? 150 : 188;
      r = v; g = v * 0.85; b = v * 0.85; break;
    }
    case 'keyboard': {
      const kx = x % 46, ky = y % 46;
      const v = kx < 40 && ky < 40 ? 78 : 30;
      r = v; g = v; b = v + 4; break;
    }
    default: { // dark
      const v = 28 + valueNoise(x * 0.03, y * 0.03, seed) * 22 + (hash3(x, y, seed) - 0.5) * 5;
      r = v; g = v; b = v + 3;
    }
  }
  return [r * tint[0], g * tint[1], b * tint[2]];
}

// ------------------------------------------------------------------ Szene

/**
 * Zeichnet ein Bild. `scene`: { receipt, W, H, f, look:{x,y} (Papierpunkt in der Bildmitte-Naehe), centerPx:{x,y}, s (px je mm),
 * phi, psi, theta, crumple (mm), bg, bgSeed, fx:{...}, others:[...], seed }.
 */
export function renderScene(scene) {
  const { receipt, W, H } = scene;
  const cam = camera(scene);
  const rnd = mulberry32(scene.seed * 13 + 5);
  const fx = { shadow: false, glare: false, finger: false, blur: 0, motion: 0, noise: 2, gain: 1, tint: [1, 1, 1], ...scene.fx };
  const rgba = new Uint8ClampedArray(W * H * 4);
  const coverage = new Float32Array(W * H);

  // Knitter: glatte Hoehe + einige Knicke; verschiebt das Papier leicht und schattiert es
  const waves = [];
  for (let i = 0; i < 5; i++) {
    const ang = rnd() * Math.PI * 2, k = 0.12 + rnd() * 0.45;
    waves.push({ kx: Math.cos(ang) * k, ky: Math.sin(ang) * k, ph: rnd() * 6.28, a: (0.3 + rnd()) });
  }
  const creases = [];
  for (let i = 0; i < Math.round(scene.crumple * 3); i++) {
    const ang = rnd() * Math.PI, o = (rnd() - 0.5) * receipt.lmm * 0.8;
    creases.push({ nx: Math.cos(ang), ny: Math.sin(ang), o: o * 0.5, sign: rnd() < 0.5 ? -1 : 1 });
  }
  const crumple = scene.crumple || 0;
  const light = [0.6, -0.8];

  const paper = (X, Y) => {
    const u = X + receipt.wmm / 2, v = Y + receipt.lmm / 2;
    if (u < 0 || u > receipt.wmm || v < 0 || v > receipt.lmm) return null;
    let uu = u, vv = v, shade = 1;
    if (crumple > 0) {
      let hx = 0, hy = 0;
      for (const w of waves) {
        const c = Math.cos(w.kx * u + w.ky * v + w.ph) * w.a;
        hx += c * w.kx; hy += c * w.ky;
      }
      uu += crumple * 0.2 * hx; vv += crumple * 0.2 * hy;
      shade += crumple * 0.35 * (hx * light[0] + hy * light[1]);
      for (const c of creases) {
        const dist = u * c.nx + v * c.ny - (c.o + receipt.wmm * 0.5 * c.nx + receipt.lmm * 0.5 * c.ny);
        const g = Math.exp(-(dist * dist) / 0.5);
        shade -= c.sign * 0.2 * g * Math.sign(dist) * 1.4;
        uu += c.nx * 0.5 * g * Math.sign(dist); vv += c.ny * 0.5 * g * Math.sign(dist);
      }
    }
    const inkV = receipt.ink(uu, vv);
    const grain = 0.985 + hash3(Math.floor(u * 7), Math.floor(v * 7), 99) * 0.03;
    const base = shade * grain;
    const t = receipt.tone;
    const k = inkV * 0.92;
    return [(236 * (1 - k) + t * k) * base, (234 * (1 - k) + t * k) * base, (228 * (1 - k) + (t + 6) * k) * base];
  };

  const others = scene.others || [];
  const SS = 2;
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      let r = 0, g = 0, b = 0, cov = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const px = x + (sx + 0.5) / SS, py = y + (sy + 0.5) / SS;
          const hit = scene.nosheet ? null : cam.unproject(px, py);
          const colour = hit ? paper(hit.X, hit.Y) : null;
          if (colour) { r += colour[0]; g += colour[1]; b += colour[2]; cov++; continue; }
          let bgc = null;
          for (const o of others) {
            const dx = px - o.x, dy = py - o.y, c = Math.cos(o.a), s2 = Math.sin(o.a);
            const lx = dx * c + dy * s2, ly = -dx * s2 + dy * c;
            if (Math.abs(lx) < o.w / 2 && Math.abs(ly) < o.h / 2) { bgc = [o.v, o.v - 2, o.v - 8]; break; }
          }
          bgc = bgc || background(scene.bg, px, py, scene.bgSeed, fx.tint);
          // Schatten des Papiers: das Papier, etwas nach links oben verschoben, wuerfe ihn hierher
          if (!scene.nosheet) {
            const shadowHit = cam.unproject(px - 4, py - 6);
            if (shadowHit && Math.abs(shadowHit.X) < receipt.wmm / 2 && Math.abs(shadowHit.Y) < receipt.lmm / 2) bgc = [bgc[0] * 0.66, bgc[1] * 0.66, bgc[2] * 0.66];
          }
          r += bgc[0]; g += bgc[1]; b += bgc[2];
        }
      }
      const n = SS * SS, i = y * W + x;
      coverage[i] = cov / n;
      rgba[i * 4] = r / n; rgba[i * 4 + 1] = g / n; rgba[i * 4 + 2] = b / n; rgba[i * 4 + 3] = 255;
    }
  }

  // Schatten einer Hand: eine Haelfte des Bildes dunkler, mit weichem Rand
  if (fx.shadow) {
    const ang = rnd() * Math.PI * 2, nx = Math.cos(ang), ny = Math.sin(ang), off = (rnd() - 0.5) * 0.4 * Math.min(W, H), strength = 0.45 + rnd() * 0.2;
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      const dd = (x - W / 2) * nx + (y - H / 2) * ny - off;
      const m = 1 - strength * Math.min(1, Math.max(0, dd / 40 + 0.5));
      const i = (y * W + x) * 4; rgba[i] *= m; rgba[i + 1] *= m; rgba[i + 2] *= m;
    }
  }
  // Spiegelung: ein heller Fleck, nur auf dem Papier
  if (fx.glare) {
    const gx = W * (0.3 + rnd() * 0.4), gy = H * (0.3 + rnd() * 0.4), rad = Math.min(W, H) * (0.12 + rnd() * 0.1);
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      const i = y * W + x; if (coverage[i] < 0.5) continue;
      const add = 95 * Math.exp(-(((x - gx) ** 2 + (y - gy) ** 2) / (rad * rad)));
      rgba[i * 4] += add; rgba[i * 4 + 1] += add; rgba[i * 4 + 2] += add;
    }
  }
  // Ein Finger, der von aussen auf den Rand des Papiers zeigt
  if (fx.finger) {
    const corners = [[-1, -1], [1, -1], [1, 1], [-1, 1]].map(([sx, sy]) => cam.project(sx * receipt.wmm / 2, sy * receipt.lmm / 2));
    const k = Math.floor(rnd() * 4), a = corners[k], b = corners[(k + 1) % 4];
    const t = 0.2 + rnd() * 0.6, ex = a.x + (b.x - a.x) * t, ey = a.y + (b.y - a.y) * t;
    const cx = W / 2, cy = H / 2, len = Math.hypot(ex - cx, ey - cy) || 1, dx = (ex - cx) / len, dy = (ey - cy) / len;
    const fxc = ex + dx * 38, fyc = ey + dy * 38, ang = Math.atan2(dy, dx);
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
      const lx = (x - fxc) * Math.cos(ang) + (y - fyc) * Math.sin(ang), ly = -(x - fxc) * Math.sin(ang) + (y - fyc) * Math.cos(ang);
      if ((lx * lx) / (55 * 55) + (ly * ly) / (17 * 17) < 1) {
        const i = (y * W + x) * 4; rgba[i] = 214; rgba[i + 1] = 170; rgba[i + 2] = 150;
      }
    }
  }

  blurAndNoise(rgba, W, H, fx, rnd);

  // Loesung: die Ecken des Papiers im Bild und das auf das Bild zugeschnittene Vieleck
  const quad = [[-1, -1], [1, -1], [1, 1], [-1, 1]].map(([sx, sy]) => cam.project(sx * receipt.wmm / 2, sy * receipt.lmm / 2));
  return { rgba, W, H, quad, truth: scene.nosheet ? null : clipToRect(quad, W, H), project: cam.project, unproject: cam.unproject };
}

function blurAndNoise(rgba, W, H, fx, rnd) {
  const passes = [];
  if (fx.blur > 0) passes.push({ kx: fx.blur, ky: fx.blur });
  if (fx.motion > 0) passes.push({ kx: fx.motion, ky: 0 });
  // eine leichte Weichzeichnung gehoert zu jeder Optik
  passes.push({ kx: 0.6, ky: 0.6 });
  for (const { kx, ky } of passes) {
    const rx = Math.round(kx), ry = Math.round(ky);
    if (rx < 1 && ry < 1) continue;
    const tmp = new Float32Array(rgba.length);
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) for (let c = 0; c < 3; c++) {
      let sum = 0, n = 0;
      for (let dx = -rx; dx <= rx; dx++) { const xx = Math.min(W - 1, Math.max(0, x + dx)); sum += rgba[(y * W + xx) * 4 + c]; n++; }
      tmp[(y * W + x) * 4 + c] = sum / n;
    }
    for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) for (let c = 0; c < 3; c++) {
      let sum = 0, n = 0;
      for (let dy = -ry; dy <= ry; dy++) { const yy = Math.min(H - 1, Math.max(0, y + dy)); sum += tmp[(yy * W + x) * 4 + c]; n++; }
      rgba[(y * W + x) * 4 + c] = sum / n;
    }
  }
  for (let i = 0; i < W * H; i++) {
    const noise = (rnd() + rnd() + rnd() - 1.5) * fx.noise * 1.6;
    for (let c = 0; c < 3; c++) rgba[i * 4 + c] = rgba[i * 4 + c] * fx.gain + noise;
  }
}

// ------------------------------------------------------------------ Kategorien

const rad = deg => deg * Math.PI / 180;

/** Die Szene einer Kategorie; alles Zufaellige kommt aus dem Seed. */
export function sceneFor(category, seed, W = 640, H = 480) {
  const rnd = mulberry32(seed * 7919 + CATEGORIES.indexOf(category) * 101 + 3);
  const range = (a, b) => a + rnd() * (b - a);
  const sign = () => (rnd() < 0.5 ? -1 : 1);
  const wmm = rnd() < 0.5 ? 58 : 80;
  let lmm = range(110, 230);
  const f = W * 0.9;
  let fill = range(0.6, 0.84);
  let phi = rad(range(-6, 6)), psi = rad(range(-6, 6)), theta = rad(range(-6, 6));
  let bg = ['dark', 'wood', 'carpet'][Math.floor(rnd() * 3)];
  const fx = {};
  let crumple = 0, others = [], nosheet = false;
  let s;

  switch (category) {
    case 'skew': phi = rad(sign() * range(20, 38)); psi = rad(sign() * range(8, 28)); break;
    case 'rotate': theta = rad(sign() * range(18, 80)); break;
    case 'crumple': crumple = range(1.2, 3.2); break;
    case 'lightdesk': bg = rnd() < 0.5 ? 'white' : 'marble'; break;
    case 'pattern': bg = rnd() < 0.5 ? 'gingham' : 'keyboard'; break;
    case 'shadow': fx.shadow = true; break;
    case 'glare': fx.glare = true; break;
    case 'occluded': fx.finger = true; break;
    case 'clutter': break;
    case 'small': fill = range(0.22, 0.38); break;
    case 'cut': lmm = range(330, 560); break;
    case 'lowlight': fx.gain = range(0.28, 0.42); fx.noise = 7; fx.tint = [1.05, 1, 0.82]; break;
    case 'blurry': if (rnd() < 0.5) fx.blur = Math.round(range(2, 4)); else fx.motion = Math.round(range(4, 9)); break;
    case 'nosheet': nosheet = true; break;
    default: break;
  }

  if (category === 'cut') s = range(0.42, 0.62) * W / wmm;
  else s = fill * H / lmm;

  const centerPx = { x: W / 2 + (rnd() - 0.5) * 0.1 * W, y: H / 2 + (rnd() - 0.5) * 0.08 * H };
  if (category === 'clutter') {
    // ein zweites, kleineres Blatt auf dem Tisch - nicht der Bon
    for (let i = 0; i < 1 + Math.floor(rnd() * 2); i++) {
      const side = rnd() < 0.5 ? -1 : 1;
      others.push({ x: W / 2 + side * (0.38 + rnd() * 0.06) * W, y: H * range(0.25, 0.75), w: range(60, 110), h: range(80, 150), a: rad(range(-30, 30)), v: 232 });
    }
  }
  return {
    category, seed, W, H, f, receipt: makeReceipt(seed, wmm, lmm), look: { x: 0, y: 0 }, centerPx, s,
    phi, psi, theta, crumple, bg, bgSeed: seed + 17, fx, others, nosheet
  };
}

export function renderCategory(category, seed, W = 640, H = 480) {
  return renderScene(sceneFor(category, seed, W, H));
}

// ------------------------------------------------------------------ langer Bon in Streifen

/**
 * Ein langer Bon, von oben nach unten aufgenommen: das Handy faehrt am Bon entlang, jede Aufnahme zeigt
 * ein Stueck, aufeinanderfolgende ueberlappen zur Haelfte bis zu zwei Dritteln. Handzittern dreht und
 * neigt jede Aufnahme ein wenig, manche sind vom Fahren verwackelt.
 */
export function renderStrip(seed, options = {}) {
  const W = 640, H = 480;
  const rnd = mulberry32(seed * 53 + 11);
  const wmm = rnd() < 0.5 ? 58 : 80;
  const lmm = options.length ?? 230 + rnd() * 400;
  const receipt = makeReceipt(seed, wmm, lmm);
  const f = W * 0.9;
  const s = (0.5 + rnd() * 0.2) * W / wmm;
  const frameMm = H / s;
  const bg = ['dark', 'wood', 'carpet'][Math.floor(rnd() * 3)];
  const shake = options.shake ?? 1;
  const frames = [];
  let y = -lmm / 2 + frameMm * (0.42 + rnd() * 0.1);
  for (let k = 0; k < 40; k++) {
    const scene = {
      receipt, W, H, f,
      look: { x: (rnd() - 0.5) * 3 * shake, y },
      centerPx: { x: W / 2 + (rnd() - 0.5) * 0.05 * W * shake, y: H / 2 },
      s: s * (1 + (rnd() - 0.5) * 0.06 * shake),
      phi: rad((rnd() - 0.5) * 10 * shake), psi: rad((rnd() - 0.5) * 10 * shake), theta: rad((rnd() - 0.5) * 8 * shake),
      crumple: 0, bg, bgSeed: seed + 5, fx: { noise: 2, ...(rnd() < 0.25 * shake ? { motion: 2 + Math.floor(rnd() * 3) } : {}) },
      others: [], seed: seed * 1000 + k
    };
    frames.push({ ...renderScene(scene), centerY: y });
    if (y + frameMm / 2 >= lmm / 2 + 4) break;
    y += frameMm * (0.3 + rnd() * 0.2);
  }
  return { frames, receipt, wmm, lmm, W, H };
}

// ------------------------------------------------------------------ Messen

/** Das Vieleck auf das Bild [0,W]x[0,H] zuschneiden (Sutherland-Hodgman). */
export function clipToRect(poly, W, H) {
  const edges = [
    [p => p.x >= 0, (a, b) => ({ x: 0, y: a.y + (b.y - a.y) * (0 - a.x) / (b.x - a.x) })],
    [p => p.x <= W, (a, b) => ({ x: W, y: a.y + (b.y - a.y) * (W - a.x) / (b.x - a.x) })],
    [p => p.y >= 0, (a, b) => ({ y: 0, x: a.x + (b.x - a.x) * (0 - a.y) / (b.y - a.y) })],
    [p => p.y <= H, (a, b) => ({ y: H, x: a.x + (b.x - a.x) * (H - a.y) / (b.y - a.y) })]
  ];
  let out = poly.map(p => ({ x: p.x, y: p.y }));
  for (const [inside, cut] of edges) {
    const input = out; out = [];
    for (let i = 0; i < input.length; i++) {
      const a = input[i], b = input[(i + 1) % input.length];
      const ia = inside(a), ib = inside(b);
      if (ia && ib) out.push(b);
      else if (ia && !ib) out.push(cut(a, b));
      else if (!ia && ib) { out.push(cut(a, b)); out.push(b); }
    }
    if (!out.length) return [];
  }
  return out;
}

function inside(poly, x, y) {
  let c = 0;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const a = poly[i], b = poly[j];
    if ((a.y > y) !== (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) c = !c;
  }
  return !!c;
}

/** Ueberdeckung zweier Vielecke im Bild (0..1), durch Abtasten mit Schritt `step`. */
export function overlap(a, b, W, H, step = 2) {
  if (!a?.length || !b?.length) return 0;
  let both = 0, either = 0;
  for (let y = step / 2; y < H; y += step) for (let x = step / 2; x < W; x += step) {
    const ia = inside(a, x, y), ib = inside(b, x, y);
    if (ia && ib) both++;
    if (ia || ib) either++;
  }
  return either ? both / either : 0;
}

// ------------------------------------------------------------------ PNG zum Ansehen

import { deflateSync } from 'node:zlib';

export function pngOf(rgba, W, H, draw = []) {
  const raw = Buffer.alloc((W * 4 + 1) * H);
  const img = new Uint8ClampedArray(rgba);
  for (const { poly, color } of draw) {
    for (let i = 0; i < poly.length; i++) {
      const a = poly[i], b = poly[(i + 1) % poly.length], n = Math.ceil(Math.hypot(b.x - a.x, b.y - a.y));
      for (let k = 0; k <= n; k++) {
        const x = Math.round(a.x + (b.x - a.x) * k / n), y = Math.round(a.y + (b.y - a.y) * k / n);
        for (let dx = -1; dx <= 0; dx++) for (let dy = -1; dy <= 0; dy++) {
          const xx = x + dx, yy = y + dy;
          if (xx >= 0 && xx < W && yy >= 0 && yy < H) { const o = (yy * W + xx) * 4; img[o] = color[0]; img[o + 1] = color[1]; img[o + 2] = color[2]; }
        }
      }
    }
  }
  for (let y = 0; y < H; y++) { raw[y * (W * 4 + 1)] = 0; Buffer.from(img.buffer, y * W * 4, W * 4).copy(raw, y * (W * 4 + 1) + 1); }
  const crcTable = new Uint32Array(256).map((_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
  const crc = buf => { let c = 0xFFFFFFFF; for (const byte of buf) c = crcTable[(c ^ byte) & 0xFF] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; };
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const td = Buffer.concat([Buffer.from(type), data]);
    const c = Buffer.alloc(4); c.writeUInt32BE(crc(td));
    return Buffer.concat([len, td, c]);
  };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(W, 0); ihdr.writeUInt32BE(H, 4); ihdr[8] = 8; ihdr[9] = 6;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
