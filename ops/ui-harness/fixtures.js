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
      // The two everyday accounts carry the two balance types a reader has to be able to tell apart:
      // a1 is the bank's AVAILABLE figure (pending authorisations already deducted) with the bank's own
      // as-of date, a2 is a BOOKED figure with no as-of date at all - so the row must say "Abgerufen",
      // not "Datenstand". `meaning` is what the server derives from balanceType.
      {
        id: 'a1', name: 'Girokonto', displayName: 'Girokonto', institutionName: 'Sparkasse',
        iban: 'DE02120300000000202051', ibanLast4: '2051', provider: 'test', accountType: 'checking',
        currency: 'EUR', isActive: true, includeInNetWorth: true, groupId: null, sortOrder: 1,
        ownerUserIds: [],
        latestBalance: {
          amount: 2431.55, currency: 'EUR', balanceType: 'interimAvailable', meaning: 'available',
          referenceDate: '2026-09-09', capturedAt: iso('2026-09-09T06:12:00Z'), source: 'provider'
        },
        balances: [{
          amount: 2431.55, currency: 'EUR', balanceType: 'interimAvailable', meaning: 'available',
          referenceDate: '2026-09-09', capturedAt: iso('2026-09-09T06:12:00Z'), source: 'provider'
        }]
      },
      {
        id: 'a2', name: 'Tagesgeld mit langem Namen', displayName: 'Tagesgeld mit langem Namen',
        institutionName: 'Sparkasse', iban: 'DE02500105170137075030', ibanLast4: '5030',
        provider: 'test', accountType: 'savings', currency: 'EUR', isActive: true,
        includeInNetWorth: true, groupId: null, sortOrder: 2, ownerUserIds: [],
        latestBalance: {
          amount: 18250.10, currency: 'EUR', balanceType: 'closingBooked', meaning: 'booked',
          capturedAt: iso('2026-09-09T06:12:00Z'), source: 'provider'
        },
        balances: [{
          amount: 18250.10, currency: 'EUR', balanceType: 'closingBooked', meaning: 'booked',
          capturedAt: iso('2026-09-09T06:12:00Z'), source: 'provider'
        }]
      },
      // A wallet-per-currency account in the shape the real API returns: one headline balance plus every
      // currency it holds, and a base-currency value covering ALL of them (100 EUR + 55 USD + 2m IDR).
      {
        id: 'a3', displayName: 'PayPal', institutionName: 'PayPal', provider: 'test', accountType: 'wallet',
        currency: 'EUR', isActive: true, includeInNetWorth: true, groupId: 'g1', sortOrder: 3,
        latestBalance: { amount: 100, currency: 'EUR', balanceType: 'closingBooked', meaning: 'booked', capturedAt: iso('2026-09-09') },
        balances: [
          { amount: 100, currency: 'EUR', balanceType: 'closingBooked', meaning: 'booked', capturedAt: iso('2026-09-09') },
          { amount: 2000000, currency: 'IDR', balanceType: 'closingBooked', meaning: 'booked', capturedAt: iso('2026-09-09') },
          { amount: 55, currency: 'USD', balanceType: 'closingBooked', meaning: 'booked', capturedAt: iso('2026-09-09') }
        ],
        baseValue: 250, baseCurrency: 'EUR'
      },
      // A foreign account with NO convertible rate: it must not be counted as zero inside a confident
      // base-currency subtotal, so the subtotal is marked instead.
      {
        id: 'a4', displayName: 'IDR Wallet', institutionName: 'Bank Mandiri', provider: 'test',
        accountType: 'checking', currency: 'IDR', isActive: true, includeInNetWorth: true,
        groupId: 'g1', sortOrder: 4,
        latestBalance: { amount: 5000000, currency: 'IDR', balanceType: 'closingBooked', meaning: 'booked', capturedAt: iso('2026-09-09') },
        balances: [{ amount: 5000000, currency: 'IDR', balanceType: 'closingBooked', meaning: 'booked', capturedAt: iso('2026-09-09') }],
        baseValue: null, baseCurrency: null
      },
      // A cash account anchored by hand: no provider type behind the figure, so it gets NO booked or
      // available claim - the row says "manuell erfasst" and the owner's own as-of date instead.
      {
        id: 'a6', displayName: 'Bargeld', institutionName: 'Haushalt', provider: 'manual',
        accountType: 'cash', currency: 'EUR', isActive: true, includeInNetWorth: true,
        groupId: null, sortOrder: 6,
        latestBalance: {
          amount: 240, currency: 'EUR', balanceType: 'manual', meaning: 'recorded',
          referenceDate: '2026-08-31', capturedAt: iso('2026-09-01T09:00:00Z'),
          source: 'manual', note: 'Bargeld gezählt'
        },
        balances: [{
          amount: 240, currency: 'EUR', balanceType: 'manual', meaning: 'recorded',
          referenceDate: '2026-08-31', capturedAt: iso('2026-09-01T09:00:00Z'),
          source: 'manual', note: 'Bargeld gezählt'
        }]
      },
      // A Finanzguru history import with no balance yet: the export file carries only bookings, so the
      // account arrives archived and out of net worth until someone gives it a balance. It must offer
      // exactly that - the server has accepted a manual balance for this provider since P0-4, but the
      // list only showed the button for provider === 'manual'.
      {
        id: 'a5', displayName: 'Bargeld (Import)', institutionName: 'Finanzguru Import',
        provider: 'finanzguru-import', product: 'Imported history', accountType: 'checking',
        currency: 'EUR', isActive: false, includeInNetWorth: false, groupId: null, sortOrder: 5,
        latestBalance: null, balances: [], baseValue: null, baseCurrency: null
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
    // The real /api/contracts shape (amount + billingCycle + server-computed monthlyEquivalent /
    // annualizedAmount), not a hand-made "monthlyAmount". The last three rows are the reported merge
    // case: ONE utility contract that changed its paying account twice, so three rows exist - and the
    // oldest of them carries no currency at all, which must still be mergeable with the EUR ones.
    'contracts': [
      {
        id: 'k1', name: 'Stromvertrag', providerName: 'Stadtwerke', kind: 'contract', amount: 78.5,
        currency: 'EUR', billingCycle: 'monthly', interval: 1, monthlyEquivalent: 78.5, annualizedAmount: 942,
        nextDueDate: iso('2026-10-01'), categoryId: 'c1', accountId: 'a1', autoDetected: false, isActive: true,
        createdAt: '2025-01-04T09:00:00Z', updatedAt: '2026-09-01T09:00:00Z'
      },
      {
        id: 'k2', name: 'Mobilfunk', providerName: 'Telekom', kind: 'subscription', amount: 29.99,
        currency: 'EUR', billingCycle: 'monthly', interval: 1, monthlyEquivalent: 29.99, annualizedAmount: 359.88,
        nextDueDate: iso('2026-09-20'), categoryId: 'c1', accountId: 'a1', autoDetected: false, isActive: true,
        createdAt: '2025-02-04T09:00:00Z', updatedAt: '2026-09-01T09:00:00Z'
      },
      {
        id: 'k3', name: 'WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG', providerName: 'WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG',
        kind: 'contract', amount: 182, currency: '', billingCycle: 'monthly', interval: 1,
        monthlyEquivalent: 182, annualizedAmount: 2184, nextDueDate: iso('2026-04-01'), categoryId: 'c1',
        accountId: 'a1', autoDetected: true, isActive: true,
        createdAt: '2024-05-04T09:00:00Z', updatedAt: '2026-04-02T09:00:00Z'
      },
      {
        id: 'k4', name: 'WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG', providerName: 'WEG AM KÖNIGSTRÄßLE 1 5 VERTR D PPG',
        kind: 'contract', amount: 182, currency: 'EUR', billingCycle: 'monthly', interval: 1,
        monthlyEquivalent: 182, annualizedAmount: 2184, nextDueDate: iso('2026-07-01'), categoryId: 'c1',
        accountId: 'a2', autoDetected: true, isActive: true,
        createdAt: '2026-04-04T09:00:00Z', updatedAt: '2026-07-02T09:00:00Z'
      },
      {
        id: 'k5', name: 'WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG', providerName: 'WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG',
        kind: 'contract', amount: 182, currency: 'EUR', billingCycle: 'monthly', interval: 1,
        monthlyEquivalent: 182, annualizedAmount: 2184, nextDueDate: iso('2026-10-01'), categoryId: 'c1',
        accountId: 'a3', autoDetected: true, isActive: true,
        createdAt: '2026-07-04T09:00:00Z', updatedAt: '2026-09-02T09:00:00Z'
      }
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
      // Incomplete because the IDR wallet (a4) has no rate: the message has to name the value AND the
      // rate, not just say "something is missing".
      // USD wurde umgerechnet (Kurs + Fixing-Datum), IDR nicht (kein Kurs) - beide Faelle in einer Zeile.
      // Der zweite Kurs ist absichtlich veraltet, damit die Stale-Markierung sichtbar ist.
      accounts: {
        amount: 21250.30, isComplete: false, missingCurrencies: ['IDR'],
        ratesUsed: [
          { currency: 'USD', rate: 0.862, rateDate: iso('2026-09-09'), ageInDays: 1, isStale: false },
          { currency: 'CHF', rate: 1.0431, rateDate: iso('2026-08-28'), ageInDays: 13, isStale: true }
        ]
      },
      currency: 'EUR', isComplete: false, missingCurrencies: ['IDR'],
      // Real estate is its own converted slice of manualAssets, so the allocation donut never has to
      // guess it from native asset values.
      manualAssets: { amount: 12000, isComplete: true }, investments: { amount: 20000, isComplete: true },
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
    'insights': [],

    // ---- Altersvorsorge (bAV) ----
    'pension/overview': {
      currency: 'EUR', totalBalance: 18342.77, monthlyEmployeeContribution: 169,
      monthlyEmployerContribution: 169, guaranteedMonthlyAnnuity: 108.98,
      projectedMonthlyAnnuity: 194.13, isComplete: true, missingCurrencies: [],
      unconvertedBalances: [], contractsWithoutSnapshot: 1, contractsWithEstimatedCosts: 1
    },
    'pension/contracts': [
      {
        id: 'bav1', providerName: 'Allianz Lebensversicherung AG',
        tariffName: 'PrivatRente Perspektive (bAV)', employerName: 'Muster Maschinenbau GmbH',
        implementationRoute: 'direct_insurance', status: 'active', currency: 'EUR',
        includeInNetWorth: true, hasPolicyNumber: true, policyNumberLast4: '4711',
        startDate: '2016-07-01', retirementDate: '2051-08-01', guaranteeQuotaPercent: 80,
        guaranteedAnnuityFactor: 26.42, snapshotCount: 3, holdsCapital: true, isPaidUp: false,
        currentSnapshot: {
          effectiveDate: '2024-12-31', currency: 'EUR', balance: 15980.4, guaranteedBalance: 12740,
          source: 'document', isCurrent: true, projectionIsSimulation: false,
          projectionReturnPercent: 5, projectionBasis: 'document_forecast'
        },
        currentContribution: { employeeAmount: 169, employerTotalAmount: 169, partsTotalAmount: 338, currency: 'EUR', cycle: 'monthly', validFrom: '2025-01-01' }
      },
      {
        id: 'bav2', providerName: 'Unterstützungskasse der Muster Maschinenbau GmbH e.V.',
        tariffName: null, employerName: 'Muster Maschinenbau GmbH',
        implementationRoute: 'provident_fund', status: 'paid_up', currency: 'EUR',
        includeInNetWorth: true, hasPolicyNumber: false, policyNumberLast4: null,
        snapshotCount: 0, holdsCapital: true, isPaidUp: true,
        currentSnapshot: null, currentContribution: null
      },
      // A contract with a guarantee but NO annuity factor: its projected MONTHLY figure comes back
      // null, and the screen has to say why rather than blanking the row.
      {
        id: 'bav3', providerName: 'Pensionskasse Metall VVaG',
        tariffName: 'Klassik Garantie', employerName: 'Muster Maschinenbau GmbH',
        implementationRoute: 'pension_fund', status: 'active', currency: 'EUR',
        includeInNetWorth: true, hasPolicyNumber: false, policyNumberLast4: null,
        startDate: '2019-04-01', retirementDate: '2049-05-01', guaranteeQuotaPercent: 100,
        guaranteedAnnuityFactor: null, snapshotCount: 2, holdsCapital: true, isPaidUp: false,
        currentSnapshot: {
          effectiveDate: '2025-12-31', currency: 'EUR', balance: 2362.37, guaranteedBalance: 2362.37,
          source: 'document', isCurrent: true, projectionIsSimulation: false
        },
        currentContribution: { employeeAmount: 50, employerTotalAmount: 0, partsTotalAmount: 50, currency: 'EUR', cycle: 'monthly', validFrom: '2025-01-01' }
      },
      // A foreign-currency contract with no rate: it is what makes a projection INCOMPLETE, and the
      // totals have to say so instead of quietly dropping it.
      {
        id: 'bav4', providerName: 'Swiss Life AG', tariffName: 'Vorsorge CH',
        employerName: 'Muster Schweiz AG', implementationRoute: 'direct_insurance', status: 'active',
        currency: 'CHF', includeInNetWorth: true, hasPolicyNumber: false, policyNumberLast4: null,
        startDate: '2021-01-01', retirementDate: '2052-02-01', snapshotCount: 1,
        holdsCapital: true, isPaidUp: false,
        currentSnapshot: {
          effectiveDate: '2025-12-31', currency: 'CHF', balance: 4100, source: 'document', isCurrent: true
        },
        currentContribution: null
      }
    ],

    // The document list carries every state the Dokumente tab has to render: a rich matched one, an
    // OCR'd unmatched one with warnings, a failed extraction, and one that is already committed.
    // Order matters below: the per-document keys must sit BEFORE 'pension/documents', because match()
    // falls back to the first key the path starts with.
    'pension/documents/d1': {
      document: {
        id: 'd1', bavContractId: null, kind: 'annual_statement',
        originalFileName: 'Standmitteilung-2025-Allianz.pdf', mediaType: 'application/pdf',
        byteSize: 486_212, pageCount: 4, extractionStatus: 'parsed', extractionConfidence: 0.86,
        extractionSource: 'deterministic', textLayerUsed: true, extractionError: null,
        extractedAt: '2026-09-11T07:12:04Z', reviewedByUserId: null, reviewedAt: null,
        createdAt: '2026-09-11T07:12:00Z'
      },
      draft: {
        contract: {
          providerName: 'Allianz Lebensversicherung AG', tariffName: 'PrivatRente Perspektive (bAV)',
          policyNumber: null, implementationRoute: 'direct_insurance',
          employerName: 'Muster Maschinenbau GmbH', policyHolderName: 'Muster Maschinenbau GmbH',
          insuredPersonName: 'Julia Muster', startDate: null, retirementDate: '2051-08-01',
          currency: 'EUR', guaranteeQuotaPercent: 80, guaranteedAnnuityFactor: 26.42, status: 'active'
        },
        snapshot: {
          effectiveDate: '2025-12-31', currency: 'EUR', balance: 18342.77, guaranteedBalance: 14120.5,
          surrenderValue: null, securityAssetsAmount: 11230.4, fundAssetsAmount: 7112.37,
          guaranteedCapitalAtRetirement: 41250, guaranteedMonthlyAnnuity: 108.98,
          projectedCapitalAtRetirement: 73480, projectedMonthlyAnnuity: 194.13,
          projectionReturnPercent: 5, projectionBasis: 'document_forecast'
        },
        // 169 + 25.35 + 143.65 = 338.00 against a printed 340.00: the mismatch has to be SHOWN and
        // never summed away or corrected.
        contribution: {
          validFrom: '2025-01-01', cycle: 'monthly', currency: 'EUR', employeeAmount: 169,
          employerSubsidyAmount: 25.35, employerAmount: 143.65, statedTotalAmount: 340
        },
        allocations: [
          { fundName: 'Allianz Strategiefonds Wachstum', isin: 'DE0008476201', weightPercent: 60, amount: 4267.42, currency: 'EUR', ongoingChargesPercent: 1.45, ongoingChargesEstimated: false, assetClass: 'mixed' },
          { fundName: 'iShares Core MSCI World UCITS ETF mit einem sehr langen Fondsnamen zum Umbruchtest', isin: 'IE00B4L5Y983', weightPercent: 40, amount: 2844.95, currency: 'EUR', ongoingChargesPercent: 0.2, ongoingChargesEstimated: true, assetClass: 'equity' }
        ],
        costs: [
          { kind: 'administration_on_capital', basis: 'percent_of_capital', amount: null, percent: 0.35, timing: 'ongoing', isEstimated: false, estimateBasis: null, continuesWhenPaidUp: true },
          { kind: 'acquisition', basis: 'percent_of_sum', amount: null, percent: 2.5, timing: 'incurred', isEstimated: true, estimateBasis: 'Aus der Effektivkostenangabe auf Seite 3 abgeleitet.', continuesWhenPaidUp: false }
        ],
        provenance: [
          { field: 'contract.providerName', page: 1, confidence: 0.99, source: 'deterministic', matchedLabel: 'Versicherer' },
          { field: 'contract.tariffName', page: 1, confidence: 0.94, source: 'deterministic', matchedLabel: 'Tarif' },
          { field: 'contract.policyNumber', page: 1, confidence: 0.97, source: 'deterministic', matchedLabel: 'Versicherungsnummer' },
          { field: 'contract.employerName', page: 1, confidence: 0.88, source: 'deterministic', matchedLabel: 'Versicherungsnehmer' },
          { field: 'contract.insuredPersonName', page: 1, confidence: 0.91, source: 'deterministic', matchedLabel: 'Versicherte Person' },
          { field: 'contract.retirementDate', page: 2, confidence: 0.93, source: 'deterministic', matchedLabel: 'Rentenbeginn' },
          { field: 'contract.guaranteeQuotaPercent', page: 3, confidence: 0.71, source: 'codex', matchedLabel: 'Garantieniveau' },
          { field: 'contract.guaranteedAnnuityFactor', page: 3, confidence: 0.66, source: 'deterministic', matchedLabel: 'Rentenfaktor' },
          { field: 'snapshot.effectiveDate', page: 2, confidence: 0.98, source: 'deterministic', matchedLabel: 'Stand zum' },
          { field: 'snapshot.balance', page: 2, confidence: 0.96, source: 'deterministic', matchedLabel: 'Vertragsguthaben' },
          { field: 'snapshot.guaranteedBalance', page: 2, confidence: 0.9, source: 'deterministic', matchedLabel: 'davon garantiert' },
          { field: 'snapshot.securityAssetsAmount', page: 2, confidence: 0.84, source: 'deterministic', matchedLabel: 'Sicherungsvermögen' },
          { field: 'snapshot.fundAssetsAmount', page: 2, confidence: 0.84, source: 'deterministic', matchedLabel: 'Fondsvermögen' },
          { field: 'snapshot.guaranteedCapitalAtRetirement', page: 2, confidence: 0.92, source: 'deterministic', matchedLabel: 'garantiertes Kapital' },
          { field: 'snapshot.guaranteedMonthlyAnnuity', page: 2, confidence: 0.92, source: 'deterministic', matchedLabel: 'garantierte Monatsrente' },
          { field: 'snapshot.projectedCapitalAtRetirement', page: 2, confidence: 0.79, source: 'deterministic', matchedLabel: 'mögliches Kapital bei 5 % Wertentwicklung' },
          { field: 'snapshot.projectedMonthlyAnnuity', page: 2, confidence: 0.79, source: 'deterministic', matchedLabel: 'mögliche Monatsrente' },
          { field: 'snapshot.projectionReturnPercent', page: 2, confidence: 0.95, source: 'deterministic', matchedLabel: 'Wertentwicklung' },
          { field: 'contribution.employeeAmount', page: 3, confidence: 0.89, source: 'deterministic', matchedLabel: 'Entgeltumwandlung' },
          { field: 'contribution.employerSubsidyAmount', page: 3, confidence: 0.83, source: 'deterministic', matchedLabel: 'Arbeitgeberzuschuss' },
          { field: 'contribution.employerAmount', page: 3, confidence: 0.8, source: 'deterministic', matchedLabel: 'arbeitgeberfinanziert' },
          { field: 'contribution.statedTotalAmount', page: 3, confidence: 0.87, source: 'deterministic', matchedLabel: 'Gesamtbeitrag' },
          { field: 'allocations[0].fundName', page: 4, confidence: 0.81, source: 'deterministic', matchedLabel: 'Fonds' },
          { field: 'allocations[0].weightPercent', page: 4, confidence: 0.78, source: 'deterministic', matchedLabel: 'Anteil' },
          { field: 'allocations[1].fundName', page: 4, confidence: 0.62, source: 'codex', matchedLabel: 'Fonds' },
          { field: 'allocations[1].weightPercent', page: 4, confidence: 0.6, source: 'codex', matchedLabel: 'Anteil' },
          { field: 'costs[0].percent', page: 3, confidence: 0.74, source: 'deterministic', matchedLabel: 'Verwaltungskosten' },
          { field: 'costs[1].percent', page: 3, confidence: 0.52, source: 'codex', matchedLabel: 'Effektivkosten' }
        ],
        confidence: 0.86, source: 'deterministic', pageCount: 4, textLayerUsed: true,
        unresolved: ['contract.startDate', 'snapshot.surrenderValue']
      },
      match: {
        matched: true, contractId: 'bav1', matchedOn: 'policy_number',
        contract: { id: 'bav1', providerName: 'Allianz Lebensversicherung AG', tariffName: 'PrivatRente Perspektive (bAV)', employerName: 'Muster Maschinenbau GmbH', status: 'active', currency: 'EUR' }
      },
      warnings: [
        'Die drei Beitragsanteile ergeben 338,00 €, als Gesamtbeitrag steht 340,00 € im Dokument. Prüfe, welche Zahl stimmt – korrigiert wird hier nichts.',
        'Die Abschlusskosten stehen nicht ausdrücklich im Dokument und sind aus der Effektivkostenangabe abgeleitet. Sie werden als Schätzung übernommen.'
      ]
    },
    // OCR'd: no text layer, so every value is RECOGNISED and not read, the confidences are low and
    // four fields could not be filled at all.
    'pension/documents/d2': {
      document: {
        id: 'd2', bavContractId: null, kind: 'annual_statement',
        originalFileName: 'scan-standmitteilung.jpg', mediaType: 'image/jpeg',
        byteSize: 2_914_330, pageCount: 2, extractionStatus: 'parsed', extractionConfidence: 0.41,
        extractionSource: 'codex', textLayerUsed: false, extractionError: null,
        extractedAt: '2026-09-10T18:40:22Z', reviewedByUserId: null, reviewedAt: null,
        createdAt: '2026-09-10T18:40:11Z'
      },
      draft: {
        contract: {
          providerName: 'Volkswohl Bund Lebensversicherung a.G.', tariffName: null, policyNumber: null,
          implementationRoute: 'direct_insurance', employerName: null, policyHolderName: null,
          insuredPersonName: 'Julia Muster', startDate: null, retirementDate: null,
          currency: 'EUR', guaranteeQuotaPercent: null, guaranteedAnnuityFactor: null, status: 'active'
        },
        snapshot: {
          effectiveDate: '2025-12-31', currency: 'EUR', balance: 6120.5, guaranteedBalance: null,
          surrenderValue: null, securityAssetsAmount: null, fundAssetsAmount: null,
          guaranteedCapitalAtRetirement: 18400, guaranteedMonthlyAnnuity: null,
          projectedCapitalAtRetirement: null, projectedMonthlyAnnuity: null,
          projectionReturnPercent: null, projectionBasis: null
        },
        contribution: null,
        allocations: [],
        costs: [],
        provenance: [
          { field: 'contract.providerName', page: 1, confidence: 0.58, source: 'codex', matchedLabel: 'Versicherer' },
          { field: 'contract.insuredPersonName', page: 1, confidence: 0.44, source: 'codex', matchedLabel: null },
          { field: 'snapshot.effectiveDate', page: 1, confidence: 0.51, source: 'deterministic', matchedLabel: 'Stand' },
          { field: 'snapshot.balance', page: 1, confidence: 0.39, source: 'deterministic', matchedLabel: 'Guthaben' },
          { field: 'snapshot.guaranteedCapitalAtRetirement', page: 2, confidence: 0.33, source: 'codex', matchedLabel: null }
        ],
        confidence: 0.41, source: 'codex', pageCount: 2, textLayerUsed: false,
        unresolved: ['contract.employerName', 'contract.retirementDate', 'snapshot.guaranteedBalance', 'snapshot.guaranteedMonthlyAnnuity']
      },
      match: { matched: false, contractId: null, matchedOn: null, contract: null },
      warnings: [
        'Das Dokument hatte keine Textschicht, alle Werte kommen aus der Texterkennung. Prüfe besonders das Guthaben und den Stichtag.',
        'Zu diesem Dokument wurde kein Beitrag gefunden. Ergänze ihn selbst, wenn er auf dem Papier steht.'
      ]
    },
    // Nothing readable: a photo without recognisable text. The screen has to explain it as a category
    // and offer the manual path, not show a status code.
    'pension/documents/d3': {
      document: {
        id: 'd3', bavContractId: null, kind: 'certificate',
        originalFileName: 'foto-versicherungsschein.heic', mediaType: 'image/heic',
        byteSize: 3_410_002, pageCount: 1, extractionStatus: 'failed', extractionConfidence: null,
        extractionSource: null, textLayerUsed: false, extractionError: 'no_text',
        extractedAt: '2026-09-09T20:01:00Z', reviewedByUserId: null, reviewedAt: null,
        createdAt: '2026-09-09T20:00:55Z'
      },
      draft: null,
      match: null,
      warnings: []
    },
    // Already committed: the draft is gone on purpose, so the same numbers do not sit in two places.
    'pension/documents/d4': {
      document: {
        id: 'd4', bavContractId: 'bav1', kind: 'annual_statement',
        originalFileName: 'Standmitteilung-2024-Allianz.pdf', mediaType: 'application/pdf',
        byteSize: 452_004, pageCount: 4, extractionStatus: 'committed', extractionConfidence: 0.9,
        extractionSource: 'deterministic', textLayerUsed: true, extractionError: null,
        extractedAt: '2025-02-03T09:00:00Z', reviewedByUserId: 'u1', reviewedAt: '2025-02-03T09:05:00Z',
        createdAt: '2025-02-03T08:59:40Z'
      },
      draft: null,
      match: null,
      warnings: []
    },
    'pension/documents': [
      { id: 'd1', bavContractId: null, kind: 'annual_statement', originalFileName: 'Standmitteilung-2025-Allianz.pdf', mediaType: 'application/pdf', byteSize: 486_212, pageCount: 4, extractionStatus: 'parsed', extractionConfidence: 0.86, extractionSource: 'deterministic', textLayerUsed: true, extractionError: null, extractedAt: '2026-09-11T07:12:04Z', reviewedByUserId: null, reviewedAt: null, createdAt: '2026-09-11T07:12:00Z' },
      { id: 'd2', bavContractId: null, kind: 'annual_statement', originalFileName: 'scan-standmitteilung.jpg', mediaType: 'image/jpeg', byteSize: 2_914_330, pageCount: 2, extractionStatus: 'parsed', extractionConfidence: 0.41, extractionSource: 'codex', textLayerUsed: false, extractionError: null, extractedAt: '2026-09-10T18:40:22Z', reviewedByUserId: null, reviewedAt: null, createdAt: '2026-09-10T18:40:11Z' },
      { id: 'd3', bavContractId: null, kind: 'certificate', originalFileName: 'foto-versicherungsschein.heic', mediaType: 'image/heic', byteSize: 3_410_002, pageCount: 1, extractionStatus: 'failed', extractionConfidence: null, extractionSource: null, textLayerUsed: false, extractionError: 'no_text', extractedAt: '2026-09-09T20:01:00Z', reviewedByUserId: null, reviewedAt: null, createdAt: '2026-09-09T20:00:55Z' },
      { id: 'd4', bavContractId: 'bav1', kind: 'annual_statement', originalFileName: 'Standmitteilung-2024-Allianz.pdf', mediaType: 'application/pdf', byteSize: 452_004, pageCount: 4, extractionStatus: 'committed', extractionConfidence: 0.9, extractionSource: 'deterministic', textLayerUsed: true, extractionError: null, extractedAt: '2025-02-03T09:00:00Z', reviewedByUserId: 'u1', reviewedAt: '2025-02-03T09:05:00Z', createdAt: '2025-02-03T08:59:40Z' }
    ]
  };

  // What a commit answers: what it wrote AND what it refused. A snapshot whose date already exists is
  // information, not a failure, so the harness returns one so the screen can be checked saying so.
  const PENSION_COMMIT = {
    documentId: 'd1', contractId: 'bav1', snapshotId: 's9', contractCreated: false,
    applied: ['contract_matched', 'snapshot:2025-12-31', 'contribution:2025-01-01', 'allocations', 'costs'],
    skipped: ['snapshot_exists:2024-12-31', 'ein_serverseitiger_token_den_das_modul_nicht_kennt']
  };

  // ---- Altersvorsorge: the projection (POST, so it never reaches the GET fixture map) ----
  //
  // Computed rather than canned, because the point of the Simulation tab is that the figures MOVE
  // with the scenario: a hardcoded answer would look identical at 3 % and at 7 % and would hide the
  // one thing the screen is for. The monthly rate is the twelfth root of the annual one, the way
  // IBavProjectionCalculator states it - annual / 12 overstates a 7 % assumption by ~0.23 pp a year.
  function grow(balance, monthly, months, annualPercent, annualCostPercent) {
    const rate = Math.pow(1 + annualPercent / 100, 1 / 12) - 1;
    const cost = Math.pow(1 - annualCostPercent / 100, 1 / 12) - 1;
    let value = Number(balance) || 0;
    for (let index = 0; index < months; index += 1) value = (value + monthly) * (1 + rate) * (1 + cost);
    return Math.round(value * 100) / 100;
  }
  const round2 = value => Math.round(value * 100) / 100;

  // Every case the tab has to render, in one answer: two projectable contracts (one of them without an
  // annuity factor), one excluded for a missing retirement date, one for a missing rate - which is
  // also what makes the result incomplete.
  function pensionProjection(request) {
    const returnPercent = Number(request?.returnPercent ?? 5);
    const paying = request?.continueContributions !== false;
    const ids = request?.contractIds;
    const wanted = id => !Array.isArray(ids) || ids.length === 0 || ids.includes(id);

    const specs = [
      {
        contractId: 'bav1', providerName: 'Allianz Lebensversicherung AG', months: 311,
        balance: 15980.4, asOf: '2024-12-31', employee: 169, employer: 169, cost: 0.9,
        guaranteedCapital: 28400, guaranteedAnnuity: 108.98, annuityFactor: 26.42, estimates: true
      },
      {
        contractId: 'bav3', providerName: 'Pensionskasse Metall VVaG', months: 287,
        balance: 2362.37, asOf: '2025-12-31', employee: 50, employer: 0, cost: 0.6,
        guaranteedCapital: 19850, guaranteedAnnuity: 61.4, annuityFactor: null, estimates: false
      }
    ];

    const projected = specs.filter(spec => wanted(spec.contractId)).map(spec => {
      const monthly = paying ? spec.employee + spec.employer : 0;
      const capital = grow(spec.balance, monthly, spec.months, returnPercent, spec.cost);
      return {
        contractId: spec.contractId, providerName: spec.providerName, currency: 'EUR',
        currentBalance: spec.balance, balanceAsOf: spec.asOf,
        guaranteedCapital: spec.guaranteedCapital, guaranteedMonthlyAnnuity: spec.guaranteedAnnuity,
        projectedCapital: capital,
        // Null on purpose where the contract states no factor: a monthly annuity invented from a
        // capital sum would be the most misleading number on the screen.
        projectedMonthlyAnnuity: spec.annuityFactor == null ? null : round2(capital / 10000 * spec.annuityFactor),
        returnPercent, monthsToRetirement: spec.months,
        employeeContributionsAhead: paying ? round2(spec.employee * spec.months) : 0,
        employerContributionsAhead: paying ? round2(spec.employer * spec.months) : 0,
        costsApplied: round2(capital * spec.cost / 100), costsIncludeEstimates: spec.estimates,
        blocker: null
      };
    });

    const excluded = [
      {
        contractId: 'bav2', providerName: 'Unterstützungskasse der Muster Maschinenbau GmbH e.V.',
        currency: 'EUR', currentBalance: null, balanceAsOf: null, guaranteedCapital: null,
        guaranteedMonthlyAnnuity: null, projectedCapital: null, projectedMonthlyAnnuity: null,
        returnPercent, monthsToRetirement: 0, employeeContributionsAhead: 0,
        employerContributionsAhead: 0, costsApplied: 0, costsIncludeEstimates: false,
        blocker: 'no_retirement_date'
      },
      {
        contractId: 'bav4', providerName: 'Swiss Life AG', currency: 'CHF', currentBalance: 4100,
        balanceAsOf: '2025-12-31', guaranteedCapital: null, guaranteedMonthlyAnnuity: null,
        projectedCapital: null, projectedMonthlyAnnuity: null, returnPercent,
        monthsToRetirement: 316, employeeContributionsAhead: 0, employerContributionsAhead: 0,
        costsApplied: 0, costsIncludeEstimates: false, blocker: 'missing_rate'
      }
    ].filter(item => wanted(item.contractId));

    const sum = key => round2(projected.reduce((total, item) => total + (Number(item[key]) || 0), 0));
    return {
      currency: 'EUR', returnPercent, contracts: projected.concat(excluded),
      totalCurrentBalance: sum('currentBalance'),
      totalGuaranteedCapital: sum('guaranteedCapital'),
      totalProjectedCapital: sum('projectedCapital'),
      totalGuaranteedMonthlyAnnuity: sum('guaranteedMonthlyAnnuity'),
      totalProjectedMonthlyAnnuity: sum('projectedMonthlyAnnuity'),
      // A missing rate makes the result incomplete - never 1:1, never 0.
      isComplete: !excluded.some(item => item.blocker === 'missing_rate'),
      missingCurrencies: excluded.some(item => item.blocker === 'missing_rate') ? ['CHF'] : [],
      excluded
    };
  }

  // The three comparison verdicts are reachable from the form, so each one can be looked at:
  //   - both sides on the SAME return         -> sameMoneySameAssumptions, delta 0, the sentence;
  //   - different returns                     -> different_assumptions, "not comparable as contracts";
  //   - same return but the guarantee contract-> cause ['costs'], a real, attributed delta.
  function pensionComparison(request) {
    const left = pensionProjection(request?.left);
    const right = pensionProjection(request?.right);
    const leftReturn = Number(request?.left?.returnPercent ?? 5);
    const rightReturn = Number(request?.right?.returnPercent ?? 5);
    if (leftReturn !== rightReturn) {
      return {
        left, right,
        capitalDelta: round2(right.totalProjectedCapital - left.totalProjectedCapital),
        annuityDelta: round2(right.totalProjectedMonthlyAnnuity - left.totalProjectedMonthlyAnnuity),
        cause: ['different_assumptions'], sameMoneySameAssumptions: false
      };
    }
    const touchesGuaranteeContract = [request?.left, request?.right]
      .some(side => (side?.contractIds || []).includes('bav3'));
    if (touchesGuaranteeContract) {
      return {
        left, right, capitalDelta: -4820.55, annuityDelta: -12.73,
        cause: ['costs'], sameMoneySameAssumptions: false
      };
    }
    // Same money, same assumptions: the delta MUST be zero. 50 € + 288 € is 338 €.
    return { left, right, capitalDelta: 0, annuityDelta: 0, cause: ['none'], sameMoneySameAssumptions: true };
  }

  const KEYS = Object.keys(FIXTURES);

  function match(pathname) {
    // "/bff/backend/api/transactions?x=1" -> "transactions"
    const after = pathname.replace(/^\/bff\/(backend|banking)\//, '').replace(/^api\//, '');
    const head = after.split('/')[0].split('?')[0];
    if (Object.prototype.hasOwnProperty.call(FIXTURES, head)) return FIXTURES[head];
    const hit = KEYS.find(key => after.startsWith(key));
    return hit ? FIXTURES[hit] : undefined;
  }

  // Two pension-document writes have to answer with something real rather than { ok: true }: the
  // upload returns the detail the review screen renders (so the screen is reachable at all), and the
  // commit returns applied/skipped (so the honest "was already there" half can be looked at).
  // A file whose name contains "doppelt" answers 409 the way the real upload does, so the conflict
  // path (offer the document that already holds those pages, never a dead error) can be walked.
  function writeAnswer(method, pathname, init) {
    const after = pathname.replace(/^\/bff\/(backend|banking)\//, '').replace(/^api\//, '');
    if (after.startsWith('pension/projection')) {
      let body = init?.body;
      if (typeof body === 'string') { try { body = JSON.parse(body); } catch { body = null; } }
      return /^pension\/projection\/compare(\?|$)/.test(after)
        ? { status: 200, body: pensionComparison(body) }
        : { status: 200, body: pensionProjection(body) };
    }
    if (!after.startsWith('pension/documents')) return undefined;
    if (/\/commit(\?|$)/.test(after)) return { status: 200, body: PENSION_COMMIT };
    if (method === 'POST' && /^pension\/documents(\?|$)/.test(after)) {
      const name = init?.body instanceof FormData ? String(init.body.get('document')?.name || '') : '';
      return /doppelt/i.test(name)
        ? { status: 409, body: { error: 'Diese Datei ist hier schon eingelesen.', existingDocumentId: 'd1' } }
        : { status: 200, body: FIXTURES['pension/documents/d1'] };
    }
    return undefined;
  }

  const realFetch = window.fetch.bind(window);

  window.fetch = async (input, init) => {
    const raw = typeof input === 'string' ? input : (input?.url ?? String(input));
    const url = new URL(raw, location.origin);
    const isBff = url.pathname.startsWith('/bff/') || url.pathname.startsWith('/api/');
    if (!isBff) return realFetch(input, init);

    const method = (init?.method || (typeof input !== 'string' && input?.method) || 'GET').toUpperCase();
    // Writes succeed with an echo so confirm/save paths can be walked without a backend.
    const write = method === 'GET' ? null : writeAnswer(method, url.pathname, init);
    const status = write?.status ?? 200;
    const body = method === 'GET' ? (match(url.pathname) ?? []) : (write?.body ?? { id: 'stub', ok: true });
    window.__harnessCalls = window.__harnessCalls || [];
    window.__harnessCalls.push(`${method} ${url.pathname}`);
    // secure-fetch.js captures globalThis.fetch at module load, and this stub is installed before it -
    // so this stub IS its nativeFetch and the harness server never sees a write. That makes the server
    // log useless for reading a payload, and __harnessWrites the only place an audit can check what a
    // screen really sends (e.g. that a review draft keeps the three contribution shares apart).
    if (method !== 'GET' && method !== 'HEAD') {
      window.__harnessWrites = window.__harnessWrites || [];
      let payload = init?.body;
      if (typeof payload === 'string') { try { payload = JSON.parse(payload); } catch { /* keep the text */ } }
      else if (payload instanceof FormData) payload = Object.fromEntries([...payload.entries()]
        .map(([key, value]) => [key, value instanceof File ? `File(${value.name}, ${value.size})` : value]));
      window.__harnessWrites.push({ method, path: url.pathname, body: payload });
    }
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
  };

  // The app remembers the active space locally; pre-set it so no picker blocks the first render.
  try { localStorage.setItem('fullworth.spaceId', SPACE); } catch { /* ignore */ }
  window.__harnessReady = true;
})();
