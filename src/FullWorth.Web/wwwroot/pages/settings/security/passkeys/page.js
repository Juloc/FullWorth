import { initializePasskeyManagement } from '../../../../passkeys/passkeys.js';

// Vorher waren das 61 Zeilen: eigene Sprachdatei laden, eigene Übersetzung anwenden, eigenes Theme
// setzen, eigenen Titel schreiben. Alles davon macht die Hülle ohnehin, und alles davon war eine
// zweite Fassung, die auseinanderlaufen konnte. Übrig bleibt der eine Aufruf, um den es geht.
let reload = null;

export function renderPasskeys(ctx) {
  if (reload) return reload();

  reload = initializePasskeyManagement({
    root: ctx.$('#view-passkeys'),
    // Die Registrierung meldet sich unter einem anderen Schlüssel, als die Seite ihn nennt.
    message: path => ctx.get(path === 'passkeys.registering' ? 'passkeys.adding' : path),
    locale: document.documentElement.lang
  });
}
