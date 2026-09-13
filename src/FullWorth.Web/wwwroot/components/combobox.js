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

/** @typedef {{ id: string, label: string, icon?: string|null, hint?: string|null }} ComboboxItem */

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
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'icon-button combobox-trigger';
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

export async function openCombobox(ctx, {
  title,
  searchPlaceholder,
  items,
  selectEl = null,
  onSelect,
  extra = null
} = {}) {
  let list;
  try {
    list = typeof items === 'function' ? await items() : (items ?? (selectEl ? itemsFromSelect(selectEl) : []));
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  const heading = title || ctx.get('combobox.pick');
  const dialog = ctx.dialog(`<div class="dialog-card drawer combobox-dialog">
    <div class="panel-head"><h2>${ctx.esc(heading)}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <input type="search" data-search placeholder="${ctx.esc(searchPlaceholder || ctx.get('combobox.search'))}" autocomplete="off">
    <div class="refund-candidates" data-list role="listbox"></div>
    ${extra?.html || ''}
  </div>`);

  const rows = dialog.querySelector('[data-list]');
  const search = dialog.querySelector('[data-search]');
  let shown = list;

  const choose = id => { onSelect?.(id); dialog.close(); };

  const render = query => {
    const needle = query.trim().toLowerCase();
    shown = needle ? list.filter(item => item.label.toLowerCase().includes(needle)) : list;
    rows.innerHTML = shown.length
      ? shown.map((item, index) => `<button type="button" class="row candidate-row" role="option" data-id="${ctx.esc(item.id)}" data-index="${index}">`
        + `<div class="row-main"><div class="row-title">${item.icon ? ctx.esc(item.icon) + ' ' : ''}${ctx.esc(item.label)}</div>`
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
  return dialog;
}
