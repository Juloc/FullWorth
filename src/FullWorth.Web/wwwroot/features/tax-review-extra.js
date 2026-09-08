// Tax assistant — year-review checklist, CSV/JSON export, per-candidate document upload and the
// advanced analysis toggles. Composed directly into features/tax.js's render pass instead of the old
// MutationObserver-based patch layer: tax.js calls renderTaxYearPanel()/wireDocumentUploads() right
// after it draws the candidate list, and openSettingsDialog() calls renderAdvancedSettings() to extend
// the shared settings dialog. No feature module here talks to /bff/* directly — reads go through
// ctx.api (the shared client from core/services.js), and the CSV/JSON download needs the raw Response
// (for its blob body), so it uses apiClient.backendResponse — the same shared client wealth-portability.js
// uses for its backup download — never a hand-rolled fetch/BFF URL.
import { apiClient } from '../core/services.js';

function lang() { return (document.documentElement.lang || '').startsWith('en') ? 'en' : 'de'; }
const copy = {
  de: {
    title: 'Steuerjahr-Check', ready: 'Steuerjahr ist nach den erkannten Daten bereit.', check: 'Jahresprüfung',
    exportCsv: 'CSV exportieren', exportJson: 'JSON exportieren', exportFailed: 'Export fehlgeschlagen.',
    upload: 'Beleg hinzufügen', uploading: 'Wird hochgeladen…', added: 'Beleg hinzugefügt.',
    noTarget: 'Für diesen Hinweis ist kein direkter Beleg-Upload verfügbar.',
    advanced: 'Analyse-Einstellungen', automatic: 'Automatisch analysieren', transactions: 'Bankbuchungen analysieren',
    purchases: 'Käufe und Artikel analysieren', documents: 'Belege als Evidenz analysieren',
    ai: 'KI-Hinweise nutzen, falls ein Provider konfiguriert ist', notifications: 'Offene Steuerhinweise an der Navigation anzeigen',
    ownerHint: 'Diese Bereichseinstellungen können nur Eigentümer ändern.',
    deleteData: 'Steuerdaten löschen',
    deleteConfirm: 'Alle erzeugten Steuerhinweise, Lernregeln und Analyseverläufe dieses Finanzbereichs löschen? Bankbuchungen und Belege selbst bleiben erhalten.',
    deleted: 'Steuerdaten gelöscht.', deleteFailed: 'Steuerdaten konnten nicht gelöscht werden.'
  },
  en: {
    title: 'Tax year check', ready: 'The tax year is ready based on the detected data.', check: 'Year review',
    exportCsv: 'Export CSV', exportJson: 'Export JSON', exportFailed: 'Export failed.',
    upload: 'Add receipt', uploading: 'Uploading…', added: 'Receipt added.',
    noTarget: 'No direct receipt upload is available for this suggestion.',
    advanced: 'Analysis settings', automatic: 'Analyze automatically', transactions: 'Analyze bank transactions',
    purchases: 'Analyze purchases and items', documents: 'Analyze receipts as evidence',
    ai: 'Use AI suggestions when a provider is configured', notifications: 'Show open tax suggestions in navigation',
    ownerHint: 'Only fullworth-space owners can change these settings.',
    deleteData: 'Delete tax data',
    deleteConfirm: 'Delete all generated tax suggestions, learned rules and analysis history for this finance space? Bank transactions and receipts themselves are kept.',
    deleted: 'Tax data deleted.', deleteFailed: 'Tax data could not be deleted.'
  }
};
function t(key) { return copy[lang()][key] || key; }

// ---- year-review checklist panel + CSV/JSON export (tax.js's toolbar hosts the tab/year controls;
// this fills the `[data-tax-year-panel]` slot that tax.js renders below the hero card). ----

export async function renderTaxYearPanel(ctx, host, year) {
  const slot = host.querySelector('[data-tax-year-panel]');
  if (!slot) return;
  let review;
  try { review = await ctx.api(`api/tax/years/${year}/review`); }
  catch { slot.hidden = true; slot.innerHTML = ''; return; }

  const checks = review?.checks || [];
  slot.hidden = false;
  slot.innerHTML = `
    <div class="panel-head tax-review-head">
      <div><h2>${ctx.esc(t('title'))}</h2><span class="tax-year-review-state ${review?.ready ? 'is-ready' : 'is-open'}">${ctx.esc(review?.ready ? t('ready') : t('check'))}</span></div>
      <div class="tax-export-actions">
        <button type="button" class="btn btn-secondary" data-tax-export="csv">${ctx.esc(t('exportCsv'))}</button>
        <button type="button" class="btn btn-secondary" data-tax-export="json">${ctx.esc(t('exportJson'))}</button>
      </div>
    </div>
    <div class="tax-year-review-list">${checks.map(check => `<div class="tax-year-review-check tax-review-${ctx.esc(check.severity)}"><strong>${ctx.esc(check.count || '')}</strong><span>${ctx.esc(check.message)}</span></div>`).join('')}</div>`;
  slot.querySelectorAll('[data-tax-export]').forEach(button =>
    button.addEventListener('click', () => downloadExport(ctx, year, button.dataset.taxExport, button)));
}

async function downloadExport(ctx, year, format, button) {
  const original = button.textContent;
  button.disabled = true;
  try {
    const response = await apiClient.backendResponse(`api/tax/years/${year}/export?format=${encodeURIComponent(format)}`);
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `fullworth-tax-${year}.${format}`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  } catch (error) {
    ctx.toast(error.message || t('exportFailed'));
  } finally {
    button.disabled = false;
    button.textContent = original;
  }
}

// ---- per-candidate document upload (only for 'needs_document' purchase/purchase-item candidates;
// tax.js's drawCandidates() already tags each row with data-tax-candidate-id). ----

export function wireDocumentUploads(ctx, host, visibleCandidates, { onUploaded } = {}) {
  host.querySelectorAll('.tax-case[data-tax-candidate-id]').forEach(row => {
    const candidate = visibleCandidates.find(c => String(c.id) === row.dataset.taxCandidateId);
    if (!candidate || candidate.status !== 'needs_document' || !['purchase', 'purchase_item'].includes(candidate.sourceType)) return;
    const actions = row.querySelector('.tax-case-actions');
    if (!actions || actions.querySelector('[data-tax-upload-document]')) return;
    const button = document.createElement('button');
    button.type = 'button';
    button.className='btn btn-secondary';
    button.dataset.taxUploadDocument = '1';
    button.textContent = t('upload');
    button.addEventListener('click', () => uploadDocument(ctx, candidate, button, onUploaded));
    actions.prepend(button);
  });
}

async function uploadDocument(ctx, candidate, button, onUploaded) {
  let target;
  try { target = await ctx.api(`api/tax/candidates/${candidate.id}/document-target`); }
  catch { ctx.toast(t('noTarget')); return; }
  if (!target?.uploadPath) { ctx.toast(t('noTarget')); return; }

  const input = document.createElement('input');
  input.type = 'file';
  input.accept = 'image/jpeg,image/png,image/webp,image/heic,application/pdf';
  input.hidden = true;
  document.body.appendChild(input);
  input.addEventListener('change', async () => {
    const file = input.files?.[0];
    input.remove();
    if (!file) return;
    const original = button.textContent;
    button.disabled = true;
    button.textContent = t('uploading');
    try {
      const form = new FormData();
      form.append('document', file);
      form.append('documentType', 'receipt');
      await ctx.api(target.uploadPath, { method: 'POST', body: form });
      await ctx.api(`api/tax/analyze?year=${candidate.taxYear}`, { method: 'POST' });
      ctx.toast(t('added'));
      onUploaded?.();
    } catch (error) {
      ctx.toast(error.message);
    } finally {
      button.disabled = false;
      button.textContent = original;
    }
  }, { once: true });
  input.click();
}

// ---- advanced per-space analysis toggles + delete-data, appended into tax.js's settings dialog. ----

export function renderAdvancedSettings(ctx, container, settings, { onSaved } = {}) {
  container.querySelector('#tax-advanced-settings')?.remove();
  const section = document.createElement('div');
  section.id = 'tax-advanced-settings';
  section.className = 'tax-advanced-settings';
  section.innerHTML = `<h3>${ctx.esc(t('advanced'))}</h3>
    <div class="tax-advanced-grid">
      ${toggleHtml(ctx, 'automaticAnalysisEnabled', t('automatic'), settings.automaticAnalysisEnabled)}
      ${toggleHtml(ctx, 'analyzeTransactions', t('transactions'), settings.analyzeTransactions)}
      ${toggleHtml(ctx, 'analyzePurchases', t('purchases'), settings.analyzePurchases)}
      ${toggleHtml(ctx, 'analyzeDocuments', t('documents'), settings.analyzeDocuments)}
      ${toggleHtml(ctx, 'aiAnalysisEnabled', t('ai'), settings.aiAnalysisEnabled)}
      ${toggleHtml(ctx, 'showTaxNotifications', t('notifications'), settings.showTaxNotifications)}
    </div>
    <div class="tax-advanced-footer"><span>${ctx.esc(t('ownerHint'))}</span><button type="button" class="btn btn-secondary tax-delete-data">${ctx.esc(t('deleteData'))}</button></div>`;
  container.appendChild(section);

  let current = settings;
  section.querySelectorAll('input[data-tax-setting-key]').forEach(input => {
    input.addEventListener('change', async event => {
      const target = event.currentTarget;
      const key = target.dataset.taxSettingKey;
      const previous = !!current[key];
      const next = { ...current, [key]: target.checked };
      target.disabled = true;
      try {
        current = await ctx.api('api/tax/settings', ctx.jsonBody({
          enabled: next.enabled,
          countryCode: next.countryCode,
          defaultTaxYear: next.defaultTaxYear,
          automaticAnalysisEnabled: next.automaticAnalysisEnabled,
          aiAnalysisEnabled: next.aiAnalysisEnabled,
          analyzeTransactions: next.analyzeTransactions,
          analyzePurchases: next.analyzePurchases,
          analyzeDocuments: next.analyzeDocuments,
          showTaxNotifications: next.showTaxNotifications
        }, 'PUT'));
        ctx.toast(ctx.get('common.saved'));
        if (['analyzeDocuments', 'analyzePurchases', 'analyzeTransactions'].includes(key)) {
          await ctx.api(`api/tax/analyze?year=${current.defaultTaxYear}`, { method: 'POST' });
        }
        onSaved?.(current);
      } catch (error) {
        target.checked = previous;
        ctx.toast(error.status === 403 ? t('ownerHint') : (error.message || ctx.get('common.error')));
      } finally {
        target.disabled = false;
      }
    });
  });

  section.querySelector('.tax-delete-data').addEventListener('click', async () => {
    if (!await ctx.confirm(t('deleteConfirm'), { destructive: true, confirmLabel: t('deleteData') })) return;
    try {
      await ctx.api('api/tax/data', { method: 'DELETE' });
      ctx.toast(t('deleted'));
      onSaved?.(current);
    } catch (error) {
      ctx.toast(error.status === 403 ? t('ownerHint') : (error.message || t('deleteFailed')));
    }
  });
}

function toggleHtml(ctx, key, label, checked) {
  return `<label class="check tax-advanced-toggle"><input type="checkbox" data-tax-setting-key="${key}" ${checked ? 'checked' : ''}><span>${ctx.esc(label)}</span></label>`;
}
