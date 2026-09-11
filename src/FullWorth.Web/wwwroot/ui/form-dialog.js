import { ButtonRole, buttonClass } from './buttons.js';
import { createDialog } from './dialog.js';
import { esc } from './ux-kit.js';

/**
 * The form primitive the dialogs never had.
 *
 * `createDialog` gives a shell: header, close button, mobile swipe. Everything *inside* was
 * hand-written HTML at each of 76 call sites, so all 76 independently re-decided field order and
 * grouping, whether anything is optional, the label/input markup and therefore the spacing, where a
 * validation error appears, what the actions row looks like, and the mobile treatment. That is the
 * measured cause of the dialogs reading as over-complex (docs/UI_AUDIT.md): nothing shared existed to
 * be consistent *with*.
 *
 * So this owns the decisions, once:
 *
 * - the label/input markup and the required marker;
 * - `group`, which puts fields side by side on one row instead of a flat list of equal weight;
 * - `advanced: true`, which hides a field behind a `<details>` — the disclosure the booking filter
 *   needed and the pattern the wealth page's custom range already uses;
 * - the error position: inline under its own field, not a toast and not a banner at the top, so the
 *   message sits where the wrong value is;
 * - the actions row: primary on the right, a destructive action separated from it so "Löschen" is
 *   never adjacent to "Speichern";
 * - Enter submits, Escape cancels, and focus starts on the first field rather than on the close button.
 *
 * It deliberately does NOT own: fetching, saving, toasts, or what a field means. A caller gets values
 * and decides.
 */

/** The field kinds a dialog in this app actually uses. `money` is `number` with a currency step. */
export const FieldKind = Object.freeze({
  Text: 'text',
  Number: 'number',
  Money: 'money',
  Date: 'date',
  Select: 'select',
  Check: 'check',
  Textarea: 'textarea'
});

function inputType(kind) {
  if (kind === FieldKind.Number || kind === FieldKind.Money) return 'number';
  if (kind === FieldKind.Date) return 'date';
  return 'text';
}

function attributes(field) {
  const parts = [];
  const add = (name, value) => {
    if (value === undefined || value === null || value === '') return;
    parts.push(`${name}="${esc(value)}"`);
  };
  add('name', field.name);
  if (field.required) parts.push('required');
  if (field.readOnly) parts.push('readonly');
  add('placeholder', field.placeholder);
  add('maxlength', field.maxLength);
  add('minlength', field.minLength);
  add('min', field.min);
  add('max', field.max);
  add('autocomplete', field.autocomplete);
  // A money field is cents-precise unless the caller says otherwise; a bare number is not.
  if (field.kind === FieldKind.Money) add('step', field.step ?? '0.01');
  else add('step', field.step);
  add('inputmode', field.inputMode ?? (field.kind === FieldKind.Money ? 'decimal' : undefined));
  return parts.join(' ');
}

function optionsHtml(field, value) {
  // Escape hatch for the app's own option builders - ctx.categoryOptions() returns ready <option>
  // markup with the selection already applied, and rebuilding the category tree here would be a second
  // implementation of it. Only a caller's own trusted helper may fill this; never user input.
  if (field.rawOptions) return field.rawOptions;
  return (field.options || [])
    .map(option => {
      const optionValue = option.value ?? option;
      const label = option.label ?? String(optionValue);
      const selected = String(optionValue) === String(value ?? '') ? ' selected' : '';
      return `<option value="${esc(optionValue)}"${selected}>${esc(label)}</option>`;
    })
    .join('');
}

function fieldHtml(field, value) {
  // The error element exists from the start: creating it on demand is what makes a dialog jump when
  // validation fails, and a jumping dialog hides the very message it just produced.
  const error = `<span class="fw-field-error" data-error-for="${esc(field.name)}" aria-live="polite"></span>`;
  const hint = field.hint ? `<span class="fw-field-hint">${esc(field.hint)}</span>` : '';
  const required = field.required ? ' <span class="fw-field-required" aria-hidden="true">*</span>' : '';
  // One element, not two: .fw-field is a grid, so a bare <span> beside the label text becomes its own
  // row and the asterisk drops under the label instead of sitting next to it.
  const label = `<span class="fw-field-label">${esc(field.label)}${required}</span>`;

  if (field.kind === FieldKind.Check) {
    // A checkbox's label belongs after the box, which is the one case where the order flips.
    return `<label class="check fw-field fw-field--check" data-field="${esc(field.name)}">`
      + `<input type="checkbox" ${attributes({ ...field, required: false })}${value ? ' checked' : ''}>`
      + ` <span>${label}</span>${hint}${error}</label>`;
  }

  const control = field.kind === FieldKind.Select
    ? `<select ${attributes(field)}>${optionsHtml(field, value)}</select>`
    : field.kind === FieldKind.Textarea
      ? `<textarea ${attributes(field)} rows="${esc(field.rows ?? 2)}">${esc(value ?? '')}</textarea>`
      : `<input type="${inputType(field.kind)}" ${attributes(field)} value="${esc(value ?? '')}">`;

  return `<label class="fw-field" data-field="${esc(field.name)}">${label}${control}${hint}${error}</label>`;
}

/**
 * Consecutive fields sharing a `group` render on one row. Grouping is what turns "14 controls of equal
 * weight" into something readable, and it has to be the caller's statement about meaning — a layout
 * heuristic would pair "Betrag" with "Notiz" because they happen to be adjacent.
 */
function groupFields(fields) {
  const rows = [];
  for (const field of fields) {
    const last = rows[rows.length - 1];
    if (field.group && last?.group === field.group) last.fields.push(field);
    else rows.push({ group: field.group || null, fields: [field] });
  }
  return rows;
}

function rowsHtml(fields, values) {
  return groupFields(fields)
    .map(row => {
      const inner = row.fields.map(field => fieldHtml(field, values[field.name])).join('');
      return row.fields.length > 1 ? `<div class="fw-field-row">${inner}</div>` : inner;
    })
    .join('');
}

function actionsHtml(actions) {
  // The destructive action is separated by a spacer rather than merely coloured: colour alone still
  // puts "Löschen" one slip of the thumb away from "Speichern" on a 375 px screen.
  const destructive = actions.filter(action => action.role === ButtonRole.Danger);
  const rest = actions.filter(action => action.role !== ButtonRole.Danger);
  const button = action => `<button type="${action.submit ? 'submit' : 'button'}"`
    + ` class="${buttonClass(action.role || ButtonRole.Secondary)}"`
    + ` data-action="${esc(action.name)}"${action.disabled ? ' disabled' : ''}>${esc(action.label)}</button>`;
  return `<div class="dialog-actions fw-dialog-actions">`
    + destructive.map(button).join('')
    + (destructive.length ? '<span class="fw-actions-spacer"></span>' : '')
    + rest.map(button).join('')
    + `</div>`;
}

/**
 * Builds the dialog and returns the handles a caller needs. `create` is injectable so a feature can
 * keep routing through `ctx.dialog`, and so this is testable without a DOM-less shim.
 *
 * @returns {{dialog: HTMLDialogElement, form: HTMLFormElement, values: () => Record<string, unknown>,
 *   setError: (name: string, message: string) => void, setFormError: (message: string) => void,
 *   clearErrors: () => void, close: (result?: string) => void, field: (name: string) => HTMLElement|null}}
 */
export function createFormDialog({
  title,
  subtitle = '',
  fields = [],
  actions = [],
  className = '',
  mobileMode,
  values = {},
  advancedLabel = 'Mehr',
  closeLabel = 'Schließen',
  fallbackError = 'Das hat nicht funktioniert.',
  onSubmit,
  create = html => createDialog(html, { className, mobileMode, closeLabel })
} = {}) {
  const plain = fields.filter(field => !field.advanced);
  const advanced = fields.filter(field => field.advanced);
  const head = `<div class="panel-head"><div><h2>${esc(title)}</h2>`
    + (subtitle ? `<div class="row-sub">${esc(subtitle)}</div>` : '')
    + `</div><button type="button" data-close aria-label="${esc(closeLabel)}">×</button></div>`;

  // An `<details>` and not a second dialog: a nested dialog loses the values already typed, which is
  // the failure the "advanced" fields were hidden to avoid in the first place.
  const advancedHtml = advanced.length
    ? `<details class="fw-dialog-advanced"><summary>${esc(advancedLabel)}`
      + `<span class="fw-advanced-count" data-advanced-count></span></summary>`
      + rowsHtml(advanced, values) + `</details>`
    : '';

  // Not every failure belongs to a field. A rejected save ("Konto existiert nicht mehr") used to
  // become a toast that outlived the dialog it came from, or - worse - got pinned to whichever field
  // the caller guessed. It goes here, next to the button that caused it, and the caller does not
  // have to invent a field to blame.
  const formError = `<p class="fw-form-error" data-form-error aria-live="polite"></p>`;

  const dialog = create(`<form class="dialog-card fw-form-dialog${className ? ' ' + esc(className) : ''}">`
    + head + rowsHtml(plain, values) + advancedHtml + formError + actionsHtml(actions) + `</form>`);

  const form = dialog.querySelector('form');

  const values_ = () => {
    const data = {};
    for (const field of fields) {
      const element = form.elements.namedItem(field.name);
      if (!element) continue;
      if (field.kind === FieldKind.Check) data[field.name] = element.checked;
      else if (field.kind === FieldKind.Number || field.kind === FieldKind.Money) {
        const raw = String(element.value ?? '').trim();
        // An empty number field is *absent*, not zero. Number('') is 0 and finite, which is how an
        // unset value becomes a real 0,00 € in a payload.
        data[field.name] = raw === '' ? null : Number(raw);
      } else {
        const raw = String(element.value ?? '').trim();
        data[field.name] = raw === '' ? null : raw;
      }
    }
    return data;
  };

  const field = name => form.querySelector(`[data-field="${CSS.escape(name)}"]`);

  const clearErrors = () => {
    form.querySelectorAll('.fw-field-error').forEach(node => { node.textContent = ''; });
    form.querySelectorAll('.fw-field.is-invalid').forEach(node => node.classList.remove('is-invalid'));
    const banner = form.querySelector('[data-form-error]');
    if (banner) banner.textContent = '';
  };

  // Never silently empty: a caller passing a falsy message still gets a visible refusal, because a
  // dialog that declines to save and says nothing is indistinguishable from a broken button.
  const setFormError = message => {
    const banner = form.querySelector('[data-form-error]');
    if (banner) banner.textContent = message || fallbackError;
  };

  const setError = (name, message) => {
    const target = form.querySelector(`[data-error-for="${CSS.escape(name)}"]`);
    if (!target) return;
    target.textContent = message || '';
    const wrapper = field(name);
    wrapper?.classList.toggle('is-invalid', Boolean(message));
    // If the field is inside the collapsed disclosure, open it — an error the user cannot see is
    // indistinguishable from a form that refuses to submit for no reason.
    if (message) wrapper?.closest('details')?.setAttribute('open', '');
    if (message) form.elements.namedItem(name)?.focus();
  };

  const close = result => { if (dialog.open) dialog.close(result ?? 'cancel'); };

  dialog.querySelector('[data-close]')?.addEventListener('click', () => close());
  for (const action of actions) {
    if (action.submit || typeof action.onClick !== 'function') continue;
    form.querySelector(`[data-action="${CSS.escape(action.name)}"]`)
      ?.addEventListener('click', () => action.onClick({ values: values_(), setError, setFormError, clearErrors, close, dialog }));
  }

  if (typeof onSubmit === 'function') {
    form.addEventListener('submit', event => {
      event.preventDefault();
      clearErrors();
      onSubmit({ values: values_(), setError, setFormError, clearErrors, close, dialog });
    });
  }

  // How many hidden fields are actually set, shown on the closed summary. A filter that is set but
  // invisible silently changes what the user is looking at, which is worse than a long form - so the
  // disclosure always says how much is inside it.
  const isSet = field => {
    const element = form.elements.namedItem(field.name);
    if (!element) return false;
    if (field.kind === FieldKind.Check) return element.checked === true;
    return String(element.value ?? '').trim() !== '';
  };
  const counter = form.querySelector('[data-advanced-count]');
  const updateCount = () => {
    if (!counter) return;
    const count = advanced.filter(isSet).length;
    counter.textContent = count ? `(${count})` : '';
  };
  if (counter) {
    form.addEventListener('input', updateCount);
    form.addEventListener('change', updateCount);
    updateCount();
  }

  // Focus the first real field, not the close button: a dialog that opens with the close button
  // focused answers Enter with "cancel".
  const first = plain.concat(advanced).map(f => form.elements.namedItem(f.name)).find(Boolean);
  dialog.addEventListener('close', () => { /* createDialog removes the node */ }, { once: true });
  queueMicrotask(() => first?.focus());

  return { dialog, form, values: values_, setError, setFormError, clearErrors, close, field };
}

/** Opens it and returns the same handles, so a caller does not have to remember `showModal`. */
export function openFormDialog(options) {
  const handles = createFormDialog(options);
  handles.dialog.showModal();
  return handles;
}
