// Ein Kontaktbogen: je Kategorie eine Szene mit ihrer Loesung (gruen) - zum Ansehen, nicht zum Messen.
//   node ops/receipt-vision-bench/sheet.mjs out.png [seed]
import { writeFileSync } from 'node:fs';
import { CATEGORIES, renderCategory, pngOf } from './scene-lib.mjs';

const out = process.argv[2] ?? 'sheet.png';
const seed = Number(process.argv[3] ?? 1);
const cols = 5, W = 640, H = 480, scale = 2;
const tw = W / scale, th = H / scale, rows = Math.ceil(CATEGORIES.length / cols);
const sheet = new Uint8ClampedArray(cols * tw * rows * th * 4);
CATEGORIES.forEach((category, index) => {
  const scene = renderCategory(category, seed, W, H);
  const ox = (index % cols) * tw, oy = Math.floor(index / cols) * th;
  for (let y = 0; y < th; y++) for (let x = 0; x < tw; x++) {
    const s = ((y * scale) * W + x * scale) * 4, d = ((oy + y) * cols * tw + ox + x) * 4;
    sheet[d] = scene.rgba[s]; sheet[d + 1] = scene.rgba[s + 1]; sheet[d + 2] = scene.rgba[s + 2]; sheet[d + 3] = 255;
  }
  const poly = scene.truth?.length ? scene.truth.map(p => ({ x: ox + p.x / scale, y: oy + p.y / scale })) : [];
  const drawn = pngOf(sheet, cols * tw, rows * th, []); // nur damit der Aufruf geprueft ist
  if (poly.length) {
    for (let i = 0; i < poly.length; i++) {
      const a = poly[i], b = poly[(i + 1) % poly.length], n = Math.ceil(Math.hypot(b.x - a.x, b.y - a.y));
      for (let k = 0; k <= n; k++) {
        const x = Math.round(a.x + (b.x - a.x) * k / n), y = Math.round(a.y + (b.y - a.y) * k / n), o = (y * cols * tw + x) * 4;
        if (x >= 0 && y >= 0 && x < cols * tw) { sheet[o] = 0; sheet[o + 1] = 230; sheet[o + 2] = 60; }
      }
    }
  }
  void drawn;
});
writeFileSync(out, pngOf(sheet, cols * tw, rows * th));
console.log('geschrieben:', out, `${cols * tw}x${rows * th}`);
