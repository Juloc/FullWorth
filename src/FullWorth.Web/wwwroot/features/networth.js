import { openRealEstateDetail } from './wealth-real-estate.js';

// Unified wealth view. Totals/history come from /api/wealth/*; type-specific modules own detail logic.
let ctx = null;
let lastOverview = null;

const ASSET_KINDS = [
  'real_estate', 'vehicle', 'precious_metal', 'collectible',
  'receivable', 'business_interest', 'insurance_pension', 'other'
];
const LIABILITY_KINDS = ['loan', 'mortgage', 'credit_card', 'other'];
const CYCLES = ['monthly', 'quarterly', 'yearly', 'weekly'];

const COPY = {
  de: {
    addValue: 'Wert hinzufügen', chooseType: 'Art des Vermögenswerts',
    chooseTypeHint: 'Wähle den Typ. Weitere Details können später ergänzt werden.',
    investmentHint: 'Aktien, ETFs und andere Wertpapiere werden über ein Depot verwaltet und nicht als manueller Wert angelegt.',
    investmentTotal: 'Investments gesamt', portfolio: 'Depot', active: 'Aktiv',
    realEstate: 'Immobilien', vehicles: 'Fahrzeuge', otherValues: 'Weitere Werte',
    valueHistory: 'Werthistorie', details: 'Details', updateValue: 'Wert aktualisieren', current: 'Aktuell',
    noValuations: 'Noch keine Bewertungen vorhanden.', fxIncomplete: 'Gesamtsumme unvollständig: Für mindestens eine Währung fehlt ein Wechselkurs.',
    dataIncomplete: 'Daten unvollständig', composition: 'Zusammensetzung', accounts: 'Konten', manualAssets: 'Weitere Vermögenswerte', investments: 'Investments', debt: 'Schulden',
    real_estate: 'Immobilie', vehicle: 'Fahrzeug', precious_metal: 'Edelmetall', collectible: 'Sammlerstück / Wertgegenstand',
    receivable: 'Forderung / privates Darlehen', business_interest: 'Unternehmensbeteiligung', insurance_pension: 'Versicherung / Vorsorge', other: 'Sonstiger Wert',
    manual: 'Manuell', purchase_price: 'Kaufpreis', internal_estimate: 'FullWorth-Schätzung', external_provider: 'Externer Anbieter', appraisal: 'Gutachten', import: 'Import', legacy: 'Übernommen'
  },
  en: {
    addValue: 'Add asset', chooseType: 'Asset type', chooseTypeHint: 'Choose a type. Additional details can be completed later.',
    investmentHint: 'Stocks, ETFs and other securities are managed through an investment portfolio, not as manual assets.',
    investmentTotal: 'Investments total', portfolio: 'Portfolio', active: 'Active',
    realEstate: 'Real estate', vehicles: 'Vehicles', otherValues: 'Other assets',
    valueHistory: 'Value history', details: 'Details', updateValue: 'Update value', current: 'Current', noValuations: 'No valuations yet.',
    fxIncomplete: 'Total is incomplete: at least one required FX rate is missing.', dataIncomplete: 'Data incomplete', composition: 'Composition',
    accounts: 'Accounts', manualAssets: 'Other assets', investments: 'Investments', debt: 'Debt',
    real_estate: 'Real estate', vehicle: 'Vehicle', precious_metal: 'Precious metal', collectible: 'Collectible / valuable',
    receivable: 'Receivable / private loan', business_interest: 'Business interest', insurance_pension: 'Insurance / pension', other: 'Other asset',
    manual: 'Manual', purchase_price: 'Purchase price', internal_estimate: 'FullWorth estimate', external_provider: 'External provider', appraisal: 'Appraisal', import: 'Import', legacy: 'Migrated'
  }
};

function t(key) {
  const lang = (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? 'de' : 'en';
  return COPY[lang][key] || key;
}

function ensureStyles() {
  if (document.querySelector('link[data-wealth-assets-css]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/wealth-assets.css';
  link.dataset.wealthAssetsCss = '1';
  document.head.appendChild(link);
}

export function bindNetWorth(context) {
  ctx = context;
  ensureStyles();
  ctx.$('[data-action="new-asset"]')?.addEventListener('click', () => openAssetWizard());
  ctx.$('[data-action="new-liability"]')?.addEventListener('click', () => openLiabilityDialog());
}

export function newAsset(context) {
  if (context) ctx = context;
  ensureStyles();
  return openAssetWizard();
}

export async function renderNetWorth(context) {
  ctx = context;
  ensureStyles();
  const end = new Date();
  const start = new Date();
  start.setMonth(end.getMonth() - 12);
  const [overview, history, assets, liabilities, accounts, portfolios] = await Promise.all([
    ctx.api('api/wealth/overview'),
    ctx.api(`api/wealth/history?from=${localDate(start)}&to=${localDate(end)}`).catch(() => []),
    ctx.api('api/assets'),
    ctx.api('api/liabilities'),
    ctx.api('api/accounts').catch(() => []),
    ctx.api('api/investments/portfolios').catch(() => [])
  ]);

  lastOverview = overview;
  const currency = overview.currency;
  ctx.$('#nw-total').textContent = ctx.money(overview.netWorth, currency);
  ctx.$('#nw-assets').textContent = ctx.money(overview.totalAssets, currency);
  ctx.$('#nw-liabilities').textContent = ctx.money(overview.totalLiabilities, currency);
  renderFxState(overview);

  const linkedInvestmentAccounts = new Set((portfolios || []).map(item => item.accountId).filter(Boolean));
  renderAccounts((accounts || []).filter(account => !linkedInvestmentAccounts.has(account.id)));
  renderAssets(assets || []);
  renderLiabilities(liabilities || []);
  renderInvestments(portfolios || [], overview);
  renderTrend(history || [], currency, overview);
  setChange(history || [], currency);
}

function renderFxState(overview) {
  const card = ctx.$('#nw-total')?.closest('.metric');
  let note = card?.querySelector('.fx-incomplete');
  if (overview.isComplete) { note?.remove(); return; }
  if (!note) {
    note = document.createElement('div');
    note.className = 'fx-incomplete';
    card?.appendChild(note);
  }
  const missing = (overview.missingCurrencies || []).join(', ');
  note.textContent = `${t('fxIncomplete')}${missing ? ` (${missing})` : ''}`;
}

function setChange(history, currency) {
  const el = ctx.$('#nw-change');
  if (!el) return;
  const usable = (history || []).filter(point => Number.isFinite(Number(point.netWorth)));
  if (usable.length < 2) { el.textContent = '—'; el.className = ''; return; }
  const delta = Number(usable.at(-1).netWorth) - Number(usable[0].netWorth);
  el.textContent = `${!ctx.isPrivate() && delta > 0 ? '+' : ''}${ctx.money(delta, currency)}`;
  el.className = delta > 0 ? 'positive' : delta < 0 ? 'negative' : '';
}

function renderTrend(history, currency, overview) {
  const el = ctx.$('#nw-trend');
  if (!el) return;
  const composition = [
    [t('accounts'), overview.accounts?.amount || 0],
    [t('manualAssets'), overview.manualAssets?.amount || 0],
    [t('investments'), overview.investments?.amount || 0],
    [t('debt'), -(overview.totalLiabilities || 0)]
  ];
  const legend = `<div class="wealth-composition" aria-label="${ctx.esc(t('composition'))}">${composition.map(([label, amount]) =>
    `<div class="wealth-composition-item"><span>${ctx.esc(label)}</span><strong class="${amount < 0 ? 'negative' : ''}">${ctx.money(amount, currency)}</strong></div>`).join('')}</div>`;
  const usable = (history || []).filter(point => Number.isFinite(Number(point.netWorth)));
  if (!usable.length) { el.innerHTML = `${legend}${emptyRow()}`; return; }
  const values = usable.map(point => Number(point.netWorth));
  const min = Math.min(...values); const max = Math.max(...values); const span = max - min || 1;
  const width = 900; const height = 210;
  const points = values.map((value, index) => `${(index / (values.length - 1 || 1)) * width},${height - ((value - min) / span) * (height - 20) - 10}`).join(' ');
  el.innerHTML = `${legend}<div class="wealth-chart"><svg viewBox="0 0 ${width} ${height}" role="img" aria-label="${ctx.esc(ctx.get('analytics.trend'))}"><polyline points="${points}" fill="none" stroke="currentColor" stroke-width="3" vector-effect="non-scaling-stroke"/></svg><div class="row-sub">${ctx.money(values.at(-1), currency)}</div></div>`;
}

function renderAccounts(accounts) {
  const el = ctx.$('#nw-accounts'); if (!el) return; el.innerHTML = '';
  if (!accounts.length) { el.innerHTML = emptyRow(); return; }
  const groups = new Map();
  for (const account of accounts) {
    const key = account.institutionName || ctx.get('accounts.manual');
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(account);
  }
  const frag = document.createDocumentFragment();
  for (const [institution, list] of groups) {
    const head = document.createElement('div'); head.className = 'row-group'; head.textContent = institution; frag.appendChild(head);
    for (const account of list) {
      const row = document.createElement('div'); row.className = 'row';
      const balance = account.latestBalance ? ctx.money(account.latestBalance.amount, account.latestBalance.currency) : '—';
      row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(account.displayName || account.institutionName)}</div></div><div class="amount">${balance}</div>`;
      frag.appendChild(row);
    }
  }
  el.appendChild(frag);
}

function renderAssets(assets) {
  const el = ctx.$('#assets-list'); if (!el) return; el.innerHTML = '';
  if (!assets.length) { el.innerHTML = emptyRow(); return; }
  const groups = [
    [t('realEstate'), assets.filter(item => item.kind === 'real_estate')],
    [t('vehicles'), assets.filter(item => item.kind === 'vehicle')],
    [t('otherValues'), assets.filter(item => !['real_estate', 'vehicle'].includes(item.kind))]
  ].filter(([, items]) => items.length);
  const frag = document.createDocumentFragment();
  for (const [title, items] of groups) {
    const head = document.createElement('div'); head.className = 'row-group wealth-group-title'; head.textContent = title; frag.appendChild(head);
    for (const asset of items) frag.appendChild(assetRow(asset));
  }
  el.appendChild(frag);
}

function assetRow(asset) {
  const row = document.createElement('div');
  row.className = `row nw-item${asset.includeInNetWorth ? '' : ' nw-excluded'}`;
  const detailAction = asset.kind === 'real_estate'
    ? `<button class="icon-button" data-detail title="${ctx.esc(t('details'))}" aria-label="${ctx.esc(t('details'))}">›</button>`
    : `<button class="icon-button" data-history title="${ctx.esc(t('valueHistory'))}" aria-label="${ctx.esc(t('valueHistory'))}">↗</button>`;
  row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(asset.name)}${asset.includeInNetWorth ? '' : ` <span class="tx-marker">${ctx.esc(ctx.get('networth.excluded'))}</span>`}</div><div class="row-sub">${ctx.esc(t(asset.kind || 'other'))}${asset.valuedAt ? ` · ${ctx.esc(dateValue(asset.valuedAt))}` : ''}</div></div><div class="row-side"><span class="amount">${ctx.money(asset.currentValue, asset.currency)}</span>${detailAction}<button class="icon-button" data-toggle title="${ctx.esc(ctx.get(asset.includeInNetWorth ? 'networth.exclude' : 'networth.include'))}">${asset.includeInNetWorth ? '◉' : '○'}</button><button class="icon-button" data-edit title="${ctx.esc(ctx.get('common.edit'))}">✎</button></div>`;
  row.querySelector('[data-edit]').onclick = () => openAssetForm(asset.kind || 'other', asset);
  row.querySelector('[data-history]')?.addEventListener('click', () => openValuationHistory(asset));
  row.querySelector('[data-detail]')?.addEventListener('click', () => openRealEstateDetail(ctx, asset, () => renderNetWorth(ctx)));
  row.querySelector('[data-toggle]').onclick = async () => {
    try { await ctx.api(`api/assets/${asset.id}`, jsonBody({ ...assetToWrite(asset), includeInNetWorth: !asset.includeInNetWorth }, 'PUT')); await renderNetWorth(ctx); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
  return row;
}

function renderLiabilities(liabilities) {
  const el = ctx.$('#liabilities-list'); if (!el) return; el.innerHTML = '';
  if (!liabilities.length) { el.innerHTML = emptyRow(); return; }
  const frag = document.createDocumentFragment();
  for (const item of liabilities) {
    const row = document.createElement('div'); row.className = `row nw-item${item.includeInNetWorth ? '' : ' nw-excluded'}`;
    row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(item.name)}${item.includeInNetWorth ? '' : ` <span class="tx-marker">${ctx.esc(ctx.get('networth.excluded'))}</span>`}</div><div class="row-sub">${ctx.esc(ctx.get(`networth.liabilityKind_${item.kind || 'other'}`))}</div></div><div class="row-side"><span class="amount">${ctx.money(item.currentBalance, item.currency)}</span><button class="icon-button" data-toggle>${item.includeInNetWorth ? '◉' : '○'}</button><button class="icon-button" data-edit>✎</button></div>`;
    row.querySelector('[data-edit]').onclick = () => openLiabilityDialog(item);
    row.querySelector('[data-toggle]').onclick = async () => {
      try { await ctx.api(`api/liabilities/${item.id}`, jsonBody({ ...liabilityToWrite(item), includeInNetWorth: !item.includeInNetWorth }, 'PUT')); await renderNetWorth(ctx); }
      catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
    };
    frag.appendChild(row);
  }
  el.appendChild(frag);
}

function renderInvestments(portfolios, overview) {
  const panel = ctx.$('#nw-investments'); const list = ctx.$('#nw-investments-list'); if (!panel || !list) return;
  const active = (portfolios || []).filter(item => item.isArchived !== true); const total = Number(overview.investments?.amount || 0);
  if (!active.length && total === 0) { panel.hidden = true; list.innerHTML = ''; return; }
  panel.hidden = false; list.innerHTML = `<div class="row wealth-investment-total"><div class="row-main"><div class="row-title">${ctx.esc(t('investmentTotal'))}</div><div class="row-sub">${ctx.esc(overview.investmentDataIncomplete ? t('dataIncomplete') : t('active'))}</div></div><div class="amount">${ctx.money(total, overview.currency)}</div></div>`;
  for (const portfolio of active) {
    const row = document.createElement('div'); row.className = 'row';
    row.innerHTML = `<div class="row-main"><div class="row-title">${ctx.esc(portfolio.name)}</div><div class="row-sub">${ctx.esc(t('portfolio'))} · ${ctx.esc(portfolio.currency || overview.currency)}</div></div>`;
    list.appendChild(row);
  }
}

function openAssetWizard() {
  const dlg = ctx.dialog(`<form method="dialog" class="dialog-card wealth-wizard"><div class="panel-head"><div><h2>${ctx.esc(t('addValue'))}</h2><div class="row-sub">${ctx.esc(t('chooseTypeHint'))}</div></div><button value="cancel" aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div><div class="wealth-type-grid" role="list" aria-label="${ctx.esc(t('chooseType'))}">${ASSET_KINDS.map(kind => `<button type="button" class="wealth-type" data-kind="${kind}" role="listitem"><strong>${ctx.esc(t(kind))}</strong></button>`).join('')}</div><div class="wealth-investment-hint">${ctx.esc(t('investmentHint'))}</div></form>`);
  dlg.querySelectorAll('[data-kind]').forEach(button => button.onclick = () => { const kind = button.dataset.kind; dlg.close(); openAssetForm(kind); });
  dlg.showModal();
}

function openAssetForm(kind, existing) {
  const asset = existing || {}; const selectedKind = existing?.kind || kind || 'other'; const currency = asset.currency || lastOverview?.currency || 'EUR';
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><div><h2>${ctx.esc(ctx.get(existing ? 'networth.editAsset' : 'networth.newAsset'))}</h2><div class="row-sub">${ctx.esc(t(selectedKind))}</div></div><button type="button" data-close>×</button></div><label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="160" value="${ctx.esc(asset.name || '')}"></label><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.value'))}<input name="value" type="number" min="0" step="0.01" required value="${asset.currentValue ?? ''}"></label><label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" minlength="3" maxlength="3" required></label></div><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.valuedAt'))}<input name="valuedAt" type="date" value="${dateValue(asset.valuedAt)}"></label><label>${ctx.esc(ctx.get('networth.growth'))}<input name="growth" type="number" step="0.01" value="${asset.annualGrowthRate ?? ''}"></label></div><label class="check"><input type="checkbox" name="include" ${asset.includeInNetWorth === false ? '' : 'checked'}> ${ctx.esc(ctx.get('networth.includeInNetWorth'))}</label><label>${ctx.esc(ctx.get('contracts.notes'))}<textarea name="notes" maxlength="1000" rows="2">${ctx.esc(asset.notes || '')}</textarea></label><div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get(existing ? 'common.apply' : 'common.create'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    const body = { name: fd.get('name'), kind: selectedKind, currentValue: Number(fd.get('value')), currency: String(fd.get('currency') || 'EUR').toUpperCase(), valuedAt: fd.get('valuedAt') || null, annualGrowthRate: numberOrNull(fd.get('growth')), includeInNetWorth: event.currentTarget.include.checked, notes: textOrNull(fd.get('notes')) };
    try { const created = await ctx.api(existing ? `api/assets/${existing.id}` : 'api/assets', jsonBody(body, existing ? 'PUT' : 'POST')); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderNetWorth(ctx); if (!existing && selectedKind === 'real_estate' && created?.id) await openRealEstateDetail(ctx, created, () => renderNetWorth(ctx)); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

async function openValuationHistory(asset) {
  let values;
  try { values = await ctx.api(`api/assets/${asset.id}/valuations`); } catch (error) { ctx.toast(error.message || ctx.get('common.error')); return; }
  const dlg = ctx.dialog(`<div class="dialog-card wealth-history-dialog"><div class="panel-head"><div><h2>${ctx.esc(asset.name)}</h2><div class="row-sub">${ctx.esc(t('valueHistory'))}</div></div><button type="button" data-close>×</button></div><div class="wealth-valuations">${values?.length ? values.map(value => `<div class="row wealth-valuation${value.isCurrent ? ' is-current' : ''}"><div class="row-main"><div class="row-title">${ctx.money(value.amount, value.currency)}${value.isCurrent ? ` <span class="tx-marker">${ctx.esc(t('current'))}</span>` : ''}</div><div class="row-sub">${ctx.esc(dateValue(value.valuedAt))} · ${ctx.esc(t(value.method || 'manual'))}</div></div></div>`).join('') : `<div class="row-sub">${ctx.esc(t('noValuations'))}</div>`}</div><form class="wealth-value-form"><h3>${ctx.esc(t('updateValue'))}</h3><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.value'))}<input name="amount" type="number" min="0" step="0.01" value="${asset.currentValue}" required></label><label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(asset.currency)}" minlength="3" maxlength="3" required></label></div><label>${ctx.esc(ctx.get('networth.valuedAt'))}<input name="valuedAt" type="date" value="${localDate(new Date())}"></label><div class="dialog-actions"><button type="submit">${ctx.esc(ctx.get('common.apply'))}</button></div></form></div>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    try { await ctx.api(`api/assets/${asset.id}/valuations`, jsonBody({ amount: Number(fd.get('amount')), currency: String(fd.get('currency') || asset.currency).toUpperCase(), valuedAt: fd.get('valuedAt') || null, method: 'manual', isAccepted: true })); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderNetWorth(ctx); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

function openLiabilityDialog(existing) {
  const item = existing || {}; const currency = item.currency || lastOverview?.currency || 'EUR';
  const options = (list, selected, prefix) => list.map(value => `<option value="${value}"${value === selected ? ' selected' : ''}>${ctx.esc(ctx.get(prefix + value))}</option>`).join('');
  const dlg = ctx.dialog(`<form class="dialog-card"><div class="panel-head"><h2>${ctx.esc(ctx.get(existing ? 'networth.editLiability' : 'networth.newLiability'))}</h2><button type="button" data-close>×</button></div><label>${ctx.esc(ctx.get('common.name'))}<input name="name" required maxlength="160" value="${ctx.esc(item.name || '')}"></label><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.kind'))}<select name="kind">${options(LIABILITY_KINDS, item.kind || 'loan', 'networth.liabilityKind_')}</select></label><label>${ctx.esc(ctx.get('contracts.billingCycle'))}<select name="cycle">${options(CYCLES, item.paymentCycle || 'monthly', 'contracts.cycle_')}</select></label></div><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.balance'))}<input name="balance" type="number" min="0" step="0.01" required value="${item.currentBalance ?? ''}"></label><label>${ctx.esc(ctx.get('purchases.currency'))}<input name="currency" value="${ctx.esc(currency)}" minlength="3" maxlength="3" required></label></div><div class="rule-grid"><label>${ctx.esc(ctx.get('networth.interestRate'))}<input name="interest" type="number" step="0.001" value="${item.interestRate ?? ''}"></label><label>${ctx.esc(ctx.get('networth.payment'))}<input name="payment" type="number" step="0.01" value="${item.regularPayment ?? ''}"></label></div><div class="rule-grid"><label>${ctx.esc(ctx.get('contracts.nextDue'))}<input name="nextDue" type="date" value="${dateValue(item.nextDueDate)}"></label><label>${ctx.esc(ctx.get('contracts.endDate'))}<input name="end" type="date" value="${dateValue(item.endDate)}"></label></div><label class="check"><input type="checkbox" name="include" ${item.includeInNetWorth === false ? '' : 'checked'}> ${ctx.esc(ctx.get('networth.includeInNetWorth'))}</label><label>${ctx.esc(ctx.get('contracts.notes'))}<textarea name="notes" maxlength="1000" rows="2">${ctx.esc(item.notes || '')}</textarea></label><div class="dialog-actions"><button type="button" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button><button type="submit">${ctx.esc(ctx.get(existing ? 'common.apply' : 'common.create'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(event.currentTarget);
    const body = { name: fd.get('name'), kind: fd.get('kind'), currentBalance: Number(fd.get('balance')), currency: String(fd.get('currency') || 'EUR').toUpperCase(), interestRate: numberOrNull(fd.get('interest')), regularPayment: numberOrNull(fd.get('payment')), paymentCycle: fd.get('cycle'), nextDueDate: fd.get('nextDue') || null, endDate: fd.get('end') || null, includeInNetWorth: event.currentTarget.include.checked, notes: textOrNull(fd.get('notes')) };
    try { await ctx.api(existing ? `api/liabilities/${existing.id}` : 'api/liabilities', jsonBody(body, existing ? 'PUT' : 'POST')); dlg.close(); ctx.toast(ctx.get('common.saved')); await renderNetWorth(ctx); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

function assetToWrite(item) { return { name: item.name, kind: item.kind || 'other', currentValue: item.currentValue, currency: item.currency, valuedAt: item.valuedAt || null, annualGrowthRate: item.annualGrowthRate ?? null, includeInNetWorth: item.includeInNetWorth !== false, notes: item.notes || null }; }
function liabilityToWrite(item) { return { name: item.name, kind: item.kind || 'other', currentBalance: item.currentBalance, currency: item.currency, interestRate: item.interestRate ?? null, regularPayment: item.regularPayment ?? null, paymentCycle: item.paymentCycle || 'monthly', nextDueDate: item.nextDueDate || null, endDate: item.endDate || null, includeInNetWorth: item.includeInNetWorth !== false, notes: item.notes || null }; }
function jsonBody(body, method = 'POST') { return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }; }
function emptyRow() { return `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div></div>`; }
function dateValue(value) { return value ? String(value).slice(0, 10) : ''; }
function localDate(value) { return `${value.getFullYear()}-${String(value.getMonth() + 1).padStart(2, '0')}-${String(value.getDate()).padStart(2, '0')}`; }
function numberOrNull(value) { const text = String(value ?? '').trim(); return text === '' ? null : Number(text); }
function textOrNull(value) { const text = String(value ?? '').trim(); return text || null; }
