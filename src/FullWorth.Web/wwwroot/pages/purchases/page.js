import { initializePurchaseEnhancements } from './gpt-normal.js';
import { identityIcon, ensureOfficialBrandCatalog } from '../../features/ux-kit.js';
import { emptyRow } from '../../components/empty.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { confirmDialog, confirmMessage } from '../../components/confirm.js';
import { createSelectionList } from '../../components/selection-list.js';
import { spriteHref } from '../../components/sprite.js';
import { openReview } from './review.js';

// Purchases & receipts (UI_UX_SPEC §16). Amazon orders use the same Purchase/PurchaseItem model as
// scanned receipts. The Amazon connector only supplies source data; review, categories and bank
// reconciliation remain one shared FullWorth flow.

let ctx = null;

// Die Tabelle der Kaeufe: Spalten stehen in page.css, Kopf, Zeilen und Platzhalter in styles/data-table.css.
// Der Server blaettert (limit/offset) und zaehlt (counts); die Seite laedt die naechste Seite, sobald das Ende
// der Liste in die Naehe des Bildschirms kommt.
const PAGE = 40;
let purchaseRows = [];
let hasMore = false;
let loading = false;
let drawnList = '';
let selected = new Set();
// Haken, Gesamt-Haken und Zaehlung kommen vom gemeinsamen Baustein; die Seite haelt nur die Ids, damit die
// Auswahl ein Neuzeichnen der Tabelle ueberlebt. Gewaehlt werden kann, was noch nicht bestaetigt ist.
const selection = createSelectionList({
  onChange: ({ selectedIds }) => {
    selected = new Set(selectedIds);
    for (const row of document.querySelectorAll('#purchases-list .dt-row'))
      row.classList.toggle('is-selected', selected.has(row.dataset.purchase));
    showPurchaseBar();
  }
});

export function bindPurchases(context) {
  ctx = context;
  initializePurchaseEnhancements(ctx);
  const reload = () => { purchaseRows = []; selected = new Set(); drawnList = ''; renderPurchases(ctx); };
  ctx.$('#purchase-source').addEventListener('change', reload);
  ctx.$('#purchase-scope').addEventListener('change', reload);
  ctx.$('#purchases-list').addEventListener('click', event => {
    const row = event.target.closest('.dt-row');
    if (row && !event.target.closest('.fw-check')) openDetail(row.dataset.purchase);
  });
  new IntersectionObserver(entries => { if (entries[0].isIntersecting) morePurchases().catch(() => {}); }, { rootMargin: '480px' })
    .observe(ctx.$('#purchases-sentinel'));
  document.getElementById('action-bar-selection').addEventListener('click', event => {
    if (event.target.closest('[data-bar="confirm"]')) confirmSelectedPurchases().catch(err => ctx.toast(err.message || ctx.get('common.error')));
  });
  // Die Aktion der Seite steht in der Aktionsleiste. Belege annehmen (Scannen, Kamera, Import) ist Sache der Belege-Seite.
  const amazon = document.createElement('button');
  amazon.id = 'amazon-import';
  amazon.type = 'button';
  amazon.className = buttonClass(ButtonRole.Secondary);
  amazon.innerHTML = `<svg viewBox="0 0 24 24"><use href="${spriteHref('ui-cart')}"></use></svg><span class="action-label"></span>`;
  amazon.addEventListener('click', openAmazonConnection);
  document.getElementById('action-bar-extra').append(amazon);
}

function listQuery(offset, limit) {
  const query = new URLSearchParams({ limit, offset });
  for (const [name, id] of [['source', '#purchase-source'], ['scope', '#purchase-scope']]) {
    const value = ctx.$(id).value;
    if (value) query.set(name, value);
  }
  return `api/purchases?${query}`;
}

export async function renderPurchases(context) {
  ctx = context;
  await ensureOfficialBrandCatalog(ctx.api);
  refreshAmazonButton().catch(() => {});
  const source = ctx.$('#purchase-source').value;
  if (!purchaseRows.length) { loading = true; drawPurchases(); }
  try {
    const limit = Math.min(100, Math.max(PAGE, purchaseRows.length));
    const [counts, rows] = await Promise.all([
      ctx.api(`api/purchases/counts${source ? `?source=${encodeURIComponent(source)}` : ''}`),
      ctx.api(listQuery(0, limit))
    ]);
    showScopeCounts(counts);
    purchaseRows = rows || [];
    hasMore = purchaseRows.length === limit;
  } finally {
    loading = false;
  }
  for (const id of selected) if (!purchaseRows.some(row => row.id === id)) selected.delete(id);
  drawPurchases();
}

async function morePurchases() {
  if (loading || !hasMore) return;
  loading = true;
  drawPurchases();
  try {
    const rows = (await ctx.api(listQuery(purchaseRows.length, PAGE))) || [];
    purchaseRows = purchaseRows.concat(rows);
    hasMore = rows.length === PAGE;
  } finally {
    loading = false;
    drawPurchases();
  }
}

// Die Zahlen stehen an den Auswahlpunkten des Filters.
function showScopeCounts(counts) {
  const labels = { review: ['purchases.needsReview', counts.review], unlinked: ['purchases.unlinked', counts.unlinked] };
  for (const option of ctx.$('#purchase-scope').options)
    if (labels[option.value]) option.textContent = `${ctx.get(labels[option.value][0])} (${labels[option.value][1]})`;
}

function drawPurchases() {
  const list = ctx.$('#purchases-list');
  const html = loading && !purchaseRows.length
    ? Array.from({ length: 4 }, () => '<div class="dt-skeleton"><span class="dt-sk-line shimmer"></span><span class="dt-sk-line shimmer"></span></div>').join('')
    : (purchaseRows.length
      ? purchaseHead() + purchaseRows.map(purchaseRow).join('')
      : emptyRow(ctx.get('common.empty'))) + (loading ? '<div class="dt-skeleton"><span class="dt-sk-line shimmer"></span><span class="dt-sk-line shimmer"></span></div>' : '');
  if (html !== drawnList) {
    drawnList = html;
    list.innerHTML = html;
    selection.mount(list);
  }
  showPurchaseBar();
}

function purchaseHead() {
  return `<div class="dt-head">
    <label class="fw-check"><input type="checkbox" data-select-all></label>
    <span>${ctx.esc(ctx.get('purchases.title'))}</span>
    <span class="dt-wide">${ctx.esc(t('Datum', 'Date'))}</span>
    <span class="dt-wide">${ctx.esc(ctx.get('purchases.items'))}</span>
    <span class="dt-wide">${ctx.esc(t('Quelle', 'Source'))}</span>
    <span class="dt-wide"></span>
    <span class="dt-right">${ctx.esc(t('Betrag', 'Amount'))}</span>
  </div>`;
}

// A word-label pill hung on a purchase name (Design System §10, matching the transactions markers):
// amber "Prüfen" while a purchase is unconfirmed, neutral "Nicht verknüpft" while a confirmed receipt
// still has no bank booking. Confirmed & linked purchases carry no pill, keeping the list calm.
function attentionTag(x) {
  if (x.status !== 'confirmed')
    return `<span class="purchase-tag purchase-tag--review">${ctx.esc(ctx.get('purchases.review'))}</span>`;
  const linked = x.source === 'amazon' ? true : !!x.transactionId;
  if (!linked)
    return `<span class="purchase-tag purchase-tag--unlinked">${ctx.esc(ctx.get('purchases.unlinked'))}</span>`;
  return '';
}

function purchaseRow(x) {
  const name = x.merchant || x.externalOrderId || ctx.get('purchases.receipt');
  const source = x.source === 'amazon' ? 'Amazon' : ctx.get('purchases.receipt');
  const date = ctx.esc(ctx.date(x.purchaseDate));
  const count = (x.items || []).length;
  const tag = attentionTag(x);
  const order = x.source === 'amazon' && x.externalOrderId ? ctx.esc(x.externalOrderId) : '';
  const check = x.status !== 'confirmed'
    ? `<label class="fw-check"><input type="checkbox" data-select-item value="${ctx.esc(x.id)}"${selected.has(x.id) ? ' checked' : ''}></label>`
    : '<span></span>';
  return `<div class="dt-row is-clickable${selected.has(x.id) ? ' is-selected' : ''}" data-purchase="${ctx.esc(x.id)}">
    ${check}
    <span class="dt-cell dt-lead"><span class="purchase-ident">${identityIcon(name, { categoryIconKey: x.categoryIconKey })}</span>
      <span class="dt-main"><span class="dt-name">${ctx.esc(name)}</span>${order ? `<span class="dt-sub">${order}</span>` : ''}
        <span class="dt-meta">${date} · ${count} ${ctx.esc(ctx.get('purchases.items'))}${tag ? ` · ${tag}` : ''}</span></span></span>
    <span class="dt-cell dt-wide">${date}</span>
    <span class="dt-cell dt-wide">${count}</span>
    <span class="dt-cell dt-wide">${ctx.esc(source)}</span>
    <span class="dt-cell dt-wide">${tag}</span>
    <span class="dt-cell dt-right amount">${ctx.money(x.totalAmount, x.currency)}</span>
  </div>`;
}

// Die Auswahl-Aktion wohnt in der Aktionsleiste der Seite und ersetzt dort die Hauptaktion, solange etwas
// gewaehlt ist. Neu gezeichnet wird nur, wenn sich die Zahl aendert.
let purchaseBarKey = '';
function showPurchaseBar() {
  document.getElementById('action-bar').classList.toggle('has-selection', selected.size > 0);
  const key = String(selected.size);
  if (key === purchaseBarKey) return;
  purchaseBarKey = key;
  document.getElementById('action-bar-selection').innerHTML = selected.size
    ? `<span class="action-bar-count">${ctx.esc(t(`${selected.size} ausgewählt`, `${selected.size} selected`))}</span>
       <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-bar="confirm"><svg viewBox="0 0 24 24"><use href="${spriteHref('ui-check')}"></use></svg><span>${ctx.esc(ctx.get('purchases.confirm'))}</span></button>`
    : '';
}

// Eine Sammelbestaetigung wiegt schwer, also fragt sie einmal nach - und bestaetigt dann Kauf fuer Kauf
// ueber dieselbe Summenpruefung wie die Einzelbestaetigung. Einer, dem sie nicht genuegt, bleibt angehakt und
// auf "Zu pruefen"; die anderen sind trotzdem durch.
async function confirmSelectedPurchases() {
  const ids = [...selected];
  if (!await confirmMessage({
    title: t('Käufe bestätigen', 'Confirm purchases'),
    message: t(`${ids.length} Kauf/Käufe bestätigen? Jeder wird einzeln geprüft; was nicht aufgeht, bleibt in der Liste.`,
               `Confirm ${ids.length} purchase(s)? Each is checked on its own; anything that does not add up stays in the list.`),
    confirmLabel: t('Bestätigen', 'Confirm'),
    cancelLabel: t('Abbrechen', 'Cancel')
  })) return;
  let ok = 0;
  for (const id of ids) {
    try {
      await ctx.api(`api/purchases/${id}/confirm`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ createSafeAllocations: true, allowUnlinked: true }) });
      selected.delete(id);
      ok++;
    } catch { /* bleibt angehakt */ }
  }
  const left = ids.length - ok;
  ctx.toast(left
    ? t(`${ok} bestätigt, ${left} stimmen nicht und bleiben zu prüfen.`, `${ok} confirmed, ${left} do not add up and stay in review.`)
    : t(`${ok} bestätigt.`, `${ok} confirmed.`));
  await renderPurchases(ctx);
}

// "/purchases#purchase=<id>" - how the receipts page sends someone to the details of one purchase.
export async function openPurchaseFromHash() {
  const id = /^#purchase=([0-9a-f-]{36})$/i.exec(location.hash)?.[1];
  if (!id) return;
  history.replaceState(null, '', location.pathname);
  await openDetail(id);
}

async function openDetail(id) {
  await openReview(ctx, id, { source: ctx.$('#purchase-source').value, onClose: changed => { if (changed) renderPurchases(ctx).catch(() => {}); } });
}

async function refreshAmazonButton() {
  const button = ctx?.$('#amazon-import');
  if (!button) return;
  let label = t('Amazon verbinden', 'Connect Amazon');
  try {
    const status = await ctx.api('api/purchases/amazon/status');
    if (status?.connected) label = t('Amazon synchronisieren', 'Sync Amazon');
    if (status?.status === 'requires_reauth') label = t('Amazon neu verbinden', 'Reconnect Amazon');
  } catch { /* nicht erreichbar: es bleibt bei "verbinden" */ }
  button.querySelector('.action-label').textContent = label;
  button.setAttribute('aria-label', label);
}

async function openAmazonConnection() {
  let status;
  try { status = await ctx.api('api/purchases/amazon/status'); }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }

  const dlg = ctx.dialog(`<form class="dialog-card" data-amazon-form>
    <div class="panel-head"><h2>Amazon</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div data-amazon-body></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  renderAmazonConnectionBody(dlg, status);
  dlg.showModal();
}

function renderAmazonConnectionBody(dlg, status) {
  const body = dlg.querySelector('[data-amazon-body]');
  if (status?.connected) {
    const last = status.lastSuccessfulSyncAt ? new Date(status.lastSuccessfulSyncAt).toLocaleString() : t('Noch nie', 'Never');
    body.innerHTML = `<div class="row-sub">${t('Verbunden. Letzte erfolgreiche Synchronisierung:', 'Connected. Last successful sync:')} ${ctx.esc(last)}</div>
      ${status.lastError ? `<div class="row-sub negative">${ctx.esc(status.lastError)}</div>` : ''}
      <div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Danger)}" data-disconnect>${t('Trennen', 'Disconnect')}</button><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-sync-days="365">${t('1 Jahr', '1 year')}</button><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-sync-days="36500">${t('Alle', 'All')}</button><button type="button" class="${buttonClass(ButtonRole.Primary)}" data-sync-days="90">${t('90 Tage synchronisieren', 'Sync 90 days')}</button></div>
      <div class="amazon-sync-history" data-amazon-history></div>`;
    body.querySelectorAll('[data-sync-days]').forEach(button => button.addEventListener('click', () => runAmazonSync(dlg, Number(button.dataset.syncDays))));
    body.querySelector('[data-disconnect]').addEventListener('click', async () => {
      try { await ctx.api('api/purchases/amazon/connection', { method: 'DELETE' }); dlg.close(); await refreshAmazonButton(); }
      catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
    });
    loadAmazonSyncHistory(dlg);
    return;
  }

  body.innerHTML = `${status?.lastError ? `<div class="row-sub negative">${ctx.esc(status.lastError)}</div>` : ''}
    <div class="row-sub">${t('FullWorth speichert nur die verschlüsselte Amazon-Sitzung. Passwort und Bestätigungscode werden nicht gespeichert.', 'FullWorth stores only the encrypted Amazon session. Password and verification code are not stored.')}</div>
    <label>${t('Amazon E-Mail', 'Amazon email')}<input name="amazonEmail" type="email" autocomplete="username" required></label>
    <label>${t('Amazon Passwort', 'Amazon password')}<input name="amazonPassword" type="password" autocomplete="current-password" required></label>
    <div class="dialog-actions"><button type="button" data-connect>${t('Verbinden', 'Connect')}</button></div>`;
  body.querySelector('[data-connect]').addEventListener('click', () => startAmazonLogin(dlg));
}

async function startAmazonLogin(dlg) {
  const email = dlg.querySelector('[name="amazonEmail"]')?.value || '';
  const passwordBox = dlg.querySelector('[name="amazonPassword"]');
  const password = passwordBox?.value || '';
  if (!email || !password) return;
  if (passwordBox) passwordBox.value = '';
  setAmazonBusy(dlg, t('Amazon-Anmeldung läuft…', 'Signing in to Amazon…'));
  try {
    const result = await ctx.api('api/purchases/amazon/connect/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email, password }) });
    await handleAmazonLoginResult(dlg, result);
  } catch (err) { renderAmazonConnectionBody(dlg, { connected: false, lastError: err.message || ctx.get('common.error') }); }
}

async function completeAmazonLogin(dlg, challengeId, otp = null) {
  setAmazonBusy(dlg, t('Amazon-Bestätigung wird geprüft…', 'Checking Amazon verification…'));
  try {
    const result = await ctx.api(`api/purchases/amazon/connect/${challengeId}/complete`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ otp }) });
    await handleAmazonLoginResult(dlg, result);
  } catch (err) { renderAmazonConnectionBody(dlg, { connected: false, lastError: err.message || ctx.get('common.error') }); }
}

async function handleAmazonLoginResult(dlg, result) {
  if (result?.status === 'connected') {
    await refreshAmazonButton();
    await runAmazonSync(dlg, 90);
    return;
  }
  const body = dlg.querySelector('[data-amazon-body]');
  if (result?.status === 'otp' && result.challengeId) {
    body.innerHTML = `<div class="row-sub">${t('Amazon-Bestätigungscode eingeben.', 'Enter the Amazon verification code.')}</div><label>${t('Code', 'Code')}<input name="amazonOtp" inputmode="numeric" autocomplete="one-time-code" required></label><div class="dialog-actions"><button type="button" data-otp>${t('Bestätigen', 'Verify')}</button></div>`;
    body.querySelector('[data-otp]').addEventListener('click', () => { const otp = body.querySelector('[name="amazonOtp"]')?.value || ''; body.querySelector('[name="amazonOtp"]').value = ''; completeAmazonLogin(dlg, result.challengeId, otp); });
    return;
  }
  if (result?.status === 'approval' && result.challengeId) {
    body.innerHTML = `<div class="row-sub">${t('Amazon-Anmeldung auf dem anderen Gerät bestätigen. Danach hier fortfahren.', 'Approve the Amazon sign-in on the other device, then continue here.')}</div><div class="dialog-actions"><button type="button" data-approved>${t('Ich habe bestätigt', 'I approved it')}</button></div>`;
    body.querySelector('[data-approved]').addEventListener('click', () => completeAmazonLogin(dlg, result.challengeId, null));
    return;
  }
  renderAmazonConnectionBody(dlg, { connected: false, lastError: result?.message || t('Amazon-Anmeldung fehlgeschlagen.', 'Amazon sign-in failed.') });
}

async function runAmazonSync(dlg, historyDays) {
  setAmazonBusy(dlg, t('Amazon-Bestellungen werden synchronisiert…', 'Syncing Amazon orders…'));
  try {
    const result = await ctx.api('api/purchases/amazon/sync', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ historyDays }) });
    ctx.toast(t(`${result.ordersImported ?? 0} Amazon-Bestellungen synchronisiert`, `${result.ordersImported ?? 0} Amazon orders synced`));
    dlg.close();
    await refreshAmazonButton();
    await renderPurchases(ctx);
  } catch (err) {
    let status = null;
    try { status = await ctx.api('api/purchases/amazon/status'); } catch { }
    renderAmazonConnectionBody(dlg, status || { connected: false, lastError: err.message || ctx.get('common.error') });
  }
}

async function loadAmazonSyncHistory(dlg) {
  const host = dlg.querySelector('[data-amazon-history]');
  if (!host) return;
  try {
    const runs = await ctx.api('api/purchases/amazon/sync-runs?limit=10');
    if (!runs?.length) { host.innerHTML = ''; return; }
    host.innerHTML = `<div class="row-group">${ctx.esc(t('Sync-Verlauf', 'Sync history'))}</div>` + runs.map(run => `
      <div class="row" data-run-id="${ctx.esc(run.id)}">
        <div class="row-main">
          <div class="row-title">${ctx.esc(new Date(run.startedAt).toLocaleString())}</div>
          <div class="row-sub">${run.purchasesCreated} ${ctx.esc(t('neu', 'new'))} · ${ctx.esc(run.status)}${run.rolledBackAt ? ` · ${ctx.esc(t('rückgängig gemacht', 'rolled back'))}` : ''}</div>
        </div>
        ${!run.rolledBackAt && run.purchasesCreated > 0 ? `<div class="row-side"><button type="button" class="${buttonClass(ButtonRole.Danger)}" data-rollback-run>${ctx.esc(t('Rückgängig', 'Undo'))}</button></div>` : ''}
      </div>`).join('');
    host.querySelectorAll('[data-rollback-run]').forEach(button => button.addEventListener('click', () => rollbackAmazonSyncRun(dlg, button)));
  } catch { host.innerHTML = ''; }
}

async function rollbackAmazonSyncRun(dlg, button) {
  const runId = button.closest('[data-run-id]')?.dataset.runId;
  if (!runId) return;
  const confirmed = await confirmDialog(ctx,
    t('Dieser Amazon-Sync wird rückgängig gemacht. Bestellungen, an denen seither nichts geändert wurde, werden gelöscht - inklusive gespeicherter Belegdatei. Bereits bezahlte, zurückgegebene oder bestätigte Bestellungen bleiben erhalten.',
      'This Amazon sync will be undone. Orders nobody has touched since are deleted, including their stored receipt file. Orders already paid, returned or confirmed are kept.'),
    { title: t('Sync rückgängig machen', 'Undo sync'), confirmLabel: t('Rückgängig machen', 'Undo'), destructive: true });
  if (!confirmed) return;
  try {
    const result = await ctx.api(`api/purchases/amazon/sync-runs/${runId}/rollback`, { method: 'POST' });
    ctx.toast(t(`${result.removed} Bestellungen entfernt, ${result.kept} behalten`, `${result.removed} orders removed, ${result.kept} kept`));
    await loadAmazonSyncHistory(dlg);
    await renderPurchases(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

function setAmazonBusy(dlg, text) {
  const body = dlg.querySelector('[data-amazon-body]');
  if (body) body.innerHTML = `<div class="row state-loading"><div class="row-sub">${ctx.esc(text)}</div></div>`;
}

function t(de, en) { return (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en; }
