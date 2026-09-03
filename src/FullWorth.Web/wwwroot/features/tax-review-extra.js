const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const lang = () => document.documentElement.lang?.startsWith('en') ? 'en' : 'de';
const copy = {
  de: {
    title:'Steuerjahr-Check', ready:'Steuerjahr ist nach den erkannten Daten bereit.', upload:'Beleg hinzufügen', uploading:'Wird hochgeladen…',
    added:'Beleg hinzugefügt.', noTarget:'Für diesen Hinweis ist kein direkter Beleg-Upload verfügbar.', check:'Jahresprüfung',
    exportCsv:'CSV exportieren', exportJson:'JSON exportieren', exportFailed:'Export fehlgeschlagen.',
    advanced:'Analyse-Einstellungen', automatic:'Automatisch analysieren', transactions:'Bankbuchungen analysieren', purchases:'Käufe und Artikel analysieren',
    documents:'Belege als Evidenz analysieren', ai:'KI-Hinweise nutzen, falls ein Provider konfiguriert ist', notifications:'Offene Steuerhinweise an der Navigation anzeigen',
    ownerHint:'Diese Bereichseinstellungen können nur Eigentümer ändern.', saved:'Gespeichert.', deleteData:'Steuerdaten löschen',
    deleteConfirm:'Alle erzeugten Steuerhinweise, Lernregeln und Analyseverläufe dieses Finanzbereichs löschen? Bankbuchungen und Belege selbst bleiben erhalten.',
    deleted:'Steuerdaten gelöscht.', deleteFailed:'Steuerdaten konnten nicht gelöscht werden.', openNotice:'Offene Steuerhinweise'
  },
  en: {
    title:'Tax year check', ready:'The tax year is ready based on the detected data.', upload:'Add receipt', uploading:'Uploading…',
    added:'Receipt added.', noTarget:'No direct receipt upload is available for this suggestion.', check:'Year review',
    exportCsv:'Export CSV', exportJson:'Export JSON', exportFailed:'Export failed.',
    advanced:'Analysis settings', automatic:'Analyze automatically', transactions:'Analyze bank transactions', purchases:'Analyze purchases and items',
    documents:'Analyze receipts as evidence', ai:'Use AI suggestions when a provider is configured', notifications:'Show open tax suggestions in navigation',
    ownerHint:'Only fullworth-space owners can change these settings.', saved:'Saved.', deleteData:'Delete tax data',
    deleteConfirm:'Delete all generated tax suggestions, learned rules and analysis history for this finance space? Bank transactions and receipts themselves are kept.',
    deleted:'Tax data deleted.', deleteFailed:'Tax data could not be deleted.', openNotice:'Open tax suggestions'
  }
};
const t = key => copy[lang()][key] || key;
const spaceId = () => localStorage.getItem('finance.space') || '';
const year = () => Number($('#tax-year')?.value || new Date().getFullYear());
const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

function scoped(path) {
  const [base, query = ''] = path.replace(/^\//, '').split('?');
  const params = new URLSearchParams(query);
  if (spaceId() && !params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', spaceId());
  return `/bff/backend/${base}${params.toString() ? `?${params}` : ''}`;
}
async function request(path, options = {}) {
  const response = await fetch(scoped(path), { credentials:'same-origin', ...options });
  if (!response.ok) {
    const error = new Error(String(response.status));
    error.status = response.status;
    try {
      const body = await response.json();
      error.message = body?.error || body?.message || body?.title || error.message;
    } catch {}
    throw error;
  }
  return response;
}
async function api(path, options = {}) {
  const response = await request(path, options);
  if (response.status === 204) return null;
  return response.json();
}
function jsonOptions(method, body) {
  return { method, headers:{'Content-Type':'application/json'}, body:JSON.stringify(body) };
}
function toast(message) {
  const el = $('#toast'); if (!el) return;
  el.textContent = message; el.classList.add('show');
  clearTimeout(toast.timer); toast.timer = setTimeout(() => el.classList.remove('show'), 3000);
}
function ensureCss() {
  if ($('link[data-tax-review-extra]')) return;
  const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = '/features/tax-review-extra.css'; link.dataset.taxReviewExtra = '1';
  document.head.appendChild(link);
}

let generation = 0;
let scheduled = null;
let running = false;
let settingsLoading = false;
let settingsSpace = '';
let noticeLoading = false;

async function enhance() {
  if (!spaceId()) return;
  await Promise.all([enhanceSettings(), refreshNavNotice()]);

  if (running) return;
  const view = $('#view-tax');
  const list = $('#tax-candidate-list');
  if (!view || !list || !view.classList.contains('active')) return;
  running = true;
  const current = ++generation;
  try {
    const taxYear = year();
    const [review, candidates] = await Promise.all([
      api(`api/tax/years/${taxYear}/review`),
      api(`api/tax/candidates?year=${taxYear}`)
    ]);
    if (current !== generation) return;
    renderReview(view, review);
    const visible = location.pathname.startsWith('/tax/review')
      ? (candidates || []).filter(x => ['needs_review','detected','needs_document','incomplete'].includes(x.status))
      : (candidates || []);
    addDocumentActions(list, visible);
  } catch (error) {
    console.debug('Tax year review extension unavailable', error);
  } finally {
    running = false;
  }
}

async function refreshNavNotice() {
  const nav = $('#tax-nav');
  if (!nav || noticeLoading || nav.hidden) {
    if (nav?.hidden) $('.tax-nav-badge', nav)?.remove();
    return;
  }
  noticeLoading = true;
  try {
    const settings = await api('api/tax/settings');
    if (!settings?.enabled || !settings.showTaxNotifications) {
      $('.tax-nav-badge', nav)?.remove();
      return;
    }
    const taxYear = Number(settings.defaultTaxYear || year());
    const summary = await api(`api/tax/years/${taxYear}/summary`);
    const count = Number(summary?.needsReviewCount || 0) + Number(summary?.needsDocumentCount || 0);
    let badge = $('.tax-nav-badge', nav);
    if (!count) {
      badge?.remove();
      return;
    }
    if (!badge) {
      badge = document.createElement('span');
      badge.className = 'tax-nav-badge';
      nav.appendChild(badge);
    }
    badge.textContent = count > 99 ? '99+' : String(count);
    badge.title = t('openNotice');
    badge.setAttribute('aria-label', `${t('openNotice')}: ${count}`);
  } catch (error) {
    console.debug('Tax navigation notice unavailable', error);
  } finally {
    noticeLoading = false;
  }
}

function renderReview(view, review) {
  let panel = $('#tax-year-review-panel', view);
  if (!panel) {
    panel = document.createElement('article');
    panel.id = 'tax-year-review-panel'; panel.className = 'panel tax-year-review';
    const cases = $('.tax-cases', view); cases?.before(panel);
  }
  const checks = review?.checks || [];
  panel.innerHTML = `<div class="panel-head tax-review-head"><div><h2>${esc(t('title'))}</h2><span class="tax-year-review-state ${review?.ready ? 'is-ready' : 'is-open'}">${esc(review?.ready ? t('ready') : t('check'))}</span></div><div class="tax-export-actions"><button type="button" class="ghost" data-tax-export="csv">${esc(t('exportCsv'))}</button><button type="button" class="ghost" data-tax-export="json">${esc(t('exportJson'))}</button></div></div>
    <div class="tax-year-review-list">${checks.map(check => `<div class="tax-year-review-check tax-review-${esc(check.severity)}"><strong>${esc(check.count || '')}</strong><span>${esc(check.message)}</span></div>`).join('')}</div>`;
  $$('[data-tax-export]', panel).forEach(button => button.addEventListener('click', () => downloadExport(button.dataset.taxExport, button)));
}

async function downloadExport(format, button) {
  const original = button.textContent;
  button.disabled = true;
  try {
    const response = await request(`api/tax/years/${year()}/export?format=${encodeURIComponent(format)}`);
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `fullworth-tax-${year()}.${format}`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (error) {
    toast(error.message || t('exportFailed'));
  } finally {
    button.disabled = false;
    button.textContent = original;
  }
}

async function enhanceSettings() {
  const panel = $('#tax-settings-panel');
  const grid = $('.tax-settings-grid', panel);
  const currentSpace = spaceId();
  if (!panel || !grid || !currentSpace || settingsLoading) return;
  if ($('#tax-advanced-settings', panel) && settingsSpace === currentSpace) return;

  settingsLoading = true;
  try {
    const settings = await api('api/tax/settings');
    $('#tax-advanced-settings', panel)?.remove();
    const section = document.createElement('div');
    section.id = 'tax-advanced-settings';
    section.className = 'tax-advanced-settings';
    section.innerHTML = `<h3>${esc(t('advanced'))}</h3>
      <div class="tax-advanced-grid">
        ${settingToggle('automaticAnalysisEnabled', t('automatic'), settings.automaticAnalysisEnabled)}
        ${settingToggle('analyzeTransactions', t('transactions'), settings.analyzeTransactions)}
        ${settingToggle('analyzePurchases', t('purchases'), settings.analyzePurchases)}
        ${settingToggle('analyzeDocuments', t('documents'), settings.analyzeDocuments)}
        ${settingToggle('aiAnalysisEnabled', t('ai'), settings.aiAnalysisEnabled)}
        ${settingToggle('showTaxNotifications', t('notifications'), settings.showTaxNotifications)}
      </div>
      <div class="tax-advanced-footer"><span>${esc(t('ownerHint'))}</span><button type="button" class="ghost tax-delete-data">${esc(t('deleteData'))}</button></div>`;
    grid.appendChild(section);
    section.dataset.settings = JSON.stringify(settings);
    $$('input[data-tax-setting-key]', section).forEach(input => input.addEventListener('change', saveAdvancedSetting));
    $('.tax-delete-data', section)?.addEventListener('click', deleteTaxData);
    settingsSpace = currentSpace;
  } catch (error) {
    console.debug('Tax advanced settings unavailable', error);
  } finally {
    settingsLoading = false;
  }
}

function settingToggle(key, label, checked) {
  return `<label class="check tax-advanced-toggle"><input type="checkbox" data-tax-setting-key="${esc(key)}" ${checked ? 'checked' : ''}><span>${esc(label)}</span></label>`;
}

async function saveAdvancedSetting(event) {
  const input = event.currentTarget;
  const section = input.closest('#tax-advanced-settings');
  if (!section) return;
  const settings = JSON.parse(section.dataset.settings || '{}');
  const key = input.dataset.taxSettingKey;
  const previous = !!settings[key];
  settings[key] = input.checked;
  input.disabled = true;
  try {
    const updated = await api('api/tax/settings', jsonOptions('PUT', {
      enabled: settings.enabled,
      countryCode: settings.countryCode,
      defaultTaxYear: settings.defaultTaxYear,
      automaticAnalysisEnabled: settings.automaticAnalysisEnabled,
      aiAnalysisEnabled: settings.aiAnalysisEnabled,
      analyzeTransactions: settings.analyzeTransactions,
      analyzePurchases: settings.analyzePurchases,
      analyzeDocuments: settings.analyzeDocuments,
      showTaxNotifications: settings.showTaxNotifications
    }));
    section.dataset.settings = JSON.stringify(updated);
    toast(t('saved'));
    if (key === 'analyzeDocuments' || key === 'analyzePurchases' || key === 'analyzeTransactions') {
      await api(`api/tax/analyze?year=${year()}`, { method:'POST' });
    }
    await refreshNavNotice();
    schedule();
  } catch (error) {
    input.checked = previous;
    toast(error.status === 403 ? t('ownerHint') : error.message);
  } finally {
    input.disabled = false;
  }
}

async function deleteTaxData(event) {
  if (!window.confirm(t('deleteConfirm'))) return;
  const button = event.currentTarget;
  button.disabled = true;
  try {
    await api('api/tax/data', { method:'DELETE' });
    $('.tax-nav-badge', $('#tax-nav'))?.remove();
    toast(t('deleted'));
    schedule();
    $('#refresh')?.click();
  } catch (error) {
    toast(error.status === 403 ? t('ownerHint') : (error.message || t('deleteFailed')));
  } finally {
    button.disabled = false;
  }
}

function addDocumentActions(list, candidates) {
  const rows = $$('.tax-case', list);
  rows.forEach((row, index) => {
    const candidate = candidates[index];
    if (!candidate) return;
    row.dataset.taxCandidateId = candidate.id;
    if (candidate.status !== 'needs_document' || !['purchase','purchase_item'].includes(candidate.sourceType)) return;
    const actions = $('.tax-case-actions', row);
    if (!actions || $('[data-tax-upload-document]', actions)) return;
    const button = document.createElement('button');
    button.type = 'button'; button.className = 'ghost'; button.dataset.taxUploadDocument = '1'; button.textContent = t('upload');
    button.addEventListener('click', () => uploadDocument(candidate, button));
    actions.prepend(button);
  });
}

async function uploadDocument(candidate, button) {
  let target;
  try { target = await api(`api/tax/candidates/${candidate.id}/document-target`); }
  catch { toast(t('noTarget')); return; }
  if (!target?.uploadPath) { toast(t('noTarget')); return; }

  const input = document.createElement('input');
  input.type = 'file'; input.accept = 'image/jpeg,image/png,image/webp,image/heic,application/pdf'; input.hidden = true;
  document.body.appendChild(input);
  input.addEventListener('change', async () => {
    const file = input.files?.[0]; input.remove(); if (!file) return;
    const original = button.textContent; button.disabled = true; button.textContent = t('uploading');
    try {
      const form = new FormData(); form.append('document', file); form.append('documentType', 'receipt');
      await api(target.uploadPath, { method:'POST', body:form });
      await api(`api/tax/analyze?year=${year()}`, { method:'POST' });
      toast(t('added'));
      $('#refresh')?.click();
      schedule();
    } catch (error) { toast(error.message); }
    finally { button.disabled = false; button.textContent = original; }
  }, { once:true });
  input.click();
}

function schedule() { clearTimeout(scheduled); scheduled = setTimeout(enhance, 80); }
function init() {
  ensureCss();
  new MutationObserver(records => {
    if (records.some(record => [...record.addedNodes].some(node => node instanceof Element &&
      (node.matches?.('#tax-nav,#view-tax,.tax-case,#tax-candidate-list,#tax-settings-panel') || node.querySelector?.('#tax-nav,.tax-case,#tax-candidate-list,#tax-settings-panel'))))) schedule();
    if (spaceId() !== settingsSpace) schedule();
  }).observe(document.body, { childList:true, subtree:true });
  document.addEventListener('click', event => {
    if (event.target.closest?.('#tax-nav,[data-tax-tab],#tax-analyze,#refresh,[data-view="settings"]')) schedule();
  }, true);
  window.addEventListener('popstate', schedule);
  schedule();
}
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once:true }); else init();
