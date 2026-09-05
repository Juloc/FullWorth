// Audit log viewer (UI_UX_SPEC security/activity). Read-only, Owner-only list of recorded actions in
// the space (who did what, when), newest first. Backend: GET /api/audit returns a bare array of
// { id, actorUserId?, action, entityType, entityId?, occurredAt }. It supports action/entityType exact
// filters, a capped row limit (max 500), and a keyset "before"+"beforeId" cursor for loading older
// pages. Action strings are stable technical identifiers (e.g. "budget.created"); we humanize them for
// display and for the filter dropdown labels rather than maintaining a per-action translation.

let ctx = null;
// Cursor of the last-loaded (oldest shown) row for the keyset "load older" fetch; null when at the end.
let cursor = null;
// Day-key of the last rendered row, so we emit one date header per calendar day across appended pages
// (reset on a fresh, non-append render).
let lastDay = null;

// Known audit action ids and entity types (a stable, curated set — the audit columns are free-form
// strings, so this drives the filter dropdowns; a new action added in code just won't be filterable
// until listed here, but still shows in the unfiltered list).
const ACTIONS = [
  'bank_connection.connected', 'bank_connection.reconnected', 'bank_connection.disconnected', 'bank_connection.error', 'bank_connection.synced',
  'external.write.used',
  'contract.created', 'contract.updated', 'contract.archived',
  'budget.created', 'budget.updated', 'budget.archived',
  'category.created', 'category.updated', 'category.archived', 'category.unarchived',
  'category.rule.created', 'category.rule.updated', 'category.rules.reapplied',
  'account.ownership.granted', 'account.ownership.revoked',
  'asset.created', 'asset.updated', 'liability.created', 'liability.updated',
  'space.created', 'space.member.added', 'space.member.removed',
];
const ENTITY_TYPES = [
  'BankConnection', 'BankingIngestion', 'RecurringContract', 'Budget', 'FinanceCategory',
  'CategorizationRule', 'AccountOwner', 'Asset', 'Liability', 'FullWorthSpaceMember', 'FullWorthSpace',
];

export function bindAudit(context) {
  ctx = context;
  populate(ctx.$('#audit-action'), ACTIONS, v => humanize(v));
  populate(ctx.$('#audit-entity-type'), ENTITY_TYPES, v => v);
  ctx.$('#audit-action')?.addEventListener('change', () => renderAudit(ctx));
  ctx.$('#audit-entity-type')?.addEventListener('change', () => renderAudit(ctx));
  ctx.$('#audit-limit')?.addEventListener('change', () => renderAudit(ctx));
  ctx.$('#audit-more')?.addEventListener('click', () => { if (cursor) fetchPage(true); });
}

// Fill a filter <select> with an "all" option followed by one option per value (value = raw id, label
// via the given formatter). Guarded so re-binding does not duplicate options.
function populate(select, values, label) {
  if (!select || select.dataset.filled) return;
  select.innerHTML = `<option value="">${ctx.esc(ctx.get('common.all'))}</option>` +
    values.map(v => `<option value="${ctx.esc(v)}">${ctx.esc(label(v))}</option>`).join('');
  select.dataset.filled = '1';
}

export async function renderAudit(context) {
  ctx = context;
  cursor = null;
  await fetchPage(false);
}

async function fetchPage(append) {
  const list = ctx.$('#audit-list');
  if (!list) return;
  const limit = ctx.$('#audit-limit')?.value || '100';
  const params = new URLSearchParams({ limit });
  const action = ctx.$('#audit-action')?.value;
  const entityType = ctx.$('#audit-entity-type')?.value;
  if (action) params.set('action', action);
  if (entityType) params.set('entityType', entityType);
  if (append && cursor) { params.set('before', cursor.before); params.set('beforeId', cursor.beforeId); }

  let events;
  try { events = (await ctx.api(`api/audit?${params}`)) || []; }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }

  if (!append) {
    list.innerHTML = '';
    lastDay = null;
    if (!events.length) {
      list.innerHTML = `<div class="audit-empty state-empty">`
        + `<svg class="audit-empty-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M8 4h6l4 4v12H6V6"/><path d="M14 4v4h4"/><path d="M9.5 13h5M9.5 16.5h3"/></svg>`
        + `<div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div>`
        + `</div>`;
    }
  }
  const frag = document.createDocumentFragment();
  for (const e of events) {
    // Group consecutive rows under a per-day header for a scannable timeline; the cursor/paging logic
    // still works off the raw events array, so grouping is purely presentational.
    const key = dayKey(e.occurredAt);
    if (key !== lastDay) { lastDay = key; frag.appendChild(dayHeader(dayLabel(e.occurredAt))); }
    frag.appendChild(rowFor(e));
  }
  list.appendChild(frag);

  // A full page means more may exist → keep a cursor on the oldest row and show "load older".
  const more = ctx.$('#audit-more');
  if (events.length === Number(limit)) {
    const last = events[events.length - 1];
    cursor = { before: last.occurredAt, beforeId: last.id };
    if (more) more.hidden = false;
  } else {
    cursor = null;
    if (more) more.hidden = true;
  }
}

// "bank_connection.disconnected" -> "Bank connection disconnected". Language-agnostic humanization of a
// technical action id; the raw string stays recognizable while reading cleanly.
function humanize(action) {
  const words = String(action || '').replace(/[._]+/g, ' ').trim();
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : '—';
}

function actor(e) {
  if (!e.actorUserId) return ctx.get('audit.system');
  return `${ctx.get('audit.user')} ${String(e.actorUserId).slice(0, 8)}`;
}

function when(value) {
  if (!value) return '';
  try { return new Date(value).toLocaleString(); } catch { return String(value); }
}

// Time-of-day for a row (the calendar date lives in the day header); falls back to the full stamp.
function clock(value) {
  if (!value) return '';
  const d = new Date(value);
  if (isNaN(d)) return String(value);
  try { return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); }
  catch { return when(value); }
}

// Stable per-calendar-day key used to decide when to emit a new day header.
function dayKey(value) {
  if (!value) return '';
  const d = new Date(value);
  return isNaN(d) ? String(value) : d.toDateString();
}

// Localized, human date shown in a day header (e.g. "Fr., 5. Sept. 2026").
function dayLabel(value) {
  if (!value) return '—';
  const d = new Date(value);
  if (isNaN(d)) return String(value);
  try { return d.toLocaleDateString(undefined, { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric' }); }
  catch { return d.toDateString(); }
}

// A date divider separating the timeline into calendar days.
function dayHeader(label) {
  const el = document.createElement('div');
  el.className = 'audit-day';
  el.innerHTML = `<span class="audit-day-label">${ctx.esc(label)}</span><span class="audit-day-line" aria-hidden="true"></span>`;
  return el;
}

function rowFor(e) {
  const row = document.createElement('div');
  row.className = 'row audit-row';
  const entity = e.entityId ? `${ctx.esc(e.entityType)} · ${ctx.esc(String(e.entityId).slice(0, 8))}` : ctx.esc(e.entityType || '');
  const meta = entity ? `${ctx.esc(actor(e))} · ${entity}` : ctx.esc(actor(e));
  row.innerHTML = `
    <div class="audit-mark" aria-hidden="true"></div>
    <div class="row-main">
      <div class="row-title">${ctx.esc(humanize(e.action))}</div>
      <div class="row-sub audit-meta">${meta}</div>
    </div>
    <div class="row-sub audit-time" title="${ctx.esc(when(e.occurredAt))}">${ctx.esc(clock(e.occurredAt))}</div>`;
  return row;
}
