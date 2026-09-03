// Canonical purchase discount editor. Purchase.DiscountAmount is a derived mirror; all mutations use
// /discounts so basket promotions are never forced onto an arbitrary product and manual corrections
// remain distinguishable from OCR/Amazon/Codex imports.

const text = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;

const TYPES = [
  ['price_reduction', 'Preisreduzierung', 'Price reduction'],
  ['percentage', 'Prozent-Rabatt', 'Percentage discount'],
  ['coupon', 'Coupon', 'Coupon'],
  ['loyalty', 'Treue-/App-Rabatt', 'Loyalty/app discount'],
  ['multibuy', 'Mehrkauf-Aktion', 'Multibuy'],
  ['bundle', 'Bundle-Aktion', 'Bundle'],
  ['employee', 'Mitarbeiterrabatt', 'Employee discount'],
  ['promotion', 'Aktion', 'Promotion'],
  ['other', 'Sonstiger Rabatt', 'Other discount']
];

export async function mountPurchaseDiscountActions({ dlg, purchase, writable, api, esc, makeDialog, money, showError, refresh }) {
  const side = dlg?.querySelector('.pa-work-side');
  if (!side || side.querySelector('[data-pa-discounts-card]')) return;

  const card = document.createElement('section');
  card.className = 'pa-card pa-discounts-card';
  card.dataset.paDiscountsCard = '';
  const confirmCard = side.querySelector('.pa-confirm-card');
  side.insertBefore(card, confirmCard || null);

  let rows = [];
  const load = async () => {
    card.innerHTML = `<div class="pa-card-head"><div><h3>${esc(text('Rabatte', 'Discounts'))}</h3><div class="row-sub">${esc(text('Beträge sind immer positiv: wie viel du gespart hast.', 'Amounts are always positive: how much you saved.'))}</div></div></div><div class="state-loading">${esc(text('Lade Rabatte…', 'Loading discounts…'))}</div>`;
    try {
      rows = (await api(`api/purchases/${purchase.id}/discounts`)) || [];
      render();
    } catch (error) {
      card.innerHTML = `<div class="pa-card-head"><h3>${esc(text('Rabatte', 'Discounts'))}</h3></div><div class="pa-error">${esc(error.message)}</div>`;
    }
  };

  const render = () => {
    const total = rows.reduce((sum, row) => sum + Number(row.amount || 0), 0);
    const itemById = new Map((purchase.items || []).map(item => [String(item.id), item]));
    card.innerHTML = `<div class="pa-card-head"><div><h3>${esc(text('Rabatte', 'Discounts'))}</h3><div class="row-sub">${esc(text('Kanonische Rabattzeilen; der Summenwert oben im Kauf ist nur ein Spiegel.', 'Canonical discount rows; the purchase summary value is only a mirror.'))}</div></div>${writable ? `<button type="button" class="ghost" data-discount-add>${esc(text('Rabatt hinzufügen', 'Add discount'))}</button>` : ''}</div>
      <div class="pa-discount-total"><span>${esc(text('Erkannte Ersparnis', 'Recognized savings'))}</span><strong>${esc(money(total, purchase.currency || 'EUR'))}</strong></div>
      <div class="pa-discount-list">${rows.length ? rows.map(row => {
        const item = row.purchaseItemId ? itemById.get(String(row.purchaseItemId)) : null;
        const confidence = row.confidence == null ? '' : ` · ${Math.round(Number(row.confidence) * 100)}%`;
        return `<div class="pa-discount-row" data-discount-id="${esc(row.id)}"><div><strong>${esc(row.label || typeLabel(row.type))}</strong><span>${esc(typeLabel(row.type))} · ${esc(item ? item.name : text('Warenkorb', 'Basket'))}</span><small>${esc(sourceLabel(row.source))}${esc(confidence)}</small></div><div class="pa-discount-row-actions"><strong>−${esc(money(row.amount, purchase.currency || 'EUR'))}</strong>${writable ? `<button type="button" class="ghost" data-discount-edit="${esc(row.id)}">${esc(text('Bearbeiten', 'Edit'))}</button><button type="button" class="ghost danger" data-discount-delete="${esc(row.id)}">${esc(text('Löschen', 'Delete'))}</button>` : ''}</div></div>`;
      }).join('') : `<div class="state-empty">${esc(text('Keine strukturierten Rabatte gespeichert.', 'No structured discounts stored.'))}</div>`}</div>`;

    card.querySelector('[data-discount-add]')?.addEventListener('click', () => openEditor(null));
    card.querySelectorAll('[data-discount-edit]').forEach(button => button.addEventListener('click', () =>
      openEditor(rows.find(row => String(row.id) === button.dataset.discountEdit) || null)));
    card.querySelectorAll('[data-discount-delete]').forEach(button => button.addEventListener('click', async () => {
      if (!window.confirm(text('Rabatt wirklich löschen? Der Kauf wird wieder auf „Zu prüfen“ gesetzt.', 'Delete this discount? The purchase will return to needs-review.'))) return;
      try {
        await api(`api/purchases/${purchase.id}/discounts/${button.dataset.discountDelete}`, { method: 'DELETE' });
        await refresh();
      } catch (error) { showError(dlg, error.message); }
    }));
  };

  const openEditor = row => {
    const itemOptions = (purchase.items || []).map(item => `<option value="${esc(item.id)}" ${String(row?.purchaseItemId || '') === String(item.id) ? 'selected' : ''}>${esc(item.name)}</option>`).join('');
    const typeOptions = TYPES.map(([value, de, en]) => `<option value="${value}" ${String(row?.type || 'other') === value ? 'selected' : ''}>${esc(text(de, en))}</option>`).join('');
    const editor = makeDialog(`<form class="pa-dialog-card pa-picker pa-discount-editor"><div class="panel-head"><div><h2>${esc(row ? text('Rabatt bearbeiten', 'Edit discount') : text('Rabatt hinzufügen', 'Add discount'))}</h2><div class="row-sub">${esc(text('Nur tatsächlich erkennbare Rabattmechanik auswählen. Unklar? „Sonstiger Rabatt“ verwenden.', 'Choose only mechanics supported by the source. If unclear, use “Other discount”.'))}</div></div><button type="button" data-close>×</button></div>
      <div class="pa-form-grid"><label>${esc(text('Typ', 'Type'))}<select name="type">${typeOptions}</select></label><label>${esc(text('Zuordnung', 'Assignment'))}<select name="purchaseItemId"><option value="">${esc(text('Warenkorb / gesamter Kauf', 'Basket / whole purchase'))}</option>${itemOptions}</select></label><label>${esc(text('Betrag gespart', 'Amount saved'))}<input name="amount" type="number" min="0.01" step="0.01" value="${esc(row?.amount ?? '')}" required></label><label>${esc(text('Prozent (optional)', 'Percentage (optional)'))}<input name="percentage" type="number" min="0" max="100" step="0.01" value="${esc(row?.percentage ?? '')}"></label></div>
      <label>${esc(text('Bezeichnung', 'Label'))}<input name="label" maxlength="250" value="${esc(row?.label || '')}" placeholder="${esc(text('z. B. App-Coupon 2 €', 'e.g. App coupon €2'))}"></label>
      <div class="pa-form-grid"><label>${esc(text('Coupon-Code (optional)', 'Coupon code (optional)'))}<input name="couponCode" maxlength="120" value="${esc(row?.couponCode || '')}"></label><label>${esc(text('Quelltext (optional)', 'Raw source text (optional)'))}<input name="rawText" maxlength="1000" value="${esc(row?.rawText || '')}"></label></div>
      ${row ? `<div class="row-sub">${esc(text('Beim Speichern wird eine automatisch erkannte/importierte Zeile bewusst zu einer manuellen Korrektur; ihre AI-Confidence wird entfernt.', 'Saving intentionally promotes an extracted/imported row to a manual correction and clears its AI confidence.'))}</div>` : ''}
      <div class="dialog-actions"><button type="button" data-close>${esc(text('Abbrechen', 'Cancel'))}</button><button type="submit">${esc(text('Speichern', 'Save'))}</button></div><div class="pa-dialog-error" data-error hidden></div></form>`);
    editor.querySelectorAll('[data-close]').forEach(button => button.onclick = () => editor.close());
    editor.querySelector('form').onsubmit = async event => {
      event.preventDefault();
      const form = new FormData(event.currentTarget);
      const amount = Number(form.get('amount'));
      const percentageText = String(form.get('percentage') || '').trim();
      if (!(amount > 0)) { showError(editor, text('Rabattbetrag muss größer als 0 sein.', 'Discount amount must be greater than zero.')); return; }
      const payload = {
        purchaseItemId: form.get('purchaseItemId') || null,
        type: String(form.get('type') || 'other'),
        label: String(form.get('label') || '').trim() || null,
        amount,
        percentage: percentageText ? Number(percentageText) : null,
        couponCode: String(form.get('couponCode') || '').trim() || null,
        rawText: String(form.get('rawText') || '').trim() || null
      };
      try {
        await api(row ? `api/purchases/${purchase.id}/discounts/${row.id}` : `api/purchases/${purchase.id}/discounts`, {
          method: row ? 'PATCH' : 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });
        editor.close();
        await refresh();
      } catch (error) { showError(editor, error.message); }
    };
    editor.showModal();
  };

  await load();
}

function typeLabel(value) {
  const row = TYPES.find(([key]) => key === String(value || '').toLowerCase());
  return row ? text(row[1], row[2]) : text('Sonstiger Rabatt', 'Other discount');
}

function sourceLabel(value) {
  const source = String(value || 'manual').toLowerCase();
  if (source === 'manual') return text('Manuell', 'Manual');
  if (source === 'codex') return 'Codex/GPT';
  if (source === 'amazon') return 'Amazon';
  if (source === 'ocr') return 'OCR';
  if (source === 'migration') return text('Übernommen', 'Migrated');
  return source;
}
