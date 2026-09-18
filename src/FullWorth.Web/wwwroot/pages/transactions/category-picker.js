// Category picker (§10.5): search across full category paths, show the icon, and create a category
// inline without leaving the current flow.
//
// The dialog, the search, the icon rows and the keyboard now live in ui/combobox.js — this file kept
// its own copy of all of that, which is why accounts and every other long list had a bare <select>.
// What stays here is the only part that is actually about categories: building a path label, and the
// inline "create" form.
//
// Still layered ON TOP of an existing <select>: picking a row sets selectEl.value and dispatches
// 'change', so every existing form (FormData reads, plain sel.value reads) keeps working unchanged.

import { attachCombobox, openCombobox } from '../../components/combobox.js';
import { categoryComboboxItems } from '../../components/category-combobox.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';

export function attachCategoryPicker(ctx, selectEl) {
  return attachCombobox(ctx, selectEl, {
    title: ctx.get('categories.pick'),
    searchPlaceholder: ctx.get('categories.pickSearch'),
    items: () => loadItems(ctx),
    extra: createSlot(ctx),
    // Das Feld selbst ist die Auswahl - kein natives Feld mit Lupe daneben (#121).
    anchored: true,
    placeholder: ctx.get('common.uncategorized')
  });
}

function slugify(name) {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') || `cat-${Date.now()}`;
}

// The path-building (full path as `hint`, indentation, icon inheritance) now lives in
// components/category-combobox.js so every other category select can use it too - this stays a thin
// wrapper that only knows the one route.
async function loadItems(ctx) {
  const categories = await ctx.api('api/categories');
  return categoryComboboxItems(categories);
}

/** The inline "create a category" form, as the combobox's caller-supplied slot. */
function createSlot(ctx) {
  return {
    html: `<form data-extra-form hidden><label>${ctx.esc(ctx.get('categories.new'))}`
      + `<input name="name" maxlength="120"></label>`
      + `<div class="dialog-actions"><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${ctx.esc(ctx.get('common.create'))}</button></div></form>`
      + `<button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-extra-toggle>${ctx.esc(ctx.get('categories.new'))}</button>`,
    onSubmit: async ({ form, selectEl, choose }) => {
      const name = String(new FormData(form).get('name') || '').trim();
      if (!name) return;
      try {
        const created = await ctx.api('api/categories', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ key: slugify(name), name, parentId: null, icon: null, sortOrder: null })
        });
        // A freshly created category is not in the select yet, and picking it would otherwise set a
        // value the select does not know — which reads back as empty on submit.
        if (selectEl) {
          const option = document.createElement('option');
          option.value = created.id;
          option.textContent = name;
          selectEl.appendChild(option);
        }
        choose(created.id);
      } catch (error) {
        ctx.toast(error.message || ctx.get('common.error'));
      }
    }
  };
}

// Opens the picker and calls onSelect(categoryId) with the chosen (or freshly created) id. `selectEl`
// is optional and only used to append a newly-created <option> when the picker layers over a <select>;
// callers without a select (e.g. the transactions list category chip) just pass a callback and, so the
// open list can still highlight today's value, the id it already has via `selectedId`.
export async function openCategoryPicker(ctx, onSelect, selectEl = null, selectedId = null) {
  return openCombobox(ctx, {
    title: ctx.get('categories.pick'),
    searchPlaceholder: ctx.get('categories.pickSearch'),
    items: () => loadItems(ctx),
    selectEl,
    selectedId,
    onSelect,
    extra: createSlot(ctx)
  });
}
