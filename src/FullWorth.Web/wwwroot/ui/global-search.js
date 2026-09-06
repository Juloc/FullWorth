// Global search UI. Search aggregation stays presentation-side; all data access uses ctx.api.

export function openGlobalSearch(ctx) {
  const dlg = ctx.dialog(`<form method="dialog" class="dialog-card search-dialog">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('search.title'))}</h2></div>
    <input id="search-input" type="search" autocomplete="off"
      data-i18n-placeholder="search.placeholder"
      placeholder="${ctx.esc(ctx.get('search.placeholder'))}">
    <div id="search-results" class="rows"></div>
  </form>`);

  const input = dlg.querySelector('#search-input');
  const results = dlg.querySelector('#search-results');
  let timer = null;

  input.addEventListener('input', () => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => runSearch(ctx, input.value.trim(), results, dlg), 220);
  });

  dlg.showModal();
  input.focus();
}

async function runSearch(ctx, query, results, dlg) {
  if (query.length < 2) {
    results.innerHTML = `<div class="row state-empty"><div class="row-sub">${ctx.esc(ctx.get('search.hint'))}</div></div>`;
    return;
  }

  ctx.skeleton(results, 3);

  try {
    const [transactions, accounts, categories, contracts, purchases, assets] = await Promise.all([
      ctx.api(`api/transactions?limit=8&query=${encodeURIComponent(query)}`).catch(() => ({ items: [] })),
      ctx.api('api/accounts').catch(() => []),
      ctx.api('api/categories').catch(() => []),
      ctx.api('api/contracts').catch(() => []),
      ctx.api('api/purchases').catch(() => []),
      ctx.api('api/assets').catch(() => [])
    ]);

    const normalized = query.toLowerCase();
    const groups = [
      [
        ctx.get('search.transactions'),
        (transactions.items || []).map(item => ({
          title: item.counterparty || '—',
          sub: `${ctx.date(item.bookingDate)} · ${ctx.money(item.amount, item.currency)}`,
          go: 'transactions'
        }))
      ],
      [
        ctx.get('search.accounts'),
        (accounts || [])
          .filter(item => (item.displayName || item.institutionName || '').toLowerCase().includes(normalized))
          .map(item => ({ title: item.displayName || item.institutionName, sub: item.institutionName, go: 'accounts' }))
      ],
      [
        ctx.get('nav.categories'),
        (categories || [])
          .filter(item => (item.name || '').toLowerCase().includes(normalized))
          .slice(0, 8)
          .map(item => ({ title: item.name, sub: '', go: 'categories' }))
      ],
      [
        ctx.get('nav.contracts'),
        (contracts || [])
          .filter(item => (item.name || '').toLowerCase().includes(normalized))
          .slice(0, 8)
          .map(item => ({ title: item.name, sub: ctx.money(item.amount, item.currency), go: 'contracts' }))
      ],
      [
        ctx.get('nav.purchases'),
        (purchases || [])
          .filter(item => (item.merchant || item.externalOrderId || '').toLowerCase().includes(normalized))
          .slice(0, 8)
          .map(item => ({
            title: item.merchant || item.externalOrderId || '—',
            sub: `${ctx.date(item.purchaseDate)} · ${ctx.money(item.totalAmount, item.currency)}`,
            go: 'purchases'
          }))
      ],
      [
        ctx.get('portfolio.assets'),
        (assets || [])
          .filter(item => (item.name || '').toLowerCase().includes(normalized))
          .slice(0, 8)
          .map(item => ({ title: item.name, sub: ctx.money(item.currentValue, item.currency), go: 'networth' }))
      ]
    ].filter(([, items]) => items.length);

    if (!groups.length) {
      ctx.empty(results, ctx.get('search.none'));
      return;
    }

    results.innerHTML = groups.map(([label, items]) =>
      `<div class="search-group">${ctx.esc(label)}</div>` +
      items.map(item =>
        `<button type="button" class="row search-hit" data-go="${item.go}">
          <div class="row-main">
            <div class="row-title">${ctx.esc(item.title)}</div>
            ${item.sub ? `<div class="row-sub">${ctx.esc(item.sub)}</div>` : ''}
          </div>
        </button>`).join('')
    ).join('');

    results.querySelectorAll('[data-go]').forEach(button => {
      button.addEventListener('click', () => {
        dlg.close();
        ctx.showView(button.dataset.go);
      });
    });
  } catch (error) {
    ctx.empty(results, error.message || ctx.get('common.error'));
  }
}
