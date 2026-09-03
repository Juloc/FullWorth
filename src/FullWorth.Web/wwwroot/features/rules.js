// Categorization rules (UI_UX_SPEC §11). A rule matches transactions on a set of ANDed conditions
// (text field/mode/pattern, direction, amount range, MCC) and applies an action (assign a category,
// optionally mark as transfer, optionally stop further rules). The builder previews live how many
// existing transactions a draft would match before it is saved (§11.3), and rules can be edited,
// enabled/disabled and re-applied to history. Backend: /api/categorization-rules (GET/POST/PUT),
// /preview (dry-run of a draft), /reapply (apply the whole set to existing transactions).

let ctx = null;

const FIELDS = ['any', 'counterparty', 'normalized_counterparty', 'description', 'mcc'];
const MODES = ['contains', 'equals', 'starts_with', 'ends_with'];
const DIRECTIONS = ['any', 'expense', 'income'];
// The backend treats any match field that isn't one of the four specific ones (including its own
// "combined" default) as "search all fields" — the frontend's canonical value for that is "any", so
// normalize unknown/legacy values to it for both display and the edit <select>.
const fieldKey = f => FIELDS.includes(f) ? f : 'any';

export function bindRules(context) {
  ctx = context;
  ctx.$('[data-action="new-rule"]').addEventListener('click', () => openRuleDialog());
  ctx.$('#rules-reapply')?.addEventListener('click', reapply);
}

// Opens the create dialog; used by the page-header primary action.
export function newRule(context) { if (context) ctx = context; return openRuleDialog(); }

export async function renderRules(context) {
  ctx = context;
  const rows = (await ctx.api('api/categorization-rules')) || [];
  const list = ctx.$('#rules-list');
  list.innerHTML = '';
  if (!rows.length) {
    list.innerHTML = `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`;
    return;
  }
  const frag = document.createDocumentFragment();
  for (const r of rows) {
    const row = document.createElement('div');
    row.className = 'row rule-row' + (r.isEnabled ? '' : ' rule-off');
    row.innerHTML = `
      <div class="row-main">
        <div class="row-title">${ctx.esc(r.name)} ${r.isEnabled ? '' : `<span class="tx-marker">${ctx.esc(ctx.get('rules.disabled'))}</span>`}</div>
        <div class="row-sub">${ctx.esc(conditionSummary(r))}</div>
      </div>
      <div class="row-side rule-actions">
        <span class="rule-prio">${ctx.esc(ctx.get('rules.priority'))} ${r.priority}</span>
        <button class="icon-button" data-toggle aria-label="${ctx.esc(ctx.get(r.isEnabled ? 'rules.disable' : 'rules.enable'))}" title="${ctx.esc(ctx.get(r.isEnabled ? 'rules.disable' : 'rules.enable'))}">${r.isEnabled ? '⏸' : '▶'}</button>
        <button class="icon-button" data-edit aria-label="${ctx.esc(ctx.get('rules.edit'))}" title="${ctx.esc(ctx.get('rules.edit'))}">✎</button>
      </div>`;
    row.querySelector('[data-edit]').addEventListener('click', () => openRuleDialog(r));
    row.querySelector('[data-toggle]').addEventListener('click', () => toggle(r));
    frag.appendChild(row);
  }
  list.appendChild(frag);
}

function conditionSummary(r) {
  const parts = [];
  if (r.pattern) parts.push(`${ctx.get('rules.field_' + fieldKey(r.matchField))} ${ctx.get('rules.mode_' + (r.matchMode || 'contains'))} „${r.pattern}"`);
  if (r.direction && r.direction !== 'any') parts.push(ctx.get('rules.direction_' + r.direction));
  if (r.minAmount != null || r.maxAmount != null) {
    const lo = r.minAmount != null ? r.minAmount : '';
    const hi = r.maxAmount != null ? r.maxAmount : '';
    parts.push(`${lo}–${hi} €`);
  }
  if (r.merchantCategoryCode) parts.push(`MCC ${r.merchantCategoryCode}`);
  const action = [];
  if (r.markAsTransfer) action.push(ctx.get('rules.markTransfer'));
  if (r.stopProcessing) action.push(ctx.get('rules.stop'));
  const head = parts.length ? parts.join(' · ') : ctx.get('rules.matchAll');
  return action.length ? `${head} → ${action.join(', ')}` : head;
}

// Toggle enable/disable by re-writing the rule with the flag flipped (no dedicated endpoint needed).
async function toggle(r) {
  try {
    await ctx.api(`api/categorization-rules/${r.id}`, jsonRule({ ...ruleToDraft(r), isEnabled: !r.isEnabled }, 'PUT'));
    await renderRules(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

async function reapply() {
  if (!await ctx.confirm(ctx.get('rules.reapplyConfirm'), { confirmLabel: ctx.get('rules.reapply') })) return;
  try {
    const res = await ctx.api('api/categorization-rules/reapply?apply=true', { method: 'POST' });
    ctx.toast(ctx.get('rules.reapplyDone').replace('{changed}', res?.changed ?? 0).replace('{evaluated}', res?.evaluated ?? 0));
    await renderRules(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

function ruleToDraft(r) {
  return {
    name: r.name || '', isEnabled: r.isEnabled !== false, priority: r.priority ?? 100, target: 'transaction',
    matchField: fieldKey(r.matchField), matchMode: r.matchMode || 'contains', pattern: r.pattern || '',
    direction: r.direction || 'any', minAmount: r.minAmount ?? null, maxAmount: r.maxAmount ?? null,
    merchantCategoryCode: r.merchantCategoryCode || null, categoryId: r.categoryId || null,
    markAsTransfer: !!r.markAsTransfer, stopProcessing: !!r.stopProcessing
  };
}

function jsonRule(draft, method) {
  return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(draft) };
}

const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

async function openRuleDialog(existing) {
  let options;
  try { options = await ctx.categoryOptions(); } catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  const r = existing || {};
  const opt = (list, sel, prefix) => list.map(v => `<option value="${v}"${sel === v ? ' selected' : ''}>${ctx.esc(ctx.get(prefix + v))}</option>`).join('');
  const dlg = ctx.dialog(`<form class="dialog-card rule-dialog">
    <div class="panel-head"><h2>${ctx.esc(ctx.get(existing ? 'rules.edit' : 'rules.new'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="160" value="${ctx.esc(r.name || '')}"></label>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('rules.field'))}<select name="field">${opt(FIELDS, r.matchField || 'any', 'rules.field_')}</select></label>
      <label>${ctx.esc(ctx.get('rules.mode'))}<select name="mode">${opt(MODES, r.matchMode || 'contains', 'rules.mode_')}</select></label>
    </div>
    <label>${ctx.esc(ctx.get('rules.pattern'))}<input name="pattern" maxlength="200" value="${ctx.esc(r.pattern || '')}"></label>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('rules.direction'))}<select name="direction">${opt(DIRECTIONS, r.direction || 'any', 'rules.direction_')}</select></label>
      <label>${ctx.esc(ctx.get('rules.mcc'))}<input name="mcc" maxlength="8" value="${ctx.esc(r.merchantCategoryCode || '')}"></label>
    </div>
    <div class="rule-grid">
      <label>${ctx.esc(ctx.get('rules.minAmount'))}<input name="minAmount" type="number" step="0.01" min="0" value="${r.minAmount ?? ''}"></label>
      <label>${ctx.esc(ctx.get('rules.maxAmount'))}<input name="maxAmount" type="number" step="0.01" min="0" value="${r.maxAmount ?? ''}"></label>
    </div>
    <label>${ctx.esc(ctx.get('transactions.category'))}<select name="category" required>${options}</select></label>
    <label>${ctx.esc(ctx.get('rules.priority'))}<input name="priority" type="number" value="${r.priority ?? 100}" required></label>
    <label class="check"><input type="checkbox" name="markAsTransfer" ${r.markAsTransfer ? 'checked' : ''}> ${ctx.esc(ctx.get('rules.markTransfer'))}</label>
    <label class="check"><input type="checkbox" name="stopProcessing" ${r.stopProcessing ? 'checked' : ''}> ${ctx.esc(ctx.get('rules.stop'))}</label>
    <label class="check"><input type="checkbox" name="isEnabled" ${r.isEnabled === false ? '' : 'checked'}> ${ctx.esc(ctx.get('rules.enabled'))}</label>
    <div class="rule-preview" data-preview><div class="row-sub">${ctx.esc(ctx.get('rules.previewHint'))}</div></div>
    <div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get(existing ? 'common.apply' : 'common.create'))}</button></div>
  </form>`);
  if (r.categoryId) { const sel = dlg.querySelector('[name=category]'); if (sel) sel.value = r.categoryId; }
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();

  const form = dlg.querySelector('form');
  const previewBox = dlg.querySelector('[data-preview]');
  let timer = null;
  const schedulePreview = () => { clearTimeout(timer); timer = setTimeout(() => runPreview(form, previewBox), 350); };
  form.addEventListener('input', schedulePreview);
  form.addEventListener('change', schedulePreview);

  form.onsubmit = async e => {
    e.preventDefault();
    const draft = readDraft(form);
    if (!draft.categoryId || draft.categoryId === EMPTY_GUID) { ctx.toast(ctx.get('rules.categoryRequired')); return; }
    try {
      const path = existing ? `api/categorization-rules/${existing.id}` : 'api/categorization-rules';
      await ctx.api(path, jsonRule(draft, existing ? 'PUT' : 'POST'));
      dlg.close(); ctx.toast(ctx.get('common.saved')); await renderRules(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
  runPreview(form, previewBox);
}

function readDraft(form) {
  const fd = new FormData(form);
  const num = v => { const s = String(v ?? '').trim(); return s === '' ? null : Number(s); };
  return {
    name: fd.get('name') || 'draft', isEnabled: form.isEnabled.checked, priority: Number(fd.get('priority') || 100), target: 'transaction',
    matchField: fd.get('field'), matchMode: fd.get('mode'), pattern: (fd.get('pattern') || '').trim(),
    direction: fd.get('direction'), minAmount: num(fd.get('minAmount')), maxAmount: num(fd.get('maxAmount')),
    merchantCategoryCode: (fd.get('mcc') || '').trim() || null, categoryId: fd.get('category') || EMPTY_GUID,
    markAsTransfer: form.markAsTransfer.checked, stopProcessing: form.stopProcessing.checked
  };
}

async function runPreview(form, box) {
  const draft = readDraft(form);
  const hasCondition = draft.pattern || draft.minAmount != null || draft.maxAmount != null || draft.merchantCategoryCode || (draft.direction && draft.direction !== 'any');
  if (!hasCondition) { box.innerHTML = `<div class="row-sub">${ctx.esc(ctx.get('rules.previewHint'))}</div>`; return; }
  box.classList.add('is-loading');
  try {
    const res = await ctx.api('api/categorization-rules/preview', jsonRule(draft, 'POST'));
    const head = ctx.get('rules.previewCount').replace('{matched}', res.matched).replace('{evaluated}', res.evaluated) + (res.scanCapped ? ' (' + ctx.get('rules.previewCapped') + ')' : '');
    const items = (res.sample || []).map(s =>
      `<div class="preview-row"><span class="preview-label">${ctx.esc(s.date || '')} · ${ctx.esc(s.label || '—')}</span><span class="preview-amt">${ctx.money(s.amount, s.currency)}</span></div>`).join('');
    box.innerHTML = `<div class="preview-head">${ctx.esc(head)}</div>${items}`;
  } catch (err) {
    box.innerHTML = `<div class="row-sub">${ctx.esc(err.message || ctx.get('common.error'))}</div>`;
  } finally { box.classList.remove('is-loading'); }
}
