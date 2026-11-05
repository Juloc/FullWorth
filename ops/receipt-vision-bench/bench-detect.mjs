// Misst, wie gut die Belegkamera den Bon im Bild findet - an kuenstlichen Fotos mit bekannter Loesung.
//
//   node ops/receipt-vision-bench/bench-detect.mjs                      alle Kategorien, Seeds 1-12, alle Quellen
//   node ops/receipt-vision-bench/bench-detect.mjs --seeds 1001-1012     Pruefsatz: nie zum Einstellen benutzt
//   node ops/receipt-vision-bench/bench-detect.mjs --det helligkeit,kanten,alle --cats crumple,skew
//   node ops/receipt-vision-bench/bench-detect.mjs --width 240           Breite des Analysebildes (Standard 160)
//   node ops/receipt-vision-bench/bench-detect.mjs --fails dir           Bilder der Fehlschlaege (gruen: Loesung, rot: Fund)
//
// gut = Ueberdeckung >= 0.93 (ein Zuschnitt, den man nicht nachbessern muesste), ok >= 0.85,
// falsch = etwas gefunden, aber nicht der Bon, verfehlt = nichts gefunden.
// Fuer "nosheet" (kein Bon im Bild) zaehlt jede Antwort als Fehlalarm.
import { mkdirSync, writeFileSync } from 'node:fs';
import { CATEGORIES, renderCategory, overlap, clipToRect, pngOf } from './scene-lib.mjs';
import { loadModules } from './load.mjs';

const args = process.argv.slice(2);
const opt = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : fallback; };
const [from, to] = opt('seeds', '1-12').split('-').map(Number);
const cats = opt('cats', CATEGORIES.join(',')).split(',');
const detNames = opt('det', 'alle').split(',');
const width = Number(opt('width', 160));
const failsDir = opt('fails', null);
const W = 640, H = 480;

const vision = (await loadModules())['receipt-vision'];
// Eine Erkennung ist eine Auswahl der Kandidatenquellen: "helligkeit", "textur", "kanten", mit "+" verbunden;
// "alle" ist die Auswahl der Kamera.
const modelFile = opt('model', null);
const model = modelFile ? (await import('node:fs')).default.readFileSync(modelFile, 'utf8') : null;
const detectors = {};
for (const name of detNames) {
  const sources = name === 'alle' ? vision.SOURCES : name.split('+');
  for (const source of sources) if (!vision.SOURCES.includes(source)) throw new Error(`unbekannte Quelle: ${source}`);
  const options = { sources, ...(model ? { model: JSON.parse(model) } : {}) };
  detectors[name] = rgba => vision.findReceipt(vision.toGray(rgba, W, H, width), options)?.corners ?? null;
}

const stats = {};
for (const det of detNames) for (const cat of cats) stats[`${det}/${cat}`] = { n: 0, good: 0, ok: 0, wrong: 0, miss: 0, iou: 0, ms: 0 };
if (failsDir) mkdirSync(failsDir, { recursive: true });

for (const cat of cats) {
  for (let seed = from; seed <= to; seed++) {
    const scene = renderCategory(cat, seed, W, H);
    for (const det of detNames) {
      const t0 = performance.now();
      const corners = detectors[det](scene.rgba);
      const ms = performance.now() - t0;
      const row = stats[`${det}/${cat}`];
      row.n++; row.ms += ms;
      const found = corners ? clipToRect(corners.map(p => ({ x: p.x * W, y: p.y * H })), W, H) : null;
      if (cat === 'nosheet') { if (found?.length) row.wrong++; else row.good++; continue; }
      if (!found?.length) { row.miss++; save(scene, det, cat, seed, null); continue; }
      const iou = overlap(found, scene.truth, W, H, 2);
      row.iou += iou;
      if (iou >= 0.93) row.good++; else if (iou >= 0.85) row.ok++; else { row.wrong++; save(scene, det, cat, seed, found); }
    }
  }
}

function save(scene, det, cat, seed, found) {
  if (!failsDir) return;
  const draw = [{ poly: scene.truth, color: [0, 230, 60] }];
  if (found) draw.push({ poly: found, color: [255, 40, 40] });
  writeFileSync(`${failsDir}/${det}-${cat}-${seed}.png`, pngOf(scene.rgba, W, H, draw));
}

const pad = (v, n) => String(v).padEnd(n), num = (v, n) => String(v).padStart(n);
for (const det of detNames) {
  console.log(`\n${det}  (Analysebild ${width} px breit, Seeds ${from}-${to})`);
  console.log(pad('Kategorie', 11) + num('n', 4) + num('gut', 5) + num('ok', 4) + num('falsch', 7) + num('verfehlt', 9) + num('IoU', 7) + num('ms', 7));
  const total = { n: 0, good: 0, ok: 0, wrong: 0, miss: 0, iou: 0, ms: 0 };
  for (const cat of cats) {
    const r = stats[`${det}/${cat}`];
    const scored = cat === 'nosheet' ? 0 : r.n - r.miss;
    console.log(pad(cat, 11) + num(r.n, 4) + num(r.good, 5) + num(r.ok, 4) + num(r.wrong, 7) + num(r.miss, 9) + num(cat === 'nosheet' ? '-' : (scored ? (r.iou / r.n).toFixed(2) : '0.00'), 7) + num((r.ms / r.n).toFixed(1), 7));
    if (cat !== 'nosheet') for (const key of Object.keys(total)) total[key] += r[key];
  }
  console.log(pad('mit Bon', 11) + num(total.n, 4) + num(total.good, 5) + num(total.ok, 4) + num(total.wrong, 7) + num(total.miss, 9) + num((total.iou / Math.max(1, total.n)).toFixed(2), 7) + num((total.ms / Math.max(1, total.n)).toFixed(1), 7));
  if (cats.includes('nosheet')) console.log(`Fehlalarme ohne Bon: ${stats[`${det}/nosheet`].wrong} von ${stats[`${det}/nosheet`].n}`);
}
