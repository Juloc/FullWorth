// Tax assistant — restored on the view-registry architecture (was a working feature on main; the
// architecture cleanup removed the frontend while /api/tax/* stayed intact server-side). Surfaces
// potentially tax-relevant expenses detected from transactions/purchases/documents so the owner can
// confirm/reject each candidate before it counts toward a tax year.
//
// Two URLs share one view: /tax (overview) and /tax/review (open candidates only). core/router.js
// resolves a view from only the first path segment, so both land on the registered 'tax' view; this
// module reads the full pathname itself (like contracts.js's own history.pushState use for its
// filter/sort state) to pick the active tab and to keep Back/Forward working between them.
//
// Year-review checklist, CSV/JSON export, per-candidate document upload and the advanced analysis
// toggles live in tax-review-extra.js and are composed in directly below — no MutationObserver
// polling, no cross-view DOM patching (FrontendArchitectureGuardTests forbids both).
import { sectionCard, esc } from '../ui/ux-kit.js';
import { renderTaxYearPanel, renderAdvancedSettings, wireDocumentUploads } from './tax-review-extra.js';

let ctx = null;
let year = null; // sticky across re-renders (tab switch, decide, analyze) until the user picks another
let candidates = [];

const T = {
  de: {
    title: 'Steuern', overview: 'Übersicht', review: 'Prüfen', year: 'Steuerjahr',
    analyze: 'Neu analysieren', analyzing: 'Analysiert…',
    possible: 'Möglicherweise relevant', confirmed: 'Bestätigt', needsReview: 'Zu prüfen', missingDocs: 'Beleg fehlt',
    openCases: 'Offene Hinweise', allCases: 'Alle Hinweise', none: 'Keine offenen Steuerhinweise.',
    confirm: 'Bestätigen', reject: 'Nicht relevant', edit: 'Anteil ändern', eligible: 'Berücksichtigter Anteil', document: 'Beleg vorhanden',
    confidenceHigh: 'Starker Hinweis', confidenceMedium: 'Prüfen', confidenceLow: 'Unsicher',
    disclaimer: 'Hinweise sind eine Vorprüfung und keine Steuerberatung.', breakdown: 'Nach Kategorie',
    settingsTitle: 'Steuerassistent', personal: 'Für mich aktivieren',
    personalHint: 'Analysiert nur Daten, auf die du Zugriff hast. Abschalten entfernt die Ansicht für dich.',
    spaceOff: 'Der Steuerassistent ist für diesen Finanzbereich deaktiviert.', spaceOn: 'Für diesen Finanzbereich aktiviert.',
    enableSpace: 'Für Bereich aktivieren', ownerOnly: 'Nur Eigentümer des Finanzbereichs können diese Einstellung ändern.',
    amountPrompt: 'Welcher Anteil ist steuerlich relevant?', percent: 'Prozent',
    sourceTransaction: 'Bankbuchung', sourcePurchase: 'Kauf', sourceItem: 'Kaufartikel', fallbackTitle: 'Steuerhinweis',
    statusConfirmed: 'Bestätigt', statusRejected: 'Nicht relevant', statusNeedsReview: 'Zu prüfen', statusNeedsDocument: 'Beleg fehlt',
    statusDetected: 'Erkannt', statusIncomplete: 'Unvollständig', analysisDone: 'Analyse abgeschlossen.', settingsBtn: 'Einstellungen'
  },
  en: {
    title: 'Taxes', overview: 'Overview', review: 'Review', year: 'Tax year',
    analyze: 'Analyze again', analyzing: 'Analyzing…',
    possible: 'Potentially relevant', confirmed: 'Confirmed', needsReview: 'Needs review', missingDocs: 'Receipt missing',
    openCases: 'Open suggestions', allCases: 'All suggestions', none: 'No open tax suggestions.',
    confirm: 'Confirm', reject: 'Not relevant', edit: 'Change share', eligible: 'Eligible share', document: 'Receipt available',
    confidenceHigh: 'Strong suggestion', confidenceMedium: 'Review', confidenceLow: 'Uncertain',
    disclaimer: 'Suggestions are a preliminary review and are not tax advice.', breakdown: 'By category',
    settingsTitle: 'Tax assistant', personal: 'Enable for me',
    personalHint: 'Only analyzes data you can access. Turning it off removes this view for you.',
    spaceOff: 'The tax assistant is disabled for this finance space.', spaceOn: 'Enabled for this finance space.',
    enableSpace: 'Enable for space', ownerOnly: 'Only fullworth-space owners can change this setting.',
    amountPrompt: 'What share is tax-relevant?', percent: 'Percent',
    sourceTransaction: 'Bank transaction', sourcePurchase: 'Purchase', sourceItem: 'Purchase item', fallbackTitle: 'Tax suggestion',
    statusConfirmed: 'Confirmed', statusRejected: 'Not relevant', statusNeedsReview: 'Needs review', statusNeedsDocument: 'Receipt missing',
    statusDetected: 'Detected', statusIncomplete: 'Incomplete', analysisDone: 'Analysis complete.', settingsBtn: 'Settings'
  }
};
function lang() { return (document.documentElement.lang || '').startsWith('en') ? 'en' : 'de'; }
function tr() { return T[lang()]; }

function isReviewPath() { return location.pathname.startsWith('/tax/review'); }
function taxPath(tab) { return tab === 'review' ? '/tax/review' : '/tax'; }

export function bindTax(context) {
  ctx = context;
}

export async function renderTax(context) {
  ctx = context;
  const host = ctx.$('#view-tax');
  if (!host) return;

  let settings = null, profile = null, loadError = null;
  try {
    [settings, profile] = await Promise.all([
      ctx.api('api/tax/settings'),
      ctx.api('api/tax/profile/settings')
    ]);
  } catch (err) { loadError = err; }

  if (year == null) year = Number(settings?.defaultTaxYear || new Date().getFullYear());

  const enabled = !loadError && !!settings?.enabled && !!profile?.assistantEnabled;
  if (!enabled) {
    host.innerHTML = gateHtml(settings, profile, loadError);
    wireGate(host);
    return;
  }

  const reviewOnly = isReviewPath();
  host.innerHTML = viewHtml(reviewOnly);
  wireControls(host, settings, profile);
  await loadData(host, reviewOnly);
}

// ---- disabled/gate state (personal opt-in and/or space-level opt-in are off) ----

function gateHtml(settings, profile, loadError) {
  if (loadError) {
    return sectionCard(tr().title, `<div class="row-sub">${esc(loadError.message || ctx.get('common.error'))}</div>`, { className: 'tax-summary' });
  }
  const spaceOff = !settings?.enabled;
  const body = `
    <p class="tax-setting-copy">${esc(spaceOff ? tr().spaceOff : tr().spaceOn)}</p>
    <label class="check"><input id="tax-personal-enabled" type="checkbox" ${profile?.assistantEnabled ? 'checked' : ''}><span>${esc(tr().personal)}</span></label>
    <div class="tax-setting-copy">${esc(tr().personalHint)}</div>
    ${spaceOff ? `<button type="button" id="tax-space-enable" class="btn btn-secondary">${esc(tr().enableSpace)}</button>` : ''}`;
  return sectionCard(tr().title, body, { className: 'tax-summary tax-gate' });
}

function wireGate(host) {
  host.querySelector('#tax-personal-enabled')?.addEventListener('change', async e => {
    const checked = e.currentTarget.checked;
    e.currentTarget.disabled = true;
    try {
      await ctx.api('api/tax/profile/settings', ctx.jsonBody({ assistantEnabled: checked }, 'PUT'));
      ctx.toast(ctx.get('common.saved'));
      await renderTax(ctx);
    } catch (err) {
      e.currentTarget.checked = !checked;
      ctx.toast(err.message || ctx.get('common.error'));
    } finally { e.currentTarget.disabled = false; }
  });
  host.querySelector('#tax-space-enable')?.addEventListener('click', async e => {
    e.currentTarget.disabled = true;
    try {
      await ctx.api('api/tax/settings', ctx.jsonBody({ ...settingsWritePayload(null), enabled: true }, 'PUT'));
      ctx.toast(ctx.get('common.saved'));
      await renderTax(ctx);
    } catch (err) {
      ctx.toast(err.status === 403 ? tr().ownerOnly : (err.message || ctx.get('common.error')));
    } finally { e.currentTarget.disabled = false; }
  });
}

function settingsWritePayload(base) {
  const s = base || { countryCode: 'DE', defaultTaxYear: new Date().getFullYear(), automaticAnalysisEnabled: true, aiAnalysisEnabled: false, analyzeTransactions: true, analyzePurchases: true, analyzeDocuments: true, showTaxNotifications: true };
  return {
    countryCode: s.countryCode || 'DE',
    defaultTaxYear: s.defaultTaxYear || new Date().getFullYear(),
    automaticAnalysisEnabled: s.automaticAnalysisEnabled !== false,
    aiAnalysisEnabled: !!s.aiAnalysisEnabled,
    analyzeTransactions: s.analyzeTransactions !== false,
    analyzePurchases: s.analyzePurchases !== false,
    analyzeDocuments: s.analyzeDocuments !== false,
    showTaxNotifications: s.showTaxNotifications !== false
  };
}

// ---- enabled state: toolbar + hero + breakdown + candidate list ----

function yearOptionsHtml() {
  const current = new Date().getFullYear();
  const values = [];
  for (let y = current + 1; y >= 2020; y--) values.push(y);
  if (year != null && !values.includes(year)) values.push(year);
  return values.sort((a, b) => b - a).map(y => `<option value="${y}"${y === year ? ' selected' : ''}>${y}</option>`).join('');
}

function viewHtml(reviewOnly) {
  return `<div class="tax-toolbar">
      <div class="tax-tabs">
        <button type="button" class="${reviewOnly ? '' : 'active'}" data-tax-tab="overview">${esc(tr().overview)}</button>
        <button type="button" class="${reviewOnly ? 'active' : ''}" data-tax-tab="review">${esc(tr().review)}</button>
      </div>
      <label class="tax-year"><span>${esc(tr().year)}</span><select id="tax-year">${yearOptionsHtml()}</select></label>
      <button type="button" class="btn btn-secondary" data-tax-settings>${esc(tr().settingsBtn)}</button>
      <button type="button" id="tax-analyze" class="primary-action">${esc(tr().analyze)}</button>
    </div>
    <article class="fw-card tax-hero">
      <div class="tax-hero-top">
        <div class="tax-hero-lead"><span class="fw-summary-label">${esc(tr().possible)}</span><div class="fw-summary-value" id="tax-possible">—</div></div>
        <dl class="tax-hero-side">
          <div><dt>${esc(tr().confirmed)}</dt><dd class="amount" id="tax-confirmed">—</dd></div>
          <div><dt>${esc(tr().needsReview)}</dt><dd class="amount" id="tax-review-count">—</dd></div>
          <div><dt>${esc(tr().missingDocs)}</dt><dd class="amount" id="tax-doc-count">—</dd></div>
        </dl>
      </div>
      <div class="tax-alloc" id="tax-breakdown-bar" hidden></div>
    </article>
    <article class="fw-card tax-breakdown-card" id="tax-breakdown-card" hidden>
      <div class="fw-card-head"><h3 class="fw-card-title">${esc(tr().breakdown)}</h3></div>
      <div id="tax-breakdown" class="rows tax-breakdown-list"></div>
    </article>
    <div data-tax-year-panel class="panel tax-year-review" hidden></div>
    <article class="panel tax-cases">
      <div class="panel-head"><h2 id="tax-list-title">${esc(reviewOnly ? tr().openCases : tr().allCases)}</h2></div>
      <div id="tax-candidate-list" class="tax-review-list">
        <div class="tax-loading"></div><div class="tax-loading"></div><div class="tax-loading"></div>
      </div>
    </article>
    <p class="tax-disclaimer">${esc(tr().disclaimer)}</p>`;
}

function wireControls(host, settings, profile) {
  host.querySelector('[data-tax-tab="overview"]').addEventListener('click', () => switchTab('overview'));
  host.querySelector('[data-tax-tab="review"]').addEventListener('click', () => switchTab('review'));
  host.querySelector('#tax-year').addEventListener('change', e => { year = Number(e.target.value); renderTax(ctx); });
  host.querySelector('#tax-analyze').addEventListener('click', () => analyze(host));
  host.querySelector('[data-tax-settings]').addEventListener('click', () => openSettingsDialog(settings, profile));
}

function switchTab(tab) {
  const path = taxPath(tab);
  if (location.pathname !== path) history.pushState({ view: 'tax' }, '', path);
  renderTax(ctx);
}

async function loadData(host, reviewOnly) {
  const list = host.querySelector('#tax-candidate-list');
  try {
    const [summary, rows] = await Promise.all([
      ctx.api(`api/tax/years/${year}/summary`),
      ctx.api(`api/tax/candidates?year=${year}`)
    ]);
    candidates = rows || [];
    const currency = candidates[0]?.currency || 'EUR';
    const confirmedAmount = Number(summary.confirmedAmount || 0);
    const reviewCount = Number(summary.needsReviewCount || 0);
    const docCount = Number(summary.needsDocumentCount || 0);
    setText(host, '#tax-possible', ctx.money(summary.suggestedAmount, currency));
    setText(host, '#tax-confirmed', ctx.money(confirmedAmount, currency));
    host.querySelector('#tax-confirmed')?.classList.toggle('tax-pos', confirmedAmount > 0);
    setText(host, '#tax-review-count', String(reviewCount));
    host.querySelector('#tax-review-count')?.classList.toggle('tax-warn', reviewCount > 0);
    setText(host, '#tax-doc-count', String(docCount));
    host.querySelector('#tax-doc-count')?.classList.toggle('tax-warn', docCount > 0);
    drawBreakdown(host, candidates);
    const visible = reviewOnly
      ? candidates.filter(c => ['needs_review', 'detected', 'needs_document', 'incomplete'].includes(c.status))
      : candidates;
    drawCandidates(host, visible);
    await renderTaxYearPanel(ctx, host, year);
    wireDocumentUploads(ctx, host, visible, { onUploaded: () => renderTax(ctx) });
  } catch (err) {
    const breakdownCard = host.querySelector('#tax-breakdown-card');
    if (breakdownCard) breakdownCard.hidden = true;
    list.innerHTML = `<div class="row state-empty"><div class="row-sub">${esc(err.message || ctx.get('common.error'))}</div></div>`;
  }
}

function setText(host, selector, value) {
  const el = host.querySelector(selector);
  if (el) el.textContent = value;
}

function statusLabel(status) {
  const x = tr();
  const map = { confirmed: x.statusConfirmed, rejected: x.statusRejected, needs_review: x.statusNeedsReview, needs_document: x.statusNeedsDocument, detected: x.statusDetected, incomplete: x.statusIncomplete };
  return map[status] || status;
}
function confidenceLabel(confidence) {
  const n = Number(confidence || 0);
  return n >= 0.7 ? tr().confidenceHigh : n >= 0.4 ? tr().confidenceMedium : tr().confidenceLow;
}
function sourceTypeLabel(sourceType) {
  const x = tr();
  return sourceType === 'transaction' ? x.sourceTransaction : sourceType === 'purchase_item' ? x.sourceItem : x.sourcePurchase;
}

function drawCandidates(host, items) {
  const list = host.querySelector('#tax-candidate-list');
  if (!items.length) {
    list.innerHTML = `<div class="row state-empty"><div class="row-sub">${esc(tr().none)}</div></div>`;
    return;
  }
  list.innerHTML = '';
  const frag = document.createDocumentFragment();
  for (const c of items) {
    const row = document.createElement('div');
    row.className = `tax-case tax-status-${c.status}`;
    row.dataset.taxCandidateId = c.id;
    const final = ['confirmed', 'rejected', 'ignored'].includes(c.status);
    row.innerHTML = `
      <div class="tax-case-main">
        <div class="tax-case-top">
          <div>
            <div class="row-title">${esc(c.sourceTitle || tr().fallbackTitle)}</div>
            <div class="row-sub">${esc([c.sourceDate ? ctx.date(c.sourceDate) : '', sourceTypeLabel(c.sourceType)].filter(Boolean).join(' · '))}</div>
          </div>
          <div class="amount">${ctx.money(c.eligibleAmount, c.currency)}</div>
        </div>
        <div class="tax-badges">
          <span>${esc(c.taxCategoryName || c.taxCategoryCode || '—')}</span>
          <span>${esc(confidenceLabel(c.confidence))}</span>
          <span>${esc(statusLabel(c.status))}</span>
          ${c.hasDocument ? `<span class="tax-doc-ok">${esc(tr().document)}</span>` : ''}
        </div>
        <p>${esc(c.explanation || '')}</p>
        ${Number(c.eligiblePercentage) !== 100 ? `<div class="row-sub">${esc(tr().eligible)}: ${esc(c.eligiblePercentage)}% · ${ctx.money(c.grossAmount, c.currency)} → ${ctx.money(c.eligibleAmount, c.currency)}</div>` : ''}
      </div>
      <div class="tax-case-actions">
        ${!final ? `<button type="button" class="btn btn-secondary" data-share>${esc(tr().edit)}</button><button type="button" class="btn btn-secondary" data-reject>${esc(tr().reject)}</button><button type="button" data-confirm>${esc(tr().confirm)}</button>` : ''}
      </div>`;
    row.querySelector('[data-confirm]')?.addEventListener('click', () => decide(c.id, 'confirm'));
    row.querySelector('[data-reject]')?.addEventListener('click', () => decide(c.id, 'reject'));
    row.querySelector('[data-share]')?.addEventListener('click', () => openEditShare(c));
    frag.appendChild(row);
  }
  list.appendChild(frag);
}

function drawBreakdown(host, items) {
  const card = host.querySelector('#tax-breakdown-card');
  const bar = host.querySelector('#tax-breakdown-bar');
  const list = host.querySelector('#tax-breakdown');
  if (!card || !list) return;
  const groups = new Map();
  for (const c of items || []) {
    if (['rejected', 'ignored'].includes(c.status)) continue;
    const amount = Number(c.eligibleAmount || 0);
    if (!(amount > 0)) continue;
    const key = c.taxCategoryCode || c.taxCategoryName || '—';
    const group = groups.get(key) || { name: c.taxCategoryName || c.taxCategoryCode || '—', amount: 0, count: 0, currency: c.currency || 'EUR' };
    group.amount += amount;
    group.count += 1;
    groups.set(key, group);
  }
  const rows = [...groups.values()].sort((a, b) => b.amount - a.amount);
  const total = rows.reduce((sum, g) => sum + g.amount, 0);
  if (!rows.length || !(total > 0)) {
    card.hidden = true;
    if (bar) { bar.hidden = true; bar.innerHTML = ''; }
    list.innerHTML = '';
    return;
  }
  card.hidden = false;
  const cat = i => (i % 8) + 1;
  if (bar) {
    bar.hidden = false;
    bar.innerHTML = rows.map((g, i) => `<span class="tax-seg" data-cat="${cat(i)}" style="flex:${g.amount} 1 0"></span>`).join('');
  }
  list.innerHTML = rows.map((g, i) => `<div class="row tax-brow"><span class="cat-dot" data-cat="${cat(i)}"></span><div class="tax-brow-main"><div class="row-title">${esc(g.name)}</div><div class="row-sub">${g.count} · ${Math.round(g.amount / total * 100)}%</div></div><div class="amount">${ctx.money(g.amount, g.currency)}</div></div>`).join('');
}

async function decide(id, action) {
  try {
    await ctx.api(`api/tax/candidates/${id}/${action}`, { method: 'POST' });
    await renderTax(ctx);
  } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
}

function openEditShare(c) {
  const dlg = ctx.dialog(`<form class="dialog-card">
    <div class="panel-head"><h2>${esc(tr().amountPrompt)}</h2><button type="button" data-close aria-label="${esc(ctx.get('common.close'))}">×</button></div>
    <label>${esc(tr().percent)}<input name="pct" type="number" min="0" max="100" step="1" value="${esc(c.eligiblePercentage)}" required></label>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(ctx.get('common.cancel'))}</button><button type="submit">${esc(ctx.get('common.save'))}</button></div>
  </form>`);
  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async e => {
    e.preventDefault();
    const pct = Number(new FormData(e.currentTarget).get('pct'));
    try {
      await ctx.api(`api/tax/candidates/${c.id}`, ctx.jsonBody({ taxCategoryId: null, eligiblePercentage: pct, status: null }, 'PUT'));
      dlg.close();
      ctx.toast(ctx.get('common.saved'));
      await renderTax(ctx);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  };
  dlg.showModal();
}

async function analyze(host) {
  const button = host.querySelector('#tax-analyze');
  if (button) { button.disabled = true; button.textContent = tr().analyzing; }
  try {
    await ctx.api(`api/tax/analyze?year=${year}`, { method: 'POST' });
    ctx.toast(tr().analysisDone);
    await renderTax(ctx);
  } catch (err) {
    ctx.toast(err.message || ctx.get('common.error'));
    if (button) { button.disabled = false; button.textContent = tr().analyze; }
  }
}

// ---- settings dialog: personal opt-in, plus the advanced analysis toggles from tax-review-extra.js
// composed directly into the same dialog (a single settings surface instead of the old two
// DOM-injected panels spread across the Settings view and a MutationObserver). The per-space
// enable/disable toggle only ever needs to live in the gate screen above: this dialog is only
// reachable from the fully-enabled view, at which point the space is already on for everyone. ----

function openSettingsDialog(settings, profile) {
  const dlg = ctx.dialog(`<div class="dialog-card">
    <div class="panel-head"><h2>${esc(tr().settingsTitle)}</h2><button type="button" data-close aria-label="${esc(ctx.get('common.close'))}">×</button></div>
    <div class="settings-grid tax-settings-grid">
      <label class="check"><input id="tax-personal-enabled" type="checkbox" ${profile?.assistantEnabled ? 'checked' : ''}><span>${esc(tr().personal)}</span></label>
      <div class="tax-setting-copy">${esc(tr().personalHint)}</div>
    </div>
  </div>`);
  const card = dlg.querySelector('.dialog-card');
  dlg.querySelector('[data-close]').onclick = () => dlg.close();

  card.querySelector('#tax-personal-enabled').addEventListener('change', async e => {
    const checked = e.currentTarget.checked;
    e.currentTarget.disabled = true;
    try {
      await ctx.api('api/tax/profile/settings', ctx.jsonBody({ assistantEnabled: checked }, 'PUT'));
      ctx.toast(ctx.get('common.saved'));
    } catch (err) {
      e.currentTarget.checked = !checked;
      ctx.toast(err.message || ctx.get('common.error'));
    } finally { e.currentTarget.disabled = false; }
  });

  renderAdvancedSettings(ctx, card, settings, { onSaved: () => {} });

  dlg.addEventListener('close', () => renderTax(ctx), { once: true });
  dlg.showModal();
}
