import { ButtonRole, buttonClass } from '../ui/buttons.js';
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
  let options;
  try {
    options = await ctx.categoryOptions();
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  const dlg = ctx.dialog(`<form class="dialog-card">
    <h2>${ctx.esc(ctx.get('categories.new'))}</h2>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="120"></label>
    <label>${ctx.esc(ctx.get('categories.icon'))}<input name="icon" maxlength="8" placeholder="🏷️"></label>
    <label>${ctx.esc(ctx.get('categories.parent'))}
      <select name="parent"><option value="">${ctx.esc(ctx.get('categories.topLevel'))}</option>${options}</select>
    </label>
    <div class="dialog-actions">
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button>
      <button type="submit" class="${buttonClass(ButtonRole.Primary)}">${ctx.esc(ctx.get('common.create'))}</button>
    </div>
  </form>`);

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
        icon: form.get('icon') || null,
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
  if (!roots.length) { tree.innerHTML = `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`; return; }
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
      <span class="cat-name">${node.icon ? ctx.esc(node.icon) + ' ' : ''}${ctx.esc(node.name)}${node.isArchived ? ` <span class="tx-marker">${ctx.esc(ctx.get('categories.archived'))}</span>` : ''}</span>
      <span class="cat-actions">
        <button class="icon-button" data-edit aria-label="${ctx.esc(ctx.get('categories.edit'))}" title="${ctx.esc(ctx.get('categories.edit'))}">✎</button>
        ${node.isArchived
          ? `<button class="icon-button" data-restore aria-label="${ctx.esc(ctx.get('categories.restore'))}" title="${ctx.esc(ctx.get('categories.restore'))}">↩</button>`
          : `<button class="icon-button" data-archive aria-label="${ctx.esc(ctx.get('categories.archive'))}" title="${ctx.esc(ctx.get('categories.archive'))}">🗄</button>`}
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

// Parent options exclude the node itself and its descendants (can't move under its own subtree).
function parentOptions(node, all, selected) {
  const banned = new Set([node.id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const c of all) if (c.parentId && banned.has(c.parentId) && !banned.has(c.id)) { banned.add(c.id); grew = true; }
  }
  return all.filter(c => !banned.has(c.id) && !c.isArchived)
    .map(c => `<option value="${c.id}"${selected === c.id ? ' selected' : ''}>${ctx.esc(c.name)}</option>`).join('');
}

function openEdit(node, all) {
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><h2>${ctx.esc(ctx.get('categories.edit'))}</h2><button type="button" data-close>×</button></div>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="120" value="${ctx.esc(node.name)}"></label>
    <label>${ctx.esc(ctx.get('categories.icon'))}<input name="icon" maxlength="8" value="${ctx.esc(node.icon || '')}"></label>
    <label>${ctx.esc(ctx.get('categories.parent'))}<select name="parent"><option value="">${ctx.esc(ctx.get('categories.topLevel'))}</option>${parentOptions(node, all, node.parentId)}</select></label>
    <div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${ctx.esc(ctx.get('common.apply'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const fd = new FormData(e.currentTarget);
    try {
      await ctx.api(`api/categories/${node.id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: fd.get('name'), parentId: fd.get('parent') || null, icon: fd.get('icon') || null, sortOrder: node.sortOrder ?? null }) });
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
