// Merchant registry (UI_UX_SPEC data management). A merchant groups the many raw counterparty strings
// banks send ("PAYPAL *SPOTIFY", "Spotify AB") under one canonical name via normalized aliases; the
// longest matching alias wins when resolving a transaction's counterparty. Backend: /api/merchants
// (GET list with aliases, POST create, PUT rename, DELETE, POST /{id}/merge), plus per-merchant alias
// add/remove. All writes require the space Owner role.

let ctx = null;
const trashIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m2 0v12a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V7"/></svg>';
const editIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20h4L18 10l-4-4L4 16v4Z"/><path d="M13.5 6.5 17.5 10.5"/></svg>';
const mergeIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 4v6a4 4 0 0 0 4 4h6"/><path d="M7 20V10"/><path d="m14 11 3 3-3 3"/></svg>';

export function bindMerchants(context) {
  ctx = context;
  ctx.$('[data-action="new-merchant"]')?.addEventListener('click', () => openMerchantDialog());
}

// Used by the page-header primary action.
export function newMerchant(context) { if (context) ctx = context; return openMerchantDialog(); }

export async function renderMerchants(context) {
  ctx = context;
  const list = ctx.$('#merchants-list');
  if (!list) return;
  let merchants;
  try { merchants = (await ctx.api('api/merchants')) || []; }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  list.innerHTML = '';
  if (!merchants.length) {
    list.innerHTML = `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`;
    return;
  }
  const frag = document.createDocumentFragment();
  for (const m of merchants) frag.appendChild(rowFor(m, merchants));
  list.appendChild(frag);
}

function rowFor(m, all) {
  const row = document.createElement('div');
  row.className = 'row merchant-row';
  const chips = (m.aliases || []).map(a =>
    `<span class="chip">${ctx.esc(a.normalizedAlias)}<button type="button" data-remove-alias="${a.id}" aria-label="${ctx.esc(ctx.get('merchants.removeAlias'))}" title="${ctx.esc(ctx.get('merchants.removeAlias'))}">×</button></span>`).join('');
  const canMerge = (all || []).length > 1;
  row.innerHTML = `
    <div class="row-main">
      <div class="row-title">${ctx.esc(m.name)}</div>
      <div class="chips">${chips}<button type="button" class="chip add" data-add-alias>+ ${ctx.esc(ctx.get('merchants.addAlias'))}</button></div>
    </div>
    <div class="row-side">
      <button type="button" class="icon-button" data-rename aria-label="${ctx.esc(ctx.get('merchants.rename'))}" title="${ctx.esc(ctx.get('merchants.rename'))}">${editIcon}</button>
      ${canMerge ? `<button type="button" class="icon-button" data-merge aria-label="${ctx.esc(ctx.get('merchants.mergeInto'))}" title="${ctx.esc(ctx.get('merchants.mergeInto'))}">${mergeIcon}</button>` : ''}
      <button type="button" class="icon-button" data-delete aria-label="${ctx.esc(ctx.get('common.delete'))}" title="${ctx.esc(ctx.get('common.delete'))}">${trashIcon}</button>
    </div>`;
  row.querySelector('[data-add-alias]').addEventListener('click', () => addAlias(m));
  row.querySelectorAll('[data-remove-alias]').forEach(b => b.addEventListener('click', () => removeAlias(m, b.dataset.removeAlias)));
  row.querySelector('[data-rename]').addEventListener('click', () => openRenameDialog(m));
  row.querySelector('[data-merge]')?.addEventListener('click', () => openMergeDialog(m, all));
  row.querySelector('[data-delete]').addEventListener('click', () => deleteMerchant(m));
  return row;
}

function openMerchantDialog() {
  const dlg = ctx.dialog(`<form class="dialog-card">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('merchants.new'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="200" autocomplete="off"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get('common.create'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const name = new FormData(e.currentTarget).get('name');
    try {
      await ctx.api('api/merchants', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) });
      dlg.close(); ctx.toast(ctx.get('common.saved')); await renderMerchants(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

function openRenameDialog(m) {
  const dlg = ctx.dialog(`<form class="dialog-card">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('merchants.renameTitle'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="200" autocomplete="off" value="${ctx.esc(m.name)}"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get('common.save'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const name = new FormData(e.currentTarget).get('name');
    try {
      await ctx.api(`api/merchants/${m.id}`, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name }) });
      dlg.close(); ctx.toast(ctx.get('common.saved')); await renderMerchants(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

// Merge this merchant (the source) into another the user picks (the target); the source is deleted and
// its aliases move to the target. Backend keeps the target: POST /api/merchants/{target}/merge {source}.
function openMergeDialog(m, all) {
  const others = (all || []).filter(x => x.id !== m.id);
  if (!others.length) { ctx.toast(ctx.get('merchants.mergeNone')); return; }
  const options = others.map(x => `<option value="${x.id}">${ctx.esc(x.name)}</option>`).join('');
  const dlg = ctx.dialog(`<form class="dialog-card">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('merchants.mergeTitle').replace('{name}', () => m.name))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('merchants.mergeTarget'))}<select name="target">${options}</select></label>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit" class="danger">${ctx.esc(ctx.get('merchants.merge'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const targetId = new FormData(e.currentTarget).get('target');
    if (!await ctx.confirm(ctx.get('merchants.mergeConfirm').replace(/\{name\}/g, () => m.name), { destructive: true, confirmLabel: ctx.get('merchants.merge') })) return;
    try {
      await ctx.api(`api/merchants/${targetId}/merge`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceMerchantId: m.id }) });
      dlg.close(); ctx.toast(ctx.get('common.saved')); await renderMerchants(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

// A merchant name/alias is a single free-text value, so a one-field prompt dialog is the whole flow.
function addAlias(m) {
  const dlg = ctx.dialog(`<form class="dialog-card">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('merchants.addAliasFor').replace('{name}', () => m.name))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('merchants.alias'))}<input name="alias" required maxlength="200" autocomplete="off"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get('common.add'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const alias = new FormData(e.currentTarget).get('alias');
    try {
      await ctx.api(`api/merchants/${m.id}/aliases`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ alias }) });
      dlg.close(); ctx.toast(ctx.get('common.saved')); await renderMerchants(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

async function removeAlias(m, aliasId) {
  try {
    await ctx.api(`api/merchants/${m.id}/aliases/${aliasId}`, { method: 'DELETE' });
    await renderMerchants(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

async function deleteMerchant(m) {
  if (!await ctx.confirm(ctx.get('merchants.deleteConfirm').replace('{name}', () => m.name), { destructive: true, confirmLabel: ctx.get('common.delete') })) return;
  try {
    await ctx.api(`api/merchants/${m.id}`, { method: 'DELETE' });
    ctx.toast(ctx.get('common.deleted')); await renderMerchants(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}
