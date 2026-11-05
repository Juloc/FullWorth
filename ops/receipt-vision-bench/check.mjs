// Schnelle Probe, ob die Belegkamera noch so gut erkennt und zusammensetzt wie gemessen:
//
//   node ops/receipt-vision-bench/check.mjs      (etwa eine halbe Minute, Ende mit Fehlercode, wenn etwas nachgelassen hat)
//
// Die Szenen sind Seeds, die nie zum Einstellen benutzt wurden. Die Grenzen liegen mit Abstand unter den
// gemessenen Werten (siehe README): die Probe soll eine Verschlechterung melden, nicht Rauschen.
import { CATEGORIES, renderCategory, renderStrip, overlap, clipToRect } from './scene-lib.mjs';
import { loadModules } from './load.mjs';

const mods = await loadModules();
const vision = mods['receipt-vision'], stitch = mods['receipt-stitch'];
const failures = [];
const expect = (ok, text) => { console.log(`${ok ? 'ok     ' : 'FEHLER '} ${text}`); if (!ok) failures.push(text); };

// --- Erkennung
const W = 640, H = 480;
let withSheet = 0, good = 0, wrong = 0, falseAlarms = 0, sheetless = 0;
for (const category of CATEGORIES) {
  for (const seed of [5001, 5002]) {
    const scene = renderCategory(category, seed, W, H);
    const found = vision.findReceipt(vision.toGray(scene.rgba, W, H, 160));
    if (category === 'nosheet') { sheetless++; if (found) falseAlarms++; continue; }
    withSheet++;
    if (!found) continue;
    const polygon = clipToRect(found.corners.map(p => ({ x: p.x * W, y: p.y * H })), W, H);
    if (overlap(polygon, scene.truth, W, H, 2) >= 0.85) good++; else wrong++;
  }
}
expect(good / withSheet >= 0.8, `Erkennung: ${good} von ${withSheet} Bons sauber gefunden (Grenze 80 %)`);
expect(wrong / withSheet <= 0.1, `Erkennung: ${wrong} von ${withSheet} falsch (Grenze 10 %)`);
expect(falseAlarms === 0, `Erkennung: ${falseAlarms} von ${sheetless} Fehlalarme ohne Bon (Grenze 0)`);

// --- Zusammensetzen
const W0 = 360;
let seams = 0, accepted = 0, bad = 0, splitSum = 0, splitMax = 0;
for (const seed of [5101, 5102, 5103]) {
  const strip = renderStrip(seed);
  const strips = [], truth = [];
  for (const frame of strip.frames) {
    const found = vision.findReceipt(vision.toGray(frame.rgba, strip.W, strip.H, 160));
    if (!found) continue;
    const rect = vision.stripCorners(found.corners.map(p => ({ x: p.x * strip.W, y: p.y * strip.H })), found.continuesUp, found.continuesDown);
    const [tl, tr, br, bl] = rect;
    const widthPx = (Math.hypot(tr.x - tl.x, tr.y - tl.y) + Math.hypot(br.x - bl.x, br.y - bl.y)) / 2;
    const heightPx = (Math.hypot(bl.x - tl.x, bl.y - tl.y) + Math.hypot(br.x - tr.x, br.y - tr.y)) / 2;
    const height = Math.max(8, Math.round(heightPx * W0 / widthPx));
    strips.push({ rgba: vision.warp(frame.rgba, strip.W, strip.H, rect, W0, height), width: W0, height });
    truth.push({ top: frame.unproject((tl.x + tr.x) / 2, (tl.y + tr.y) / 2)?.Y, bottom: frame.unproject((bl.x + br.x) / 2, (bl.y + br.y) / 2)?.Y });
  }
  const joined = stitch.stitchStrips(strips);
  // Geteilt wird ohne Verlust und ohne Doppeltes: die Seiten ergeben zusammen wieder genau das lange Bild
  for (const segment of joined.segments) {
    const pages = stitch.splitIntoPages(segment);
    splitSum += pages.reduce((s, page) => s + page.height, 0) - segment.height;
    splitMax = Math.max(splitMax, ...pages.map(page => page.height / page.width));
  }
  joined.seams.forEach((seam, i) => {
    seams++;
    if (!seam.ok) return;
    accepted++;
    if (Math.abs(seam.overlap - (truth[i].bottom - truth[i + 1].top) * W0 / strip.wmm) > 6) bad++;
  });
}
expect(bad === 0, `Zusammensetzen: ${bad} von ${accepted} angenommenen Naehten falsch (Grenze 0)`);
expect(accepted / seams >= 0.75, `Zusammensetzen: ${accepted} von ${seams} Naehten angenommen (Grenze 75 %)`);

expect(splitSum === 0, `Teilen: Seitenhoehen weichen um ${splitSum} Zeilen vom langen Bild ab (Grenze 0)`);
expect(splitMax <= 1.6 * 1.15 + 1e-9, `Teilen: hoechste Seite ${splitMax.toFixed(2)} x ihre Breite (Grenze ${(1.6 * 1.15).toFixed(2)})`);

process.exit(failures.length ? 1 : 0);
