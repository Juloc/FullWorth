// Installed before app.js. Answers the BFF from canned data so the real frontend renders without a
// login. Unknown endpoints return an empty array/object, which every view tolerates - that still
// exercises the real layout, which is what the mobile audit needs.
(() => {
  const SPACE = '11111111-1111-1111-1111-111111111111';
  const iso = d => d;

  const FIXTURES = {
    'fullworth-spaces': [{ id: SPACE, name: 'Haushalt', baseCurrency: 'EUR', role: 'owner', isDefault: true }],
    'categories': [
      { id: 'c1', name: 'Lebensmittel', kind: 'expense', parentId: null, isArchived: false, iconKey: 'groceries' },
      { id: 'c2', name: 'Restaurant', kind: 'expense', parentId: null, isArchived: false, iconKey: 'restaurants' },
      { id: 'c3', name: 'Gehalt', kind: 'income', parentId: null, isArchived: false, iconKey: 'salary' },
      { id: 'c4', name: 'Supermarkt mit sehr langem Namen zum Umbruchtest', kind: 'expense', parentId: 'c1', isArchived: false, iconKey: 'groceries' }
    ],
    'accounts': [
      { id: 'a1', name: 'Girokonto', iban: 'DE02120300000000202051', currency: 'EUR', balance: 2431.55, type: 'checking', groupId: null, ownerUserIds: [] },
      { id: 'a2', name: 'Tagesgeld mit langem Namen', iban: 'DE02500105170137075030', currency: 'EUR', balance: 18250.10, type: 'savings', groupId: null, ownerUserIds: [] },
      // A wallet-per-currency account in the shape the real API returns: one headline balance plus every
      // currency it holds, and a base-currency value covering ALL of them (100 EUR + 55 USD + 2m IDR).
      {
        id: 'a3', displayName: 'PayPal', institutionName: 'PayPal', provider: 'test', accountType: 'wallet',
        currency: 'EUR', isActive: true, includeInNetWorth: true, groupId: 'g1', sortOrder: 3,
        latestBalance: { amount: 100, currency: 'EUR', balanceType: 'closingBooked', capturedAt: iso('2026-09-09') },
        balances: [
          { amount: 100, currency: 'EUR', balanceType: 'closingBooked', capturedAt: iso('2026-09-09') },
          { amount: 2000000, currency: 'IDR', balanceType: 'closingBooked', capturedAt: iso('2026-09-09') },
          { amount: 55, currency: 'USD', balanceType: 'closingBooked', capturedAt: iso('2026-09-09') }
        ],
        baseValue: 250, baseCurrency: 'EUR'
      },
      // A foreign account with NO convertible rate: it must not be counted as zero inside a confident
      // base-currency subtotal, so the subtotal is marked instead.
      {
        id: 'a4', displayName: 'IDR Wallet', institutionName: 'Bank Mandiri', provider: 'test',
        accountType: 'checking', currency: 'IDR', isActive: true, includeInNetWorth: true,
        groupId: 'g1', sortOrder: 4,
        latestBalance: { amount: 5000000, currency: 'IDR', balanceType: 'closingBooked', capturedAt: iso('2026-09-09') },
        balances: [{ amount: 5000000, currency: 'IDR', balanceType: 'closingBooked', capturedAt: iso('2026-09-09') }],
        baseValue: null, baseCurrency: null
      }
    ],
    'account-groups': [{ id: 'g1', name: 'Alltag', sortOrder: 1 }],
    // Two connections: one healthy, one FinTS parked on a TAN. The second must offer "TAN eingeben",
    // never "Neu verbinden" - reconnecting discards the challenge the bank is waiting for.
    'bank-connections': [
      {
        id: 'c1', provider: 'enable-banking', institutionName: 'Testbank', country: 'DE',
        status: 'AUTHORIZED', healthStatus: 'authorized', validUntil: iso('2026-12-31'),
        lastSyncedAt: iso('2026-09-09'), daysUntilExpiry: 112, nextSyncAllowedAt: null
      },
      {
        id: 'c2', provider: 'fints', institutionName: 'ING', country: 'DE',
        status: 'TAN_REQUIRED', healthStatus: 'tan_required', validUntil: iso('2026-12-31'),
        lastSyncedAt: iso('2026-09-08'), daysUntilExpiry: 112, nextSyncAllowedAt: null,
        lastError: 'FINTS_TAN_REQUIRED'
      }
    ],
    'transactions': {
      // Two pending entries and a booking on the same day: the API sorts pending ahead of everything
      // booked, so the "Heute" header below them still marks today. The undated one is the shape a card
      // authorisation arrives in - a status and a value date, no booking date yet.
      items: [
        { id: 'tp1', bookingDate: iso('2026-09-10'), valueDate: iso('2026-09-10'), amount: -12.40, currency: 'EUR', counterparty: 'Backwerk', description: 'Kartenzahlung', status: 'PDNG', accountId: 'a1', isSplit: false },
        { id: 'tp2', bookingDate: null, valueDate: iso('2026-09-09'), amount: -64.00, currency: 'EUR', counterparty: 'Tankstelle Nord', description: 'Kartenzahlung', status: 'PDNG', accountId: 'a1', isSplit: false },
        { id: 't0', bookingDate: iso('2026-09-10'), amount: -9.99, currency: 'EUR', counterparty: 'Spotify', description: 'Abo', categoryId: 'c1', accountId: 'a1', status: 'BOOK', isSplit: false },
        { id: 't1', bookingDate: iso('2026-09-08'), amount: -42.19, currency: 'EUR', counterparty: 'REWE Markt GmbH', description: 'Einkauf', categoryId: 'c1', accountId: 'a1', isSplit: false },
        { id: 't2', bookingDate: iso('2026-09-07'), amount: -18.90, currency: 'EUR', counterparty: 'Trattoria da Enzo mit sehr langem Namen', description: 'Abendessen', categoryId: 'c2', accountId: 'a1', isSplit: false },
        { id: 't3', bookingDate: iso('2026-08-28'), amount: 2810.44, currency: 'EUR', counterparty: 'Arbeitgeber AG', description: 'Gehalt August', categoryId: 'c3', accountId: 'a1', isSplit: false }
      ],
      total: 6, page: 1, pageSize: 50
    },
    'contracts': [
      { id: 'k1', name: 'Stromvertrag', providerName: 'Stadtwerke', monthlyAmount: 78.5, currency: 'EUR', status: 'active', nextDueDate: iso('2026-10-01'), categoryId: 'c1', accountId: 'a1' },
      { id: 'k2', name: 'Mobilfunk', providerName: 'Telekom', monthlyAmount: 29.99, currency: 'EUR', status: 'active', nextDueDate: iso('2026-09-20'), categoryId: 'c1', accountId: 'a1' }
    ],
    'budgets': [],
    'notifications': [],
    // One EUR house and one huge IDR asset: the old ratio-of-native-values split put nearly all of
    // manualAssets into 'other', because 5.000.000 IDR dwarfs 9.000 EUR as a bare number.
    'assets': [
      { id: 'as1', name: 'Wohnung', kind: 'real_estate', currentValue: 9000, currency: 'EUR', includeInNetWorth: true },
      { id: 'as2', name: 'IDR Anlage', kind: 'other', currentValue: 5000000, currency: 'IDR', includeInNetWorth: true }
    ],
    'networth': { total: 20681.65, series: [], groups: [] },
    // Wealth view. The history rises by a flat 600 per month over exactly 12 months, so the
    // projection card's derived savings rate must come out at 600 - a value that is wrong by any
    // rounding mistake is visible immediately.
    'wealth/overview': {
      netWorth: 48250.30, totalAssets: 32000, totalLiabilities: 5000,
      accounts: { amount: 21250.30 }, currency: 'EUR', isComplete: true, missingCurrencies: [],
      // Real estate is its own converted slice of manualAssets, so the allocation donut never has to
      // guess it from native asset values.
      manualAssets: { amount: 12000 }, investments: { amount: 20000 },
      realEstateAssets: { amount: 9000 }
    },
    'wealth/history': [
      { date: '2025-09-09', netWorth: 41050.30 }, { date: '2025-10-09', netWorth: 41650.30 },
      { date: '2025-11-09', netWorth: 42250.30 }, { date: '2025-12-09', netWorth: 42850.30 },
      { date: '2026-01-09', netWorth: 43450.30 }, { date: '2026-02-09', netWorth: 44050.30 },
      { date: '2026-03-09', netWorth: 44650.30 }, { date: '2026-04-09', netWorth: 45250.30 },
      { date: '2026-05-09', netWorth: 45850.30 }, { date: '2026-06-09', netWorth: 46450.30 },
      { date: '2026-07-09', netWorth: 47050.30 }, { date: '2026-08-09', netWorth: 47650.30 },
      { date: '2026-09-09', netWorth: 48250.30 }
    ],
    'wealth/booking-activity': [],
    'preferences/wealth.projection': { value: {} },
    'preferences/wealth.emergencyFund': { value: {} },
    'liabilities': [],
    'investments/portfolios': [],
    'insights': []
  };

  const KEYS = Object.keys(FIXTURES);

  function match(pathname) {
    // "/bff/backend/api/transactions?x=1" -> "transactions"
    const after = pathname.replace(/^\/bff\/(backend|banking)\//, '').replace(/^api\//, '');
    const head = after.split('/')[0].split('?')[0];
    if (Object.prototype.hasOwnProperty.call(FIXTURES, head)) return FIXTURES[head];
    const hit = KEYS.find(key => after.startsWith(key));
    return hit ? FIXTURES[hit] : undefined;
  }

  const realFetch = window.fetch.bind(window);

  window.fetch = async (input, init) => {
    const raw = typeof input === 'string' ? input : (input?.url ?? String(input));
    const url = new URL(raw, location.origin);
    const isBff = url.pathname.startsWith('/bff/') || url.pathname.startsWith('/api/');
    if (!isBff) return realFetch(input, init);

    const method = (init?.method || (typeof input !== 'string' && input?.method) || 'GET').toUpperCase();
    // Writes succeed with an echo so confirm/save paths can be walked without a backend.
    const body = method === 'GET' ? (match(url.pathname) ?? []) : { id: 'stub', ok: true };
    window.__harnessCalls = window.__harnessCalls || [];
    window.__harnessCalls.push(`${method} ${url.pathname}`);
    return new Response(JSON.stringify(body), { status: 200, headers: { 'content-type': 'application/json' } });
  };

  // The app remembers the active space locally; pre-set it so no picker blocks the first render.
  try { localStorage.setItem('fullworth.spaceId', SPACE); } catch { /* ignore */ }
  window.__harnessReady = true;
})();
