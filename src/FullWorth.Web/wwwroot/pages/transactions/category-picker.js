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
import { categoryIconInner } from '../../components/icons.js';

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

function chainOf(category, byId) {
  const chain = [];
  let current = category;
  while (current) {
    chain.unshift(current);
    current = current.parentId ? byId.get(current.parentId) : null;
  }
  return chain;
}

function slugify(name) {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') || `cat-${Date.now()}`;
}

// Die Liste steht als Baum: sortiert nach dem vollen Pfad, angezeigt nur mit dem eigenen Namen und
// einer Einrueckung. Der volle Pfad bleibt als `hint` erhalten - danach wird auch gesucht, sonst
// fände "Supermarkt" die Unterkategorie von "Lebensmittel" nicht mehr, sobald man nach dem Elternteil
// sucht. Das Symbol kommt fertig gerendert herein; die Combobox weiß nichts von Kategorien.
async function loadItems(ctx) {
  const categories = await ctx.api('api/categories');
  const byId = new Map(categories.map(category => [category.id, category]));
  return categories
    .map(category => {
      const chain = chainOf(category, byId);
      const path = chain.map(node => node.name).join(' › ');
      return {
        id: category.id,
        label: category.name,
        hint: chain.length > 1 ? path : null,
        depth: chain.length - 1,
        iconHtml: categoryIconInner(category.icon || inheritedIcon(chain)),
        sortKey: path,
      };
    })
    .sort((a, b) => a.sortKey.localeCompare(b.sortKey));
}

// Eine Unterkategorie ohne eigenes Symbol zeigt das ihres Elternteils, statt gar keines - sonst
// stehen in einem Baum lauter leere Stellen neben genau einem Symbol ganz oben.
function inheritedIcon(chain) {
  for (let index = chain.length - 1; index >= 0; index--) if (chain[index].icon) return chain[index].icon;
  return null;
}

/** The inline "create a category" form, as the combobox's caller-supplied slot. */
function createSlot(ctx) {
  return {
    html: `<form data-extra-form hidden><label>${ctx.esc(ctx.get('categories.new'))}`
      + `<input name="name" maxlength="120"></label>`
      + `<div class="dialog-actions"><button type="submit">${ctx.esc(ctx.get('common.create'))}</button></div></form>`
      + `<button type="button" class="ghost" data-extra-toggle>${ctx.esc(ctx.get('categories.new'))}</button>`,
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
// callers without a select (e.g. the transactions list category chip) just pass a callback.
export async function openCategoryPicker(ctx, onSelect, selectEl = null) {
  return openCombobox(ctx, {
    title: ctx.get('categories.pick'),
    searchPlaceholder: ctx.get('categories.pickSearch'),
    items: () => loadItems(ctx),
    selectEl,
    onSelect,
    extra: createSlot(ctx)
  });
}
