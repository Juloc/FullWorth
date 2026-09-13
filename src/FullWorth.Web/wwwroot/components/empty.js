// Leerer Zustand.
//
// Dieselben zwei verschachtelten <div> standen an dreizehn Stellen, jedes mit seiner eigenen
// Maskierung der Meldung. Eine Kopie hätte sich jederzeit anders entwickeln können als die anderen
// zwölf, und genau das ist der Grund, warum es sie nicht mehr gibt.
//
// Das Laden und der Fehler wohnen nicht hier: eine Ladezeile hat die Form ihrer echten Zeile, damit
// beim Austausch nichts springt, und ein Fehler ist eine Meldung wie jede andere - beides gehört
// der Seite. Was allen gemeinsam ist, ist das Flimmern, und das ist .shimmer in components.css.

import { esc } from '../core/html.js';

export function emptyRow(message) {
  return `<div class="row state-empty"><div class="row-sub">${esc(message)}</div></div>`;
}
