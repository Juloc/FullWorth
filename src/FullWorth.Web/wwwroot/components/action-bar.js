// Die Aktionsleiste einer Seite: ihre Hauptaktion, weitere Aktionen und die Aktionen ihrer Auswahl.
//
// Sie gehoert der Seite, nicht der Huelle: die Seite bindet das Markup (Pages/Shared/_ActionBar.cshtml)
// in ihren Abschnitt "Actions" ein und befuellt es selbst. Die Huelle weiss nichts davon - sie stellt nur
// den Platz am Ende der Seite bereit.

import { spriteHref } from './sprite.js';

const GLYPH = Object.freeze({
  add: 'ui-plus',
  edit: 'ui-edit',
  upload: 'ui-upload',
  scan: 'ui-camera'
});

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, character =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
}

/**
 * Die Hauptaktion der Seite: Beschriftung, Art (bestimmt das Symbol) und was sie tut.
 * Das Symbol ist ein echtes SVG aus dem Sprite, kein CSS-content.
 */
export function setPrimaryAction(label, onClick, kind = 'add') {
  const button = document.getElementById('primary-action');
  if (!button) return;
  button.innerHTML = `<svg viewBox="0 0 24 24"><use href="${spriteHref(GLYPH[kind] || GLYPH.add)}"></use></svg>`
    + `<span class="btn-primary-label">${escapeHtml(label)}</span>`;
  button.onclick = onClick;
  button.hidden = false;
}
