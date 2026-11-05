// Lernt die Gewichte der Bewertung (MODEL in receipt-vision.js) an kuenstlichen Fotos.
//
//   node ops/receipt-vision-bench/learn.mjs dump --seeds 101-125 --out dump.json
//       zeichnet die Szenen, laesst alle Kandidaten bewerten und schreibt ihre Zahlen und ihre
//       Ueberdeckung mit der Loesung auf
//   node ops/receipt-vision-bench/learn.mjs fit dump.json --emit model.json
//       passt ein logistisches Modell an (Ziel: je besser die Ueberdeckung, desto naeher an 1), sucht die
//       Mindestwertung und schreibt MODEL-Werte
//
// Das Modell darf nie an den Szenen gemessen werden, an denen es gelernt hat: bench-detect --seeds
// 1001-1012 --model model.json nimmt Szenen, die hier nicht vorkamen.
import { readFileSync, writeFileSync } from 'node:fs';
import { CATEGORIES, renderCategory, overlap, clipToRect } from './scene-lib.mjs';
import { loadModules } from './load.mjs';

const [command, ...rest] = process.argv.slice(2);
const opt = (name, fallback) => { const i = rest.indexOf(`--${name}`); return i >= 0 ? rest[i + 1] : fallback; };
const W = 640, H = 480, WIDTH = 160;
const KEYS = ['edgeMean', 'edgeMin', 'contrast', 'texture', 'area', 'borders', 'known'];

if (command === 'dump') {
  const [from, to] = opt('seeds', '101-125').split('-').map(Number);
  const vision = (await loadModules())['receipt-vision'];
  const scenes = [];
  for (const category of CATEGORIES) {
    for (let seed = from; seed <= to; seed++) {
      const scene = renderCategory(category, seed, W, H);
      const candidates = vision.rateCandidates(vision.toGray(scene.rgba, W, H, WIDTH)).map(c => ({
        f: KEYS.map(key => c.features[key]),
        iou: scene.truth ? overlap(clipToRect(c.corners.map(p => ({ x: p.x * W / WIDTH, y: p.y * W / WIDTH })), W, H), scene.truth, W, H, 4) : 0
      }));
      scenes.push({ category, seed, candidates });
    }
    console.error(category, 'fertig');
  }
  writeFileSync(opt('out', 'dump.json'), JSON.stringify({ keys: KEYS, scenes }));
} else if (command === 'fit') {
  const dump = JSON.parse(readFileSync(rest[0], 'utf8'));
  const rows = [];
  for (const scene of dump.scenes) for (const c of scene.candidates) rows.push({ x: [1, ...c.f], t: Math.min(1, Math.max(0, (c.iou - 0.5) / 0.45)) ** 1.5 });
  const dim = KEYS.length + 1;
  let w = new Array(dim).fill(0);
  const lambda = 1e-3 * rows.length;
  for (let iteration = 0; iteration < 60; iteration++) {
    const grad = new Array(dim).fill(0);
    const hess = Array.from({ length: dim }, () => new Array(dim).fill(0));
    for (const { x, t } of rows) {
      const z = x.reduce((s, v, i) => s + v * w[i], 0), p = 1 / (1 + Math.exp(-z)), r = p * (1 - p) + 1e-6;
      for (let i = 0; i < dim; i++) { grad[i] += (p - t) * x[i]; for (let j = 0; j < dim; j++) hess[i][j] += r * x[i] * x[j]; }
    }
    for (let i = 1; i < dim; i++) { grad[i] += lambda * w[i] * 0.01; hess[i][i] += lambda * 0.01; }
    const step = solve(hess, grad);
    w = w.map((v, i) => v - step[i]);
  }
  const model = { threshold: 0.5, bias: w[0], weights: Object.fromEntries(KEYS.map((key, i) => [key, w[i + 1]])) };
  // Mindestwertung: die, bei der (gute Treffer - falsche Treffer) am groessten ist
  const best = dump.scenes.map(scene => {
    let top = null;
    for (const c of scene.candidates) {
      const z = model.bias + c.f.reduce((s, v, i) => s + v * w[i + 1], 0), score = 1 / (1 + Math.exp(-z));
      if (!top || score > top.score) top = { score, iou: c.iou };
    }
    return { category: scene.category, top };
  });
  let bestThreshold = 0.5, bestValue = -Infinity;
  for (let thr = 0.1; thr < 0.95; thr += 0.01) {
    let value = 0;
    for (const { category, top } of best) {
      if (!top || top.score < thr) continue;
      if (category === 'nosheet') value -= 2;
      else value += top.iou >= 0.85 ? 1 : -2;
    }
    if (value > bestValue) { bestValue = value; bestThreshold = thr; }
  }
  model.threshold = Number(bestThreshold.toFixed(2));
  for (const key of Object.keys(model.weights)) model.weights[key] = Number(model.weights[key].toFixed(3));
  model.bias = Number(model.bias.toFixed(3));
  console.log(JSON.stringify(model, null, 2));
  if (opt('emit', null)) writeFileSync(opt('emit', null), JSON.stringify(model));
} else {
  console.error('learn.mjs dump|fit ...');
  process.exit(1);
}

/** Loest A x = b (Gauss mit Spaltenwahl). */
function solve(A, b) {
  const n = b.length, M = A.map((row, i) => [...row, b[i]]);
  for (let c = 0; c < n; c++) {
    let p = c;
    for (let r = c + 1; r < n; r++) if (Math.abs(M[r][c]) > Math.abs(M[p][c])) p = r;
    [M[c], M[p]] = [M[p], M[c]];
    for (let r = c + 1; r < n; r++) { const f = M[r][c] / (M[c][c] || 1e-12); for (let k = c; k <= n; k++) M[r][k] -= f * M[c][k]; }
  }
  const x = new Array(n).fill(0);
  for (let r = n - 1; r >= 0; r--) { let s = M[r][n]; for (let k = r + 1; k < n; k++) s -= M[r][k] * x[k]; x[r] = s / (M[r][r] || 1e-12); }
  return x;
}
