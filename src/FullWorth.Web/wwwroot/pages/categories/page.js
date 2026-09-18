import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { emptyRow } from '../../components/empty.js';
import { categoryIconInner, categoryIconPicker, selectedIconKey } from '../../components/icons.js';
// Der "Übergeordnet"-Select trägt hier denselben Baum wie überall sonst (#157) - ohne Suche musste man
// ihn beim Anlegen/Verschieben einer Unterkategorie in einem tief verschachtelten Baum durchscrollen.
import { attachCombobox } from '../../components/combobox.js';
import { categoryComboboxItems } from '../../components/category-combobox.js';
// Category tree (UI_UX_SPEC §10). Hierarchical view with expand/collapse; each node can be renamed,
// re-iconed and MOVED to another parent (accessible explicit Move via the edit dialog, §10.2), or
// archived (§10.4). Archived categories stay on history and are hidden unless "Show archived" is on.
// Backend: GET /api/categories (+includeArchived), POST (create), PUT {id} (rename/icon/move),
// DELETE {id} (archive). No unarchive endpoint yet — flagged, not invented.

let ctx = null;
const collapsed = new Set(); // category ids collapsed by the user this session

export function bindCategories(context) {
  ctx = context;
  ctx.$('#cat-archived').addEventListener('change', () => renderCategories(ctx));
}

export async function newCategory(context) {
  ctx = context;
  let options, categories;
  try {
    [options, categories] = await Promise.all([ctx.categoryOptions(), ctx.api('api/categories')]);
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  const dlg = ctx.dialog(`<form class="dialog-card">
    <h2>${ctx.esc(ctx.get('categories.new'))}</h2>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="120"></label>
    <label>${ctx.esc(ctx.get('categories.icon'))}<span data-icon-picker></span></label>
    <label>${ctx.esc(ctx.get('categories.parent'))}
      <select name="parent"><option value="">${ctx.esc(ctx.get('categories.topLevel'))}</option>${options}</select>
    </label>
    <div class="dialog-actions">
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button>
      <button type="submit" class="${buttonClass(ButtonRole.Primary)}">${ctx.esc(ctx.get('common.create'))}</button>
    </div>
  </form>`);

  const iconPicker = categoryIconPicker(null, { none: ctx.get('categories.iconNone') });
  dlg.querySelector('[data-icon-picker]').replaceWith(iconPicker);
  attachCombobox(ctx, dlg.querySelector('select[name="parent"]'), {
    title: ctx.get('categories.parent'),
    anchored: true,
    items: () => categoryComboboxItems(categories)
  });
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const name = String(form.get('name') || '').trim();
    const key = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') || `cat-${Date.now()}`;

    try {
      await ctx.api('api/categories', ctx.jsonBody({
        key,
        name,
        parentId: form.get('parent') || null,
        icon: selectedIconKey(iconPicker),
        sortOrder: null
      }));
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderCategories(ctx);
    } catch (error) {
      ctx.toast(error.message || ctx.get('common.error'));
    }
  };

  dlg.showModal();
}

export async function renderCategories(context) {
  ctx = context;
  const showArchived = ctx.$('#cat-archived').checked;
  const rows = await ctx.api(`api/categories${showArchived ? '?includeArchived=true' : ''}`);
  const tree = ctx.$('#categories-tree');
  const all = rows || [];
  const byParent = new Map();
  for (const c of all) {
    const key = c.parentId || '__root';
    if (!byParent.has(key)) byParent.set(key, []);
    byParent.get(key).push(c);
  }
  for (const list of byParent.values()) list.sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0) || a.name.localeCompare(b.name));

  tree.innerHTML = '';
  const roots = byParent.get('__root') || [];
  if (!roots.length) { tree.innerHTML = emptyRow(ctx.get('common.empty')); return; }
  const frag = document.createDocumentFragment();
  frag.appendChild(summaryStrip(all, roots));
  let catSeq = 0;
  for (const root of roots) renderNode(root, byParent, frag, 0, all, (catSeq++ % 8) + 1);
  tree.appendChild(frag);
}

// Small header/summary strip above the tree. Counts only; labels reuse existing i18n keys.
function summaryStrip(all, roots) {
  const archived = all.filter(c => c.isArchived).length;
  const showArchived = ctx.$('#cat-archived').checked;
  const item = (n, key) => `<div class="cat-sum-item"><strong>${n}</strong><span>${ctx.esc(ctx.get(key))}</span></div>`;
  const el = document.createElement('div');
  el.className = 'cat-summary';
  el.innerHTML = item(all.length, 'categories.title')
    + item(roots.length, 'categories.topLevel')
    + (showArchived && archived ? item(archived, 'categories.archived') : '');
  return el;
}

function renderNode(node, byParent, parent, depth, all, catIndex) {
  const children = byParent.get(node.id) || [];
  const row = document.createElement('div');
  row.className = 'cat-node' + (node.isArchived ? ' cat-archived' : '');
  row.dataset.categoryId = node.id;
  row.dataset.categoryName = node.name;
  row.style.setProperty('--depth', depth);
  const isCollapsed = collapsed.has(node.id);
  row.innerHTML = `
    <div class="cat-row">
      <button class="cat-twist" ${children.length ? '' : 'disabled'} aria-label="${ctx.esc(ctx.get(isCollapsed ? 'categories.expand' : 'categories.collapse'))}">${children.length ? (isCollapsed ? '▸' : '▾') : '·'}</button>
      <span class="cat-dot" data-cat="${catIndex}" aria-hidden="true"></span>
      <span class="cat-icon" aria-hidden="true">${categoryIconInner(node.icon) || ''}</span><span class="cat-name">${ctx.esc(node.name)}${node.isArchived ? ` <span class="tx-marker">${ctx.esc(ctx.get('categories.archived'))}</span>` : ''}</span>
      <span class="cat-actions">
        <button class="${buttonClass(ButtonRole.Icon)}" data-edit aria-label="${ctx.esc(ctx.get('categories.edit'))}" title="${ctx.esc(ctx.get('categories.edit'))}">✎</button>
        ${node.isArchived
          ? `<button class="${buttonClass(ButtonRole.Icon)}" data-restore aria-label="${ctx.esc(ctx.get('categories.restore'))}" title="${ctx.esc(ctx.get('categories.restore'))}">↩</button>`
          : `<button class="${buttonClass(ButtonRole.Icon)}" data-archive aria-label="${ctx.esc(ctx.get('categories.archive'))}" title="${ctx.esc(ctx.get('categories.archive'))}">🗄</button>`}
      </span>
    </div>`;
  row.querySelector('.cat-twist').addEventListener('click', () => {
    if (!children.length) return;
    if (collapsed.has(node.id)) collapsed.delete(node.id); else collapsed.add(node.id);
    renderCategories(ctx);
  });
  row.querySelector('[data-edit]').addEventListener('click', () => openEdit(node, all));
  row.querySelector('[data-archive]')?.addEventListener('click', () => archive(node));
  row.querySelector('[data-restore]')?.addEventListener('click', () => restore(node));
  parent.appendChild(row);
  if (!isCollapsed) for (const child of children) renderNode(child, byParent, parent, depth + 1, all, catIndex);
}

// Parent candidates exclude the node itself, its descendants (can't move under its own subtree) and
// archived categories (not a valid new parent). Shared by the <select> markup and the combobox items
// so the two never drift into showing different choices.
function bannedParents(node, all) {
  const banned = new Set([node.id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const c of all) if (c.parentId && banned.has(c.parentId) && !banned.has(c.id)) { banned.add(c.id); grew = true; }
  }
  for (const c of all) if (c.isArchived) banned.add(c.id);
  return banned;
}

function parentOptions(node, all, selected) {
  const banned = bannedParents(node, all);
  return all.filter(c => !banned.has(c.id))
    .map(c => `<option value="${c.id}"${selected === c.id ? ' selected' : ''}>${ctx.esc(c.name)}</option>`).join('');
}

function openEdit(node, all) {
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><h2>${ctx.esc(ctx.get('categories.edit'))}</h2><button type="button" data-close>×</button></div>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="120" value="${ctx.esc(node.name)}"></label>
    <label>${ctx.esc(ctx.get('categories.icon'))}<span data-icon-picker></span></label>
    <label>${ctx.esc(ctx.get('categories.parent'))}<select name="parent"><option value="">${ctx.esc(ctx.get('categories.topLevel'))}</option>${parentOptions(node, all, node.parentId)}</select></label>
    <div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${ctx.esc(ctx.get('common.apply'))}</button></div></form>`);
  const iconPicker = categoryIconPicker(node.icon, { none: ctx.get('categories.iconNone') });
  dlg.querySelector('[data-icon-picker]').replaceWith(iconPicker);
  attachCombobox(ctx, dlg.querySelector('select[name="parent"]'), {
    title: ctx.get('categories.parent'),
    anchored: true,
    items: () => categoryComboboxItems(all, { exclude: bannedParents(node, all) })
  });
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    try {
      await ctx.api(`api/categories/${node.id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: fd.get('name'), parentId: fd.get('parent') || null, icon: selectedIconKey(iconPicker), sortOrder: node.sortOrder ?? null }) });
      dlg.close(); ctx.toast(ctx.get('common.saved')); await renderCategories(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

async function archive(node) {
  if (!await ctx.confirm(ctx.get('categories.archiveConfirm').replace('{name}', node.name), { destructive: true, confirmLabel: ctx.get('categories.archive') })) return;
  try {
    await ctx.api(`api/categories/${node.id}`, { method: 'DELETE' });
    ctx.toast(ctx.get('categories.archived')); await renderCategories(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

async function restore(node) {
  try {
    await ctx.api(`api/categories/${node.id}/restore`, { method: 'POST' });
    ctx.toast(ctx.get('categories.restored')); await renderCategories(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}
