// Das Original neben der Auswertung: eine Folge von Seitenbildern mit Reitern, Breite/Zoom und dem Weg,
// die Datei selbst zu oeffnen. Das Bauteil kennt weder eine Seite noch den Server - wer es benutzt, gibt
// fertige Bildadressen herein (eine PDF-Seite ist dort schon ein Bild, siehe die Seiten-Route der Belege).
//
//   const viewer = createDocumentViewer();
//   host.append(viewer.element);
//   viewer.setSources([{ label: 'Seite 1', imageUrl: '/…/pages/1', openUrl: '/…/content' }]);
//
// source = { label, imageUrl, openUrl? }. Die Flaeche scrollt selbst (overflow), nie die Seite dahinter:
// ein Finger schiebt, zwei Finger zoomen, ein Doppeltipp wechselt zwischen Breite und Detail.
import { spriteHref } from './sprite.js';

const t = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('en') ? en : de;
const esc = value => String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
const MIN_ZOOM = 100;
const MAX_ZOOM = 400;
const DETAIL_ZOOM = 220;
const STEP = 1.25;
const icon = id => `<svg viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref(id)}"></use></svg>`;

export function createDocumentViewer() {
  const element = document.createElement('div');
  element.className = 'dv';
  element.innerHTML = `
    <div class="dv-bar">
      <div class="dv-tabs" role="tablist" aria-label="${esc(t('Seiten des Originals', 'Pages of the original'))}"></div>
      <div class="dv-tools">
        <button type="button" class="dv-tool" data-zoom="out" aria-label="${esc(t('Verkleinern', 'Zoom out'))}">${icon('ui-minus')}</button>
        <button type="button" class="dv-tool dv-fit" data-zoom="fit">${esc(t('Breite', 'Fit'))}</button>
        <button type="button" class="dv-tool" data-zoom="in" aria-label="${esc(t('Vergrößern', 'Zoom in'))}">${icon('ui-plus')}</button>
        <a class="dv-tool dv-open" target="_blank" rel="noopener" aria-label="${esc(t('Original in neuem Tab öffnen', 'Open original in a new tab'))}" hidden>${icon('ui-document')}</a>
      </div>
    </div>
    <div class="dv-stage" tabindex="0" role="region" aria-label="${esc(t('Original', 'Original'))}"><div class="dv-empty"></div></div>`;
  const tabs = element.querySelector('.dv-tabs');
  const stage = element.querySelector('.dv-stage');
  const openLink = element.querySelector('.dv-open');

  let sources = [];
  let current = 0;
  let zoom = MIN_ZOOM;
  const image = () => stage.querySelector('.dv-img');

  function applyZoom(next, focus) {
    const img = image();
    const clamped = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Math.round(next)));
    if (!img) { zoom = clamped; return; }
    const rect = stage.getBoundingClientRect();
    const fx = focus ? focus.x - rect.left : rect.width / 2;
    const fy = focus ? focus.y - rect.top : rect.height / 2;
    const ax = img.offsetWidth ? (stage.scrollLeft + fx) / img.offsetWidth : 0;
    const ay = img.offsetHeight ? (stage.scrollTop + fy) / img.offsetHeight : 0;
    zoom = clamped;
    img.style.width = `${zoom}%`;
    stage.classList.toggle('is-zoomed', zoom > MIN_ZOOM);
    stage.scrollLeft = ax * img.offsetWidth - fx;
    stage.scrollTop = ay * img.offsetHeight - fy;
  }

  function show(index) {
    if (!sources.length) {
      stage.innerHTML = `<div class="dv-empty">${esc(t('Kein Original vorhanden.', 'No original available.'))}</div>`;
      openLink.hidden = true;
      return;
    }
    current = Math.min(Math.max(0, index), sources.length - 1);
    const source = sources[current];
    tabs.querySelectorAll('[data-page]').forEach((tab, i) => tab.setAttribute('aria-selected', String(i === current)));
    openLink.hidden = !source.openUrl;
    if (source.openUrl) openLink.href = source.openUrl;
    stage.classList.add('is-loading');
    stage.innerHTML = `<img class="dv-img" alt="${esc(source.label)}" style="width:${zoom}%" draggable="false">`;
    const img = image();
    img.addEventListener('load', () => stage.classList.remove('is-loading'), { once: true });
    img.addEventListener('error', () => {
      stage.classList.remove('is-loading');
      stage.innerHTML = `<div class="dv-empty">${esc(t('Das Original lässt sich hier nicht anzeigen.', 'The original cannot be shown here.'))}</div>`;
    }, { once: true });
    img.src = source.imageUrl;
    stage.scrollTo(0, 0);
  }

  function setSources(next) {
    sources = next || [];
    tabs.innerHTML = sources.length > 1
      ? sources.map((source, i) => `<button type="button" role="tab" class="dv-tab" data-page="${i}" aria-selected="false">${esc(source.label)}</button>`).join('')
      : '';
    tabs.hidden = sources.length < 2;
    show(0);
  }

  tabs.addEventListener('click', event => {
    const tab = event.target.closest('[data-page]');
    if (tab) show(Number(tab.dataset.page));
  });
  element.querySelector('.dv-tools').addEventListener('click', event => {
    const button = event.target.closest('[data-zoom]');
    if (!button) return;
    const mode = button.dataset.zoom;
    applyZoom(mode === 'fit' ? MIN_ZOOM : mode === 'in' ? zoom * STEP : zoom / STEP);
  });

  // Doppeltipp: zwischen "ganze Breite" und Detail, an der Stelle, die man getippt hat.
  stage.addEventListener('dblclick', event => applyZoom(zoom > MIN_ZOOM ? MIN_ZOOM : DETAIL_ZOOM, { x: event.clientX, y: event.clientY }));
  // Trackpad-Zwicken kommt als Strg+Rad; ohne Strg scrollt das Rad die Flaeche.
  stage.addEventListener('wheel', event => {
    if (!event.ctrlKey) return;
    event.preventDefault();
    applyZoom(zoom * (event.deltaY < 0 ? 1.1 : 1 / 1.1), { x: event.clientX, y: event.clientY });
  }, { passive: false });

  // Zeiger: zwei Finger zoomen, die Maus zieht die Flaeche (ein Finger scrollt von selbst).
  const pointers = new Map();
  let pinch = null;
  let drag = null;
  const distance = () => { const [a, b] = [...pointers.values()]; return Math.hypot(a.x - b.x, a.y - b.y); };
  const middle = () => { const [a, b] = [...pointers.values()]; return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }; };
  stage.addEventListener('pointerdown', event => {
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pointers.size === 2) { pinch = { start: distance(), zoom }; drag = null; }
    else if (event.pointerType === 'mouse' && zoom > MIN_ZOOM) {
      drag = { x: event.clientX, y: event.clientY, left: stage.scrollLeft, top: stage.scrollTop };
      stage.setPointerCapture(event.pointerId);
      stage.classList.add('is-dragging');
    }
  });
  stage.addEventListener('pointermove', event => {
    if (!pointers.has(event.pointerId)) return;
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pinch && pointers.size === 2 && pinch.start > 0) applyZoom(pinch.zoom * distance() / pinch.start, middle());
    else if (drag) { stage.scrollLeft = drag.left - (event.clientX - drag.x); stage.scrollTop = drag.top - (event.clientY - drag.y); }
  });
  const release = event => {
    pointers.delete(event.pointerId);
    if (pointers.size < 2) pinch = null;
    if (drag) { drag = null; stage.classList.remove('is-dragging'); }
  };
  stage.addEventListener('pointerup', release);
  stage.addEventListener('pointercancel', release);

  return { element, setSources };
}
