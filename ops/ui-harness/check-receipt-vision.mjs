// Prueft die Bilderkennung der Belegkamera (wwwroot/pages/purchases/receipt-vision.js) an
// synthetischen Bildern: Ecken, Geraderechnen, Schaerfe, Bewegung, Bon ueber den Bildrand.
//
//   node ops/ui-harness/check-receipt-vision.mjs
//
// Das Modul ist ein Browser-Modul ohne "type": "module" im Paket; node bekommt es deshalb als
// temporaere .mjs-Kopie. Es prueft die Logik, nicht die Kamera - ob die Schwellen auf einem echten
// Handy stimmen, zeigt nur ein echtes Handy.
import { copyFileSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const copy = join(mkdtempSync(join(tmpdir(), 'receipt-vision-')), 'receipt-vision.mjs');
copyFileSync(join(here, '../../src/FullWorth.Web/wwwroot/pages/purchases/receipt-vision.js'), copy);
const { toGray, sharpness, motion, findReceipt, outputSize, warp } = await import(pathToFileURL(copy).href);

function inPoly(x, y, q) { let s = 0; for (let i = 0; i < 4; i++) { const a = q[i], b = q[(i + 1) % 4]; const c = (b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x); s += c > 0 ? 1 : -1; } return Math.abs(s) === 4; }
function scene(W, H, quad, bg = 20, paper = 235, blurPx = 0) {
  const d = new Uint8ClampedArray(W * H * 4);
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const o = (y * W + x) * 4; let v = bg + ((x * 7 + y * 13) % 9); // leichtes Rauschen
    if (inPoly(x + .5, y + .5, quad)) { v = paper; if ((y % 14) < 3 && (x % 29) < 18) v = 70; } // "Text"
    d[o] = d[o + 1] = d[o + 2] = v; d[o + 3] = 255;
  }
  if (blurPx) { const c = new Uint8ClampedArray(d); for (let y = blurPx; y < H - blurPx; y++) for (let x = blurPx; x < W - blurPx; x++) { let s = 0, n = 0; for (let dy = -blurPx; dy <= blurPx; dy++) for (let dx = -blurPx; dx <= blurPx; dx++) { s += c[((y + dy) * W + x + dx) * 4]; n++; } const o = (y * W + x) * 4; d[o] = d[o + 1] = d[o + 2] = s / n; } }
  return d;
}
const W = 640, H = 480;
const cases = {
  gerade: [{ x: 200, y: 40 }, { x: 440, y: 40 }, { x: 440, y: 440 }, { x: 200, y: 440 }],
  perspektive: [{ x: 230, y: 30 }, { x: 420, y: 50 }, { x: 470, y: 440 }, { x: 170, y: 420 }],
  gedreht: [{ x: 300, y: 30 }, { x: 460, y: 120 }, { x: 330, y: 420 }, { x: 150, y: 330 }],
};
let fail = 0;
for (const [name, quad] of Object.entries(cases)) {
  const rgba = scene(W, H, quad);
  const g = toGray(rgba, W, H, 160);
  const r = findReceipt(g);
  if (!r) { console.log(name, 'NICHT ERKANNT'); fail++; continue; }
  const found = r.corners.map(p => ({ x: p.x * W, y: p.y * H }));
  const err = quad.map((q, i) => Math.hypot(q.x - found[i].x, q.y - found[i].y));
  const maxErr = Math.max(...err);
  console.log(name, 'max Eckenabweichung px', maxErr.toFixed(1), 'Abdeckung', r.coverage.toFixed(2));
  if (maxErr > 10) { fail++; console.log('  FEHLER Ecken', found.map(p => `${p.x.toFixed(0)},${p.y.toFixed(0)}`).join(' ')); }
  // Geraderechnen: der Rand des Ergebnisses darf kein Hintergrund sein
  const size = outputSize(found); const out = warp(rgba, W, H, found, size.width, size.height);
  let dark = 0, n = 0;
  for (let y = 0; y < size.height; y++) for (let x = 0; x < size.width; x++) if (x < 3 || y < 3 || x >= size.width - 3 || y >= size.height - 3) { n++; if (out[(y * size.width + x) * 4] < 40) dark++; }
  console.log('  Ergebnis', size.width + 'x' + size.height, 'dunkle Randpixel', (100 * dark / n).toFixed(1) + '%'); if (dark / n > 0.05) { fail++; console.log('  FEHLER: Hintergrund im Rand'); }
}
// kein Beleg: nur Hintergrund
{ const rgba = scene(W, H, [{ x: 0, y: 0 }, { x: 1, y: 0 }, { x: 1, y: 1 }, { x: 0, y: 1 }]); const r = findReceipt(toGray(rgba, W, H, 160)); console.log('leer ->', r ? 'ERKANNT (falsch)' : 'null'); if (r) fail++; }
// Schaerfe: scharf vs unscharf
{ const q = cases.gerade; const a = toGray(scene(W, H, q), W, H, 320), b = toGray(scene(W, H, q, 45, 235, 3), W, H, 320);
  const sa = sharpness(a.gray, a.width, a.height), sb = sharpness(b.gray, b.width, b.height); console.log('Schaerfe scharf', sa.toFixed(0), 'unscharf', sb.toFixed(0)); if (!(sa > sb * 3)) fail++; }
// Bewegung
{ const a = toGray(scene(W, H, cases.gerade), W, H, 160).gray, b = toGray(scene(W, H, cases.perspektive), W, H, 160).gray; console.log('Bewegung gleich', motion(a, a).toFixed(2), 'anders', motion(a, b).toFixed(2)); if (!(motion(a, b) > 1)) fail++; }
// Beleg ueber den Rand (langer Bon): oben und unten abgeschnitten
{ const q = [{ x: 200, y: -50 }, { x: 440, y: -50 }, { x: 440, y: 540 }, { x: 200, y: 540 }]; const r = findReceipt(toGray(scene(W, H, q), W, H, 160)); console.log('Bon ueber Rand: ', r ? `erkannt, geht weiter oben=${r.continuesUp} unten=${r.continuesDown}` : 'NICHT ERKANNT'); if (!r || !r.continuesUp || !r.continuesDown) fail++; }
console.log(fail ? `FEHLER: ${fail}` : 'alles ok');
process.exit(fail ? 1 : 0);
