import { toGray, findReceipt, outputSize, warp } from './receipt-vision.js';

// Fotos, die nicht aus der Belegkamera kommen (Kamera-App, Galerie): den Bon finden und gerade rechnen.
//
// Die Kamera-App des Geraets hat die bessere Optik und den Autofokus - das ist der Grund, sie zu nehmen -,
// und trotzdem soll der Bon ohne Tisch und ohne Schraege im Scan-Set ankommen. Dieselbe Erkennung wie in
// der Belegkamera, nur auf einem fertigen Foto. Findet sie nichts, bleibt das Foto, wie es ist, und der
// Benutzer kann den Rand von Hand ziehen (receipt-adjust.js).

const ANALYSIS_WIDTH = 320;
const MAX_SOURCE_SIDE = 4096;
const MAX_OUTPUT_SIDE = 3200;
// Die Erkennung liegt auf ein, zwei Pixel des kleinen Analysebildes genau; ein schmaler Rand mehr kostet nichts,
// ein abgeschnittener Rand eine Zeile Text.
const GROW = 0.012;

export function isImage(file) {
  return String(file?.type || '').startsWith('image/') || /\.(jpe?g|png|webp|heic|heif)$/i.test(file?.name || '');
}

/** Das Foto als Bild, mit der Drehung aus seinen Exif-Angaben. */
export async function loadBitmap(file) {
  return createImageBitmap(file, { imageOrientation: 'from-image' });
}

/**
 * Sucht den Bon im Foto. Ergebnis: { quad, score } mit den Ecken (0..1, oben links, oben rechts, unten rechts,
 * unten links) oder null.
 */
export function detectReceipt(bitmap) {
  const width = ANALYSIS_WIDTH;
  const height = Math.max(1, Math.round(width * bitmap.height / bitmap.width));
  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;
  const context = canvas.getContext('2d', { willReadFrequently: true });
  context.drawImage(bitmap, 0, 0, width, height);
  const found = findReceipt(toGray(context.getImageData(0, 0, width, height).data, width, height, width / 2));
  return found ? { quad: grow(found.corners), score: found.score } : null;
}

/** Das Viereck `quad` (0..1) aus dem Foto als gerades JPEG. */
export async function cropQuad(bitmap, quad, quality = 0.9) {
  const scale = Math.min(1, MAX_SOURCE_SIDE / Math.max(bitmap.width, bitmap.height));
  const width = Math.max(1, Math.round(bitmap.width * scale)), height = Math.max(1, Math.round(bitmap.height * scale));
  const source = document.createElement('canvas');
  source.width = width;
  source.height = height;
  const context = source.getContext('2d', { willReadFrequently: true });
  context.drawImage(bitmap, 0, 0, width, height);
  const corners = quad.map(p => ({ x: p.x * width, y: p.y * height }));
  const size = outputSize(corners, MAX_OUTPUT_SIDE);
  const pixels = warp(context.getImageData(0, 0, width, height).data, width, height, corners, size.width, size.height);
  const output = document.createElement('canvas');
  output.width = size.width;
  output.height = size.height;
  output.getContext('2d').putImageData(new ImageData(pixels, size.width, size.height), 0, 0);
  return new Promise((resolve, reject) => output.toBlob(blob => blob ? resolve(blob) : reject(new Error('JPEG')), 'image/jpeg', quality));
}

/** Ein Foto fuer das Scan-Set vorbereiten: gerade gerechnet, wenn der Bon gefunden wurde. */
export async function preparePhoto(file) {
  if (!isImage(file)) return { file, original: file, quad: null, straightened: false };
  let bitmap;
  try { bitmap = await loadBitmap(file); } catch { return { file, original: file, quad: null, straightened: false }; }
  try {
    const found = detectReceipt(bitmap);
    if (!found) return { file, original: file, quad: null, straightened: false };
    return { file: await fileFromQuad(bitmap, found.quad, file.name), original: file, quad: found.quad, straightened: true };
  } finally {
    bitmap.close?.();
  }
}

export async function fileFromQuad(bitmap, quad, originalName) {
  const base = String(originalName || 'beleg').replace(/\.[^.]+$/, '');
  return new File([await cropQuad(bitmap, quad)], `${base}-gerade.jpg`, { type: 'image/jpeg' });
}

/** Ecken ein Stueck nach aussen schieben, vom Mittelpunkt weg, und im Bild halten. */
function grow(corners) {
  const cx = corners.reduce((s, p) => s + p.x, 0) / 4, cy = corners.reduce((s, p) => s + p.y, 0) / 4;
  return corners.map(p => ({
    x: Math.min(1, Math.max(0, p.x + (p.x - cx) * GROW)),
    y: Math.min(1, Math.max(0, p.y + (p.y - cy) * GROW))
  }));
}
