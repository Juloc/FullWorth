// Duenne Schicht ueber createDialog() (siehe dialog.js): dieser Baustein verwaltet nur, was allen
// mehrschrittigen Assistenten dieser App gemeinsam ist - einen Koerper, in den ein Schritt sein
// Markup schreibt, die "N / M"-Anzeige, und die Sperre der Navigationsknoepfe waehrend ein Schritt
// etwas Asynchrones tut (API-Aufruf, Validierung). Kopf, Schliessen-Knopf und Handy-Wischgeste
// bleiben Sache von createDialog() - wizard.js ruehrt sie nicht an und baut sie nicht nach.
//
// components/ kennt weder eine Seite noch den Server (siehe CLAUDE.md): dieser Baustein macht selbst
// keinen einzigen fetch()-Aufruf und weiss nichts von Bankverbindungen, KI-Zugang oder
// Vermoegenswerten. Welcher API-Aufruf passiert, was validiert wird und was ein Schritt anzeigt,
// bleibt bei der aufrufenden Seite - sie liefert fertiges HTML (und escaped darin selbst, mit ihrem
// eigenen esc()) an render().
//
// Warum kein `createWizard({ steps: [...] })` mit einer festen Schrittliste: von den sechs echten
// Ablaeufen, fuer die dieser Baustein gebaut wurde, ist nur einer wirklich linear mit fester
// Schrittzahl (die Registrierungs-Einfuehrung in access-setup.js, 5 Schritte, Zahl von Anfang an
// bekannt). Die anderen sind ein Graph aus Funktionen, die sich je nach Server-Antwort und Nutzerwahl
// gegenseitig aufrufen - der KI-Zugangs-Assistent etwa springt von einer Auswahl in genau einen von
// drei Wegen und von dort wieder zurueck zur Auswahl oder zur bestehenden Konfiguration, nie in
// fester Reihenfolge und nie mit einer festen Gesamtzahl. Eine Schrittliste wuerde diesen
// Kontrollfluss verbiegen, um in ein Schema zu passen, das bei diesen Ablaeufen nicht existiert.
// Imperativ - jeder Schritt ist eine Funktion, die render() aufruft, wenn sie an der Reihe ist -
// passt auf beide Formen, die lineare wie die verzweigte, ohne dass der Baustein eine Navigation
// erzwingt, die es bei drei von sechs Ablaeufen gar nicht gibt.
//
// Warum die Zaehlung ein optionales {step, total} ist statt einer Pflichtangabe: dieselbe
// render()-Signatur deckt alle Faelle ab, die die sechs echten Ablaeufe brauchen - "N / M" (die
// Registrierungs-Einfuehrung), "keine Anzeige" (die anderen fuenf - beim KI-Zugang waere eine Zahl
// schlicht falsch, weil es keine feste Gesamtzahl gibt) und, obwohl keiner der sechs es heute
// braucht, "die Anzeige erscheint erst, sobald die Gesamtzahl feststeht" faellt aus demselben
// optionalen Parameter heraus, ohne dass der Baustein dafuer etwas Eigenes bekommen muesste: ein
// Schritt, der die Gesamtzahl erst nach einer Serverantwort kennt, uebergibt den Zaehler einfach erst
// dann.
//
// Warum die Ladesperre hier liegt und nicht bei jedem Aufrufer einzeln: das Muster (Knopf(e) sperren,
// versuchen, danach wieder freigeben) stand vorher in jedem der sechs Ablaeufe von Hand da - immer
// gleich, gelegentlich ohne finally. Eine Funktion an einer Stelle heisst: ein vergessenes finally ist
// nicht mehr moeglich, und ein Knopf bleibt nie gesperrt haengen, wenn ein Schritt einen Fehler wirft.

import { ButtonRole, buttonClass } from './buttons.js';

// Eigenstaendig exportiert (nicht nur ueber createWizard() erreichbar), weil ein einzelner
// Zwischenschritt - etwa das Modell-Speichern in access-setup.js, das seinen Knopf ohne eigene
// Wizard-Instanz sperrt - dieselbe Sperr-Logik braucht, ohne dafuer den ganzen Schritt-Koerper und
// die Zaehl-Anzeige mitzuschleppen.
export async function withBusyButtons(buttons, action) {
  const list = (Array.isArray(buttons) ? buttons : [buttons]).filter(Boolean);
  for (const button of list) button.disabled = true;
  try {
    return await action();
  } finally {
    // Nach einem erfolgreichen Schritt hat render() den Koerper meist schon ersetzt - dann sperrt
    // dieser Aufruf einen Knopf, der nicht mehr im Dokument haengt. Das ist folgenlos, aber genau
    // deshalb hier ein finally und keine Fallunterscheidung: die Alternative waere, in jedem
    // Aufrufer von Hand zu wissen, ob der eigene Schritt beim Erfolg den Koerper ersetzt.
    for (const button of list) button.disabled = false;
  }
}

/**
 * Legt den geteilten Schritt-Koerper und die Fortschrittsanzeige in einen bestehenden Dialog
 * (das Ergebnis von dialog()/createDialog()) und gibt die Handhabe dafuer zurueck.
 *
 * @param {HTMLDialogElement} dlg - der Dialog, wie dialog(html) ihn liefert. Seine Kopfzeile
 *   (Titel, Schliessen-Kreuz) ist bereits fertig - siehe ensureHeader() in dialog.js - und wird hier
 *   nicht angefasst.
 * @returns {{
 *   body: HTMLElement,
 *   render: (html: string, counter?: {step:number,total:number}|null) => HTMLElement,
 *   setCounter: (counter?: {step:number,total:number}|null) => void,
 *   busy: (buttons: HTMLElement|HTMLElement[]|null|undefined, action: () => Promise<any>) => Promise<any>
 * }}
 */
export function createWizard(dlg) {
  const card = dlg.querySelector('.dialog-card') || dlg;

  // Die Anzeige liegt AUSSERHALB des Koerpers, direkt hinter dem Kopf - so uebersteht sie den
  // Schrittwechsel (render() ersetzt nur body.innerHTML) und muss nicht, wie die fuenf Literale
  // vorher, in jedem einzelnen Schritt-String neu erzeugt werden.
  const progress = document.createElement('div');
  progress.className = 'setup-progress';
  progress.hidden = true;
  card.appendChild(progress);

  const body = document.createElement('div');
  body.dataset.wizardBody = '';
  card.appendChild(body);

  function setCounter(counter) {
    if (counter && Number.isFinite(counter.step) && Number.isFinite(counter.total)) {
      progress.textContent = counter.step + ' / ' + counter.total;
      progress.hidden = false;
    } else {
      progress.hidden = true;
      progress.textContent = '';
    }
  }

  function render(html, counter = null) {
    body.innerHTML = html;
    setCounter(counter);
    return body;
  }

  return { body, render, setCounter, busy: withBusyButtons };
}

/**
 * Baut die Aktionszeile eines Schritts (Zurueck/Weiter/Fertig/Abbrechen und aehnliche) aus fertigen
 * Bausteinen zusammen, mit denselben Rollen-Klassen wie jeder andere Knopf der App. label/attr kommen
 * bereits escaped von der aufrufenden Seite - dieser Baustein escaped nichts selbst, damit er kein
 * eigenes esc() braucht und keines von einer Seite uebernehmen muss.
 *
 * @param {{role?: string, type?: string, attr?: string, label: string, disabled?: boolean}[]} buttons
 */
export function wizardActions(buttons) {
  return '<div class="dialog-actions">' + buttons.map(b =>
    '<button type="' + (b.type || 'button') + '" class="' + buttonClass(b.role || ButtonRole.Secondary) + '"' +
    (b.attr ? ' ' + b.attr : '') + (b.disabled ? ' disabled' : '') + '>' + (b.label || '') + '</button>'
  ).join('') + '</div>';
}
