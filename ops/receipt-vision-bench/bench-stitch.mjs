// Misst das Zusammensetzen langer Bons: kuenstliche Bons, von oben nach unten in ueberlappenden
// Aufnahmen "gefahren", jede Aufnahme erkannt, gerade gerechnet und mit der naechsten verbunden.
//
//   node ops/receipt-vision-bench/bench-stitch.mjs                    Seeds 1-20
//   node ops/receipt-vision-bench/bench-stitch.mjs --seeds 1001-1020  Pruefsatz
//   node ops/receipt-vision-bench/bench-stitch.mjs --shake 2          staerkeres Zittern
//   node ops/receipt-vision-bench/bench-stitch.mjs --save dir          die zusammengesetzten Bilder ansehen
//   node ops/receipt-vision-bench/bench-stitch.mjs --dump rows.json    jede Naht mit Wertung und Fehler aufschreiben
//
// Je Naht ist die wahre Ueberlappung aus der Geometrie bekannt. Zaehlt:
//   richtig angenommen  Naht gilt, Fehler <= 6 Pixel (bei 360 Pixel Breite: gut ein Viertel einer Textzeile)
//   FALSCH angenommen   Naht gilt, Fehler > 6 Pixel - das ist der Fall, der nicht vorkommen darf
//   zu Recht verworfen  Naht gilt nicht, und sie waere falsch gewesen
//   unnoetig verworfen  Naht gilt nicht, obwohl sie richtig gewesen waere (kostet nur Bequemlichkeit)
import { mkdirSync, writeFileSync } from 'node:fs';
import { renderStrip, pngOf } from './scene-lib.mjs';
import { loadModules } from './load.mjs';

const args = process.argv.slice(2);
const opt = (name, fallback) => { const i = args.indexOf(`--${name}`); return i >= 0 ? args[i + 1] : fallback; };
const [from, to] = opt('seeds', '1-20').split('-').map(Number);
const shake = Number(opt('shake', 1));
const saveDir = opt('save', null);
const dumpFile = opt('dump', null);
const rows = [];
const mods = await loadModules();
const vision = mods['receipt-vision'], stitch = mods['receipt-stitch'];
const W0 = 360;
const TOLERANCE = 6;
if (saveDir) mkdirSync(saveDir, { recursive: true });

const tally = { seams: 0, right: 0, wrong: 0, rightReject: 0, wastedReject: 0, noDetection: 0, frames: 0, sequences: 0, fullyStitched: 0, errorSum: 0, errorMax: 0 };
const wrongSeams = [];
for (let seed = from; seed <= to; seed++) {
  const strip = renderStrip(seed, { shake });
  tally.sequences++;
  const strips = [], truth = [];
  for (const frame of strip.frames) {
    tally.frames++;
    const found = vision.findReceipt(vision.toGray(frame.rgba, strip.W, strip.H, 160));
    if (!found) { tally.noDetection++; continue; }
    const quad = found.corners.map(p => ({ x: p.x * strip.W, y: p.y * strip.H }));
    const rect = vision.stripCorners(quad, found.continuesUp, found.continuesDown);
    const [tl, tr, br, bl] = rect;
    const widthPx = (Math.hypot(tr.x - tl.x, tr.y - tl.y) + Math.hypot(br.x - bl.x, br.y - bl.y)) / 2;
    const heightPx = (Math.hypot(bl.x - tl.x, bl.y - tl.y) + Math.hypot(br.x - tr.x, br.y - tr.y)) / 2;
    const height = Math.max(8, Math.round(heightPx * W0 / widthPx));
    strips.push({ rgba: vision.warp(frame.rgba, strip.W, strip.H, rect, W0, height), width: W0, height });
    // wahre Lage dieses Stuecks auf dem Bon: Mitte der oberen und unteren Kante des Rechtecks
    const top = frame.unproject((tl.x + tr.x) / 2, (tl.y + tr.y) / 2), bottom = frame.unproject((bl.x + br.x) / 2, (bl.y + br.y) / 2);
    truth.push({ top: top?.Y, bottom: bottom?.Y });
  }
  if (strips.length < 2) continue;
  const result = stitch.stitchStrips(strips);
  let allOk = true;
  result.seams.forEach((seam, i) => {
    const trueOverlap = (truth[i].bottom - truth[i + 1].top) * W0 / strip.wmm;
    const error = Math.abs(seam.overlap - trueOverlap);
    rows.push({ seed, seam: i, score: seam.score, margin: seam.margin, error, overlap: seam.overlap, trueOverlap });
    tally.seams++;
    if (seam.ok) {
      if (error <= TOLERANCE) { tally.right++; tally.errorSum += error; tally.errorMax = Math.max(tally.errorMax, error); }
      else { tally.wrong++; wrongSeams.push(`seed ${seed} naht ${i}: Fehler ${error.toFixed(1)} px, Wertung ${seam.score.toFixed(2)}, Abstand ${seam.margin.toFixed(2)}`); }
    } else {
      allOk = false;
      if (error <= TOLERANCE) tally.wastedReject++; else tally.rightReject++;
    }
  });
  if (allOk && result.segments.length === 1) tally.fullyStitched++;
  if (saveDir) result.segments.forEach((segment, i) => writeFileSync(`${saveDir}/strip-${seed}-${i}.png`, pngOf(segment.rgba, segment.width, segment.height)));
}

if (dumpFile) writeFileSync(dumpFile, JSON.stringify(rows));
const pct = (a, b) => b ? `${(100 * a / b).toFixed(0)} %` : '-';
console.log(`Bons ${tally.sequences}, Aufnahmen ${tally.frames} (ohne Erkennung: ${tally.noDetection}), Zittern ${shake}`);
console.log(`Naehte ${tally.seams}`);
console.log(`  richtig angenommen   ${tally.right}  (${pct(tally.right, tally.seams)}), mittlerer Fehler ${(tally.errorSum / Math.max(1, tally.right)).toFixed(2)} px, groesster ${tally.errorMax.toFixed(1)} px`);
console.log(`  FALSCH angenommen    ${tally.wrong}  (${pct(tally.wrong, tally.seams)})`);
console.log(`  zu Recht verworfen   ${tally.rightReject}`);
console.log(`  unnoetig verworfen   ${tally.wastedReject}`);
console.log(`Bons ganz zu einem Bild: ${tally.fullyStitched} von ${tally.sequences}`);
for (const line of wrongSeams.slice(0, 12)) console.log('  falsch:', line);
