// Turns the flat rows from `GET api/categories` into rows a combobox can render (#121): sorted by
// full path, indented one level per ancestor, the full path kept as a search-and-display hint, and an
// icon that falls back to the nearest ancestor's own icon.
//
// This is the category picker's path-building logic (pages/transactions/category-picker.js), pulled
// out so every other category select (parent pickers, rule/budget/filter dialogs, purchase item rows)
// can use the same one instead of a sixth copy of it.
//
// Pure data transformer: no `ctx.api`, no server route, no page import - a component knows neither
// (FrontendStructureGuardTests). A caller fetches `api/categories` itself and hands the raw array in.

import { categoryIconInner } from './icons.js';

/**
 * @param {Array<{id:string,name:string,parentId?:string|null,icon?:string|null,isArchived?:boolean}>} categories
 * @param {{ exclude?: Set<string>|null, allLabel?: string|null }} [options]
 *   `exclude`: ids to drop entirely - a category and its own descendants, for a "choose a parent"
 *   picker that must not let a category become its own ancestor (callers also fold in whatever else
 *   they consider invalid, e.g. archived categories).
 *   `allLabel`: when set, prepends a `{id:'', ...}` reset/no-filter row.
 * @returns {Array<{id:string,label:string,hint:string|null,depth:number,iconHtml:string}>}
 */
export function categoryComboboxItems(categories, { exclude = null, allLabel = null } = {}) {
  const byId = new Map(categories.map(category => [category.id, category]));
  const items = categories
    .filter(category => !exclude || !exclude.has(category.id))
    .map(category => {
      const chain = chainOf(category, byId);
      const path = chain.map(node => node.name).join(' › ');
      return {
        id: category.id,
        label: category.name,
        hint: chain.length > 1 ? path : null,
        depth: chain.length - 1,
        iconHtml: categoryIconInner(category.icon || inheritedIcon(chain)),
        sortKey: path
      };
    })
    .sort((a, b) => a.sortKey.localeCompare(b.sortKey))
    .map(({ sortKey, ...item }) => item);

  return allLabel ? [{ id: '', label: allLabel, hint: null, iconHtml: null, depth: 0 }, ...items] : items;
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

// A subcategory without its own icon shows its parent's, instead of none - otherwise a tree shows one
// icon at the top and empty slots everywhere else.
function inheritedIcon(chain) {
  for (let index = chain.length - 1; index >= 0; index--) if (chain[index].icon) return chain[index].icon;
  return null;
}
