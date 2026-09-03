const CSS_HREF = '/category-intelligence.css';
const ICONS = ['🏠','🛒','🍽️','☕','🚗','⛽','⚡','🚆','✈️','🏨','💊','🩺','🦷','👓','🎮','🎬','📱','💻','🐾','🎓','💰','💳','📦','🎁','🏖️','👶','🛡️','📈','🧾','🛠️','👕','📚','❤️','🏋️'];
const PALETTE = ['#2563EB','#7C3AED','#DB2777','#E11D48','#EA580C','#D97706','#059669','#0F766E','#0284C7','#475569'];
const SEMANTIC_COLORS = {
  income:'#059669', housing:'#4F46E5', food:'#EA580C', transport:'#0284C7', vehicle:'#64748B',
  shopping:'#C026D3', health:'#E11D48', insurance:'#0F766E', subscriptions:'#7C3AED', leisure:'#D97706',
  travel:'#0EA5E9', education:'#2563EB', family:'#DB2777', pets:'#A16207', cash:'#475569', fees:'#DC2626',
  taxes:'#991B1B', savings:'#059669', debt:'#B91C1C', transfers:'#64748B', other:'#6B7280', donations:'#DB2777'
};

const TEXT = {
  de: {
    needsReview:'Zu prüfen', reviewed:'Geprüft', reviewAll:'Als geprüft markieren', unreview:'Als ungeprüft markieren',
    selected:n => `${n} ausgewählt`, category:'Kategorie', tagAdd:'Tag +', tagRemove:'Tag −', exclude:'Ausblenden', include:'Einblenden', clear:'Auswahl aufheben',
    confidence:'Erkennung', manual:'Manuell bestätigt', rule:'Eigene Regel', merchant:'Händlerkatalog', text:'Buchungstext', mcc:'MCC', catalog:'Katalog', imported:'Importiert', unclassified:'Nicht erkannt',
    learnTitle:'Diese Korrektur merken?', learnHint:'Du kannst nur diese Buchung ändern oder FullWorth dieselbe Gegenpartei für Vergangenheit und Zukunft merken lassen.',
    onlyThis:'Nur diese Buchung', allExisting:'Alle bisherigen gleichen Buchungen', always:'Auch künftig automatisch', cancel:'Abbrechen',
    learned:n => `${n} Buchung${n===1?'':'en'} aktualisiert`, suggest:'Wiederholt korrigiert', createRule:'Automatisierung erstellen',
    tags:'Tags', manageTags:'Tags bearbeiten', newTag:'Neuer Tag', tagName:'Tag-Name', create:'Anlegen', save:'Speichern', edit:'Bearbeiten', delete:'Löschen',
    color:'Farbe', icon:'Icon', chooseIcon:'Icon auswählen', bulkCategory:'Kategorie für Auswahl', uncategorized:'Nicht kategorisiert',
    reviewCount:n => `${n} ungeprüft`, noTags:'Noch keine Tags', filterTag:'Tag filtern', allTags:'Alle Tags',
    error:'Aktion konnte nicht ausgeführt werden.'
  },
  en: {
    needsReview:'Needs review', reviewed:'Reviewed', reviewAll:'Mark reviewed', unreview:'Mark unreviewed',
    selected:n => `${n} selected`, category:'Category', tagAdd:'Tag +', tagRemove:'Tag −', exclude:'Exclude', include:'Include', clear:'Clear selection',
    confidence:'Recognition', manual:'Manually confirmed', rule:'Personal rule', merchant:'Merchant catalog', text:'Transaction text', mcc:'MCC', catalog:'Catalog', imported:'Imported', unclassified:'Unrecognized',
    learnTitle:'Remember this correction?', learnHint:'Change only this transaction, all matching history, or also create a transparent rule for future transactions.',
    onlyThis:'Only this transaction', allExisting:'All matching history', always:'Also categorize future matches', cancel:'Cancel',
    learned:n => `${n} transaction${n===1?'':'s'} updated`, suggest:'Repeated correction', createRule:'Create automation',
    tags:'Tags', manageTags:'Edit tags', newTag:'New tag', tagName:'Tag name', create:'Create', save:'Save', edit:'Edit', delete:'Delete',
    color:'Color', icon:'Icon', chooseIcon:'Choose icon', bulkCategory:'Category for selection', uncategorized:'Uncategorized',
    reviewCount:n => `${n} unreviewed`, noTags:'No tags yet', filterTag:'Filter tag', allTags:'All tags',
    error:'Action could not be completed.'
  }
};

let overview = null;
let overviewById = new Map();
let categories = [];
let categoryById = new Map();
let appearances = new Map();
let tags = [];
let activeTransactionId = null;
let activeCategoryId = null;
const selectedTransactions = new Set();
let decoratingTransactions = false;
let categoryRefreshPending = false;

function lang() { return document.documentElement.lang?.startsWith('en') ? 'en' : 'de'; }
function t(key, arg) { const value = TEXT[lang()][key] ?? key; return typeof value === 'function' ? value(arg) : value; }
function esc(value) { return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c])); }
function spaceId() { return localStorage.getItem('finance.space'); }

function withSpace(path) {
  const id = spaceId();
  if (!id) return path;
  const [base, query=''] = path.split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', id);
  return `${base}?${params}`;
}

async function api(path, options) {
  const response = await fetch(`/bff/backend/${withSpace(path.replace(/^\//,''))}`, options);
  if (!response.ok) {
    let message = `${response.status}`;
    try { const body = await response.json(); message = body.error || body.title || body.message || message; } catch {}
    throw new Error(message);
  }
  if (response.status === 204) return null;
  return response.json();
}

const json = (method, body) => ({method, headers:{'Content-Type':'application/json'}, body:JSON.stringify(body)});

function toast(message) {
  const el = document.querySelector('#toast');
  if (!el) return;
  el.textContent = message;
  el.classList.add('show');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => el.classList.remove('show'), 3200);
}

function dialog(html) {
  const dlg = document.createElement('dialog');
  dlg.className = 'ci-dialog';
  dlg.innerHTML = html;
  document.body.appendChild(dlg);
  dlg.addEventListener('close', () => dlg.remove());
  return dlg;
}

function ensureCss() {
  if (document.querySelector(`link[href="${CSS_HREF}"]`)) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = CSS_HREF;
  document.head.appendChild(link);
}

function hashColor(value) {
  let hash = 0;
  for (const ch of String(value || '')) hash = ((hash << 5) - hash + ch.charCodeAt(0)) | 0;
  return PALETTE[Math.abs(hash) % PALETTE.length];
}

function categoryColor(categoryId) {
  const explicit = appearances.get(categoryId);
  if (explicit) return explicit;
  const category = categoryById.get(categoryId);
  if (!category) return '#6B7280';
  const rootKey = String(category.key || '').split('.')[0];
  return SEMANTIC_COLORS[rootKey] || hashColor(category.key || category.name);
}

async function loadReferenceData(force=false) {
  if (!force && categories.length && overview) return;
  const [cats, intel, appearanceRows, tagRows] = await Promise.all([
    api('api/categories'),
    api('api/category-intelligence/overview'),
    api('api/category-intelligence/category-appearances'),
    api('api/category-intelligence/tags')
  ]);
  categories = cats || [];
  categoryById = new Map(categories.map(x => [x.id, x]));
  overview = intel || {items:[], needsReview:0};
  overviewById = new Map((overview.items || []).map(x => [x.id, x]));
  appearances = new Map((appearanceRows || []).map(x => [x.categoryId, x.color]));
  tags = tagRows || [];
}

function categoryPath(category) {
  const chain = [];
  let cursor = category;
  const guard = new Set();
  while (cursor && !guard.has(cursor.id)) {
    guard.add(cursor.id);
    chain.unshift(cursor.name);
    cursor = cursor.parentId ? categoryById.get(cursor.parentId) : null;
  }
  return chain.join(' › ');
}

function categoryOptions(selected, includeBlank=true) {
  return `${includeBlank ? `<option value="">${esc(t('uncategorized'))}</option>` : ''}${categories.map(c =>
    `<option value="${c.id}"${c.id === selected ? ' selected' : ''}>${esc(categoryPath(c))}</option>`).join('')}`;
}

function reasonLabel(item) {
  const head = t(item?.reasonCode || 'unclassified');
  return item?.detail ? `${head}: ${item.detail}` : head;
}

function confidenceHtml(item) {
  if (!item) return '';
  const pct = Math.round(Number(item.confidence || 0) * 100);
  return `<span class="ci-confidence ci-confidence-${esc(item.reasonCode)}" title="${esc(reasonLabel(item))}">${pct}% · ${esc(t(item.reasonCode || 'unclassified'))}</span>`;
}

function tagChips(tagItems) {
  return (tagItems || []).map(tag => `<span class="ci-tag" style="--ci-tag:${esc(tag.color || hashColor(tag.name))}">${esc(tag.name)}</span>`).join('');
}

async function currentVisibleTransactions() {
  const query = new URLSearchParams({limit:'500'});
  const text = document.querySelector('#tx-query')?.value.trim();
  const direction = document.querySelector('#tx-direction')?.value;
  const flags = document.querySelector('#tx-flags')?.value;
  if (text) query.set('query', text);
  if (direction) query.set('direction', direction);
  if (flags === 'transfers') query.set('transfersOnly','true');
  if (flags === 'ignored') query.set('includeIgnored','true');
  const data = await api(`api/transactions?${query}`);
  let items = data.items || [];
  if (flags === 'pending') items = items.filter(x => x.status === 'PDNG');
  if (flags === 'ignored') items = items.filter(x => x.isIgnored);
  if (flags === 'needs_review') items = items.filter(x => overviewById.get(x.id)?.needsReview);
  if (flags === 'reviewed') items = items.filter(x => overviewById.get(x.id)?.isReviewed);
  const tagId = document.querySelector('#ci-tag-filter')?.value;
  if (tagId) items = items.filter(x => (overviewById.get(x.id)?.tags || []).some(tag => tag.id === tagId));
  return items;
}

function ensureReviewFilters() {
  const flags = document.querySelector('#tx-flags');
  if (!flags) return;
  if (!flags.querySelector('option[value="needs_review"]')) {
    flags.insertAdjacentHTML('beforeend', `<option value="needs_review">${esc(t('needsReview'))}</option><option value="reviewed">${esc(t('reviewed'))}</option>`);
  }
  const toolbar = flags.closest('.toolbar');
  let select = document.querySelector('#ci-tag-filter');
  if (toolbar && !select) {
    select = document.createElement('select');
    select.id = 'ci-tag-filter';
    select.setAttribute('aria-label', t('filterTag'));
    flags.after(select);
    select.addEventListener('change', () => decorateTransactionRows(true));
  }
  if (select) {
    const previous = select.value;
    select.innerHTML = `<option value="">${esc(t('allTags'))}</option>${tags.map(tag => `<option value="${tag.id}">${esc(tag.name)}</option>`).join('')}`;
    select.value = previous;
  }
  ensureBulkBar(toolbar);
}

function ensureBulkBar(toolbar) {
  if (!toolbar || document.querySelector('#ci-bulkbar')) return;
  const bar = document.createElement('div');
  bar.id = 'ci-bulkbar';
  bar.className = 'ci-bulkbar';
  bar.hidden = true;
  bar.innerHTML = `<strong data-ci-selected></strong>
    <button type="button" data-ci-bulk-category>${esc(t('category'))}</button>
    <button type="button" class="ghost" data-ci-bulk-reviewed>${esc(t('reviewAll'))}</button>
    <button type="button" class="ghost" data-ci-bulk-unreviewed>${esc(t('unreview'))}</button>
    <button type="button" class="ghost" data-ci-bulk-tag-add>${esc(t('tagAdd'))}</button>
    <button type="button" class="ghost" data-ci-bulk-tag-remove>${esc(t('tagRemove'))}</button>
    <button type="button" class="ghost" data-ci-bulk-exclude>${esc(t('exclude'))}</button>
    <button type="button" class="ghost" data-ci-bulk-include>${esc(t('include'))}</button>
    <button type="button" class="ghost" data-ci-clear>${esc(t('clear'))}</button>`;
  toolbar.insertAdjacentElement('afterend', bar);
  bar.querySelector('[data-ci-bulk-category]').addEventListener('click', openBulkCategoryDialog);
  bar.querySelector('[data-ci-bulk-reviewed]').addEventListener('click', () => bulkAction({isReviewed:true}));
  bar.querySelector('[data-ci-bulk-unreviewed]').addEventListener('click', () => bulkAction({isReviewed:false}));
  bar.querySelector('[data-ci-bulk-tag-add]').addEventListener('click', () => openBulkTagDialog(true));
  bar.querySelector('[data-ci-bulk-tag-remove]').addEventListener('click', () => openBulkTagDialog(false));
  bar.querySelector('[data-ci-bulk-exclude]').addEventListener('click', () => bulkAction({isIgnored:true}));
  bar.querySelector('[data-ci-bulk-include]').addEventListener('click', () => bulkAction({isIgnored:false}));
  bar.querySelector('[data-ci-clear]').addEventListener('click', () => { selectedTransactions.clear(); syncBulkBar(); decorateTransactionRows(false); });
}

function syncBulkBar() {
  const bar = document.querySelector('#ci-bulkbar');
  if (!bar) return;
  bar.hidden = selectedTransactions.size === 0;
  const label = bar.querySelector('[data-ci-selected]');
  if (label) label.textContent = t('selected', selectedTransactions.size);
}

async function decorateTransactionRows(force=false) {
  if (decoratingTransactions) return;
  const body = document.querySelector('#transactions-body');
  if (!body || !body.children.length) return;
  decoratingTransactions = true;
  try {
    await loadReferenceData(force || !overview);
    ensureReviewFilters();
    const items = await currentVisibleTransactions();
    const rows = [...body.querySelectorAll('tr')].filter(row => row.children.length >= 5);
    const flags = document.querySelector('#tx-flags')?.value;
    const tagFilter = document.querySelector('#ci-tag-filter')?.value;
    let baseItems = items;
    if (flags === 'needs_review' || flags === 'reviewed' || tagFilter) {
      const originalFlag = flags;
      const flagSelect = document.querySelector('#tx-flags');
      const tagSelect = document.querySelector('#ci-tag-filter');
      if (flagSelect && (originalFlag === 'needs_review' || originalFlag === 'reviewed')) flagSelect.value = '';
      if (tagSelect) tagSelect.value = '';
      baseItems = await currentVisibleTransactions();
      if (flagSelect) flagSelect.value = originalFlag || '';
      if (tagSelect) tagSelect.value = tagFilter || '';
    }
    const visibleIds = new Set(items.map(x => x.id));

    rows.forEach((row, index) => {
      const tx = baseItems[index];
      if (!tx) { row.hidden = true; return; }
      row.dataset.txId = tx.id;
      const intel = overviewById.get(tx.id);
      const shouldShow = visibleIds.has(tx.id);
      row.hidden = !shouldShow;
      if (!shouldShow) return;
      row.classList.toggle('ci-needs-review', !!intel?.needsReview);
      row.classList.toggle('ci-selected', selectedTransactions.has(tx.id));

      const merchantCell = row.children[1];
      merchantCell.querySelectorAll('.ci-auto-added').forEach(el => el.remove());
      let checkbox = merchantCell.querySelector('.ci-select-tx');
      if (!checkbox) {
        checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.className = 'ci-select-tx';
        checkbox.setAttribute('aria-label', t('selected', 1));
        checkbox.addEventListener('click', event => event.stopPropagation());
        checkbox.addEventListener('change', event => {
          event.stopPropagation();
          if (checkbox.checked) selectedTransactions.add(tx.id); else selectedTransactions.delete(tx.id);
          row.classList.toggle('ci-selected', checkbox.checked);
          syncBulkBar();
        });
        merchantCell.prepend(checkbox);
      }
      checkbox.checked = selectedTransactions.has(tx.id);

      const categoryCell = row.children[2];
      categoryCell.querySelectorAll('.ci-auto-added').forEach(el => el.remove());
      const color = intel?.categoryColor || (tx.categoryId ? categoryColor(tx.categoryId) : '#94A3B8');
      const meta = document.createElement('div');
      meta.className = 'ci-category-meta ci-auto-added';
      meta.innerHTML = `<span class="ci-category-dot" style="--ci-category:${esc(color)}"></span>${confidenceHtml(intel)}${intel?.needsReview ? `<span class="ci-review-pill">${esc(t('needsReview'))}</span>` : ''}`;
      categoryCell.appendChild(meta);
      if (intel?.tags?.length) {
        const tagBox = document.createElement('div');
        tagBox.className = 'ci-tags ci-auto-added';
        tagBox.innerHTML = tagChips(intel.tags);
        merchantCell.appendChild(tagBox);
      }
    });
    syncBulkBar();
    updateReviewBadge();
  } catch (error) {
    console.error('Category Intelligence decoration failed', error);
  } finally {
    decoratingTransactions = false;
  }
}

function updateReviewBadge() {
  const txButton = document.querySelector('.sidebar button[data-view="transactions"] span');
  if (!txButton || !overview) return;
  txButton.parentElement.querySelector('.ci-nav-badge')?.remove();
  if ((overview.needsReview || 0) > 0) {
    const badge = document.createElement('span');
    badge.className = 'ci-nav-badge';
    badge.textContent = overview.needsReview > 99 ? '99+' : String(overview.needsReview);
    badge.title = t('reviewCount', overview.needsReview);
    txButton.parentElement.appendChild(badge);
  }
}

async function bulkAction(extra) {
  if (!selectedTransactions.size) return;
  try {
    await api('api/category-intelligence/bulk', json('POST', {transactionIds:[...selectedTransactions], ...extra}));
    selectedTransactions.clear();
    await refreshTransactions();
  } catch (error) { toast(error.message || t('error')); }
}

async function openBulkCategoryDialog() {
  try { await loadReferenceData(); } catch (error) { toast(error.message || t('error')); return; }
  const dlg = dialog(`<form class="dialog-card ci-card"><div class="panel-head"><h2>${esc(t('bulkCategory'))}</h2><button type="button" data-close>×</button></div>
    <label>${esc(t('category'))}<select name="category">${categoryOptions(null)}</select></label>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(t('cancel'))}</button><button type="submit">${esc(t('save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const categoryId = new FormData(event.currentTarget).get('category') || null;
    dlg.close();
    await bulkAction({updateCategory:true, categoryId, isReviewed:true});
  };
  dlg.showModal();
}

async function openBulkTagDialog(add) {
  try { await loadReferenceData(); } catch (error) { toast(error.message || t('error')); return; }
  if (!tags.length) { await openTagManager(); return; }
  const dlg = dialog(`<form class="dialog-card ci-card"><div class="panel-head"><h2>${esc(add ? t('tagAdd') : t('tagRemove'))}</h2><button type="button" data-close>×</button></div>
    <div class="ci-tag-list">${tags.map(tag => `<label class="ci-tag-choice"><input type="checkbox" value="${tag.id}"><span class="ci-category-dot" style="--ci-category:${esc(tag.color || hashColor(tag.name))}"></span>${esc(tag.name)}</label>`).join('')}</div>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(t('cancel'))}</button><button type="submit">${esc(t('save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const ids = [...dlg.querySelectorAll('input:checked')].map(x => x.value);
    dlg.close();
    await bulkAction(add ? {addTagIds:ids} : {removeTagIds:ids});
  };
  dlg.showModal();
}

async function refreshTransactions() {
  overview = null;
  await loadReferenceData(true);
  const apply = document.querySelector('#tx-apply');
  if (apply) apply.click(); else await decorateTransactionRows(true);
}

function rememberActiveTransaction(event) {
  const row = event.target.closest?.('#transactions-body tr[data-tx-id]');
  if (row?.dataset.txId) activeTransactionId = row.dataset.txId;
}
document.addEventListener('click', rememberActiveTransaction, true);
document.addEventListener('keydown', event => { if (event.key === 'Enter') rememberActiveTransaction(event); }, true);

async function enhanceDetail(form) {
  if (form.dataset.ciEnhanced === '1') return;
  form.dataset.ciEnhanced = '1';
  try { await loadReferenceData(true); } catch (error) { console.error(error); return; }
  const id = activeTransactionId;
  const intel = id ? overviewById.get(id) : null;
  if (!id || !intel) return;

  const note = form.querySelector('.tx-note');
  const box = document.createElement('section');
  box.className = 'ci-detail-panel';
  box.innerHTML = `<div class="ci-detail-head"><div><strong>${esc(t('confidence'))}</strong><div class="row-sub">${esc(reasonLabel(intel))}</div></div>${confidenceHtml(intel)}</div>
    <div class="ci-tags" data-ci-detail-tags>${tagChips(intel.tags)}</div>
    <div class="ci-detail-actions"><button type="button" class="ghost" data-ci-review>${esc(intel.isReviewed ? t('unreview') : t('reviewAll'))}</button><button type="button" class="ghost" data-ci-tags>${esc(t('manageTags'))}</button>${intel.learningSuggested && intel.categoryId ? `<button type="button" data-ci-learn>${esc(t('createRule'))}</button>` : ''}</div>
    ${intel.learningSuggested ? `<div class="ci-learning-hint">${esc(t('suggest'))}</div>` : ''}`;
  if (note) note.before(box); else form.querySelector('.dialog-actions')?.before(box);

  box.querySelector('[data-ci-review]').addEventListener('click', async () => {
    try {
      await api('api/category-intelligence/review', json('POST', {transactionIds:[id], isReviewed:!intel.isReviewed}));
      form.closest('dialog')?.close();
      await refreshTransactions();
    } catch (error) { toast(error.message || t('error')); }
  });
  box.querySelector('[data-ci-tags]').addEventListener('click', () => openTransactionTags(id));
  box.querySelector('[data-ci-learn]')?.addEventListener('click', async () => {
    try {
      const result = await api('api/category-intelligence/learn', json('POST', {transactionId:id, categoryId:intel.categoryId, scope:'future'}));
      toast(t('learned', result?.changed || 0));
      form.closest('dialog')?.close();
      await refreshTransactions();
    } catch (error) { toast(error.message || t('error')); }
  });

  const save = form.querySelector('[data-save]');
  const category = form.querySelector('select[name="category"]');
  if (save && category) {
    const originalCategoryId = intel.categoryId || '';
    save.addEventListener('click', async event => {
      const newCategoryId = category.value || '';
      if (newCategoryId === originalCategoryId || !newCategoryId) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      const scope = await chooseLearningScope();
      if (!scope) return;
      const transfer = form.querySelector('[name="transfer"]')?.checked || false;
      const payload = {
        categoryId:newCategoryId,
        isIgnored:form.querySelector('[name="ignored"]')?.checked || false,
        isTransfer:transfer,
        transferPurpose:transfer ? (form.querySelector('[name="purpose"]')?.value || null) : null,
        userNote:form.querySelector('[name="note"]')?.value.trim() || null
      };
      try {
        await api(`api/transactions/${id}/classification`, json('PATCH', payload));
        let result = {changed:1};
        if (scope === 'one') {
          await api('api/category-intelligence/review', json('POST', {transactionIds:[id], isReviewed:true}));
        } else {
          result = await api('api/category-intelligence/learn', json('POST', {transactionId:id, categoryId:newCategoryId, scope}));
        }
        toast(t('learned', result?.changed || 1));
        form.closest('dialog')?.close();
        await refreshTransactions();
      } catch (error) { toast(error.message || t('error')); }
    }, true);
  }
}

function chooseLearningScope() {
  return new Promise(resolve => {
    const dlg = dialog(`<div class="dialog-card ci-card"><div class="panel-head"><h2>${esc(t('learnTitle'))}</h2><button type="button" data-choice="">×</button></div>
      <p class="row-sub">${esc(t('learnHint'))}</p>
      <div class="ci-learning-options"><button type="button" data-choice="one"><strong>${esc(t('onlyThis'))}</strong></button><button type="button" data-choice="existing"><strong>${esc(t('allExisting'))}</strong></button><button type="button" data-choice="future" class="primary-action"><strong>${esc(t('always'))}</strong></button></div>
      <div class="dialog-actions"><button type="button" data-choice="">${esc(t('cancel'))}</button></div></div>`);
    let chosen = false;
    dlg.querySelectorAll('[data-choice]').forEach(button => button.addEventListener('click', () => {
      chosen = true;
      const value = button.dataset.choice || null;
      dlg.close();
      resolve(value);
    }));
    dlg.addEventListener('close', () => { if (!chosen) resolve(null); }, {once:true});
    dlg.showModal();
  });
}

async function openTransactionTags(transactionId) {
  try { await loadReferenceData(true); } catch (error) { toast(error.message || t('error')); return; }
  const current = new Set((overviewById.get(transactionId)?.tags || []).map(x => x.id));
  const dlg = dialog(`<form class="dialog-card ci-card"><div class="panel-head"><h2>${esc(t('tags'))}</h2><button type="button" data-close>×</button></div>
    <div class="ci-tag-list" data-tag-list>${tags.length ? tags.map(tag => `<label class="ci-tag-choice"><input type="checkbox" value="${tag.id}"${current.has(tag.id)?' checked':''}><span class="ci-category-dot" style="--ci-category:${esc(tag.color || hashColor(tag.name))}"></span>${esc(tag.name)}</label>`).join('') : `<p class="row-sub">${esc(t('noTags'))}</p>`}</div>
    <button type="button" class="ghost" data-manage-tags>${esc(t('manageTags'))}</button>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(t('cancel'))}</button><button type="submit">${esc(t('save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('[data-manage-tags]').addEventListener('click', async () => { dlg.close(); await openTagManager(transactionId); });
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const tagIds = [...dlg.querySelectorAll('[data-tag-list] input:checked')].map(x => x.value);
    try {
      await api(`api/category-intelligence/transactions/${transactionId}/tags`, json('PUT', {tagIds}));
      dlg.close();
      await refreshTransactions();
    } catch (error) { toast(error.message || t('error')); }
  };
  dlg.showModal();
}

async function openTagManager(returnTransactionId=null) {
  try { await loadReferenceData(true); } catch (error) { toast(error.message || t('error')); return; }
  const dlg = dialog(`<div class="dialog-card ci-card"><div class="panel-head"><h2>${esc(t('manageTags'))}</h2><button type="button" data-close>×</button></div>
    <div class="ci-tag-list" data-existing-tags>${tags.length ? tags.map(tag => `<div class="ci-tag-choice"><span class="ci-category-dot" style="--ci-category:${esc(tag.color || hashColor(tag.name))}"></span><span class="ci-tag-grow">${esc(tag.name)}</span><button type="button" class="ghost" data-edit-tag="${tag.id}">${esc(t('edit'))}</button><button type="button" class="ghost" data-delete-tag="${tag.id}">${esc(t('delete'))}</button></div>`).join('') : `<p class="row-sub">${esc(t('noTags'))}</p>`}</div>
    <form data-create-tag><h3>${esc(t('newTag'))}</h3><label>${esc(t('tagName'))}<input name="name" maxlength="80" required></label><label>${esc(t('color'))}<input name="color" type="color" value="#2563EB"></label><div class="dialog-actions"><button type="submit">${esc(t('create'))}</button></div></form>
    <div class="dialog-actions"><button type="button" data-done>${esc(t('save'))}</button></div></div>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-done]').onclick = () => { dlg.close(); if (returnTransactionId) openTransactionTags(returnTransactionId); };
  dlg.querySelectorAll('[data-edit-tag]').forEach(button => button.addEventListener('click', () => {
    const tag = tags.find(x => x.id === button.dataset.editTag);
    dlg.close();
    if (tag) openTagEdit(tag, returnTransactionId);
  }));
  dlg.querySelectorAll('[data-delete-tag]').forEach(button => button.addEventListener('click', async () => {
    try {
      await api(`api/category-intelligence/tags/${button.dataset.deleteTag}`, {method:'DELETE'});
      dlg.close();
      await loadReferenceData(true);
      await openTagManager(returnTransactionId);
    } catch (error) { toast(error.message || t('error')); }
  }));
  dlg.querySelector('[data-create-tag]').onsubmit = async event => {
    event.preventDefault();
    const fd = new FormData(event.currentTarget);
    try {
      await api('api/category-intelligence/tags', json('POST', {name:fd.get('name'), color:fd.get('color')}));
      dlg.close();
      await loadReferenceData(true);
      ensureReviewFilters();
      await openTagManager(returnTransactionId);
    } catch (error) { toast(error.message || t('error')); }
  };
  dlg.showModal();
}

async function openTagEdit(tag, returnTransactionId=null) {
  const dlg = dialog(`<form class="dialog-card ci-card"><div class="panel-head"><h2>${esc(t('edit'))}: ${esc(tag.name)}</h2><button type="button" data-close>×</button></div>
    <label>${esc(t('tagName'))}<input name="name" maxlength="80" required value="${esc(tag.name)}"></label>
    <label>${esc(t('color'))}<input name="color" type="color" value="${esc(tag.color || hashColor(tag.name))}"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(t('cancel'))}</button><button type="submit">${esc(t('save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick = dlg.querySelector('[data-cancel]').onclick = () => { dlg.close(); openTagManager(returnTransactionId); };
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const fd = new FormData(event.currentTarget);
    try {
      await api(`api/category-intelligence/tags/${tag.id}`, json('PUT', {name:fd.get('name'), color:fd.get('color')}));
      dlg.close();
      await loadReferenceData(true);
      ensureReviewFilters();
      await openTagManager(returnTransactionId);
    } catch (error) { toast(error.message || t('error')); }
  };
  dlg.showModal();
}

async function decorateCategoryTree() {
  const tree = document.querySelector('#categories-tree');
  if (!tree || !tree.children.length || categoryRefreshPending) return;
  categoryRefreshPending = true;
  try {
    await loadReferenceData(true);
    tree.querySelectorAll('.cat-node[data-category-id]').forEach(node => {
      const id = node.dataset.categoryId;
      const name = node.querySelector('.cat-name');
      if (!name) return;
      name.querySelector('.ci-category-dot')?.remove();
      const dot = document.createElement('span');
      dot.className = 'ci-category-dot';
      dot.style.setProperty('--ci-category', categoryColor(id));
      name.prepend(dot);
    });
  } catch (error) { console.error(error); }
  finally { categoryRefreshPending = false; }
}

document.addEventListener('click', event => {
  const edit = event.target.closest?.('#categories-tree [data-edit]');
  if (edit) activeCategoryId = edit.closest('.cat-node')?.dataset.categoryId || null;
}, true);

function enhanceCategoryDialog(form) {
  if (form.dataset.ciCategoryEnhanced === '1') return;
  const iconInput = form.querySelector('input[name="icon"]');
  const parent = form.querySelector('select[name="parent"]');
  if (!iconInput || !parent) return;
  form.dataset.ciCategoryEnhanced = '1';
  const currentId = activeCategoryId;
  const currentColor = currentId ? (appearances.get(currentId) || categoryColor(currentId)) : '#2563EB';
  const wrapper = document.createElement('div');
  wrapper.className = 'ci-category-customize';
  wrapper.innerHTML = `<label>${esc(t('color'))}<input type="color" data-ci-category-color value="${esc(currentColor)}"></label><div><span class="ci-field-label">${esc(t('chooseIcon'))}</span><div class="ci-icon-picker">${ICONS.map(icon => `<button type="button" data-ci-icon="${esc(icon)}">${esc(icon)}</button>`).join('')}</div></div>`;
  parent.closest('label')?.before(wrapper);
  wrapper.querySelectorAll('[data-ci-icon]').forEach(button => button.addEventListener('click', () => { iconInput.value = button.dataset.ciIcon; }));
  const colorInput = wrapper.querySelector('[data-ci-category-color]');
  let colorTouched = !!(currentId && appearances.has(currentId));
  colorInput.addEventListener('change', () => { colorTouched = true; });

  form.addEventListener('submit', () => {
    if (!colorTouched) return;
    const color = colorInput.value;
    if (currentId) {
      api(`api/category-intelligence/category-appearances/${currentId}`, json('PUT', {color}))
        .then(() => { appearances.set(currentId, color); setTimeout(decorateCategoryTree, 150); })
        .catch(error => toast(error.message || t('error')));
      activeCategoryId = null;
      return;
    }
    const name = form.querySelector('input[name="name"]')?.value.trim();
    const parentId = parent.value || null;
    setTimeout(() => attachAppearanceToCreatedCategory(name, parentId, color), 400);
  }, true);
}

async function attachAppearanceToCreatedCategory(name, parentId, color) {
  for (let attempt=0; attempt<5; attempt++) {
    try {
      categories = await api('api/categories');
      categoryById = new Map(categories.map(x => [x.id,x]));
      const candidates = categories.filter(x => x.name === name && (x.parentId || null) === parentId);
      const created = candidates[candidates.length - 1];
      if (created) {
        await api(`api/category-intelligence/category-appearances/${created.id}`, json('PUT', {color}));
        appearances.set(created.id, color);
        setTimeout(decorateCategoryTree, 100);
        return;
      }
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 250));
  }
}

function addedElementMatches(mutation, selector) {
  return [...mutation.addedNodes].some(node => node instanceof Element && (node.matches(selector) || node.querySelector(selector)));
}

function observeUi() {
  const observer = new MutationObserver(mutations => {
    let txChanged = false;
    let categoryChanged = false;
    for (const mutation of mutations) {
      // Only react to BASE view row insertion. Chips/dots added by this module live below the rows and
      // must not recursively trigger another decoration pass.
      if (mutation.target.id === 'transactions-body' && addedElementMatches(mutation, 'tr')) txChanged = true;
      if (mutation.target.id === 'categories-tree' && addedElementMatches(mutation, '.cat-node')) categoryChanged = true;
      for (const node of mutation.addedNodes) {
        if (!(node instanceof Element)) continue;
        const detail = node.matches('.tx-detail') ? node : node.querySelector('.tx-detail');
        if (detail) queueMicrotask(() => enhanceDetail(detail));
        const nestedForms = node.querySelectorAll ? [...node.querySelectorAll('form')] : [];
        const forms = [node.matches('form') ? node : null, ...nestedForms].filter(Boolean);
        forms.forEach(form => {
          if (form.querySelector('input[name="icon"]') && form.querySelector('select[name="parent"]'))
            queueMicrotask(() => enhanceCategoryDialog(form));
        });
      }
    }
    if (txChanged) setTimeout(() => decorateTransactionRows(false), 0);
    if (categoryChanged) setTimeout(decorateCategoryTree, 0);
  });
  observer.observe(document.body, {subtree:true, childList:true});
}

async function init() {
  ensureCss();
  observeUi();
  try {
    await loadReferenceData(true);
    ensureReviewFilters();
    await decorateTransactionRows(false);
    await decorateCategoryTree();
  } catch (error) {
    // App boot may select the FullWorth Space after this module loads. Base-view mutations retry safely.
    console.debug('Category Intelligence will retry after app initialization.', error);
  }
}

init();
