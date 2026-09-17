// "Mehrere Zeilen auswaehlen, dann etwas damit tun" gab es siebenfach von Hand: die ING-Kontoauswahl,
// die Sammlungen-Kandidaten, der Beleg-Massenimport, die Vertrags-Zusammenfuehrung, die
// Transfer-Paarerkennung, die Investment-Buchungsvorschlaege und die mobile Mehrfachauswahl in den
// Buchungen. Jede Stelle hat ihre eigene Checkbox-Zeile, ihr eigenes "Alle auswaehlen" (mal mit,
// mal ohne indeterminate-Zustand), ihren eigenen "{n}/{total}"-Zaehler neu erfunden - und an zwei
// Stellen (Sammlungen, Vertragszusammenfuehrung) fehlte "Alle auswaehlen" und der Zaehler ganz.
//
// Was dieser Baustein NICHT tut: er entscheidet nie, was mit der Auswahl passiert. Jeder Aufrufer hat
// andere Aktionen (Uebernehmen/Abbrechen, Zusammenfuehren/Abbrechen, Annehmen/Verwerfen) mit anderen
// Serveraufrufen dahinter - eine Aktionsleiste hier haette entweder nur den einfachsten Fall gedeckt
// oder waere zu einer zweiten Verzweigung ueber alle sieben Faelle geworden. Der Baustein liefert nur
// den Auswahlzustand (welche Ids sind an) und, wenn gewuenscht, das Markup fuer Zeilen plus
// "Alle auswaehlen" und Zaehler; der Knopf, der die Auswahl abschickt, gehoert der aufrufenden Seite,
// die ihn mit buttonClass(ButtonRole.X) selbst baut und getSelectedIds() liest.
//
// Zwei getrennt nutzbare Teile, weil die sieben echten Faelle in zwei Formen zerfallen:
//
// 1) "Ich habe eine Liste von Kandidaten, baue mir Zeilen daraus" (ING-Kontoauswahl, Sammlungen,
//    Beleg-Vorschau, Vertrags-Zusammenfuehrung, Transfer-Paare, Investment-Buchungsvorschlaege) -
//    dafuer erzeugt selectionListHtml() reines Markup, ohne Zustand, das die aufrufende Seite in ihr
//    eigenes Dialog-/Karten-Markup einsetzt (String rein, String raus - wie wizardActions()).
// 2) "Ich habe schon eine Tabelle mit eigenen Zeilen, jede traegt bereits eine Checkbox" (die
//    Buchungsliste: Datum, Kategorie, Konto, Betrag - die Auswahl-Checkbox ist nur eine Zelle von
//    vielen, die dieser Baustein nicht mitreden darf). Dafuer bindet createSelectionList().bindRow()
//    eine einzelne, schon vorhandene Checkbox an, ohne je zu wissen, was sonst noch in der Zeile steht.
//
// Beide Formen teilen sich denselben Zustand (createSelectionList()), weil das Problem in beiden
// Faellen identisch ist: eine Menge ausgewaehlter Ids, ein Gesamtbestand, ein "Alle
// auswaehlen"-Kontrollkaestchen mit korrektem indeterminate, ein Zaehler, eine Benachrichtigung bei
// Aenderung. mount() ist die Kurzform fuer Fall 1 (sucht [data-select-item]/[data-select-all]/
// [data-selection-count] selbst); bindRow() ist der Baustein, aus dem mount() besteht, direkt nutzbar
// fuer Fall 2.

/**
 * Reines Markup, kein Zustand - wie wizardActions() in wizard.js. `items[].html` kommt bereits
 * escaped von der aufrufenden Seite (dieser Baustein escaped nichts selbst, siehe wizard.js fuer die
 * Begruendung desselben Musters); `items[].id` landet unveraendert im `value`-Attribut, weil jede der
 * sieben Stellen dafuer bereits eine serverseitige Guid/Id verwendet, nie freien Text.
 *
 * @param {{id:string, html:string, selected?:boolean, disabled?:boolean, rowClass?:string}[]} items
 *   items[].rowClass haengt sich an das gemeinsame rowClass an (z.B. ein Beleg-Dokument, das schon
 *   importiert wurde, braucht zusaetzlich ".imported" fuer sein gedimmtes Aussehen - nur diese eine
 *   Zeile, nicht die ganze Liste).
 * @param {{
 *   rowClass?: string,          // Klasse(n) des <label> je Zeile - variiert bewusst pro Aufrufer
 *                                // (ING: "check ing-select-row", Transfer-Paare: "check
 *                                // candidate-row", ...), weil das Zeilenlayout (Betrag rechts,
 *                                // Badge-Zeile, ...) Sache der Seite bleibt, nicht dieses Bausteins.
 *   rowWrapClass?: string,      // NUR gesetzt, wenn die Seite ihr <label> bereits in einen eigenen
 *                                // Rahmen packt (die ING-Auswahl tat das immer schon: ein ".row" mit
 *                                // Trennlinie/Abstand UM das ".check"-Label herum, zwei Klassen mit
 *                                // zwei verschiedenen Aufgaben - Grid-Aussenlayout aussen,
 *                                // Flex-Checkbox-Ausrichtung innen. Beide auf ein Element zu legen
 *                                // wuerde die display-Deklaration der einen die der anderen
 *                                // ueberschreiben lassen. Ohne dieses Feld ist das <label> selbst das
 *                                // Zeilenelement, wie es die uebrigen sechs Faelle schon taten.
 *   rowsClass?: string,         // ERSETZT die Klasse des umschliessenden Containers (Vorgabe "rows").
 *                                // Sechs der sieben Stellen hatten schon einen eigenen Container mit
 *                                // eigenem Layout (".contract-merge-options" ist selbst
 *                                // "display:grid", ".receipt-import-docs" ist eine schlichte
 *                                // Scrollbox ohne Grid) - "rows" zusaetzlich anzuhaengen wuerde dort
 *                                // eine zweite, widerspruechliche Layout-Deklaration einschleusen. Nur
 *                                // die ING-Liste WILL "rows" behalten und haengt es sich selbst wieder
 *                                // an ("rows ing-select-list").
 *   selectAllLabel?: string,    // bereits escaped; ohne dieses Feld entsteht KEIN "Alle
 *                                // auswaehlen"-Kopf - Vertrags-Zusammenfuehrung und die
 *                                // Investment-Buchungsvorschlaege brauchen ihn nicht und sollen ihn
 *                                // nicht bekommen, nur weil der Baustein ihn anbieten koennte.
 *   emptyHtml?: string
 * }} [options]
 * @returns {string}
 */
export function selectionListHtml(items, options = {}) {
  const rowClass = options.rowClass || 'row check';
  // "rows" traegt jede der sieben Listen; eine zusaetzliche Klasse (z.B. die scrollbare Hoehe der
  // ING-Kontoliste, ".ing-select-list") bleibt Sache der Seite, die sie schon in ihrem eigenen
  // page.css definiert hat - der Baustein haengt sie nur an, statt sie zu kennen.
  const rowsClass = options.rowsClass || 'rows';
  const head = options.selectAllLabel
    ? '<div class="row"><label class="check"><input type="checkbox" data-select-all>'
      + '<span>' + options.selectAllLabel + '</span></label>'
      + '<span class="row-sub" data-selection-count></span></div>'
    : '';
  const rows = items.map(item => {
    const cls = item.rowClass ? rowClass + ' ' + item.rowClass : rowClass;
    const label = '<label class="' + cls + '"><input type="checkbox" data-select-item value="' + item.id + '"'
      + (item.selected ? ' checked' : '') + (item.disabled ? ' disabled' : '') + '>' + item.html + '</label>';
    return options.rowWrapClass ? '<div class="' + options.rowWrapClass + '">' + label + '</div>' : label;
  }).join('');
  return head + '<div class="' + rowsClass + '">' + rows + '</div>' + (items.length ? '' : (options.emptyHtml || ''));
}

/**
 * Der Auswahlzustand: eine Menge ausgewaehlter Ids, ein Gesamtbestand, Benachrichtigung bei jeder
 * Aenderung. Kennt weder Markup noch eine bestimmte Seite (components/ kennt weder eine Seite noch den
 * Server, siehe CLAUDE.md) - alles hier ist reine Buchhaltung ueber Ids, kein einziges esc(), kein
 * fetch().
 *
 * @param {{onChange?: (state:{selectedIds:string[], count:number, total:number}) => void}} [options]
 */
export function createSelectionList(options = {}) {
  const pool = new Set();           // aktueller Bestand auswaehlbarer Ids (Grundgesamtheit fuer total)
  const selected = new Set();       // Teilmenge von pool, die gerade ausgewaehlt ist
  const boundInputs = new Map();    // id -> die eine echte Checkbox, die diese Id im DOM vertritt
  const listeners = new Set();
  if (options.onChange) listeners.add(options.onChange);
  let unmountHead = null;

  function notify() {
    const detail = { selectedIds: [...selected], count: selected.size, total: pool.size };
    for (const cb of listeners) cb(detail);
  }

  // Programmatische Aenderung (Zeile antippen, "Alle auswaehlen", Long-Press): setzt den Zustand UND,
  // falls eine echte Checkbox an diese Id gebunden ist, deren .checked - inklusive eines dispatch'ten
  // change-Events. Das ist dasselbe Prinzip, das vorher direkt in mobile-interactions.js stand
  // (Checkbox setzen + change feuern, weil .checked=... selbst kein Event ausloest): eine Seite, die
  // auf das change-Event der eigenen Checkbox hoert (Zeilenfarbe, aria-selected), bekommt eine
  // programmatische Auswahl genauso mitgeteilt wie einen echten Klick - ohne dass dieser Baustein
  // weiss, was die Seite mit dem Event vorhat.
  function setOne(id, value, { silent = false } = {}) {
    const changed = selected.has(id) !== value;
    if (changed) { if (value) selected.add(id); else selected.delete(id); }
    const input = boundInputs.get(id);
    if (input && input.checked !== value) {
      input.checked = value;
      input.dispatchEvent(new Event('change', { bubbles: true }));
      // Der dispatch oben ruft ueber den in bindRow() registrierten Listener sofort wieder hier herein
      // - aber mit input.checked bereits auf `value`, also changed=false und kein zweiter Sprung in
      // diesen Zweig (input.checked !== value ist dann falsch). Ein normales, endliches Echo.
    }
    if (changed && !silent) notify();
  }

  const api = {
    getSelectedIds: () => [...selected],
    isSelected: id => selected.has(id),
    setSelected: (id, value) => setOne(id, value),
    toggle: id => setOne(id, !selected.has(id)),

    // Kein change-Event je Zeile: das deckt sich mit dem, was vorher an jeder "Alle
    // auswaehlen"-Stelle von Hand da stand (Checkboxen direkt gesetzt, einmal am Ende neu gezaehlt),
    // nicht mit einem Klick auf jede einzelne Zeile.
    selectAll(value = true) {
      let changed = false;
      for (const id of pool) {
        if (selected.has(id) !== value) { changed = true; if (value) selected.add(id); else selected.delete(id); }
        const input = boundInputs.get(id);
        if (input && input.checked !== value) input.checked = value;
      }
      if (changed) notify();
    },

    get count() { return selected.size; },
    get total() { return pool.size; },

    // Leert Bestand, Auswahl und Bindungen, OHNE die angemeldeten onChange-Abonnenten zu verlieren -
    // die Buchungsliste ruft das vor jedem kompletten Neuaufbau der Tabelle auf (dieselbe Stelle, an
    // der vorher `selectedForCoach.clear()` stand), damit eine Id aus der alten Seite nicht in der
    // total-Zaehlung der neuen haengen bleibt.
    reset() {
      pool.clear(); selected.clear(); boundInputs.clear();
      notify();
    },

    // Bindet eine EINZELNE, bereits vorhandene Checkbox an - der Fall, in dem eine Zeile viel mehr ist
    // als eine Checkbox (die Buchungstabelle: Datum-, Kategorie-, Konto-, Betragszelle) und dieser
    // Baustein diese Zeile nicht rendern darf, weil er sie nicht kennt. Der Anfangszustand kommt VOM
    // Input (nicht umgekehrt): bei mount() traegt der Input schon das checked aus selectionListHtml()
    // (item.selected), bei einer frisch angehaengten Tabellenzeile ist er einfach leer - in beiden
    // Faellen ist das, was im DOM steht, die Wahrheit, nicht ein Anfangswert dieses Bausteins.
    bindRow(input, id) {
      pool.add(id);
      if (input.checked) selected.add(id); else selected.delete(id);
      boundInputs.set(id, input);
      input.addEventListener('change', () => setOne(id, input.checked));
    },

    // Kurzform fuer Fall 1 (siehe Dateikopf): sucht die von selectionListHtml() erzeugten
    // Data-Attribute innerhalb von root selbst, statt dass jeder Aufrufer denselben
    // querySelectorAll/indeterminate/Zaehler-Code noch einmal von Hand schreibt (das war exakt der
    // Code, der in openIngSelection() vor dieser Umstellung stand).
    //
    // @param {HTMLElement} root - Element, das die Ausgabe von selectionListHtml() bereits enthaelt
    // @param {{counterFormat?: (n:number, total:number) => string}} [mountOptions] - counterFormat
    //   bekommt das SELBE {n}/{total}-Platzhalterschema wie die uebrigen Sprachdateien dieser App
    //   (siehe bankingSetup.ingSelectedCount) und liefert den fertigen, bereits ersetzten Text.
    mount(root, mountOptions = {}) {
      if (unmountHead) { unmountHead(); unmountHead = null; }
      pool.clear(); selected.clear(); boundInputs.clear();

      // disabled-Zeilen (z.B. ein bereits importiertes Beleg-Dokument) zaehlen bewusst nicht mit -
      // weder zur Grundgesamtheit noch zur Auswahl - sonst zeigte "Alle auswaehlen" nie den Zustand
      // "alles ausgewaehlt", solange auch nur eine nicht waehlbare Zeile in der Liste stand.
      root.querySelectorAll('[data-select-item]:not(:disabled)').forEach(input => api.bindRow(input, input.value));

      const master = root.querySelector('[data-select-all]');
      const counterEl = root.querySelector('[data-selection-count]');
      if (master) master.addEventListener('change', () => api.selectAll(master.checked));

      function refreshHead() {
        if (master) {
          master.checked = api.count > 0 && api.count === api.total;
          master.indeterminate = api.count > 0 && api.count < api.total;
        }
        if (counterEl && mountOptions.counterFormat) counterEl.textContent = mountOptions.counterFormat(api.count, api.total);
      }
      unmountHead = api.onChange(refreshHead);
      notify(); // einmalig: informiert Kopfzeile UND jeden aeusseren Abonnenten (z.B. ein Uebernehmen-Knopf) ueber den frisch gebundenen Bestand
      return api;
    },

    onChange(cb) { listeners.add(cb); return () => listeners.delete(cb); }
  };
  return api;
}
