import { createDialog } from '../../components/dialog.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { loadBitmap } from './receipt-photo.js';

// Den Rand eines Bons von Hand auf dem Foto ziehen: vier Griffe, ein Viereck dazwischen.
//
// Gebraucht, wo die Erkennung nichts findet (Bon auf weissem Tisch, Schatten ueber dem Papier) oder um
// ein paar Pixel danebenliegt. Die Griffe sind Knoepfe: mit dem Finger oder der Maus ziehbar, mit den
// Pfeiltasten verschiebbar.

const KEY_STEP = 0.004;
const DISPLAY_SIDE = 1600;

/**
 * Zeigt das Foto mit dem Viereck `quad` (0..1; ohne Angabe ein Rechteck mit Rand). Antwort:
 * { quad } beim Uebernehmen, { original: true } bei "Original verwenden", null beim Abbrechen.
 */
export async function openReceiptAdjust(file, quad) {
  const bitmap = await loadBitmap(file);
  return new Promise(resolve => {
    const corners = (quad ?? [{ x: 0.12, y: 0.08 }, { x: 0.88, y: 0.08 }, { x: 0.88, y: 0.92 }, { x: 0.12, y: 0.92 }]).map(p => ({ ...p }));
    let answer = null;

    const dialog = createDialog(`<div class="dialog-card receipt-adjust">
      <div class="panel-head"><h2>${t('Rand des Belegs', 'Edge of the receipt')}</h2></div>
      <p class="row-sub">${t('Zieh die vier Punkte auf die Ecken des Papiers.', 'Drag the four points onto the corners of the paper.')}</p>
      <div class="receipt-adjust-stage" data-stage>
        <canvas></canvas>
      </div>
      <div class="dialog-actions">
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${t('Abbrechen', 'Cancel')}</button>
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-original>${t('Original verwenden', 'Use original')}</button>
        <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-apply>${t('Übernehmen', 'Apply')}</button>
      </div>
    </div>`, { className: 'receipt-adjust-dialog', closeLabel: t('Schließen', 'Close') });

    const stage = dialog.querySelector('[data-stage]');
    const canvas = stage.querySelector('canvas');
    // Das Bild behaelt sein Seitenverhaeltnis; die Buehne bekommt es von Anfang an, damit nichts springt.
    // Zum Zeigen reicht ein verkleinertes Bild (ein grosses Foto ueberschritte auf manchen Geraeten die Flaechengrenze des Canvas).
    const scale = Math.min(1, DISPLAY_SIDE / Math.max(bitmap.width, bitmap.height));
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    stage.style.aspectRatio = `${bitmap.width} / ${bitmap.height}`;

    const overlay = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    overlay.classList.add('receipt-adjust-frame');
    overlay.setAttribute('viewBox', '0 0 1 1');
    overlay.setAttribute('preserveAspectRatio', 'none');
    overlay.setAttribute('aria-hidden', 'true');
    const polygon = document.createElementNS('http://www.w3.org/2000/svg', 'polygon');
    overlay.append(polygon);
    stage.append(overlay);

    const names = [t('Ecke oben links', 'Top left corner'), t('Ecke oben rechts', 'Top right corner'), t('Ecke unten rechts', 'Bottom right corner'), t('Ecke unten links', 'Bottom left corner')];
    const handles = corners.map((corner, index) => {
      const handle = document.createElement('button');
      handle.type = 'button';
      handle.className = 'receipt-adjust-handle';
      handle.setAttribute('aria-label', names[index]);
      handle.addEventListener('pointerdown', event => {
        handle.setPointerCapture(event.pointerId);
        handle.dataset.dragging = 'true';
        event.preventDefault();
      });
      handle.addEventListener('pointermove', event => {
        if (handle.dataset.dragging !== 'true') return;
        const rect = stage.getBoundingClientRect();
        corner.x = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
        corner.y = Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height));
        place();
      });
      const release = () => { delete handle.dataset.dragging; };
      handle.addEventListener('pointerup', release);
      handle.addEventListener('pointercancel', release);
      handle.addEventListener('keydown', event => {
        const step = { ArrowLeft: [-KEY_STEP, 0], ArrowRight: [KEY_STEP, 0], ArrowUp: [0, -KEY_STEP], ArrowDown: [0, KEY_STEP] }[event.key];
        if (!step) return;
        corner.x = Math.min(1, Math.max(0, corner.x + step[0]));
        corner.y = Math.min(1, Math.max(0, corner.y + step[1]));
        place();
        event.preventDefault();
      });
      stage.append(handle);
      return handle;
    });

    function place() {
      polygon.setAttribute('points', corners.map(p => `${p.x},${p.y}`).join(' '));
      handles.forEach((handle, index) => {
        handle.style.left = `${corners[index].x * 100}%`;
        handle.style.top = `${corners[index].y * 100}%`;
      });
    }
    place();

    dialog.querySelector('[data-cancel]').addEventListener('click', () => dialog.close());
    dialog.querySelector('[data-original]').addEventListener('click', () => { answer = { original: true }; dialog.close(); });
    dialog.querySelector('[data-apply]').addEventListener('click', () => { answer = { quad: corners.map(p => ({ ...p })) }; dialog.close(); });
    dialog.addEventListener('close', () => { bitmap.close?.(); resolve(answer); });
    dialog.showModal();
  });
}

function t(de, en) { return document.documentElement.lang?.toLowerCase().startsWith('en') ? en : de; }
