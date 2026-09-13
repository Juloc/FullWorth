/**
 * The secret vault.
 *
 * Every label and description in here comes from the server. That is not indirection for its own
 * sake: a table of secret names in a file anyone can fetch is a map of what to steal, and
 * FrontendBaselineTests forbids those literals in admin/*.js for exactly that reason.
 *
 * Revealed values never touch innerHTML and never enter the DOM as markup — they go into a module
 * private Map and reach the screen only through textContent. They auto-hide after 30 seconds and the
 * moment the tab loses focus, copying reads from the Map rather than from the page, and nothing here
 * writes to localStorage, the console or the URL.
 */

const ENDPOINT = '/auth/admin/vault';
const HIDE_AFTER_MS = 30_000;

// Module-private and never serialised. Cleared on hide, on blur and when the panel reloads.
const revealed = new Map();
const timers = new Map();

const ERRORS = {
  wrong_factor: 'Falsch. Versuch es noch einmal.',
  vault_locked: 'Zu viele Fehlversuche. Der Tresor ist 15 Minuten gesperrt — die Anmeldung ist davon nicht betroffen.',
  code_already_used: 'Dieser Code wurde schon benutzt. Warte auf den nächsten.',
  elevation_required: 'Bitte erst bestätigen.',
  session_gone: 'Die Sitzung ist abgelaufen. Melde dich neu an.',
  not_set: 'Für diesen Eintrag ist nichts hinterlegt.',
  unknown_reference: 'Unbekannter Eintrag.'
};

export function createVaultPanel({ request, esc, toast }) {
  const body = () => document.querySelector('#vault-body');

  let state = null;

  function forget(reference) {
    revealed.delete(reference);
    const timer = timers.get(reference);
    if (timer) { clearTimeout(timer); timers.delete(reference); }
    const cell = document.querySelector(`[data-vault-value="${CSS.escape(reference)}"]`);
    if (cell) { cell.textContent = ''; cell.hidden = true; }
    const button = document.querySelector(`[data-vault-reveal="${CSS.escape(reference)}"]`);
    if (button) button.textContent = 'Anzeigen';
  }

  function forgetEverything() {
    [...revealed.keys()].forEach(forget);
  }

  // Leaving the tab is the cheapest way for a secret to end up on somebody else's screen — a shared
  // desktop, a screen share that was already running, a screenshot tool.
  window.addEventListener('blur', forgetEverything);
  document.addEventListener('visibilitychange', () => { if (document.hidden) forgetEverything(); });

  function ask(entry) {
    const what = state?.factor === 'totp'
      ? 'Code aus deiner Authenticator-App'
      : 'Dein Passwort';
    const why = entry?.requiresFreshFactor
      ? '\n\nDieser Eintrag verlangt die Bestätigung jedes Mal neu.'
      : '';
    // prompt(), deliberately: it cannot be read back out of the DOM, it leaves nothing behind when it
    // closes, and no password manager offers to remember it into a field it does not know.
    return window.prompt(what + why);
  }

  async function reveal(reference) {
    const entry = state?.entries.find(item => item.reference === reference);
    if (!entry) return;

    if (revealed.has(reference)) { forget(reference); return; }

    // A fresh factor for the entries that own the installation; otherwise only when no window is open.
    const secret = entry.requiresFreshFactor || !state.elevated ? ask(entry) : null;
    if (secret === '') return;                      // an empty prompt is a cancel, not an attempt
    if (secret === null && (entry.requiresFreshFactor || !state.elevated)) return;

    try {
      const result = await request(ENDPOINT + '/reveal', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reference, secret })
      });

      revealed.set(reference, result.value);
      show(reference, result.value);
      await load();
    } catch (error) {
      toast(ERRORS[error.message] || 'Fehlgeschlagen');
      await load();
    }
  }

  function show(reference, value) {
    const cell = document.querySelector(`[data-vault-value="${CSS.escape(reference)}"]`);
    if (!cell) return;
    // textContent, never innerHTML. A private key is a PEM block full of characters a parser would
    // happily read as markup.
    cell.textContent = value;
    cell.hidden = false;

    const button = document.querySelector(`[data-vault-reveal="${CSS.escape(reference)}"]`);
    if (button) button.textContent = 'Verbergen';

    const existing = timers.get(reference);
    if (existing) clearTimeout(existing);
    timers.set(reference, setTimeout(() => forget(reference), HIDE_AFTER_MS));
  }

  async function copy(reference) {
    // From the Map, not from the page: a value that is no longer on screen is still the one the
    // administrator asked for, and reading the DOM would copy an empty string after the auto-hide.
    const value = revealed.get(reference);
    if (!value) { toast('Erst anzeigen.'); return; }
    try {
      await navigator.clipboard.writeText(value);
      toast('Kopiert. Die Zwischenablage leert sich nicht von selbst.');
    } catch {
      toast('Kopieren nicht möglich.');
    }
  }

  async function elevate() {
    const secret = ask(null);
    if (!secret) return;
    try {
      await request(ENDPOINT + '/elevate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ secret })
      });
      await load();
    } catch (error) {
      toast(ERRORS[error.message] || 'Fehlgeschlagen');
      await load();
    }
  }

  function render() {
    const target = body();
    if (!target || !state) return;

    const groups = [...new Set(state.entries.map(entry => entry.group))];
    const status = state.elevated
      ? `Bestätigt · noch ${state.revealsLeft} Anzeigen`
      : (state.factor === 'totp' ? 'Bestätigung mit Authenticator-Code nötig' : 'Bestätigung mit Passwort nötig');

    target.innerHTML =
      '<div class="vault-status"><span class="row-sub">' + esc(status) + '</span>' +
      (state.elevated ? '' : '<button type="button" class="btn btn-secondary" data-vault-elevate>Bestätigen</button>') +
      '</div>' +
      groups.map(group =>
        '<section class="vault-group"><h3>' + esc(group) + '</h3><div class="rows">' +
        state.entries.filter(entry => entry.group === group).map(entry =>
          '<div class="vault-row">' +
            '<div class="row-main">' +
              '<div class="row-title">' + esc(entry.label) +
                (entry.requiresFreshFactor ? ' <span class="chip admin-chip-warn">kritisch</span>' : '') +
              '</div>' +
              '<div class="row-sub">' + esc(entry.description) + '</div>' +
              (entry.hint ? '<div class="row-sub">' + esc(entry.hint) + '</div>' : '') +
              '<code class="vault-value" data-vault-value="' + esc(entry.reference) + '" hidden></code>' +
            '</div>' +
            '<div class="vault-actions">' +
              (entry.stored
                ? '<button type="button" class="btn btn-secondary" data-vault-reveal="' + esc(entry.reference) + '">Anzeigen</button>' +
                  '<button type="button" class="btn btn-secondary" data-vault-copy="' + esc(entry.reference) + '">Kopieren</button>'
                : '') +
            '</div>' +
          '</div>').join('') +
        '</div></section>').join('');

    target.querySelector('[data-vault-elevate]')?.addEventListener('click', () => elevate().catch(console.error));
    target.querySelectorAll('[data-vault-reveal]').forEach(button =>
      button.addEventListener('click', () => reveal(button.dataset.vaultReveal).catch(console.error)));
    target.querySelectorAll('[data-vault-copy]').forEach(button =>
      button.addEventListener('click', () => copy(button.dataset.vaultCopy).catch(console.error)));

    // Anything still on screen from before this render is gone from the DOM now; drop it from memory
    // too rather than leaving a value nobody can see and nobody can clear.
    [...revealed.keys()]
      .filter(reference => !target.querySelector(`[data-vault-value="${CSS.escape(reference)}"]`))
      .forEach(forget);
    revealed.forEach((value, reference) => show(reference, value));
  }

  async function load() {
    state = await request(ENDPOINT);
    render();
  }

  return { load };
}
