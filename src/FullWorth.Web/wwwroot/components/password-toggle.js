/**
 * The show/hide eye for password fields, in one place.
 *
 * It existed exactly once: hand-written into the sign-in markup as a wrapper span, two inline SVGs
 * and four ARIA attributes. So the other fifteen password inputs in this app had none — including the
 * registration form, which is the first thing a new self-hoster types into. Copying ten lines of SVG
 * fifteen times is not a fix; it is the same defect fifteen more times.
 *
 * This upgrades existing markup instead of asking every call site to produce it. A plain
 * `<input type="password">` stays a plain input in the HTML and in every template string, and gains
 * the eye when a page enhances its root. That keeps it usable from both bundles: the auth page loads
 * none of the app's modules, so this file deliberately imports nothing.
 *
 * Idempotent on purpose — re-running it after a language switch refreshes the labels, and re-running
 * it after a partial re-render enhances only what is new.
 */

import { spriteHref } from './sprite.js';

const ENHANCED = 'passwordToggle';

const EYE_OPEN =
  '<svg class="pw-eye pw-eye-open" viewBox="0 0 24 24" width="20" height="20" fill="none" ' +
  'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" ' +
  'aria-hidden="true"><use href="' + spriteHref('ui-eye') + '"></use></svg>';

const EYE_OFF =
  '<svg class="pw-eye pw-eye-off" viewBox="0 0 24 24" width="20" height="20" fill="none" ' +
  'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" ' +
  'aria-hidden="true"><use href="' + spriteHref('ui-eye-off') + '"></use></svg>';

/**
 * @param root      element to search, or document
 * @param labels    { show, hide } - already translated. Passed in rather than imported, because the
 *                  auth page and the app carry different i18n plumbing and this file belongs to both.
 */
export function enhancePasswordInputs(root = document, labels = {}) {
  const show = labels.show || 'Passwort anzeigen';
  const hide = labels.hide || 'Passwort verbergen';
  const scope = root === document || root?.querySelectorAll ? root : document;

  for (const input of scope.querySelectorAll('input[type="password"], input[data-password-field]')) {
    const existing = input.parentElement?.querySelector(':scope > .password-toggle');
    if (existing) {
      // Already enhanced: only the wording can have changed.
      applyLabel(existing, existing.getAttribute('aria-pressed') === 'true' ? hide : show);
      existing.dataset.labelShow = show;
      existing.dataset.labelHide = hide;
      continue;
    }

    if (input.dataset[ENHANCED] === 'skip') continue;

    let control = input.parentElement;
    if (!control?.classList.contains('password-control')) {
      control = document.createElement('span');
      control.className = 'password-control';
      input.parentElement?.insertBefore(control, input);
      control.appendChild(input);
    }

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'password-toggle';
    button.setAttribute('aria-pressed', 'false');
    if (input.id) button.setAttribute('aria-controls', input.id);
    button.dataset.labelShow = show;
    button.dataset.labelHide = hide;
    button.innerHTML = EYE_OPEN + EYE_OFF;
    applyLabel(button, show);
    button.addEventListener('click', () => toggle(input, button));
    control.appendChild(button);
  }
}

function toggle(input, button) {
  const reveal = input.type === 'password';
  input.type = reveal ? 'text' : 'password';
  button.setAttribute('aria-pressed', String(reveal));
  applyLabel(button, reveal ? button.dataset.labelHide : button.dataset.labelShow);
  // Focus returns to the field, not the button: the point of looking is to keep typing.
  input.focus({ preventScroll: true });
}

function applyLabel(button, text) {
  button.setAttribute('aria-label', text);
  button.title = text;
}
