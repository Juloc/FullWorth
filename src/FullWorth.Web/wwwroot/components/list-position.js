// Die Listenposition über ein Neuzeichnen retten.
//
// Jede Seite zeichnet nach dem Speichern komplett neu (`innerHTML = ''` und wieder aufbauen). Der
// Browser setzt dabei jeden Scrollcontainer auf 0 zurück, und der Benutzer steht wieder ganz oben -
// nach jeder einzelnen Änderung.
//
// Zwei Container, nicht einer: oberhalb von 1023 px scrollt `.table-panel` selbst
// (`styles/components.css:95` setzt `overflow:auto`), darunter macht `styles/responsive.css:2` es
// `overflow:visible` und die SEITE scrollt. Wer nur `window.scrollY` sichert, repariert genau die
// halbe Anwendung.
//
// Bevorzugt wird der Anker: wenn die bearbeitete Zeile nach dem Neuzeichnen noch existiert, wird sie
// sichtbar gemacht statt eine Pixelzahl wiederherzustellen. Das hält auch dann, wenn die Liste
// unterwegs kürzer geworden ist - etwa weil die Zeile durch einen Filter herausgefallen ist.

const CONTAINER_SELECTOR = '.table-panel, [data-scroll-container]';

function scrollableContainers() {
  return [...document.querySelectorAll(CONTAINER_SELECTOR)]
    .filter(element => element.scrollHeight > element.clientHeight);
}

/**
 * Zeichnet neu und behält dabei die Position bei.
 *
 * @param {() => (void | Promise<void>)} render Das Neuzeichnen selbst.
 * @param {{anchor?: string}} [options] `anchor`: Selektor der Zeile, die sichtbar bleiben soll.
 */
export async function keepListPosition(render, options = {}) {
  const containers = scrollableContainers().map(element => ({ element, top: element.scrollTop }));
  const pageTop = window.scrollY;

  await render();

  // Ein Container, der beim Sichern gescrollt war, es aber nach dem Zeichnen nicht mehr sein kann,
  // wird stillschweigend übersprungen - `scrollTop` klemmt ohnehin auf das Maximum.
  for (const { element, top } of containers) {
    if (element.isConnected) element.scrollTop = top;
  }
  if (pageTop) window.scrollTo({ top: pageTop, behavior: 'instant' });

  if (!options.anchor) return;
  const row = document.querySelector(options.anchor);
  // `nearest` scrollt nur, wenn die Zeile wirklich außerhalb liegt - sonst bleibt alles stehen.
  if (row) row.scrollIntoView({ block: 'nearest', behavior: 'instant' });
}
