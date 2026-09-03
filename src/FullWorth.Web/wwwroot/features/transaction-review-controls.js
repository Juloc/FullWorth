// Adds the Coach spending-review control to the existing transaction drawer without coupling the
// transactions feature to Coach internals. All requests still use the authenticated BFF.
const $ = selector => document.querySelector(selector);
const langDe = () => (document.documentElement.lang || navigator.language || 'de').toLowerCase().startsWith('de');
const spaceId = () => localStorage.getItem('finance.space');
let lastTransactionId = null;
let decorating = false;

async function request(path, options = {}) {
  const space = spaceId();
  if (!space) throw new Error('fullworth_space_missing');
  const [base, query = ''] = path.replace(/^\//, '').split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', space);
  const response = await fetch(`/bff/backend/${base}?${params}`, options);
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(String(response.status));
  return response.status === 204 ? null : response.json();
}

function transactionQuery() {
  const q = new URLSearchParams({ limit: '500' });
  const text = $('#tx-query')?.value?.trim();
  const direction = $('#tx-direction')?.value;
  const flags = $('#tx-flags')?.value;
  if (text) q.set('query', text);
  if (direction) q.set('direction', direction);
  if (flags === 'transfers') q.set('transfersOnly', 'true');
  if (flags === 'ignored') q.set('includeIgnored', 'true');
  return { q, flags };
}

async function decorateRows() {
  const rows = [...document.querySelectorAll('#transactions-body .tx-row')];
  if (!rows.length || decorating) return;
  decorating = true;
  try {
    const { q, flags } = transactionQuery();
    const data = await request(`api/transactions?${q}`);
    let items = data?.items || [];
    if (flags === 'pending') items = items.filter(x => x.status === 'PDNG');
    if (flags === 'ignored') items = items.filter(x => x.isIgnored);
    rows.forEach((row, index) => {
      const item = items[index];
      if (item?.id) row.dataset.coachTransactionId = item.id;
    });
  } catch { /* optional enhancement */ }
  finally { decorating = false; }
}

function rememberTransaction(event) {
  const row = event.target instanceof Element ? event.target.closest('.tx-row[data-coach-transaction-id]') : null;
  if (!row) return;
  if (event.type === 'keydown' && event.key !== 'Enter') return;
  lastTransactionId = row.dataset.coachTransactionId || null;
  queueMicrotask(attachToOpenDrawer);
}

async function attachToOpenDrawer() {
  const form = document.querySelector('dialog[open] .tx-detail');
  if (!form || form.dataset.spendingReviewReady || !lastTransactionId) return;
  form.dataset.spendingReviewReady = 'loading';
  try {
    const detail = await request(`api/transactions/${lastTransactionId}`);
    const transaction = detail?.transaction;
    if (!transaction || Number(transaction.amount) >= 0 || transaction.isTransfer || transaction.isIgnored) {
      form.dataset.spendingReviewReady = 'skipped';
      return;
    }

    let review = await request(`api/spending-reviews/transactions/${lastTransactionId}`);
    const section = document.createElement('section');
    section.className = 'coach-inline-review';
    const heading = langDe() ? 'War diese Ausgabe es wert?' : 'Was this spending worth it?';
    const helper = langDe()
      ? 'Schnellbewertung für Worth-it-Analyse und Coach. Gründe/Notiz kannst du im Coach ergänzen.'
      : 'Quick review for Worth-it analytics and Coach. Add reasons/notes in Coach.';
    section.innerHTML = `<div class="row-title">${heading}</div><div class="row-sub">${helper}</div><div class="coach-review-actions" data-actions>
      <button type="button" class="ghost" data-sentiment="Positive">${langDe() ? 'Gut' : 'Good'}</button>
      <button type="button" class="ghost" data-sentiment="Neutral">${langDe() ? 'Neutral' : 'Neutral'}</button>
      <button type="button" class="ghost" data-sentiment="Negative">${langDe() ? 'Schlecht' : 'Bad'}</button>
      <button type="button" class="ghost danger" data-clear hidden>${langDe() ? 'Löschen' : 'Clear'}</button>
    </div>`;

    const refresh = () => {
      section.querySelectorAll('[data-sentiment]').forEach(button => {
        const active = review?.sentiment === button.dataset.sentiment;
        button.classList.toggle('active', active);
        button.setAttribute('aria-pressed', String(active));
      });
      section.querySelector('[data-clear]').hidden = !review;
    };

    section.querySelectorAll('[data-sentiment]').forEach(button => button.addEventListener('click', async () => {
      const sentiment = button.dataset.sentiment;
      button.disabled = true;
      try {
        review = await request(`api/spending-reviews/transactions/${lastTransactionId}`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ sentiment, reasons: [], note: review?.note ?? null })
        });
        refresh();
      } catch { /* drawer remains usable when review save fails */ }
      finally { button.disabled = false; }
    }));
    section.querySelector('[data-clear]').addEventListener('click', async event => {
      event.currentTarget.disabled = true;
      try {
        await request(`api/spending-reviews/transactions/${lastTransactionId}`, { method: 'DELETE' });
        review = null;
        refresh();
      } catch { /* optional enhancement */ }
      finally { event.currentTarget.disabled = false; }
    });

    refresh();
    const note = form.querySelector('.tx-note');
    if (note) form.insertBefore(section, note); else form.querySelector('.dialog-actions')?.before(section);
    form.dataset.spendingReviewReady = '1';
  } catch {
    form.dataset.spendingReviewReady = 'error';
  }
}

function observe() {
  document.addEventListener('click', rememberTransaction, true);
  document.addEventListener('keydown', rememberTransaction, true);
  const observer = new MutationObserver(() => {
    decorateRows();
    attachToOpenDrawer();
  });
  observer.observe(document.body, { childList: true, subtree: true });
  decorateRows();
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', observe, { once: true });
else observe();
