/**
 * A searchable picker for the lists that are too long to scroll: search, icons, keyboard.
 *
 * This is the category picker's machinery, taken out of the category picker. That one had search over
 * full paths, icons and inline creation — good behaviour, available for exactly one kind of list,
 * while accounts (six dialogs) and every other long list had a bare `<select>`.
 *
 * It LAYERS over a native select rather than replacing it, which is the decision the category picker
 * got right and the reason this can spread at all: picking a row sets `select.value` and dispatches
 * `change`, so every existing form keeps working — FormData, plain `.value` reads, validation, all of
 * it, unchanged. The select also stays the keyboard and mobile path; this adds a second, richer way to
 * set the same value rather than taking the first one away.
 *
 * It is deliberately NOT used for short lists. A three-option "Typ" is better as a plain select: the
 * phone's own wheel, type-ahead and zero code beat a custom dialog every time.
 */

import { ButtonRole, buttonClass } from './buttons.js';

/**
 * `iconHtml` ist fertiges Markup und wird roh eingesetzt, `icon` ist Text und wird escaped.
 * Der Unterschied ist Absicht: dieses Modul darf nicht wissen, was eine Kategorie ist, also rendert
 * der Aufrufer sein Symbol selbst (z. B. mit `categoryIconInner`) und reicht es fertig herein.
 * Vorher stand hier `esc(item.icon)` - und damit las man in der Auswahl "groceries" statt des
 * Einkaufswagens, den dieselbe Kategorie in der Buchungsliste zeigte.
 *
 * @typedef {{ id: string, label: string, icon?: string|null, iconHtml?: string|null, hint?: string|null, depth?: number }} ComboboxItem
 */

/** Items read from the select itself — the default, and why this needs no new plumbing per list. */
export function itemsFromSelect(selectEl) {
  return [...selectEl.options]
    .filter(option => option.value !== '')
    .map(option => ({ id: option.value, label: option.textContent.trim(), icon: option.dataset.icon || null }));
}

/**
 * Adds the search trigger next to a select.
 *
 * @param options.items      () => ComboboxItem[] | Promise<...>. Defaults to the select's own options.
 * @param options.extra      optional { html, onSubmit } rendered under the list — the "create a
 *                           category without leaving this flow" slot, kept as a slot so this module
 *                           does not learn what a category is.
 */
export function attachCombobox(ctx, selectEl, options = {}) {
  if (!selectEl || selectEl.dataset.combobox === 'on') return;
  selectEl.dataset.combobox = 'on';

  const label = options.title || ctx.get('combobox.pick');

  // `anchored`: das Feld SELBST ist die Auswahl. Das native <select> bleibt als Wahrheit im
  // Formular stehen (nur versteckt, damit FormData es weiter sieht) und bekommt einen Knopf davor,
  // der den aktuellen Wert samt Symbol zeigt und die Liste am Feld aufklappt.
  //
  // Ohne `anchored` bleibt es beim alten Nebeneinander aus Feld und Lupe. Das ist fuer kurze Listen
  // richtig: dort schlaegt das Rad des Telefons jeden Eigenbau.
  if (options.anchored) return attachAnchored(ctx, selectEl, options, label);

  const button = document.createElement('button');
  button.type = 'button';
  button.className = buttonClass(ButtonRole.Icon, 'combobox-trigger');
  button.title = label;
  button.setAttribute('aria-label', label);
  button.textContent = '⌕';
  selectEl.insertAdjacentElement('afterend', button);

  button.addEventListener('click', () => openCombobox(ctx, {
    ...options,
    selectEl,
    onSelect: id => {
      selectEl.value = id;
      selectEl.dispatchEvent(new Event('change', { bubbles: true }));
    }
  }));
  return button;
}

function attachAnchored(ctx, selectEl, options, label) {
  const field = document.createElement('button');
  field.type = 'button';
  field.className = 'combobox-field';
  field.setAttribute('aria-haspopup', 'listbox');
  field.setAttribute('aria-expanded', 'false');
  field.setAttribute('aria-label', label);

  selectEl.hidden = true;
  selectEl.insertAdjacentElement('afterend', field);

  let cache = null;
  const itemFor = id => cache?.find(item => String(item.id) === String(id)) || null;

  const paint = () => {
    const chosen = itemFor(selectEl.value);
    const fallback = selectEl.selectedOptions[0]?.textContent?.trim();
    const text = chosen?.label || (selectEl.value ? fallback : '') || options.placeholder || label;
    field.innerHTML = (chosen?.iconHtml ? `<span class="combobox-icon" aria-hidden="true">${chosen.iconHtml}</span>` : '')
      + `<span class="combobox-field-text${selectEl.value ? '' : ' is-placeholder'}">${ctx.esc(text)}</span>`
      + `<span class="combobox-field-caret" aria-hidden="true">▾</span>`;
  };

  // Der Wert kann auch von aussen gesetzt werden (Formular zuruecksetzen, Vorbelegung) - dann muss
  // die sichtbare Seite mitgehen.
  selectEl.addEventListener('change', paint);
  paint();

  field.addEventListener('click', async () => {
    field.setAttribute('aria-expanded', 'true');
    await openCombobox(ctx, {
      ...options,
      selectEl,
      anchorTo: field,
      onItems: list => { cache = list; },
      onSelect: id => {
        selectEl.value = id;
        selectEl.dispatchEvent(new Event('change', { bubbles: true }));
      }
    });
    field.setAttribute('aria-expanded', 'false');
    paint();
  });
  return field;
}

export async function openCombobox(ctx, {
  title,
  searchPlaceholder,
  items,
  selectEl = null,
  onSelect,
  extra = null,
  anchorTo = null,
  onItems = null
} = {}) {
  let list;
  try {
    list = typeof items === 'function' ? await items() : (items ?? (selectEl ? itemsFromSelect(selectEl) : []));
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }
  onItems?.(list);

  const heading = title || ctx.get('combobox.pick');
  const inner = `<div class="panel-head"><h2>${ctx.esc(heading)}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <input type="search" data-search placeholder="${ctx.esc(searchPlaceholder || ctx.get('combobox.search'))}" autocomplete="off">
    <div class="refund-candidates" data-list role="listbox"></div>
    ${extra?.html || ''}`;

  // Am Feld aufklappen statt als grosser Dialog in der Mitte. `<dialog>` bleibt es trotzdem: das
  // bringt Fokusfang, Escape und den Rueckweg zum ausloesenden Element geschenkt - nur eben ohne
  // Verdunklung und an der Stelle, an der der Benutzer gerade hinsieht. Unter 768 px wird daraus
  // wieder ein Blatt von unten; dieselbe Komponente, andere Dichte.
  const dialog = anchorTo
    // `mobileMode:'sheet'` nimmt das Popover aus der Ganzseiten-Behandlung heraus, die
    // `dialogs.css` unter 768 px jedem Dialog gibt - und bringt die Wischgeste zum Schliessen mit.
    ? ctx.dialog(`<div class="dialog-card combobox-dialog combobox-popover">${inner}</div>`,
        { className: 'combobox-popover-host', mobileMode: 'sheet' })
    : ctx.dialog(`<div class="dialog-card drawer combobox-dialog">${inner}</div>`);
  if (anchorTo) placeAt(dialog, anchorTo);

  const rows = dialog.querySelector('[data-list]');
  const search = dialog.querySelector('[data-search]');
  let shown = list;

  const choose = id => { onSelect?.(id); dialog.close(); };

  const render = query => {
    const needle = query.trim().toLowerCase();
    // Auch im Hinweis suchen: bei einem Baum steht dort der volle Pfad, und wer nach dem Elternteil
    // sucht, erwartet dessen Unterkategorien zu finden.
    shown = needle
      ? list.filter(item => `${item.label} ${item.hint || ''}`.toLowerCase().includes(needle))
      : list;
    rows.innerHTML = shown.length
      ? shown.map((item, index) => `<button type="button" class="row candidate-row" role="option" data-id="${ctx.esc(item.id)}" data-index="${index}"${item.depth ? ` style="--combobox-depth:${item.depth}"` : ''}>`
        + `<div class="row-main"><div class="row-title">`
        + (item.iconHtml ? `<span class="combobox-icon" aria-hidden="true">${item.iconHtml}</span>` : item.icon ? ctx.esc(item.icon) + ' ' : '')
        + `${ctx.esc(item.label)}</div>`
        + (item.hint ? `<div class="row-sub">${ctx.esc(item.hint)}</div>` : '')
        + `</div></button>`).join('')
      : `<div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div>`;
    rows.querySelectorAll('[data-id]').forEach(row =>
      row.addEventListener('click', () => choose(row.dataset.id)));
  };

  render('');
  search.addEventListener('input', event => render(event.target.value));

  // Keyboard, which the category picker never had: a list you have to reach for with the mouse is
  // slower than the select it replaced.
  search.addEventListener('keydown', event => {
    if (event.key === 'ArrowDown') { event.preventDefault(); rows.querySelector('[data-index="0"]')?.focus(); }
    else if (event.key === 'Enter' && shown.length === 1) { event.preventDefault(); choose(shown[0].id); }
  });
  rows.addEventListener('keydown', event => {
    const current = event.target.closest('[data-index]');
    if (!current) return;
    const index = Number(current.dataset.index);
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      rows.querySelector(`[data-index="${index + 1}"]`)?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      if (index === 0) search.focus();
      else rows.querySelector(`[data-index="${index - 1}"]`)?.focus();
    }
  });

  dialog.querySelector('[data-close]').onclick = () => dialog.close();
  if (extra?.onSubmit) {
    const form = dialog.querySelector('[data-extra-form]');
    form?.addEventListener('submit', event => {
      event.preventDefault();
      extra.onSubmit({ form, dialog, selectEl, choose });
    });
    dialog.querySelector('[data-extra-toggle]')?.addEventListener('click', () => {
      if (!form) return;
      form.hidden = !form.hidden;
      if (!form.hidden) form.querySelector('input')?.focus();
    });
  }

  dialog.showModal();
  search.focus();
  // Ein Klick neben die Liste schliesst sie. Bei einem Popover erwartet das jeder; beim grossen
  // Dialog gilt weiter der Schliessen-Knopf.
  if (anchorTo) {
    dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
  }
  return new Promise(resolve => dialog.addEventListener('close', () => resolve(dialog), { once: true }));
}

/**
 * Die Liste unter das Feld legen - und darueber, wenn darunter kein Platz mehr ist.
 *
 * `position:fixed` gegen das Ansichtsfenster, weil ein `<dialog>` in der Top-Layer liegt und
 * deshalb ohnehin nicht mehr im Fluss seines Formulars steht. Gerechnet wird nach dem Zeichnen,
 * sonst ist die eigene Hoehe noch 0 und die Liste kleht oben.
 */
function placeAt(dialog, anchor) {
  const card = dialog.querySelector('.combobox-popover');
  const apply = () => {
    // Am Telefon positioniert das Stylesheet (Blatt von unten). Hier nichts zu setzen ist besser,
    // als es hinterher mit !important wieder einzufangen - inline schlaegt sonst die Medienabfrage.
    if (matchMedia('(max-width:767px)').matches) {
      card.style.cssText = '';
      return;
    }
    const field = anchor.getBoundingClientRect();
    const gap = 6;
    const below = window.innerHeight - field.bottom - gap;
    const above = field.top - gap;
    const wanted = card.offsetHeight || 320;
    const openUp = below < Math.min(wanted, 240) && above > below;

    card.style.maxHeight = `${Math.max(160, (openUp ? above : below))}px`;
    card.style.width = `${Math.max(240, field.width)}px`;
    card.style.left = `${Math.min(field.left, window.innerWidth - card.offsetWidth - 8)}px`;
    card.style.top = openUp ? `${Math.max(8, field.top - card.offsetHeight - gap)}px` : `${field.bottom + gap}px`;
  };
  apply();
  requestAnimationFrame(apply);
}
