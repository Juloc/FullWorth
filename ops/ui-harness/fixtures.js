// Installed before app.js. Answers the BFF from canned data so the real frontend renders without a
// login. Unknown endpoints return an empty array/object, which every view tolerates - that still
// exercises the real layout, which is what the mobile audit needs.
(() => {
  const SPACE = '11111111-1111-1111-1111-111111111111';
  const iso = d => d;
  // Der Auftrag, den die Finanzguru-Vorschau anlegt (#131). Er steht als Konstante da, weil drei
  // Antworten ihn teilen muessen: die Vorschau nennt ihn, die Zeilenauswahl holt die Kandidaten
  // unter ihm, und das Uebernehmen adressiert ihn.
  const FINANZGURU_STAGE_JOB = '7f000000-0000-4000-8000-000000000131';
  // Der Auftrag des Spalten-Imports (#131, Abschnitt 3). Seine Zusammenfassung fuehrt zwei
  // Quellkonten: eines, das beim letzten Import schon zugeordnet wurde, und eines ohne Erinnerung -
  // nur so ist im Harness zu sehen, dass die Vorauswahl der Erinnerung folgt und nicht dem Raten.
  const MAPPING_JOB = '7f000000-0000-4000-8000-000000000132';
  // Ein PDF-Kontoauszug (#131, Abschnitt 11): ein erfundener Ikano-Auszug im Aufbau des echten - drei
  // echte Bewegungen, vier interne Umbuchungen zwischen Karte und Ratenkauf, die abgewaehlt dastehen.
  const STATEMENT_JOB = '7f000000-0000-4000-8000-000000000133';
  // Derselbe Auszug, falsch gelesen: er geht nicht auf, jede Zeile steht zur Pruefung da.
  const STATEMENT_JOB_UNSURE = '7f000000-0000-4000-8000-000000000134';
  const statementRows = note => [
    { id: '5a000000-0000-4000-8000-000000000001', bookingDate: iso('2026-09-01'), amount: 50, currency: 'EUR', counterparty: 'Lastschrifteinzug', duplicateStatus: 'new', validationStatus: 'ready', reviewNote: note },
    { id: '5a000000-0000-4000-8000-000000000002', bookingDate: iso('2026-09-01'), amount: 20, currency: 'EUR', counterparty: 'ONLINESHOP GUTSCHRIFT LU', duplicateStatus: 'new', validationStatus: 'ready', reviewNote: note },
    { id: '5a000000-0000-4000-8000-000000000003', bookingDate: iso('2026-09-01'), amount: -20, currency: 'EUR', counterparty: 'Automatische Saldenkorrektur', duplicateStatus: 'new', validationStatus: 'ready', reviewNote: note || 'internal_transfer' },
    { id: '5a000000-0000-4000-8000-000000000004', bookingDate: iso('2026-09-01'), amount: 20, currency: 'EUR', counterparty: 'Automatische Saldenkorrektur', duplicateStatus: 'new', validationStatus: 'ready', reviewNote: note || 'internal_transfer' },
    { id: '5a000000-0000-4000-8000-000000000005', bookingDate: iso('2026-09-11'), amount: -120, currency: 'EUR', counterparty: 'MOEBELHAUS BEISPIEL MUSTERSTADT DE', duplicateStatus: 'new', validationStatus: 'ready', reviewNote: note },
    { id: '5a000000-0000-4000-8000-000000000006', bookingDate: iso('2026-09-12'), amount: 120, currency: 'EUR', counterparty: 'UMBUCHUNG IN RATENKAUF - MOEBELHAUS', duplicateStatus: 'new', validationStatus: 'ready', reviewNote: note || 'internal_transfer' },
    { id: '5a000000-0000-4000-8000-000000000007', bookingDate: iso('2026-09-12'), amount: -120, currency: 'EUR', counterparty: 'RATENKAUF, 003 MONATE - MOEBELHAUS', duplicateStatus: 'new', validationStatus: 'ready', reviewNote: note || 'internal_transfer' }
  ];

  const FIXTURES = {
    [`import-jobs/${STATEMENT_JOB}/candidates`]: statementRows(null),
    [`import-jobs/${STATEMENT_JOB_UNSURE}/candidates`]: statementRows('not_reconciled'),
    [`import-mapping/jobs/${MAPPING_JOB}/summary`]: {
      sourceAccounts: [
        { source: 'Tagesgeld', count: 12, rememberedAccountId: 'a1' },
        { source: 'Neues Depotkonto', count: 3, rememberedAccountId: null }
      ],
      sourceCategories: []
    },
    [`import-jobs/${MAPPING_JOB}/candidates`]: [],
    // Der Importverlauf. Drei Faelle, die im Harness zu sehen sein muessen (#131): ein Finanzguru-
    // Import, der nur ergaenzt hat (keine neue Buchung - und trotzdem ruecknehmbar), eine Vorschau,
    // die nie uebernommen wurde (gehoert NICHT in den Verlauf), und eine, die die Aufraeumfrist
    // abgebrochen hat.
    'import-jobs': [
      { id: 'j-fg-enrich', fileName: 'alle-buchungen.xlsx', adapterKey: 'finanzguru_xlsx', status: 'completed',
        sourceRowCount: 312, readyCount: 0, duplicateCount: 309, importedCount: 0, errorCount: 0,
        createdAt: iso('2026-09-20T10:00:00Z'), completedAt: iso('2026-09-20T10:01:00Z'), rolledBackAt: null,
        rollbackAvailable: true },
      { id: 'j-fg-ready', fileName: 'alle-buchungen.xlsx', adapterKey: 'finanzguru_xlsx', status: 'ready',
        sourceRowCount: 312, readyCount: 3, duplicateCount: 309, importedCount: 0, errorCount: 0,
        createdAt: iso('2026-09-24T09:00:00Z'), completedAt: null, rolledBackAt: null, rollbackAvailable: false },
      { id: 'j-fg-cancelled', fileName: 'alt.xlsx', adapterKey: 'finanzguru_xlsx', status: 'cancelled',
        sourceRowCount: 40, readyCount: 40, duplicateCount: 0, importedCount: 0, errorCount: 0,
        createdAt: iso('2026-08-01T09:00:00Z'), completedAt: null, rolledBackAt: null, rollbackAvailable: false }
    ],
    'import/finanzguru/accounts': {
      importAccounts: [],
      attachedHistory: [],
      targetAccounts: [
        { id: 'a1', displayName: 'Haushaltskonto', institutionName: 'Sparkasse', currency: 'EUR', ibanLast4: '2051', hasCurrentBalance: true, isActive: true, includeInNetWorth: true },
        { id: 'a2', displayName: 'Tagesgeld mit langem Namen', institutionName: 'DKB', currency: 'EUR', ibanLast4: '5030', hasCurrentBalance: true, isActive: true, includeInNetWorth: true },
        // In einer anderen Waehrung: die Vorschau darf es fuer ein Euro-Quellkonto gar nicht anbieten.
        { id: 'a9', displayName: 'US-Konto', institutionName: 'Chase', currency: 'USD', ibanLast4: null, hasCurrentBalance: true, isActive: true, includeInNetWorth: true }
      ]
    },
    [`import-jobs/${FINANZGURU_STAGE_JOB}/candidates`]: [
      { id: '3a000000-0000-4000-8000-000000000001', bookingDate: iso('2026-09-12'), amount: '-42.19', currency: 'EUR', counterparty: 'REWE Markt GmbH', description: 'Einkauf', duplicateStatus: 'new' },
      { id: '3a000000-0000-4000-8000-000000000002', bookingDate: iso('2026-09-11'), amount: '-9.99', currency: 'EUR', counterparty: 'Spotify', description: 'Abo', duplicateStatus: 'new' },
      { id: '3a000000-0000-4000-8000-000000000003', bookingDate: iso('2026-09-10'), amount: '2810.44', currency: 'EUR', counterparty: 'Arbeitgeber AG', description: 'Gehalt September', duplicateStatus: 'new' },
      { id: '3a000000-0000-4000-8000-000000000004', bookingDate: iso('2026-09-09'), amount: '-12.40', currency: 'EUR', counterparty: 'Deutsche Bahn', description: 'Ticket', duplicateStatus: 'duplicate' }
    ],
    // Gehalt. Bewusst ein TEILJAHR (vier Monate) - das ist der Fall, dessen Hinweis leicht
    // vergessen wird. Ohne diese Einträge rechnete die Seite gegen den allgemeinen
    // Schreib-Stub und zeigte NaN, was in der Harness wie ein Fehler der Seite aussah.
    'compensation/profile': null,
    'compensation/scenarios': [],
    'compensation/history': [],
    // Admin-only on the server; the settings row hides itself when the call fails, so the fixture is
    // what makes the row visible here at all.
    'fullworth-spaces': [{ id: SPACE, name: 'Haushalt', baseCurrency: 'EUR', role: 'owner', isDefault: true }],
    // Die Frage aus dem Einrichtungsassistenten (#117). canChange ist true, solange niemand eine
    // Standardkategorie umbenannt hat - nur dann wird die Auswahl ueberhaupt angeboten.
    // Sammlungen (#124). Zwei Faelle, die sich unterscheiden muessen: eine dauerhafte Sammlung
    // ohne Zeitraum und ein abgeschlossenes Projekt mit einem - und bei letzterem fehlt ein
    // historischer Kurs, damit sich messen laesst, ob die Oberflaeche einen unvollstaendigen
    // Betrag als solchen zeigt statt eine exakte Summe vorzutaeuschen.
    //
    // Die Detail- und Kandidatenschluessel stehen UEBER dem Listenschluessel: die Harness ordnet
    // per Praefix zu und nimmt den laengsten Treffer, aber ein Eintrag ohne eigenen Schluessel
    // fiele in die Aufloesung "Listeneintrag nach id" und bekaeme still das falsche Gegenstueck.
    'collections/col1/candidates': [
      { transactionId: 't1', date: '2026-08-20', amount: -184, currency: 'EUR', counterparty: 'BAUHAUS', categoryName: 'Baumarkt', accountName: 'Girokonto', reasons: ['merchant', 'category'], score: 5 }
    ],
    'collections/col2/candidates': [
      { transactionId: 't2', date: '2026-08-13', amount: -680, currency: 'EUR', counterparty: 'Hotel Garda', categoryName: 'Hotel', accountName: 'Girokonto', reasons: ['merchant', 'period'], score: 5 },
      { transactionId: 't3', date: '2026-08-15', amount: -900, currency: 'EUR', counterparty: 'Vermieter', categoryName: null, accountName: 'Girokonto', reasons: ['period'], score: 2 }
    ],
    // #124: transactionIds sind bewusst gefuellt (t1/t2 stehen auch in der Buchungsliste). Die
    // Such-/Filteransicht kennzeichnet damit die bereits zugeordneten Treffer und kann sie
    // ausblenden - mit einer leeren Liste waere genau dieser Unterschied nicht nachsehbar.
    'collections/col1': { collection: { id: 'col1', name: 'Wohnung', description: 'Alles rund um die Wohnung', icon: 'home', color: null, startDate: null, endDate: null, status: 'active', transactionCount: 14, expenses: 14820, income: 0, net: -14820, currency: 'EUR', isComplete: true, missingCurrencies: [] }, categories: [
      { categoryId: 'c-mob', categoryName: 'Möbel', expenses: 4200, count: 3 },
      { categoryId: 'c-bau', categoryName: 'Baumarkt', expenses: 3100, count: 6 },
      { categoryId: 'c-ele', categoryName: 'Elektrogeräte', expenses: 2600, count: 2 }
    ], transactionIds: ['t1', 't2'] },
    'collections/col2': { collection: { id: 'col2', name: 'Gardasee 2026', description: null, icon: 'travel', color: null, startDate: '2026-08-12', endDate: '2026-08-17', status: 'completed', transactionCount: 9, expenses: 1436, income: 0, net: -1436, currency: 'EUR', isComplete: false, missingCurrencies: ['CHF'] }, categories: [
      { categoryId: 'c-hot', categoryName: 'Hotel', expenses: 680, count: 1 },
      { categoryId: 'c-res', categoryName: 'Restaurant', expenses: 286, count: 4 }
    ], transactionIds: ['t1', 't2'] },
    'collections': [{ id: 'col1', name: 'Wohnung', description: 'Alles rund um die Wohnung', icon: 'home', color: null, startDate: null, endDate: null, status: 'active', transactionCount: 14, expenses: 14820, income: 0, net: -14820, currency: 'EUR', isComplete: true, missingCurrencies: [] }, { id: 'col2', name: 'Gardasee 2026', description: null, icon: 'travel', color: null, startDate: '2026-08-12', endDate: '2026-08-17', status: 'completed', transactionCount: 9, expenses: 1436, income: 0, net: -1436, currency: 'EUR', isComplete: false, missingCurrencies: ['CHF'] }],
    // Die gewaehlte Farbe je Kategorie (#177). Nur EINE hat eine - so stehen beide Faelle
    // gleichzeitig im Baum: der Punkt mit der gewaehlten Farbe und der mit dem Zaehlerstand.
    // Zusammenfuehren (#177). Die Vorschau ist der Fall, um den es geht: es haengt etwas daran, und
    // es geht trotzdem - so ist im Harness beides zu sehen, die Zahlen UND ein aktiver Knopf.
    'category-merge/c2/preview': {
      source: { id: 'c2', name: 'Restaurant', isArchived: false, isSystem: false },
      target: { id: 'c1', name: 'Lebensmittel' },
      transactions: 34, refundCategories: 0, splitAllocations: 2, rules: 1, budgets: 1, contracts: 0,
      purchaseItems: 5, productDefaults: 0, activeChildren: 0, targetIsDescendant: false,
      canApply: true, canDeleteSource: true
    },
    // Und der Fall, der NICHT geht: unter c1 haengt c4. Ohne ihn liesse sich nicht ansehen, dass der
    // Knopf dann aus bleibt und der Grund dasteht.
    'category-merge/c1/preview': {
      source: { id: 'c1', name: 'Lebensmittel', isArchived: false, isSystem: true },
      target: { id: 'c2', name: 'Restaurant' },
      transactions: 120, refundCategories: 0, splitAllocations: 0, rules: 3, budgets: 2, contracts: 0,
      purchaseItems: 40, productDefaults: 0, activeChildren: 1, targetIsDescendant: false,
      canApply: false, canDeleteSource: false
    },
    'category-intelligence/category-appearances': [
      { categoryId: 'c1', color: '#2E7D32' }
    ],
    'categories/language': { language: 'en', canChange: true, systemCategories: 80, renamedByUser: 0 },
    'categories': [
      { id: 'c1', name: 'Lebensmittel', kind: 'expense', parentId: null, isArchived: false, icon: 'groceries' },
      { id: 'c2', name: 'Restaurant', kind: 'expense', parentId: null, isArchived: false, icon: 'restaurants' },
      { id: 'c3', name: 'Gehalt', kind: 'income', parentId: null, isArchived: false, icon: 'salary' },
      { id: 'c4', name: 'Supermarkt mit sehr langem Namen zum Umbruchtest', kind: 'expense', parentId: 'c1', isArchived: false, icon: 'groceries' }
    ],
    // Merchant registry (#157/Scheibe 14 gap - the page rendered only its empty state here before).
    // One merchant with two aliases (the everyday case) and one with none, so both the chip row and
    // the bare "+ Alias" affordance are on screen at once.
    // Die Zuordnungsprobe (#177). Die Fixture antwortet fuer JEDEN Text dasselbe - das reicht, um
    // den Weg anzusehen; welche Regel dahinter gilt, entscheidet der Server und nicht die Harness.
    // Rabattanalyse (#177). Loest die aermere /savings ab: dieselben drei Kennzahlen plus
    // Aufschluesselung nach Haendler und die Zahl der Kaeufe MIT Rabatt.
    'purchase-analytics/discount-analytics': {
      from: '2026-08-24', to: '2026-09-24', baseCurrency: 'EUR', incomplete: false,
      purchaseCount: 14, purchasesWithDiscount: 6,
      totalDiscountAmount: 23.40, itemDiscountAmount: 15.90,
      basketOrUnallocatedDiscountAmount: 7.50, discountLineAmount: 23.40,
      byMerchant: [{ name: 'REWE', amount: 14.20, count: 4 }, { name: 'dm', amount: 9.20, count: 2 }],
      byType: [{ name: 'coupon', amount: 12.30, count: 3 }, { name: 'promo', amount: 11.10, count: 3 }],
      byProduct: [{ name: 'Bio-Milch 1l', amount: 4.50, count: 3 }],
      byCategory: [{ name: 'Lebensmittel', amount: 18.70, count: 5 }]
    },
    // Gelernte Kategorien (#177). Zwei Vorschlaege - einer mit eindeutigem Gewinner ueber alle
    // Vorkommen, einer knapper: die Zahl 'count/totalOccurrences' ist genau der Unterschied, den
    // die Ansicht zeigen muss.
    // Steuerprofile (#177). ZWEI, denn bei einem erscheint der Abschnitt bewusst gar nicht - eine
    // Liste, in der man sich selbst einmal sieht, ordnet nichts.
    'tax/profiles': [
      { id: 'tp1', fullWorthSpaceId: SPACE, userId: null, displayName: 'Juli', countryCode: 'DE', assistantEnabled: true, active: true },
      { id: 'tp2', fullWorthSpaceId: SPACE, userId: null, displayName: 'Partner', countryCode: 'DE', assistantEnabled: false, active: true }
    ],
    'product-learning/category-suggestions': [
      { text: 'Bio-Milch 1l', normalizedText: 'bio milch 1l', categoryId: 'c1', category: 'Lebensmittel',
        count: 6, totalOccurrences: 6, productIdentityId: null, productName: null, currentDefaultCategoryId: null },
      { text: 'Spülmaschinentabs', normalizedText: 'spulmaschinentabs', categoryId: 'c1', category: 'Lebensmittel',
        count: 3, totalOccurrences: 5, productIdentityId: null, productName: null, currentDefaultCategoryId: null }
    ],
    'purchase-analytics/by-merchant': {
      currency: 'EUR',
      items: [{ label: 'REWE', amount: 412.80, count: 9 }, { label: 'dm', amount: 96.40, count: 3 }]
    },
    'merchants/resolve': { normalizedCounterparty: 'paypal spotify', merchantId: 'm1', merchantName: 'REWE' },
    'merchants': [
      { id: 'm1', name: 'REWE', logoAssetPath: null, aliases: [
        { id: 'ma1', normalizedAlias: 'rewe markt gmbh' }, { id: 'ma2', normalizedAlias: 'rewe sagt danke' }
      ] },
      { id: 'm2', name: 'Spotify', logoAssetPath: null, aliases: [
        { id: 'ma3', normalizedAlias: 'paypal *spotify' }
      ] },
      { id: 'm3', name: 'Deutsche Bahn', logoAssetPath: null, aliases: [] }
    ],
    // Categorization rules (#157/Scheibe 14 gap). One enabled rule with a pattern and an amount window,
    // one disabled rule (so the dimmed "rule-off" row is visible), one that both marks a transfer AND
    // stops further processing - the two badges together are the case the summary line has to render.
    'categorization-rules': [
      { id: 'r1', name: 'Supermärkte', isEnabled: true, priority: 100, matchField: 'counterparty', matchMode: 'contains',
        pattern: 'REWE', direction: 'expense', minAmount: null, maxAmount: null, merchantCategoryCode: null,
        categoryId: 'c1', markAsTransfer: false, stopProcessing: false },
      { id: 'r2', name: 'Kleinbeträge Bar', isEnabled: false, priority: 200, matchField: 'any', matchMode: 'contains',
        pattern: '', direction: 'expense', minAmount: 0, maxAmount: 15, merchantCategoryCode: null,
        categoryId: 'c2', markAsTransfer: false, stopProcessing: false },
      { id: 'r3', name: 'Eigene Konten', isEnabled: true, priority: 10, matchField: 'counterparty', matchMode: 'equals',
        pattern: 'Tagesgeld mit langem Namen', direction: 'any', minAmount: null, maxAmount: null,
        merchantCategoryCode: null, categoryId: 'c3', markAsTransfer: true, stopProcessing: true }
    ],
    // Audit log (#157/Scheibe 14 gap). Newest first, one system-triggered row (no actorUserId) and one
    // with an entity id, so both branches of the row/meta formatting are on screen.
    'audit': [
      { id: 'ev5', actorUserId: 'u1111111-1111-1111-1111-111111111111', action: 'category.rule.created', entityType: 'CategorizationRule', entityId: 'r3', occurredAt: '2026-09-17T18:20:00Z' },
      { id: 'ev4', actorUserId: 'u1111111-1111-1111-1111-111111111111', action: 'budget.updated', entityType: 'Budget', entityId: 'b2', occurredAt: '2026-09-17T09:05:00Z' },
      { id: 'ev3', actorUserId: null, action: 'bank_connection.synced', entityType: 'BankConnection', entityId: 'c5', occurredAt: '2026-09-16T06:00:00Z' },
      { id: 'ev2', actorUserId: 'u1111111-1111-1111-1111-111111111111', action: 'contract.created', entityType: 'RecurringContract', entityId: 'k5', occurredAt: '2026-09-14T08:12:00Z' },
      { id: 'ev1', actorUserId: 'u1111111-1111-1111-1111-111111111111', action: 'space.created', entityType: 'FullWorthSpace', entityId: SPACE, occurredAt: '2025-01-04T09:00:00Z' }
    ],
    // Tagesendstaende fuer die Buchungsseite (#126). Der laengere Schluessel steht vor 'accounts',
    // weil die Fixtures per Teilzeichenkette in Einfuegereihenfolge getroffen werden.
    'accounts/daily-balances': [{"date":"2026-09-02","amount":19385.45,"currency":"EUR","incomplete":true},{"date":"2026-09-03","amount":19522.85,"currency":"EUR","incomplete":false},{"date":"2026-09-04","amount":19660.25,"currency":"EUR","incomplete":false},{"date":"2026-09-05","amount":19797.65,"currency":"EUR","incomplete":false},{"date":"2026-09-06","amount":19935.05,"currency":"EUR","incomplete":false},{"date":"2026-09-07","amount":20072.45,"currency":"EUR","incomplete":false},{"date":"2026-09-08","amount":20209.85,"currency":"EUR","incomplete":false},{"date":"2026-09-09","amount":20347.25,"currency":"EUR","incomplete":false},{"date":"2026-09-10","amount":20484.65,"currency":"EUR","incomplete":false},{"date":"2026-09-11","amount":20622.05,"currency":"EUR","incomplete":false},{"date":"2026-09-12","amount":20759.45,"currency":"EUR","incomplete":false},{"date":"2026-09-13","amount":20896.85,"currency":"EUR","incomplete":false},{"date":"2026-09-14","amount":21034.25,"currency":"EUR","incomplete":false},{"date":"2026-09-15","amount":21171.65,"currency":"EUR","incomplete":false}],
    'accounts': [
      // The two everyday accounts carry the two balance types a reader has to be able to tell apart:
      // a1 is the bank's AVAILABLE figure (pending authorisations already deducted) with the bank's own
      // as-of date, a2 is a BOOKED figure with no as-of date at all - so the row must say "Abgerufen",
      // not "Datenstand". `meaning` is what the server derives from balanceType.
      {
        id: 'a1', name: 'Girokonto', displayName: 'Haushaltskonto', providerDisplayName: 'Girokonto', institutionName: 'Sparkasse',
        iban: 'DE02120300000000202051', ibanLast4: '2051', provider: 'test', accountType: 'checking',
        currency: 'EUR', isActive: true, includeInNetWorth: true, groupId: 'g0', sortOrder: 1,
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
        includeInNetWorth: true, groupId: 'g0', sortOrder: 2, ownerUserIds: [],
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
        groupId: 'g0', sortOrder: 6,
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
        currency: 'EUR', isActive: false, includeInNetWorth: false, groupId: 'g0', sortOrder: 5,
        latestBalance: null, balances: [], baseValue: null, baseCurrency: null
      },
      // Ein FinTS-Depot steht seit #133 als eigene Zeile in der Kontenliste, mit dem Kurswert seiner
      // Bestaende. accountType 'securities' schreibt genau eine Stelle - die Depotuebernahme - und die
      // Zeile erklaert darunter, warum derselbe Betrag im Nettovermoegen als Depot auftaucht.
      {
        id: 'a9', displayName: 'Direkt-Depot', institutionName: 'ING', providerDisplayName: null,
        provider: 'fints', bankConnectionId: 'c3', product: 'Direkt-Depot', accountType: 'securities',
        currency: 'EUR', isActive: true, includeInNetWorth: true, groupId: 'g0', sortOrder: 6,
        latestBalance: {
          amount: 18420.5, currency: 'EUR', balanceType: 'marketValue', source: 'provider',
          referenceDate: iso('2026-09-10')
        },
        balances: [], baseValue: null, baseCurrency: null
      }
    ],
    // Seit #125 hat jeder Bereich eine Standardgruppe; Konten ohne eigene Gruppe stehen darin.
    'account-groups': [{ id: 'g0', name: 'Allgemein', sortOrder: 0, isDefault: true }, { id: 'g1', name: 'Alltag', sortOrder: 1, isDefault: false }],
    // Aussehen und Ungelesen-Stand der Konten (#177). Seit die Kontenseite sie nicht mehr aus drei
    // Einstellungs-Blobs liest, ist DAS die Quelle fuer Symbol, Farben und den blauen Punkt - ohne
    // Fixture saehe die Harness jedes Konto mit der Vorgabe und nie einen Punkt.
    //
    // a1 traegt ein eigenes Symbol UND zwei ungelesene Buchungen, a2 gar nichts: beide Faelle stehen
    // damit gleichzeitig auf dem Schirm.
    'account-experience/group-appearances': [
      { groupId: 'g0', icon: 'home', color: '#334155', backgroundColor: '#EEF2F7' }
    ],
    'account-experience': [
      { accountId: 'a1', displayName: 'Haushaltskonto', institutionName: 'Sparkasse', provider: 'test',
        accountType: 'checking', product: null, icon: 'wallet', iconColor: '#334155', backgroundColor: '#EEF2F7',
        unseenTransactions: 2, lastSeenAt: '2026-09-08T06:00:00Z' },
      { accountId: 'a2', displayName: 'Tagesgeld', institutionName: 'ING', provider: 'test',
        accountType: 'savings', product: null, icon: null, iconColor: null, backgroundColor: null,
        unseenTransactions: 0, lastSeenAt: null }
    ],
    // Was die Bank beim Verbinden gemeldet hat - die Liste, aus der ausgewaehlt wird. Absichtlich
    // unbequem: ein Name, der die Zeile sprengt, ein Depot ohne IBAN, eine Fremdwaehrung und ein
    // Konto, das schon einmal abgewaehlt wurde (visible:false) und das Haekchen leer zeigen muss.
    'banking/fints/connections/c3/accounts': {
      connectionId: 'c3', status: 'AUTHORIZED', challenge: null,
      discovered: [
        { key: 'k1', kind: 'cash', name: 'Girokonto', ibanLast4: '4321', currency: 'EUR', accountId: null, visible: true },
        { key: 'k2', kind: 'cash', name: 'Extra-Konto mit einem ausgesprochen langen Namen', ibanLast4: '8765', currency: 'EUR', accountId: null, visible: true },
        { key: 'k3', kind: 'cash', name: 'Währungskonto', ibanLast4: '1111', currency: 'USD', accountId: null, visible: false },
        { key: 'k4', kind: 'depot', name: 'Direkt-Depot', ibanLast4: null, currency: 'EUR', accountId: null, visible: true }
      ]
    },
    // Two connections: one healthy, one FinTS parked on a TAN. The second must offer "TAN eingeben",
    // never "Neu verbinden" - reconnecting discards the challenge the bank is waiting for.
    // Der Verlauf der gescheiterten Verbindung - daraus baut der Fehlerbericht seine letzten Laeufe.
    // Was die Bank geschickt hat - die Ansicht, die das Raten beendet hat.
    'bank-connections/c5/raw-responses': [
      { id: 'r1', kind: 'mt535', label: 'Direkt-Depot', capturedAt: iso('2026-09-17'), payloadLength: 217 }
    ],
    'bank-connections/c5/raw-responses/r1': {
      id: 'r1', kind: 'mt535', label: 'Direkt-Depot', capturedAt: iso('2026-09-17'),
      payloadLength: 217, payload: ":16R:GENL\n:28E:1/ONLY\n:16S:GENL\n:16R:FIN\n:35B:ISIN IE00B4L5YC18\n/DE/A0RPWJ\nISHSIII-MSCI EM USD(ACC)\n:93B::AGGR//UNIT/0,43761\n:90B::MRKT//ACTU/EUR55,394\n:70E::HOLD//Einstandskurs EUR 48,12\n:19A::HOLD//EUR24,24\n:16S:FIN"
    },
    // #167: trigger und connector sind neu. h3 hat sie bewusst NICHT - so wie jeder Lauf, der vor
    // #167 aufgezeichnet wurde. Die Einzelheiten muessen die Zeilen dann weglassen, statt
    // "unbekannt" zu behaupten, und genau das ist im Harness nachsehbar.
    'bank-connections/c4/sync-history': [
      { id: 'h1', startedAt: iso('2026-09-16'), completedAt: iso('2026-09-16'), durationMs: 7900, result: 'error', errorCode: 'FINTS_TAN_REQUIRED', trigger: 'automatic', connector: 'fints' },
      { id: 'h2', startedAt: iso('2026-09-15'), completedAt: iso('2026-09-15'), durationMs: 8100, result: 'error', errorCode: 'FINTS_BANK_ERROR', trigger: 'manual', connector: 'fints' },
      { id: 'h3', startedAt: iso('2026-09-14'), completedAt: iso('2026-09-14'), durationMs: 5400, result: 'success', errorCode: null, trigger: null, connector: null }
    ],
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
      },
      {
        id: 'c3', provider: 'fints', institutionName: 'ING', country: 'DE',
        status: 'AUTHORIZED', healthStatus: 'selection_pending', validUntil: iso('2026-12-31'),
        lastSyncedAt: null, daysUntilExpiry: 112, nextSyncAllowedAt: null,
        lastError: 'FINTS_SELECTION_PENDING'
      },
      // Genau der gemeldete Zustand: angemeldet, gueltig, und ein Abruf ist trotzdem gescheitert.
      // Die Zeile darf dafuer NICHT "Neu verbinden" anbieten - die PIN erneut zu verlangen behebt
      // nichts, was eine abgelehnte Depotabfrage verursacht hat.
      {
        id: 'c4', provider: 'fints', institutionName: 'ING', country: 'DE',
        status: 'AUTHORIZED', healthStatus: 'error', validUntil: iso('2026-12-31'),
        lastSyncedAt: null, daysUntilExpiry: 112, nextSyncAllowedAt: iso('2026-09-17'),
        lastError: 'FINTS_BANK_ERROR'
      },
      // Eine gesunde FinTS-Verbindung - der Fall, der lange keinen Weg hatte. Wer bei seiner Bank
      // ein Konto dazunimmt, muss die Kontenliste neu holen koennen; die entsteht beim Verbinden.
      // Sichtbar bleibt hier nur der Abgleich, alles Weitere liegt hinter dem Auslassungszeichen.
      {
        id: 'c5', provider: 'fints', institutionName: 'ING', country: 'DE',
        status: 'AUTHORIZED', healthStatus: 'authorized', validUntil: iso('2026-12-31'),
        lastSyncedAt: iso('2026-09-16'), daysUntilExpiry: 112, nextSyncAllowedAt: null
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
    // #139: a dedicated key, not a sub-path of 'transactions' - match()'s generic "return the parent
    // object for anything below it" fallback only applies to a plain ARRAY fixture (see its own
    // comment); 'transactions' above is an object, so without this own, longer key the forecast
    // request silently got the whole transaction list back, unrelated to what it actually asked for.
    // Dates fall inside the horizon of 'today' (2026-09-18) the same fixtures assume everywhere else,
    // and the two contract entries reuse k1/k2 from the 'contracts' fixture below rather than inventing
    // unrelated ones, so a look at both screens shows the same two contracts.
    'transactions/forecast': {
      from: iso('2026-09-18'), to: iso('2026-12-17'), incomplete: false,
      // Aufsteigend nach date - so wie AnalyticsService.ForecastTimelineAsync sie wirklich liefert
      // (OrderBy(entry => entry.Date)). page.js verlaesst sich inzwischen selbst darauf (letztes Element
      // = spaetestes Datum statt eines eigenen Max-Scans), die Fixture muss diese Zusicherung also auch
      // einhalten, nicht nur zufaellig auf die gleiche Reihenfolge kommen.
      items: [
        { kind: 'contract', sourceId: 'k2', date: iso('2026-09-20'), label: 'Mobilfunk', subLabel: 'Telekom', amount: -29.99, currency: 'EUR', isEstimate: false, accountId: 'a1', categoryId: 'c1', categoryIconKey: null },
        { kind: 'income', sourceId: 'is1', date: iso('2026-09-27'), label: 'Gehalt', subLabel: null, amount: 2810.44, currency: 'EUR', isEstimate: false, accountId: 'a1', categoryId: null, categoryIconKey: null },
        { kind: 'budget-period', sourceId: 'b1', date: iso('2026-09-30'), label: 'Lebensmittel', subLabel: null, amount: 120.5, currency: 'EUR', isEstimate: false, accountId: null, categoryId: 'c1', categoryIconKey: null },
        { kind: 'contract', sourceId: 'k1', date: iso('2026-10-01'), label: 'Stromvertrag', subLabel: 'Stadtwerke', amount: -78.5, currency: 'EUR', isEstimate: true, accountId: 'a1', categoryId: 'c1', categoryIconKey: null },
        { kind: 'income', sourceId: 'is1', date: iso('2026-10-27'), label: 'Gehalt', subLabel: null, amount: null, currency: 'EUR', isEstimate: true, accountId: 'a1', categoryId: null, categoryIconKey: null }
      ]
    },
    // #131, Abschnitt 13: eine erkannte Luecke in der Buchungshistorie. Eigener Schluessel aus
    // demselben Grund wie bei 'transactions/forecast' - 'transactions' ist ein Objekt, ein kuerzerer
    // Treffer gaebe die ganze Buchungsliste zurueck. Nur a1 hat eine; a2 hat keine, damit beide
    // Zustaende in der Kontenliste nebeneinander zu sehen sind.
    'transactions/data-gaps': [
      { accountId: 'a1', from: iso('2026-07-01'), to: iso('2026-07-14'), days: 13 }
    ],
    // #124: die Sammlungen EINER Buchung - die Gegenrichtung zu 'collections'. Eigene Schluessel aus
    // demselben Grund wie bei 'transactions/forecast': 'transactions' ist ein Objekt, ein kuerzerer
    // Treffer wuerde also die ganze Buchungsliste zurueckgeben statt einer Id-Liste. t1 haengt an einer
    // Sammlung, t2 an zweien und t3 an keiner - damit sind im Buchungsdetail alle drei Zustaende der
    // Zusammenfassungszeile erreichbar, ohne etwas umzustellen.
    'transactions/t1/collections': ['col1'],
    'transactions/t2/collections': ['col1', 'col2'],
    'transactions/t3/collections': [],
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
    // Paperless: connected, with the tag/type/correspondent lists the filter builder offers and one
    // saved preset. Shapes follow PaperlessConnectionView / PaperlessFilterOptionsView /
    // PaperlessImportPresetView. Without these the Paperless step stayed at "connect" and everything
    // below it - the whole filter builder - was unreachable in the harness.
    'purchases/receipt-imports/paperless/connection': {
      fullWorthSpaceId: SPACE, baseUrl: 'https://paperless.example.local/', configured: true,
      defaultQuery: null, isEnabled: true, lastSyncAt: '2026-09-11T08:40:00Z', updatedAt: '2026-09-05T19:22:00Z'
    },
    'purchases/receipt-imports/paperless/options': {
      tags: [{ id: 3, name: 'Kassenbon' }, { id: 7, name: 'Rechnung' }, { id: 11, name: 'Garantie' }],
      documentTypes: [{ id: 1, name: 'Beleg' }, { id: 2, name: 'Vertrag' }],
      correspondents: [{ id: 4, name: 'REWE' }, { id: 5, name: 'Edeka' }, { id: 9, name: 'Amazon' }],
      storagePaths: [{ id: 1, name: 'Belege/2026' }],
      customFields: [{ id: 2, name: 'Betrag' }]
    },
    'purchases/receipt-imports/paperless/presets': [
      { id: 'pp1', fullWorthSpaceId: SPACE, userId: 'u1', name: 'Kassenbons',
        query: 'tag:3 AND correspondent:4', editorJson: null, autoImport: true, analyzeAutomatically: true,
        currency: 'EUR', lastSeenDocumentId: 1044, lastCheckedAt: '2026-09-11T08:40:00Z',
        lastImportedAt: '2026-09-10T18:02:00Z' }
    ],
    // The Budgets screen renders from analytics/budget-status, not from the budgets list - without this
    // key the screen was empty in the harness and its dialogs unreachable. Field names follow the real
    // records (BudgetStatusItem, BudgetPeriodStatus, BudgetView); a fixture that invents its own shape
    // is how a harness starts disagreeing with the API it stands in for.
    //
    // Three budgets, one per shape the dialog must handle: monthly (no dates), weekly (needs an anchor)
    // and a custom range (needs both, and both are required). One shared window, so the combined total
    // above the list is the honest case.
    'analytics/budget-status': {
      items: [
        { id: 'b1', name: 'Lebensmittel', categoryId: 'c1', period: 'monthly', groupId: 'bg1',
          periodStart: '2026-09-01', periodEnd: '2026-09-30',
          amount: 450, spent: 318.4, remaining: 131.6, percent: 70.8,
          baseAmount: 450, carryIn: 0, carryOver: false, carryOverOverspend: false },
        { id: 'b2', name: 'Wocheneinkauf', categoryId: 'c1', period: 'monthly', groupId: 'bg1',
          periodStart: '2026-09-01', periodEnd: '2026-09-30',
          amount: 90, spent: 84.2, remaining: 5.8, percent: 93.6,
          baseAmount: 80, carryIn: 10, carryOver: true, carryOverOverspend: false },
        { id: 'b3', name: 'Urlaubskasse', categoryId: null, period: 'monthly',
          periodStart: '2026-09-01', periodEnd: '2026-09-30',
          amount: 1200, spent: 1340, remaining: -140, percent: 111.7,
          baseAmount: 1200, carryIn: 0, carryOver: true, carryOverOverspend: true }
      ]
    },
    // Budget-Gruppen (#177). Vier Routen ohne Aufrufer, und ein groupId im Geltungsbereich, das
    // niemand setzen konnte. Zwei Gruppen, damit die Auswahl im Dialog mehr als einen Eintrag hat;
    // b3 bleibt ohne Gruppe, damit die Ueberschrift 'Ohne Gruppe' gleichzeitig auf dem Schirm steht.
    'budget-groups': [
      { id: 'bg1', name: 'Alltag', sortOrder: 100, isArchived: false },
      { id: 'bg2', name: 'Sparen', sortOrder: 200, isArchived: false }
    ],
    // #115: der Geltungsbereich eines Budgets. b1 hat einen gesetzten (zwei Kategorien, Unterkategorien
    // mitgezaehlt) - damit ist im Dialog der Zustand "Geltungsbereich ueberschreibt die Einzelkategorie"
    // erreichbar. b2 hat einen leeren, also den Normalfall. Eigene Schluessel, weil 'budgets' ein
    // anderer Pfad ist und match() hier nichts zurueckfallen laesst.
    //
    // b3 zeigt partialAccess: der Server hat beim Lesen Konten entfernt, die dieser Nutzer nicht sehen
    // darf. Das Gelesene ist dann NICHT das Gespeicherte, und ein Zurueckschreiben wuerde sie loeschen -
    // der Dialog muss den Bereich hier als unveraenderbar anzeigen.
    'budget-scopes/b1': {
      categories: [{ categoryId: 'c1', includeDescendants: true }, { categoryId: 'c2', includeDescendants: true }],
      accountIds: ['a1'], tagIds: [], merchants: [], incomeScheduleId: null,
      alertNearPercent: 80, alertCriticalPercent: 100, groupId: 'bg1', partialAccess: false
    },
    'budget-scopes/b2': {
      categories: [], accountIds: [], tagIds: [], merchants: [], incomeScheduleId: null,
      alertNearPercent: 80, alertCriticalPercent: 100, groupId: null, partialAccess: false
    },
    'budget-scopes/b3': {
      categories: [{ categoryId: 'c1', includeDescendants: false }], accountIds: [], tagIds: [], merchants: [],
      incomeScheduleId: null, alertNearPercent: 80, alertCriticalPercent: 100, groupId: null, partialAccess: true
    },
    'budgets': [
      { id: 'b1', name: 'Lebensmittel', categoryId: 'c1', amount: 450, currency: 'EUR', period: 'monthly',
        startDate: null, endDate: null, carryOver: false, carryOverOverspend: false, isActive: true },
      { id: 'b2', name: 'Wocheneinkauf', categoryId: 'c1', amount: 90, currency: 'EUR', period: 'weekly',
        startDate: '2026-09-07', endDate: null, carryOver: true, carryOverOverspend: false, isActive: true },
      { id: 'b3', name: 'Urlaubskasse', categoryId: null, amount: 1200, currency: 'EUR', period: 'custom',
        startDate: '2026-06-01', endDate: '2026-09-30', carryOver: true, carryOverOverspend: true, isActive: true }
    ],
    'budgets/b1': { id: 'b1', name: 'Lebensmittel', categoryId: 'c1', amount: 450, currency: 'EUR',
      period: 'monthly', startDate: null, endDate: null, carryOver: false, carryOverOverspend: false, isActive: true },
    'budgets/b2': { id: 'b2', name: 'Wocheneinkauf', categoryId: 'c1', amount: 90, currency: 'EUR',
      period: 'weekly', startDate: '2026-09-07', endDate: null, carryOver: true, carryOverOverspend: false, isActive: true },
    'budgets/b3': { id: 'b3', name: 'Urlaubskasse', categoryId: null, amount: 1200, currency: 'EUR',
      period: 'custom', startDate: '2026-06-01', endDate: '2026-09-30', carryOver: true, carryOverOverspend: true, isActive: true },
    // The detail drawer reads budgetId from here and hands it to the edit dialog, so this key is what
    // makes "Bearbeiten" reachable at all.
    'budgets/b1/status': {
      budgetId: 'b1', name: 'Lebensmittel', categoryId: 'c1', currency: 'EUR', period: 'monthly',
      periodStart: '2026-09-01', periodEnd: '2026-09-30',
      budgetAmount: 450, spent: 318.4, remaining: 131.6, percentUsed: 70.8,
      projectedEndSpend: 468.2, projectedOverUnder: 18.2, trend: 'Rising', partialAccess: false,
      baseBudgetAmount: 450, carryIn: 0, carryOver: false, carryOverOverspend: false,
      contributing: [
        { id: 't-b1-1', bookingDate: '2026-09-08', counterparty: 'Rewe', amount: -64.2, currency: 'EUR', category: 'Lebensmittel' },
        { id: 't-b1-2', bookingDate: '2026-09-03', counterparty: 'Edeka', amount: -41.9, currency: 'EUR', category: 'Lebensmittel' }
      ]
    },
    // b2 hatte als einziges Budget keine Status-Fixture. Das sah nicht nach einer Luecke aus, sondern
    // nach einem kaputten Bearbeiten-Knopf: ohne eigenen Schluessel beantwortet match() den Pfad mit
    // der ganzen 'budgets'-LISTE, budgetStatus.budgetId ist dann undefined, und das Detail schickt
    // anschliessend GET /api/budgets/undefined - ein leeres Formular ohne erkennbare Ursache.
    'budgets/b2/status': {
      budgetId: 'b2', name: 'Wocheneinkauf', categoryId: 'c1', currency: 'EUR', period: 'weekly',
      periodStart: '2026-09-14', periodEnd: '2026-09-20',
      budgetAmount: 90, spent: 52.3, remaining: 37.7, percentUsed: 58.1,
      projectedEndSpend: 84.6, projectedOverUnder: -5.4, trend: 'Flat', partialAccess: false,
      baseBudgetAmount: 90, carryIn: 0, carryOver: true, carryOverOverspend: false,
      contributing: [
        { id: 't-b2-1', bookingDate: '2026-09-16', counterparty: 'Aldi', amount: -52.3, currency: 'EUR', category: 'Lebensmittel' }
      ]
    },
    'budgets/b3/status': {
      budgetId: 'b3', name: 'Urlaubskasse', categoryId: null, currency: 'EUR', period: 'custom',
      periodStart: '2026-06-01', periodEnd: '2026-09-30',
      budgetAmount: 1200, spent: 1340, remaining: -140, percentUsed: 111.7,
      projectedEndSpend: 1340, projectedOverUnder: 140, trend: 'Flat', partialAccess: false,
      baseBudgetAmount: 1200, carryIn: 0, carryOver: true, carryOverOverspend: true,
      contributing: [
        { id: 't-b3-1', bookingDate: '2026-08-22', counterparty: 'Fluggesellschaft', amount: -820, currency: 'EUR', category: 'Reisen' }
      ]
    },
    // CancellationDetailsView. k1 is a contract mid-cancellation: notice given, deadline known, and a
    // sent timestamp that the dialog must show as history rather than as an editable field. k2 has
    // nothing filled in, which is what the endpoint returns for a contract nobody has touched - the
    // shape the dialog has to survive is 'every field null, status "none"'.
    // Die Liste, die die Vertragsseite beim Aufbau zieht. Ohne eigenen Schluessel beantwortet sie das
    // Array 'contracts' nach id - und 'cancellations' ist keine id, also kam still undefined zurueck
    // und die Kuendigungsspalte blieb im Harness leer, ohne dass etwas kaputt war.
    'contracts/cancellations': [
      { contractId: 'k1', minimumTermEnd: '2026-12-31', cancellationDeadline: '2026-09-30',
        cancellationStatus: 'planned', autoRenews: true, cancellationSentAt: null, cancellationConfirmedAt: null },
      { contractId: 'k2', minimumTermEnd: null, cancellationDeadline: null,
        cancellationStatus: 'none', autoRenews: false, cancellationSentAt: null, cancellationConfirmedAt: null }
    ],
    // #135: drei Faelle fuer die Fristen-Uebersicht. k2 ist dringend (unter 14 Tagen und damit
    // hervorgehoben), k1 liegt weiter weg, und der letzte Eintrag ist ueberfaellig - den muss die
    // Uebersicht weglassen, weil er die Frage "muss ich diese Woche etwas tun" nicht beantwortet.
    'contracts/cancellation-deadlines': [
      { id: 'k1', name: 'Stadtwerke Strom', deadline: '2026-09-30', days: 15, status: 'planned' },
      { id: 'k2', name: 'Mobilfunk Telekom', deadline: '2026-09-25', days: 4, status: 'none' },
      { id: 'k3', name: 'Alter Zeitungsabo', deadline: '2026-08-01', days: -51, status: 'none' }
    ],
    'contracts/k1/cancellation': {
      minimumTermEnd: '2026-12-31', noticePeriodValue: 3, noticePeriodUnit: 'months',
      renewalPeriodValue: 12, renewalPeriodUnit: 'months', autoRenews: true,
      cancellationDeadline: '2026-09-30', cancellationStatus: 'planned',
      customerNumber: 'KD-4711-2019', providerContact: 'kundenservice@stadtwerke.example\nTel. 0800 1234567',
      cancellationSentAt: null, cancellationConfirmedAt: null, updatedAt: '2026-09-05T09:12:00Z'
    },
    // Fuenf Zahlungen, damit das Detail den "Alle N Buchungen anzeigen"-Knopf ueberhaupt zeigt (er
    // erscheint erst ab mehr als vier) - genau der Weg in den Zahlungsdialog, in dem die
    // Verknuepfungen unten sichtbar werden. `id` ist die Transaktions-Id, so wie ContractPayment sie
    // wirklich traegt.
    'contracts/k1/activity': {
      contractId: 'k1', valueMode: 'automatic', amount: 78.5, currency: 'EUR', annualizedAmount: 942,
      nextExpected: iso('2026-10-01'), lastPayment: iso('2026-09-01'), paymentCount: 5, averageAmount: 78.5,
      payments: [
        { id: 'tk1', date: iso('2026-09-01'), amount: 78.5, currency: 'EUR' },
        { id: 'tk2', date: iso('2026-08-01'), amount: 78.5, currency: 'EUR' },
        { id: 'tk3', date: iso('2026-07-01'), amount: 74.9, currency: 'EUR' },
        { id: 'tk4', date: iso('2026-06-01'), amount: 74.9, currency: 'EUR' },
        { id: 'tk5', date: iso('2026-05-01'), amount: 74.9, currency: 'EUR' }
      ]
    },
    // #135: die Zahlungsverknuepfungen eines Vertrags. transactionId zeigt auf die ids aus
    // 'contracts/k1/activity' - nur zwei der Zahlungen sind verknuepft, die dritte nicht, damit im
    // Dialog beide Zustaende nebeneinander stehen (mit Herkunft + "Loesen" bzw. ohne).
    'contracts/k1/links': [
      { id: 'lnk1', transactionId: 'tk1', amount: 78.5, linkSource: 'detection', confidence: 0.94,
        date: iso('2026-09-01'), counterparty: 'Stadtwerke', transactionAmount: -78.5, currency: 'EUR' },
      { id: 'lnk2', transactionId: 'tk2', amount: 78.5, linkSource: 'manual', confidence: null,
        date: iso('2026-08-01'), counterparty: 'Stadtwerke', transactionAmount: -78.5, currency: 'EUR' }
    ],
    'contracts/k2/links': [],
    // Ein STRING als Fixture, kein Objekt: der Endpunkt liefert echtes text/plain (ein Brief ist kein
    // JSON-Dokument), und der Stub unten antwortet fuer Strings entsprechend. Waere das hier ein
    // Objekt, kaeme im Dialog JSON statt eines Briefs an - und der Fehler saehe nach einem
    // Frontend-Fehler aus, obwohl nur die Fixture die falsche Form haette.
    'contracts/k1/cancellation-letter':
      'Kündigung meines Vertrags\n\nAnbieter: Stadtwerke\nKunden-/Vertragsnummer: KD-4711-2019\n\n'
      + 'Hiermit kündige ich den oben genannten Vertrag fristgerecht zum nächstmöglichen Zeitpunkt '
      + 'unter Berücksichtigung der Kündigungsfrist (aktuelle Frist: 30.09.2026).\n'
      + 'Bitte bestätigen Sie mir die Kündigung sowie das Vertragsende schriftlich.\n',
    'contracts/k2/cancellation': {
      minimumTermEnd: null, noticePeriodValue: null, noticePeriodUnit: null,
      renewalPeriodValue: null, renewalPeriodUnit: null, autoRenews: false,
      cancellationDeadline: null, cancellationStatus: 'none',
      customerNumber: null, providerContact: null,
      cancellationSentAt: null, cancellationConfirmedAt: null, updatedAt: null
    },
    // Three import batches, one per state the card has to render: one running (so Pause is offered),
    // one paused (Fortsetzen, plus the paused marker, and its items back to pending), and one finished
    // with a failure (Fehler erneut, and a single receipt that can be analysed on its own).
    'purchases/receipt-imports/batches': [
      {
        batch: { id: 'rb1', fullWorthSpaceId: SPACE, userId: 'u1', sourceType: 'upload', sourceName: 'File upload',
          currency: 'EUR', status: 'processing', autoStart: true, createdAt: '2026-09-11T09:10:00Z',
          updatedAt: '2026-09-11T09:14:00Z', completedAt: null, pausedAt: null, isPaused: false },
        total: 4, queued: 2, processing: 1, completed: 1, needsReview: 0, skippedDuplicates: 0, failed: 0,
        items: [
          // #129: jobStage/jobEngine je Beleg. ri1 laeuft gerade (zeigt den Schritt), ri4 ist fertig
          // (zeigt, WOMIT verarbeitet wurde), und ri2/ri3 haben nichts davon - so wie jeder Beleg,
          // der noch nicht angefangen hat. Die beiden Engines sind bewusst verschieden, damit die
          // Unterscheidung KI/OCR im Harness sichtbar ist.
          { id: 'ri1', batchId: 'rb1', sourceType: 'upload', displayName: 'REWE 11.09.png', status: 'processing', receiptScanJobId: 'j1', purchaseId: 'p1', jobStage: 'ocr', jobEngine: 'tesseract' },
          { id: 'ri2', batchId: 'rb1', sourceType: 'upload', displayName: 'Edeka 10.09.png', status: 'queued', receiptScanJobId: 'j2', purchaseId: 'p2' },
          { id: 'ri3', batchId: 'rb1', sourceType: 'upload', displayName: 'Apotheke 09.09.png', status: 'queued', receiptScanJobId: 'j3', purchaseId: 'p3' },
          { id: 'ri4', batchId: 'rb1', sourceType: 'upload', displayName: 'Bahn 08.09.pdf', status: 'done', receiptScanJobId: 'j4', purchaseId: 'p4', jobStage: 'saving', jobEngine: 'codex' }
        ]
      },
      {
        batch: { id: 'rb2', fullWorthSpaceId: SPACE, userId: 'u1', sourceType: 'paperless', sourceName: 'Paperless-ngx',
          currency: 'EUR', status: 'processing', autoStart: true, createdAt: '2026-09-10T18:02:00Z',
          updatedAt: '2026-09-11T08:40:00Z', completedAt: null, pausedAt: '2026-09-11T08:40:00Z', isPaused: true },
        total: 3, queued: 3, processing: 0, completed: 0, needsReview: 0, skippedDuplicates: 0, failed: 0,
        items: [
          { id: 'ri5', batchId: 'rb2', sourceType: 'paperless', displayName: 'Beleg 1042', sourceReference: 'Dok. 1042', status: 'pending', receiptScanJobId: 'j5', purchaseId: 'p5' },
          { id: 'ri6', batchId: 'rb2', sourceType: 'paperless', displayName: 'Beleg 1043', sourceReference: 'Dok. 1043', status: 'pending', receiptScanJobId: 'j6', purchaseId: 'p6' },
          { id: 'ri7', batchId: 'rb2', sourceType: 'paperless', displayName: 'Beleg 1044', sourceReference: 'Dok. 1044', status: 'pending', receiptScanJobId: 'j7', purchaseId: 'p7' }
        ]
      },
      {
        batch: { id: 'rb3', fullWorthSpaceId: SPACE, userId: 'u1', sourceType: 'folder', sourceName: 'Import folder',
          currency: 'EUR', status: 'completed_with_errors', autoStart: false, createdAt: '2026-09-09T21:30:00Z',
          updatedAt: '2026-09-09T21:36:00Z', completedAt: '2026-09-09T21:36:00Z', pausedAt: null, isPaused: false },
        total: 2, queued: 0, processing: 0, completed: 1, needsReview: 0, skippedDuplicates: 0, failed: 1,
        items: [
          { id: 'ri8', batchId: 'rb3', sourceType: 'folder', displayName: 'scan-004.jpg', status: 'done', receiptScanJobId: 'j8', purchaseId: 'p8' },
          { id: 'ri9', batchId: 'rb3', sourceType: 'folder', displayName: 'scan-005.jpg', status: 'failed', receiptScanJobId: 'j9', purchaseId: 'p9',
            error: 'Die Seite konnte nicht gelesen werden.' }
        ]
      }
    ],
    // Recurring income: one confirmed schedule and two detections. Shapes follow the list projection in
    // CashflowParityEndpoints.ListSchedules and the IncomeCandidate record. The variable one has no
    // amount on purpose - that is a real case and must not print 0,00 EUR.
    'income-schedules': [
      { id: 'is1', name: 'Gehalt Aera GmbH', accountId: 'a1', normalizedCounterparty: 'aera gmbh',
        expectedAmount: 3200, currency: 'EUR', cycle: 'monthly', interval: 1,
        anchorDate: '2026-09-01', nextExpectedDate: '2026-10-01', valueMode: 'fixed',
        autoDetected: true, isActive: true },
      { id: 'is2', name: 'Nebentätigkeit', accountId: 'a1', normalizedCounterparty: null,
        expectedAmount: null, currency: 'EUR', cycle: 'monthly', interval: 1,
        anchorDate: null, nextExpectedDate: null, valueMode: 'average',
        autoDetected: false, isActive: true }
    ],
    'income-schedules/detection': [
      { accountId: 'a1', counterparty: 'Miete Untermieter', typicalAmount: 420, currency: 'EUR',
        cycle: 'monthly', nextExpectedDate: '2026-10-03', confidence: 0.91, occurrences: 11 },
      { accountId: 'a1', counterparty: 'Aera GmbH Bonus', typicalAmount: 1800, currency: 'EUR',
        cycle: 'yearly', nextExpectedDate: '2027-03-31', confidence: 0.72, occurrences: 3 }
    ],
    'notifications': [],
    // Overview widgets (#157/Scheibe 14 gap): DashboardResult (AnalyticsService.cs) drives net-worth,
    // available, income/expense and upcoming - without this key all four rendered their empty state.
    // Numbers agree with the other wealth/* fixtures above so the page does not contradict itself.
    // Das Flussdiagramm der freien Auswertung (#177). Bewusst MIT Rest und MIT fehlendem Kurs:
    // beide Hinweise der Ansicht sind sonst nicht zu sehen, und genau sie sind die Geldregeln -
    // ein fehlender Kurs macht die Summe unvollstaendig, und das muss dastehen.
    //
    // Die Zahlen gehen auf: 3200 Einnahmen, 1852,30 ausgegeben, 1347,70 bleiben stehen.
    'analytics/sankey': {
      currency: 'EUR', incomplete: true, reconciles: true,
      nodes: [
        { id: 'income', name: 'Einnahmen' }, { id: 'available', name: 'Verfügbar' },
        { id: 'cat-0', name: 'Wohnen' }, { id: 'cat-1', name: 'Lebensmittel' },
        { id: 'cat-2', name: 'Mobilität' }, { id: 'remaining', name: 'Bleibt übrig' }
      ],
      links: [
        { source: 'income', target: 'available', value: 3200 },
        { source: 'available', target: 'cat-0', value: 980 },
        { source: 'available', target: 'cat-1', value: 612.30 },
        { source: 'available', target: 'cat-2', value: 260 },
        { source: 'available', target: 'remaining', value: 1347.70 }
      ]
    },
    'analytics/dashboard': {
      currency: 'EUR', accounts: 21250.30, assets: 12000, liabilities: 5000, netWorth: 48250.30,
      income: 3200, expenses: 1852.30, incomplete: true,
      upcoming: [
        { name: 'Stromvertrag', providerName: 'Stadtwerke', nextDueDate: '2026-10-01', amount: 78.5, currency: 'EUR' },
        { name: 'Mobilfunk', providerName: 'Telekom', nextDueDate: '2026-09-20', amount: 29.99, currency: 'EUR' },
        { name: 'WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG', providerName: 'WEG AM KOENIGSTRAESSLE 1 5 VERTR D PPG', nextDueDate: '2026-10-01', amount: 182, currency: 'EUR' }
      ]
    },
    // Income/expense-by-period widget (only reached when a widget is configured with a period) and the
    // /analytics page itself. One incomplete month, so the fx-incomplete marker is reachable too.
    'analytics/overview': {
      currency: 'EUR', income: 3200, expenses: 1852.30, incomplete: false,
      byMonth: [
        { month: '2026-04', income: 3100, expenses: 1790 }, { month: '2026-05', income: 3100, expenses: 1705 },
        { month: '2026-06', income: 3200, expenses: 1988 }, { month: '2026-07', income: 3200, expenses: 1640 },
        { month: '2026-08', income: 3200, expenses: 1902 }, { month: '2026-09', income: 3200, expenses: 1852.30 }
      ]
    },
    // One EUR house and one huge IDR asset: the old ratio-of-native-values split put nearly all of
    // manualAssets into 'other', because 5.000.000 IDR dwarfs 9.000 EUR as a bare number.
    'assets': [
      { id: 'as1', name: 'Wohnung', kind: 'real_estate', currentValue: 9000, currency: 'EUR', includeInNetWorth: true },
      { id: 'as2', name: 'IDR Anlage', kind: 'other', currentValue: 5000000, currency: 'IDR', includeInNetWorth: true },
      // The asset the bAV contract below owns: one per contract, which is how the balance reaches net
      // worth without a manual link step.
      { id: 'as3', name: 'bAV Allianz', kind: 'insurance_pension', currentValue: 2000, currency: 'EUR', includeInNetWorth: true }
    ],
    'networth': { total: 20681.65, series: [], groups: [] },
    // Wealth view. The history rises by a flat 600 per month over exactly 12 months, so the
    // projection card's derived savings rate must come out at 600 - a value that is wrong by any
    // rounding mistake is visible immediately.
    // The forward preview's basis: 3 200 income, 1 040 of contracts marked as fixed costs, and a
    // 6-month average of 480 for everything else. Surplus 1 680 - which is what the composition rows
    // must add up to on screen, so a sign or a rounding mistake is visible immediately. The budget
    // limit is deliberately HIGHER than the observed average, because that is the interesting case:
    // it must sit beside the average and never replace it.
    'wealth/preview-basis': {
      currency: 'EUR',
      monthlyIncome: 3200,
      monthlyFixedCosts: 1040,
      monthlyVariableSpend: 480,
      monthlyBudgetLimit: 620,
      monthlySurplus: 1680,
      observedMonths: 6,
      isComplete: true,
      missingCurrencies: [],
      lines: [
        { kind: 'income', id: 'i1', name: 'Gehalt', monthlyAmount: 3200, isEstimate: false },
        { kind: 'fixed', id: 'c1', name: 'Miete Lindenhof', monthlyAmount: 880, isEstimate: false },
        { kind: 'fixed', id: 'c2', name: 'Internet & Telefon', monthlyAmount: 39.99, isEstimate: false },
        { kind: 'fixed', id: 'c3', name: 'Haftpflicht & Hausrat', monthlyAmount: 48.5, isEstimate: false },
        { kind: 'fixed', id: 'c4', name: 'Monatsticket', monthlyAmount: 49, isEstimate: false },
        { kind: 'fixed', id: 'c5', name: 'StreamNow', monthlyAmount: 14.99, isEstimate: false },
        { kind: 'fixed', id: 'c6', name: 'Mobilfunk', monthlyAmount: 7.52, isEstimate: false },
        { kind: 'variable', id: null, name: 'variable', monthlyAmount: 480, isEstimate: true }
      ]
    },
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
      realEstateAssets: { amount: 9000 },
      // The bAV balance, a second converted slice of the same manualAssets. It is also what the page
      // labels "davon gebunden" - there is no separate tied total to keep in sync here.
      pensionAssets: { amount: 2000, isComplete: true }
    },
    // Der Vermoegensverlauf mit den Reihen aus #178. Die ersten VIER Tage tragen die Aufteilung
    // bewusst nicht: so ist im Harness zu sehen, dass die Linien dort abbrechen statt auf null zu
    // fallen - genau der Unterschied zwischen "nicht festgehalten" und "null Euro".
    'wealth/history': [
      { date: '2025-09-09', currency: 'EUR', netWorth: 41050.30 },
      { date: '2025-10-09', currency: 'EUR', netWorth: 41650.30 },
      { date: '2025-11-09', currency: 'EUR', netWorth: 42250.30 },
      { date: '2025-12-09', currency: 'EUR', netWorth: 42850.30 },
      { date: '2026-01-09', currency: 'EUR', netWorth: 43450.30, accounts: 12480, investments: 10200, loans: 284800, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4360, pensionAssets: 18360, otherAssets: 6000, realEstateEquity: 15200 },
      { date: '2026-02-09', currency: 'EUR', netWorth: 44050.30, accounts: 12600, investments: 10500, loans: 284000, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4400, pensionAssets: 18450, otherAssets: 6000, realEstateEquity: 16000 },
      { date: '2026-03-09', currency: 'EUR', netWorth: 44650.30, accounts: 12720, investments: 10800, loans: 283200, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4440, pensionAssets: 18540, otherAssets: 6000, realEstateEquity: 16800 },
      { date: '2026-04-09', currency: 'EUR', netWorth: 45250.30, accounts: 12840, investments: 11100, loans: 282400, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4480, pensionAssets: 18630, otherAssets: 6000, realEstateEquity: 17600 },
      { date: '2026-05-09', currency: 'EUR', netWorth: 45850.30, accounts: 12960, investments: 11400, loans: 281600, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4520, pensionAssets: 18720, otherAssets: 6000, realEstateEquity: 18400 },
      { date: '2026-06-09', currency: 'EUR', netWorth: 46450.30, accounts: 13080, investments: 11700, loans: 280800, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4560, pensionAssets: 18810, otherAssets: 6000, realEstateEquity: 19200 },
      { date: '2026-07-09', currency: 'EUR', netWorth: 47050.30, accounts: 13200, investments: 12000, loans: 280000, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4600, pensionAssets: 18900, otherAssets: 6000, realEstateEquity: 20000 },
      { date: '2026-08-09', currency: 'EUR', netWorth: 47650.30, accounts: 13320, investments: 12300, loans: 279200, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4640, pensionAssets: 18990, otherAssets: 6000, realEstateEquity: 20800 },
      { date: '2026-09-09', currency: 'EUR', netWorth: 48250.30, accounts: 13440, investments: 12600, loans: 278400, otherLiabilities: 0, realEstateAssets: 300000, preciousMetalAssets: 4680, pensionAssets: 19080, otherAssets: 6000, realEstateEquity: 21600 }
    ],
    'wealth/booking-activity': [],
    // Gemerkte Auswertungen (#177). Sie lagen bis dahin in einem Einstellungs-Blob; das volle CRUD
    // unter /api/saved-analyses hatte keinen Aufrufer. Zwei Eintraege, einer mit relativem Zeitraum
    // und einer ohne - nur so ist im Harness zu sehen, dass 'period' die Wahl ueberlebt.
    'saved-analyses': [
      { id: 'sa1', name: 'Ausgaben nach Kategorie', schemaVersion: 1, createdAt: '2026-09-01T09:00:00Z',
        updatedAt: '2026-09-01T09:00:00Z',
        config: { query: { measure: 'spend', dimension: 'category', from: null, to: null }, chartType: 'donut', period: '1y' } },
      { id: 'sa2', name: 'Einnahmen je Monat', schemaVersion: 1, createdAt: '2026-08-11T09:00:00Z',
        updatedAt: '2026-08-11T09:00:00Z',
        config: { query: { measure: 'income', dimension: 'month', from: null, to: null }, chartType: 'bar', period: null } }
    ],
    'preferences/wealth.projection': { value: {} },
    'preferences/wealth.emergencyFund': { value: {} },
    'liabilities': [],
    // Das Depot des Girokontos a9. Drei Positionen tragen einen Einstand und damit einen Gewinn,
    // die vierte nicht: so sieht ein Depot aus, in das die Bank Bestaende liefert und der
    // Eigentuemer seine Kaeufe noch nicht eingetragen hat.
    // Merkliste (#135). Eine Liste, drei beobachtete Papiere - eines mit Zielkurs und Notiz, eines
    // ohne beides, und eines ohne bekannten Kurs: das ist der Fall, in dem die Zeile keine erfundene
    // Zahl zeigen darf.
    'investments/watchlists': [{ id: 'w1', name: 'Merkliste' }],
    'investments/watchlists/w1/items': [
      { securityId: 's1', name: 'MSCI World ETF', ticker: 'IWDA', assetType: 'etf',
        targetPrice: 95, notes: 'Nachkaufen unter 95', price: 102.4, priceDate: '2026-09-19', currency: 'EUR' },
      { securityId: 's2', name: 'Allianz SE', ticker: 'ALV', assetType: 'stock',
        targetPrice: null, notes: null, price: 288.1, priceDate: '2026-09-19', currency: 'EUR' },
      { securityId: 's3', name: 'Frisch notiert AG', ticker: 'NEU', assetType: 'stock',
        targetPrice: null, notes: null, price: null, priceDate: null, currency: 'EUR' }
    ],
    'investments/securities': [
      { id: 's1', name: 'MSCI World ETF', ticker: 'IWDA', assetType: 'etf' },
      { id: 's2', name: 'Allianz SE', ticker: 'ALV', assetType: 'stock' },
      { id: 's3', name: 'Frisch notiert AG', ticker: 'NEU', assetType: 'stock' },
      { id: 's4', name: 'Noch nicht beobachtet SE', ticker: 'FREI', assetType: 'stock' }
    ],
    'investments/portfolios': [
      { id: 'p1', name: 'Direkt-Depot', currency: 'EUR', accountId: 'a9', isArchived: false, isManual: false,
        totalValue: 3218.42, marketValue: 3218.42, positions: 4, costBasis: 2319.58, unrealizedResult: 174.39,
        gainIncomplete: true, incomplete: false }
    ],
    'investments/portfolios/p1/overview': {
      portfolio: { id: 'p1', name: 'Direkt-Depot', currency: 'EUR', accountId: 'a9' },
      asOf: '2026-09-17', marketValue: 3218.42, cash: 0, totalValue: 3218.42,
      realizedResult: 0, dividends: 0, incomplete: false,
      positions: [
        { securityId: 's1', name: 'ISHSIII-MSCI EM USD(ACC)', assetType: 'etf', quantity: 0.43761,
          costBasis: 21.06, price: 55.394, priceCurrency: 'EUR', priceDate: '2026-09-16',
          priceState: 'current', marketValue: 24.24, unrealizedResult: 3.18, costBasisIncomplete: false },
        { securityId: 's2', name: 'AMUNDI CORE MSCI WLD UE A', assetType: 'etf', quantity: 11.48992,
          costBasis: 1630.12, price: 158.215, priceCurrency: 'EUR', priceDate: '2026-09-16',
          priceState: 'current', marketValue: 1817.74, unrealizedResult: 187.62, costBasisIncomplete: false },
        { securityId: 's3', name: 'AIS-AMUN.STEUR600 U.ETF A', assetType: 'etf', quantity: 2.07808,
          costBasis: 668.40, price: 313.75, priceCurrency: 'EUR', priceDate: '2026-09-16',
          priceState: 'current', marketValue: 651.99, unrealizedResult: -16.41, costBasisIncomplete: false },
        { securityId: 's4', name: 'XTR.MSCI WORLD MOMENTUM', assetType: 'etf', quantity: 10,
          costBasis: null, price: 72.445, priceCurrency: 'EUR', priceDate: '2026-09-16',
          priceState: 'current', marketValue: 724.45, unrealizedResult: null, costBasisIncomplete: true }
      ],
      stalePrices: []
    },
    // Freigeschaltet, damit die Depot-Oberflaeche (investment-performance-ui.js) ihre
    // verwaltenden Knoepfe UND den neuen "Erkannte Kaeufe"-Tab zeigt - ohne das faehrt die Harness
    // sonst nur den Lesemodus des reichen Dialogs vor.
    'access/effective': { capabilities: { 'investments.manage': true } },
    // Der Performance-Tab (TWR/XIRR/Benchmark + Verlaufskurve). Ein Punkt traegt incomplete:true UND
    // der Wochentotal traegt seinerseits reasons - beide Warnkaesten (Kopf und Kurve) lassen sich so
    // ansehen, nicht nur behaupten.
    'investments/portfolios/p1/performance': {
      twr: 0.0812, xirr: 0.0745, benchmarkReturn: 0.0623, marketValue: 3218.42, currency: 'EUR',
      effectiveFrom: '2025-09-17', to: '2026-09-17', incomplete: true,
      reasons: ['Kurs fuer AIS-AMUN.STEUR600 U.ETF A ist 13 Tage alt.'],
      points: [
        { date: '2025-09-17', value: 2850.10, portfolioReturn: 0, benchmarkReturn: 0, incomplete: false },
        { date: '2025-12-17', value: 2960.40, portfolioReturn: 0.0387, benchmarkReturn: 0.0290, incomplete: false },
        { date: '2026-03-17', value: 3040.75, portfolioReturn: 0.0669, benchmarkReturn: 0.0410, incomplete: false },
        { date: '2026-06-17', value: 3125.90, portfolioReturn: 0.0968, benchmarkReturn: 0.0520, incomplete: true },
        { date: '2026-09-17', value: 3218.42, portfolioReturn: 0.1291, benchmarkReturn: 0.0623, incomplete: false }
      ]
    },
    // Erkannte Kaeufe (Teil B des Buchungs-Abgleichs): s5 ist ein neues Wertpapier, dessen zwei
    // Kontobuchungen beide sicher genug sind, um vorausgewaehlt zu werden; s2 haelt das Depot schon,
    // aber die dritte Buchung ist unsicher (keine erkennbare Stueckzahl) und braucht ein Auge darauf.
    'reconciliation/securities-bookings': {
      matches: [
        { transactionId: 'tx1', securityId: 's5', name: 'ISHARES CORE S&P 500 UCITS ETF', date: '2026-08-04', gross: 1500.00, currency: 'EUR', quantity: 3.021, quantityEstimated: false, confident: true },
        { transactionId: 'tx2', securityId: 's5', name: 'ISHARES CORE S&P 500 UCITS ETF', date: '2026-08-18', gross: 500.00, currency: 'EUR', quantity: 1.007, quantityEstimated: true, confident: true },
        { transactionId: 'tx3', securityId: 's2', name: 'AMUNDI CORE MSCI WLD UE A', date: '2026-07-22', gross: 300.00, currency: 'EUR', quantity: null, quantityEstimated: false, confident: false }
      ],
      summaries: [
        { securityId: 's5', name: 'ISHARES CORE S&P 500 UCITS ETF', q: 0, p: null, sumGross: 2000.00, sumQuantity: 4.028, matches: 2, confident: true },
        { securityId: 's2', name: 'AMUNDI CORE MSCI WLD UE A', q: 11.48992, p: 141.9, sumGross: 300.00, sumQuantity: null, matches: 1, confident: false }
      ]
    },
    'insights': [],

    // ---- Steuerassistent (#157/Scheibe 14 gap: the page never got past its own gate here) ----
    // Both toggles on, so the gate opens straight into the populated overview/review tabs.
    'tax/settings': {
      enabled: true, countryCode: 'DE', defaultTaxYear: 2026, automaticAnalysisEnabled: true,
      aiAnalysisEnabled: false, analyzeTransactions: true, analyzePurchases: true, analyzeDocuments: true,
      showTaxNotifications: true
    },
    'tax/profile/settings': { assistantEnabled: true },
    // Fixed year 2026 (today in this fixture set): the frontend keys year-scoped calls by segment, so
    // an arbitrary year would need its own key - see settings.defaultTaxYear above, which is why the
    // page lands on exactly this year on first render.
    'tax/years/2026/summary': { suggestedAmount: 842.30, confirmedAmount: 318.50, needsReviewCount: 2, needsDocumentCount: 1 },
    'tax/years/2026/review': {
      ready: false,
      checks: [
        { severity: 'warning', count: 2, message: 'Zwei Hinweise warten noch auf eine Entscheidung.' },
        { severity: 'warning', count: 1, message: 'Ein bestätigter Hinweis hat noch keinen Beleg.' },
        { severity: 'success', count: 1, message: 'Ein Hinweis ist bestätigt und vollständig.' }
      ]
    },
    // Four candidates covering the states the row and the badges branch on: confirmed with a document,
    // needing review, needing a document (and therefore eligible for the upload button), and one with
    // a reduced eligible share so the "82 % · X → Y" line renders too.
    'tax/candidates': [
      { id: 'tc1', sourceType: 'purchase', sourceTitle: 'Handwerkerrechnung Heizung', sourceDate: '2026-03-14',
        grossAmount: 420, eligiblePercentage: 100, eligibleAmount: 420, currency: 'EUR',
        taxCategoryName: 'Handwerkerleistungen', taxCategoryCode: 'craft', confidence: 0.91,
        status: 'confirmed', hasDocument: true, explanation: 'Lohnanteil einer Rechnung für Wartungsarbeiten an der Heizung.' },
      { id: 'tc2', sourceType: 'transaction', sourceTitle: 'Spende Kinderhilfswerk', sourceDate: '2026-05-02',
        grossAmount: 100, eligiblePercentage: 100, eligibleAmount: 100, currency: 'EUR',
        taxCategoryName: 'Spenden', taxCategoryCode: 'donation', confidence: 0.62,
        status: 'needs_review', hasDocument: false, explanation: 'Überweisung an eine als gemeinnützig bekannte Organisation.' },
      { id: 'tc3', sourceType: 'purchase_item', sourceTitle: 'Fachliteratur Steuerrecht', sourceDate: '2026-06-20',
        grossAmount: 64.90, eligiblePercentage: 100, eligibleAmount: 64.90, currency: 'EUR',
        taxCategoryName: 'Fortbildung', taxCategoryCode: 'training', confidence: 0.58,
        status: 'needs_document', hasDocument: false, explanation: 'Fachbuch, thematisch der beruflichen Fortbildung zuordenbar.' },
      { id: 'tc4', sourceType: 'purchase', sourceTitle: 'Home-Office Schreibtischstuhl', sourceDate: '2026-02-11',
        grossAmount: 249, eligiblePercentage: 82, eligibleAmount: 204.18, currency: 'EUR',
        taxCategoryName: 'Arbeitsmittel', taxCategoryCode: 'equipment', confidence: 0.77,
        status: 'confirmed', hasDocument: true, explanation: 'Überwiegend beruflich genutzt, privater Anteil abgezogen.' }
    ],

    // ---- KI & Intelligence (/settings/intelligence) ----
    //
    // Ohne diese Eintraege beantwortete die Harness /overview mit der allgemeinen leeren Liste, die
    // Seite las settings von einem Array, warf, und zeigte genau eine Zeile: die Plakette 'Fehler'.
    // 31 Pixel hoch. Gemessen wurde hier also nie die Seite, sondern ihr Fehlerzustand - und weil ein
    // Fehlerzustand nicht springt, stand /settings/intelligence in LayoutStabilityTests mit 0,000 da.
    // Genau diese leere Zustimmung findet PageContentVisibilityTests.
    //
    // Die Werte sind bewusst der eingeschaltete Fall mit Inhalt in jeder Liste: ein Zugang, ein
    // offener Vorschlag, ein Lauf, ein Audit-Ereignis. Der Leerzustand zeigt vier Saetze und misst
    // nichts.
    'intelligence/admin/overview': {
      settings: {
        enabled: true, provider: 'openai', credentialId: 'ai-cred-1', allowUserCredentials: false,
        defaultTextModel: 'gpt-5-mini', defaultVisionModel: 'gpt-5', dailyBudgetEur: 2, monthlyBudgetEur: 40,
        dailyScanEnabled: true, weeklyDeepScanEnabled: false, monthlyReviewEnabled: true,
        modules: ['categorization', 'receipts', 'coach'],
        availableModules: ['categorization', 'receipts', 'products', 'contracts', 'coach', 'collection-suggestions', 'logo-research', 'internet-research'],
        updatedAt: '2026-09-18T07:30:00Z'
      },
      providers: [
        { provider: 'openai', capabilities: 3, maximumInputBytes: 2097152, reportsUsage: true },
        { provider: 'codex', capabilities: 3, maximumInputBytes: 1048576, reportsUsage: false }
      ],
      credentialCount: 1, pendingSuggestions: 1, failedJobs: 0, todayCostEur: 0.12, monthlyCostEur: 3.48
    },
    'intelligence/admin/credentials': [
      { id: 'ai-cred-1', ownerUserId: null, provider: 'openai', name: 'Instanz-Schluessel',
        secretFingerprint: 'a1b2c3d4', createdAt: '2026-08-02T10:00:00Z', updatedAt: '2026-09-18T07:30:00Z',
        lastTestedAt: '2026-09-18T07:30:00Z', lastTestSucceeded: true }
    ],
    // Ein Vorschlag mit Begruendung und Beleg - das ist die Zeile, an der die Seite haengt: ohne sie
    // steht dort 'Keine offenen Vorschlaege' und die Annehmen/Ablehnen-Knoepfe gibt es gar nicht.
    'intelligence/admin/suggestions/pending': [
      { id: 'ai-sug-1', type: 'merchant-category', subjectId: 'BAUHAUS', confidence: 0.86,
        provider: 'openai', model: 'gpt-5-mini', createdAt: '2026-09-18T06:10:00Z',
        proposedPayloadJson: JSON.stringify({ categoryKey: 'home.hardware', direction: 'expense', evidenceSummary: 'Sieben Buchungen, alle Baumarkt-typisch.' }),
        evidenceJson: JSON.stringify({ occurrences: 7 }) }
    ],
    'intelligence/admin/runs': [
      { id: 'ai-run-1', jobType: 'daily-scan', status: 'succeeded', provider: 'openai', model: 'gpt-5-mini',
        startedAt: '2026-09-18T06:00:00Z', inputTokens: 4210, outputTokens: 318, estimatedCostEur: 0.12, actualCostEur: 0.12 }
    ],
    'intelligence/admin/audit': [
      { id: 'ai-aud-1', action: 'credential.tested', outcome: 'success', entityType: 'AiCredential',
        entityId: 'ai-cred-1', occurredAt: '2026-09-18T07:30:00Z' }
    ],
    // Der Cloud-Abschnitt derselben Seite. 'disabled' plus requiresSetupDecision:false ist der Fall,
    // den ein Selbst-Hoster ohne Cloud sieht - die Seite muss ihn zeigen koennen, ohne zu draengen.
    // Die Zusammenfassungen der geplanten Laeufe (#177). Zwei Zeitraeume, damit beide Etiketten
    // (Woche/Monat) auf dem Schirm stehen - und einer davon mit Kosten, denn genau die sind der
    // Grund, warum diese Seite sie zeigt.
    'intelligence/digests': [
      { id: 'dg1', fullWorthSpaceId: SPACE, periodType: 'monthly', periodKey: '2026-09',
        periodStart: '2026-09-01T00:00:00Z', periodEnd: '2026-10-01T00:00:00Z',
        createdAt: '2026-09-01T02:00:00Z', updatedAt: '2026-09-23T02:00:00Z',
        summary: { period: 'monthly', periodKey: '2026-09',
          suggestions: { created: 12, pending: 1, accepted: 9, rejected: 2, byType: { 'merchant-category': 12 } },
          learning: { feedbackEvents: 9, cloudEligible: 7 },
          ai: { runs: 24, succeeded: 23, failed: 1, estimatedOrActualCostEur: 3.48, inputItems: 480, outputItems: 96 },
          unresolved: { purchaseItems: 3, receipts: 1 } } },
      { id: 'dg2', fullWorthSpaceId: SPACE, periodType: 'weekly', periodKey: '2026-09-21',
        periodStart: '2026-09-21T00:00:00Z', periodEnd: '2026-09-28T00:00:00Z',
        createdAt: '2026-09-21T02:00:00Z', updatedAt: '2026-09-21T02:00:00Z',
        summary: { period: 'weekly', periodKey: '2026-09-21',
          suggestions: { created: 0, pending: 0, accepted: 0, rejected: 0, byType: {} },
          learning: { feedbackEvents: 0, cloudEligible: 0 },
          ai: { runs: 0, succeeded: 0, failed: 0, estimatedOrActualCostEur: 0, inputItems: 0, outputItems: 0 },
          unresolved: { purchaseItems: 0, receipts: 0 } } }
    ],
    'intelligence/admin/cloud': {
      mode: 'disabled', requiresSetupDecision: false, setupDecisionAt: '2026-08-02T10:00:00Z',
      cloudEndpoint: 'https://cloud.fullworth.de', currentPolicyVersion: '1', entitlementStatus: null,
      lastRegistrationAt: null, lastSubmissionAt: null, lastErrorCode: null,
      outbox: { waitingCount: 0, deadLetterCount: 0, oldestWaitingCreatedAt: null }
    },

    // ---- Coach (spending review dock + chat; #157/Scheibe 14 gap for the review/summary panel) ----
    // One conversation with one finished exchange, so the thread is not empty on first paint.
    'coach/conversations': [
      { id: 'conv1', title: 'Sparpotenzial im September', createdAt: '2026-09-15T08:00:00Z', updatedAt: '2026-09-17T09:00:00Z' }
    ],
    'coach/conversations/conv1': {
      id: 'conv1',
      messages: [
        { role: 'User', text: 'Wie sieht mein Sparpotenzial diesen Monat aus?', facts: [] },
        { role: 'Assistant', mode: 'Local',
          text: 'Du hast diesen Monat bisher 480 € für variable Ausgaben verwendet, rund 140 € weniger als im Schnitt der letzten sechs Monate.',
          facts: [{ label: 'Variable Ausgaben', value: '480 €' }, { label: 'Ø letzte 6 Monate', value: '620 €' }] }
      ]
    },
    'coach/models': {
      configured: true, provider: 'openai', defaultModel: 'gpt-5-mini',
      models: [{ id: 'gpt-5-mini', label: 'GPT-5 mini' }, { id: 'gpt-5', label: 'GPT-5' }]
    },
    'spending-reviews/summary': {
      reviewCoverage: 0.42, worthItScore: 0.68, positiveAmount: 620.50, negativeAmount: 184.20, currency: 'EUR',
      highSpendPositive: [{ label: 'Konzerttickets' }], negativeOpportunities: [{ label: 'Abo, kaum genutzt' }]
    },
    'spending-reviews/recent': [
      { transactionId: 't1', sentiment: 'Positive', reasons: ['good_value'], note: null },
      { transactionId: 't3', sentiment: 'Negative', reasons: ['impulse'], note: 'Spontankauf am Bahnhof' }
    ],

    // ---- Altersvorsorge (bAV) ----
    'pension/overview': {
      contractCount: 2, activeCount: 1, paidUpCount: 1,
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

  // A batch detail answers with exactly one element of the list, and its id sits at batch.id rather
  // than at the top level - so the generic "find by id" in match() cannot see it. Derived instead of
  // written twice: two copies of the same three batches would drift apart on the first edit.
  for (const view of FIXTURES['purchases/receipt-imports/batches'])
    FIXTURES[`purchases/receipt-imports/batches/${view.batch.id}`] = view;

  // openDetail() reads { contract, snapshots, contributions, costs, allocations }, a shape the list
  // fixture doesn't carry - without a dedicated key here match()'s generic "find by id" fallback
  // returns the bare list entry instead, and detail.contract is undefined. Derived from the list's own
  // currentSnapshot/currentContribution rather than duplicated, so the two can't drift apart.
  for (const contract of FIXTURES['pension/contracts']) {
    FIXTURES[`pension/contracts/${contract.id}`] = {
      contract,
      snapshots: contract.currentSnapshot ? [contract.currentSnapshot] : [],
      contributions: contract.currentContribution ? [contract.currentContribution] : [],
      costs: [],
      allocations: []
    };
  }
  // bav1 is the one meant to show a fully populated detail dialog: a cost row and two fund allocations.
  FIXTURES['pension/contracts/bav1'].costs = [
    { kind: 'administration_on_capital', basis: 'percent_of_capital', percent: 1.2, amount: null,
      currency: 'EUR', timing: 'ongoing', isEstimated: true,
      estimateBasis: 'Aus der letzten Standmitteilung abgeleitet.', continuesWhenPaidUp: true }
  ];
  FIXTURES['pension/contracts/bav1'].allocations = [
    { fundName: 'Allianz Global Aktienfonds', assetClass: 'equity', isin: 'DE0008474511',
      weightPercent: 65, amount: null, currency: 'EUR', ongoingChargesPercent: 1.1, ongoingChargesEstimated: false },
    { fundName: 'Allianz Rentenfonds', assetClass: 'bond', isin: 'DE0008474503',
      weightPercent: 35, amount: null, currency: 'EUR', ongoingChargesPercent: 0.6, ongoingChargesEstimated: true }
  ];

  const KEYS = Object.keys(FIXTURES);

  function match(pathname) {
    // "/bff/backend/api/transactions?x=1" -> "transactions"
    const after = pathname.replace(/^\/bff\/(backend|banking)\//, '').replace(/^api\//, '');
    // Longest key wins. Matching the first path segment first meant a list fixture shadowed every
    // detail below it: 'budgets/b3' was answered with the whole 'budgets' ARRAY, so the edit dialog
    // received an array where it expected one budget and rendered every field empty - a harness bug
    // that looks exactly like a broken dialog. It also left the pension fixtures depending on the
    // order they happen to be written in.
    const path = after.split('?')[0];
    const hit = KEYS
      .filter(key => path === key || path.startsWith(key + '/'))
      .sort((a, b) => b.length - a.length)[0];
    if (!hit) return undefined;
    const value = FIXTURES[hit];
    if (path === hit) return value;

    // A list fixture answers the details below it, by id. Without this, 'contracts/k1' was served the
    // whole 'contracts' ARRAY: the detail screen then read .id and .name off an array, got undefined,
    // and went on to call 'contracts/undefined' - a screen that looks merely empty while quietly
    // proving nothing. Only one segment deep; anything further (payments, status, ...) needs its own
    // key and falls through to the empty answer rather than to a wrong one.
    const rest = path.slice(hit.length + 1).split('/');
    if (Array.isArray(value) && rest.length === 1) {
      return value.find(item => String(item?.id) === rest[0]);
    }
    return value;
  }

  const COMPENSATION_RESULT = {
    name: 'Aktuelles Gehalt', monthsEmployedInYear: 4, salaryPaymentsInYear: 4,
    contractualGrossAnnual: 60000, bonusAnnual: 0, cashGrossAnnual: 20000,
    estimatedCashNetAnnual: 12800, estimatedCashNetMonthly: 3200,
    estimatedAverageCashNetMonthly: 3200, estimatedNetRatioPercent: 64,
    employerTotalCostAnnual: 23900, fullWorthCompensationValueAnnual: 13100,
    marginalNetFromNext100Gross: 51.4, effectiveNetValuePerWorkingHour: 18.46,
    taxes: { estimatedIncomeTaxAnnual: 2900, estimatedSolidaritySurchargeAnnual: 0, estimatedChurchTaxAnnual: 0 },
    socialInsurance: { pensionAnnual: 1860, unemploymentAnnual: 260, healthAnnual: 1580, careAnnual: 600 },
    benefits: [],
    // Beide sind im Vertrag NICHT optional (CompanyCarAnalysis / OccupationalPensionAnalysis sind
    // keine Nullable-Felder). Ein null hier hätte eine Fehlerquelle vorgetäuscht, die es nicht gibt.
    companyCar: { taxableBenefitMonthly: 0, taxableBenefitAnnual: 0, employeeContributionAnnual: 0, employerCostAnnual: 0, privateAlternativeValueAnnual: 0, estimatedNetCashImpactAnnual: 0, estimatedEffectivePersonalValueAnnual: 0 },
    occupationalPension: { employeeContributionAnnual: 0, employerContributionAnnual: 0, taxExemptEmployeeContributionAnnual: 0, socialInsuranceExemptEmployeeContributionAnnual: 0, estimatedCurrentNetSacrificeAnnual: 0, totalInvestedAnnual: 0, benefitEfficiency: 0, projectedValue: 0 },
    assumptions: ['Harness-Fixture, keine echte Berechnung.']
  };

  // Two pension-document writes have to answer with something real rather than { ok: true }: the
  // upload returns the detail the review screen renders (so the screen is reachable at all), and the
  // commit returns applied/skipped (so the honest "was already there" half can be looked at).
  // A file whose name contains "doppelt" answers 409 the way the real upload does, so the conflict
  // path (offer the document that already holds those pages, never a dead error) can be walked.
  function writeAnswer(method, pathname, init) {
    const after = pathname.replace(/^\/bff\/(backend|banking)\//, '').replace(/^api\//, '');
    if (after.startsWith('compensation/calculate')) return { status: 200, body: COMPENSATION_RESULT };
    // #177: das Flussdiagramm ist ein POST, weil die Auswahl ein ganzer Filter ist - geschrieben
    // wird nichts. Ohne eigene Antwort bekaeme die Ansicht den allgemeinen Schreib-Echo ({id:'stub'})
    // und zeigte "keine Fluesse", was im Harness wie ein Fehler der Seite aussieht.
    if (after.startsWith('analytics/sankey')) return { status: 200, body: FIXTURES['analytics/sankey'] };
    // #177: die Sicherungspruefung ist ein POST mit der ZIP im Rumpf. Ohne eigene Antwort bekaeme
    // der Dialog den allgemeinen Schreib-Echo ({id:'stub'}) - valid waere undefined, und er zeigte
    // 'nicht vollstaendig' ohne einen einzigen Grund. Hier der interessantere Fall: gueltig, aber
    // mit einer Warnung, denn genau die geht sonst beim Zeichnen unter.
    if (after.startsWith('import/wealth-backup/validate')) return { status: 200, body: {
      valid: true, fullWorthSpaceId: SPACE, schemaVersion: 1, errors: [],
      warnings: ['Backup manifest does not contain asset-document metadata.'], documentsChecked: 0
    } };
    // #115: der Vorschlag ist ein POST, weil die Auswahl eine Kategorienliste ist - geschrieben wird
    // nichts. Ohne eigene Antwort bekaeme die Seite den allgemeinen Schreib-Echo ({id:'stub'}) und
    // zeigte einen Vorschlag aus undefined-Werten. Die Zahlen wachsen mit der Anzahl gewaehlter
    // Kategorien, damit im Harness sichtbar ist, dass der Vorschlag dem Geltungsbereich folgt und
    // nicht mehr der einen Kategorie.
    // #131: der Schritt NACH der Erkennung. Ohne eigene Antwort bekam die Spaltenzuordnung den
    // allgemeinen Schreib-Echo ({id:'stub'}) und brach mit "headers is not iterable" ab - der Weg vom
    // Waehlen der Datei bis zur Vorschau liess sich im Harness also nie am Stueck ansehen.
    // Der Upload eines Kontoauszugs. Ein Dateiname mit "unsicher" liefert den Auszug, der nicht aufgeht.
    if (after.startsWith('import-jobs/upload')) {
      const uploaded = init?.body instanceof FormData ? init.body.get('file') : null;
      const unsure = /unsicher/i.test(uploaded?.name || '');
      return { status: 200, body: {
        jobId: unsure ? STATEMENT_JOB_UNSURE : STATEMENT_JOB, fileName: uploaded?.name || 'abrechnung.pdf',
        adapter: 'ikano_pdf', sourceRows: 7, ready: 7, errors: 0,
        statementAccount: 'Ikano •••• 1234',
        warnings: unsure ? ['not_reconciled'] : [],
        statementBalance: { amount: -1050, currency: 'EUR', asOf: iso('2026-09-23') }
      } };
    }
    if (after.startsWith('import-mapping/upload'))
      return { status: 200, body: { jobId: MAPPING_JOB, sourceRows: 15, ready: 15, errors: 0 } };
    if (/^import-mapping\/jobs\/[^/]+\/duplicate-preview/.test(after))
      return { status: 200, body: { candidates: [], duplicates: 0, unmapped: 0, fresh: 15 } };
    if (after.startsWith('import-mapping/detect')) {
      return { status: 200, body: {
        fileName: 'umsaetze.csv',
        headers: ['Buchungstag', 'Betrag', 'Währung', 'Empfänger', 'Verwendungszweck', 'Konto', 'Kategorie'],
        suggestedMapping: { date: 'Buchungstag', amount: 'Betrag', currency: 'Währung', counterparty: 'Empfänger', description: 'Verwendungszweck', account: 'Konto', category: 'Kategorie', externalKey: null },
        preview: [
          { Buchungstag: '10.09.2026', Betrag: '-42,19', 'Währung': 'EUR', 'Empfänger': 'REWE Markt GmbH', Verwendungszweck: 'Einkauf', Konto: 'Girokonto', Kategorie: 'Lebensmittel' },
          { Buchungstag: '09.09.2026', Betrag: '-9,99', 'Währung': 'EUR', 'Empfänger': 'Spotify', Verwendungszweck: 'Abo', Konto: 'Girokonto', Kategorie: 'Abos' },
          { Buchungstag: '28.08.2026', Betrag: '2810,44', 'Währung': 'EUR', 'Empfänger': 'Arbeitgeber AG', Verwendungszweck: 'Gehalt August', Konto: 'Girokonto', Kategorie: 'Gehalt' }
        ],
        rowCount: 48
      } };
    }
    // #131: die Quellenerkennung. Ohne eigene Antwort bekaeme die Seite den allgemeinen Schreib-Echo
    // ({id:'stub'}) und zeigte "Unbekannt" fuer jede Datei - das Verhalten, das gerade NICHT gemeint
    // ist, liesse sich also nicht ansehen. Geantwortet wird nach der Endung: das ist nicht, was der
    // Server tut (er liest den Inhalt), aber es ist ehrlich das, was eine Fixture kann, und es macht
    // jeden der fuenf Wege im Harness erreichbar.
    // #131, Schritt 4: der Zwischenschritt des Finanzguru-Imports. Ohne eigene Antwort bekaeme die
    // Seite den allgemeinen Schreib-Echo ({id:'stub'}) und zeigte eine Vorschau aus lauter
    // undefined - der Weg Datei -> Vorschau -> Zeilenauswahl -> Uebernehmen liesse sich also nirgends
    // am Stueck ansehen, und genau er ist das Neue an dieser Seite.
    if (after.startsWith('import/finanzguru/stage')) return { status: 200, body: {
      jobId: FINANZGURU_STAGE_JOB, sourceRows: 312, newRows: 3, alreadyImported: 268, matchedExisting: 41, enrichedExisting: 12,
      from: iso('2024-01-01'), to: iso('2026-09-13'),
      accounts: [
        { sourceKey: 'DE02…2051', displayName: 'C24 Girokonto', accountId: 'a1', accountName: 'Girokonto', status: 'linked', rows: 212, retargetable: true, currency: 'EUR' },
        { sourceKey: 'paypal', displayName: 'PayPal', accountId: 'a4', accountName: 'PayPal', status: 'import', rows: 74, retargetable: false, currency: 'EUR' },
        { sourceKey: 'DE44…9912', displayName: 'DKB Girokonto', accountId: null, accountName: null, status: 'new', rows: 26, retargetable: true, currency: 'EUR' }
      ]
    } };
    // Das Neurechnen nach einer Zielwahl (#131, Abschnitt 4). Mit einem Ziel fuer das DKB-Konto werden
    // aus dessen neuen Zeilen Treffer - genau der Effekt, der zeigt, dass die Vorschau neu rechnet
    // und nicht nur vormerkt.
    if (/^import\/finanzguru\/jobs\/[^/]+\/targets/.test(after)) {
      let body = init?.body;
      if (typeof body === 'string') { try { body = JSON.parse(body); } catch { body = null; } }
      const dkb = body?.accountTargets?.['DE44…9912'] || null;
      // Das Haushaltskonto fuehrt Buchungen dieser Quelle schon - so ist die Ablehnung im Harness
      // erreichbar, und mit ihr die Frage, auf welche Wahl die Seite danach zurueckfaellt.
      if (dkb === 'a1') return { status: 409, body: {
        error: 'Bookings of this source were already imported into another account. Choosing a different target would count them twice.' } };
      const names = { a1: 'Haushaltskonto', a2: 'Tagesgeld mit langem Namen' };
      return { status: 200, body: {
        jobId: FINANZGURU_STAGE_JOB, sourceRows: 312,
        newRows: dkb ? 1 : 3, alreadyImported: 268, matchedExisting: dkb ? 43 : 41, enrichedExisting: dkb ? 13 : 12,
        from: iso('2024-01-01'), to: iso('2026-09-13'),
        accounts: [
          { sourceKey: 'DE02…2051', displayName: 'C24 Girokonto', accountId: 'a1', accountName: 'Girokonto', status: 'linked', rows: 212, retargetable: true, currency: 'EUR' },
          { sourceKey: 'paypal', displayName: 'PayPal', accountId: 'a4', accountName: 'PayPal', status: 'import', rows: 74, retargetable: false, currency: 'EUR' },
          { sourceKey: 'DE44…9912', displayName: 'DKB Girokonto', accountId: dkb, accountName: dkb ? names[dkb] : null, status: dkb ? 'linked' : 'new', rows: 26, retargetable: true, currency: 'EUR' }
        ]
      } };
    }
    if (/^import\/finanzguru\/jobs\/[^/]+\/commit/.test(after)) {
      let body = init?.body;
      if (typeof body === 'string') { try { body = JSON.parse(body); } catch { body = null; } }
      // Die Auswahl entscheidet, wie viele Zeilen ankommen - ohne das zeigte das Ergebnis dieselbe
      // Zahl, egal was der Nutzer gerade abgewaehlt hat, und die Zeilenauswahl waere im Harness
      // nicht zu pruefen.
      const chosen = Array.isArray(body?.candidateIds) ? body.candidateIds.length : 3;
      return { status: 200, body: {
        sourceRows: 312, transactionsImported: chosen, alreadyImported: 268, matchedExistingTransactions: 41, enrichedExistingTransactions: 12,
        accountsMatched: 2, accountsCreated: 1, categoriesCreated: 0, categoriesMatched: 3,
        categoriesUnmapped: 0, splitTransactions: 1
      } };
    }
    if (after.startsWith('import/detect')) {
      const uploaded = init?.body instanceof FormData ? init.body.get('file') : null;
      const fileName = uploaded && typeof uploaded.name === 'string' ? uploaded.name : 'datei.csv';
      const extension = fileName.slice(fileName.lastIndexOf('.')).toLowerCase();
      const detected =
        extension === '.pdf' && /kontoauszug|ikano|abrechnung/i.test(fileName)
          ? { adapter: 'statement', confidence: 'certain', reasonKey: 'ikano_pdf', rows: 7, accounts: ['Ikano •••• 1234'], from: iso('2026-09-01'), to: iso('2026-09-23') }
        : extension === '.pdf' ? { adapter: 'broker-pdf', confidence: 'likely', reasonKey: 'pdf', rows: 0, accounts: [] }
        : extension === '.xlsx' ? { adapter: 'finanzguru', confidence: 'certain', reasonKey: 'finanzguruHeaders', rows: 312, accounts: ['C24 Girokonto', 'PayPal', 'DKB Girokonto'], from: iso('2024-01-01'), to: iso('2026-09-13') }
        : ['.xml', '.sta', '.mt940', '.940', '.camt', '.txt'].includes(extension) ? { adapter: 'statement', confidence: 'certain', reasonKey: 'camt', rows: 24, accounts: ['DE02120300000000202051'], from: iso('2026-08-01'), to: iso('2026-08-31') }
        : extension === '.csv' ? { adapter: 'transactions', confidence: 'likely', reasonKey: 'tabularColumns', rows: 48, accounts: [], headers: ['Buchungstag', 'Betrag', 'Empfänger'], suggestedMapping: { date: 'Buchungstag', amount: 'Betrag', counterparty: 'Empfänger' } }
        : { adapter: 'unknown', confidence: 'unknown', reasonKey: 'unreadable', rows: 0, accounts: [] };
      return { status: 200, body: { from: null, to: null, headers: [], suggestedMapping: null, ...detected, fileName } };
    }
    if (after.startsWith('budget-suggestions')) {
      let body = init?.body;
      if (typeof body === 'string') { try { body = JSON.parse(body); } catch { body = null; } }
      const count = Math.max(1, (body?.categories || []).length);
      return { status: 200, body: {
        cadence: 'Monthly',
        options: [
          { cadence: 'Weekly', confidence: 0.31, completePeriods: 12, activeShare: 0.58, variation: 0.44 },
          { cadence: 'Monthly', confidence: 0.82, completePeriods: 6, activeShare: 0.95, variation: 0.18 },
          { cadence: 'Quarterly', confidence: 0.4, completePeriods: 2, activeShare: 1, variation: 0.12 },
          { cadence: 'Yearly', confidence: 0.1, completePeriods: 0, activeShare: 0, variation: 0 }
        ],
        confidence: 0.82, weakData: false,
        average: 318.4 * count, suggested: 340 * count,
        periodsUsed: 6, outliersDamped: 1, currency: 'EUR',
        matchingTransactions: 47 * count, incomeDates: [], expectedNextIncome: null
      } };
    }
    if (after.startsWith('pension/projection')) {
      let body = init?.body;
      if (typeof body === 'string') { try { body = JSON.parse(body); } catch { body = null; } }
      return /^pension\/projection\/compare(\?|$)/.test(after)
        ? { status: 200, body: pensionComparison(body) }
        : { status: 200, body: pensionProjection(body) };
    }
    // Der FinTS-Ablauf ist seit #133 zweigeteilt: anmelden und melden, was da ist - danach erst
    // uebernehmen. Beide Antworten haben dieselbe Form, die Uebernahme fuellt zusaetzlich 'imported'.
    if (after.startsWith('banking/fints/')) {
      const discovered = FIXTURES['banking/fints/connections/c3/accounts'].discovered;
      if (after.endsWith('/import')) {
        let body = init?.body;
        if (typeof body === 'string') { try { body = JSON.parse(body); } catch { body = null; } }
        const hidden = Array.isArray(body?.hidden) ? body.hidden : [];
        // Ein Abruf, der mittendrin abbricht, ist kein Randfall: genau der ist gemeldet worden - vier
        // Konten angelegt, das Depot nicht, und die Antwort meldete trotzdem eine glatte Zahl. Das
        // Depot abzuwaehlen loest ihn hier aus, damit die unvollstaendige Antwort ansehbar bleibt.
        const broke = hidden.includes('k4');
        return { status: 200, body: {
          connectionId: 'c3', status: 'AUTHORIZED', challenge: null,
          discovered: discovered.map(a => ({
            ...a,
            visible: !hidden.includes(a.key),
            accountId: broke && a.kind === 'depot' ? null : 'acc-' + a.key
          })),
          imported: broke
            ? {
                accounts: discovered.filter(a => a.kind !== 'depot').length,
                depots: 0,
                hidden: hidden.length,
                missing: discovered.filter(a => a.kind === 'depot').length,
                error: 'FINTS_BANK_ERROR'
              }
            : {
                accounts: discovered.filter(a => a.kind !== 'depot').length,
                depots: discovered.filter(a => a.kind === 'depot').length,
                hidden: hidden.length,
                missing: 0,
                error: null
              }
        } };
      }
      if (after.endsWith('ing/connect'))
        return { status: 200, body: FIXTURES['banking/fints/connections/c3/accounts'] };
      return undefined;
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


  // #161: eine Vergangenheit, die laenger ist als eine Seite - ohne sie liesse sich das Nachladen im
  // Harness nicht ansehen und nicht messen. Absteigend nach Datum, direkt im Anschluss an die sechs
  // von Hand geschriebenen Zeilen oben, damit die Fixture in einer Reihenfolge steht und nicht in zwei.
  const TRANSACTION_HISTORY = (() => {
    const kinds = [
      ['REWE Markt GmbH', 'Einkauf', 'c1', -34.71],
      ['Stadtwerke', 'Abschlag Strom', 'c1', -78.5],
      ['Deutsche Bahn', 'Fahrkarte', 'c2', -19.9],
      ['Apotheke am Markt', 'Rezept', 'c2', -12.35],
      ['Arbeitgeber AG', 'Gehalt', 'c3', 2810.44],
      ['Tankstelle Nord', 'Kartenzahlung', 'c1', -62.18],
      ['Buchhandlung Lesezeit', 'Buch', 'c2', -24.0]
    ];
    const rows = [];
    const day = new Date('2026-08-27T12:00:00Z');
    for (let index = 0; index < 240; index++) {
      const [counterparty, description, categoryId, base] = kinds[index % kinds.length];
      rows.push({
        id: 'th' + index,
        bookingDate: day.toISOString().slice(0, 10),
        amount: Math.round((base - (index % 11) * 1.13) * 100) / 100,
        currency: 'EUR', counterparty, description, categoryId, accountId: 'a1',
        status: 'BOOK', isSplit: false
      });
      day.setUTCDate(day.getUTCDate() - 1);
    }
    return rows;
  })();

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
    let body = method === 'GET' ? (match(url.pathname) ?? []) : (write?.body ?? { id: 'stub', ok: true });
    // #131, Abschnitt 6: mit gewaehltem Zielkonto steht die Moebelhaus-Zahlung dort schon, so wie Finanzguru
    // sie benannt hat - die Zeile kommt als 'vermutlich vorhanden'. Wie bei der Blaetterung unten liest die
    // Fixture hier ausnahmsweise einen Parameter, weil match() nur den Pfad kennt.
    if (method === 'GET' && url.pathname.endsWith(`import-jobs/${STATEMENT_JOB}/candidates`) && url.searchParams.get('accountId')) {
      body = statementRows(null).map(row => row.amount === -120 && row.reviewNote === null
        ? { ...row, duplicate: 'probable', duplicateOf: { counterparty: 'Möbelhaus Beispiel GmbH', bookingDate: iso('2026-09-11') } }
        : row);
    }
    // #161: Blaetterung ist der eine Teil der Abfrage, den diese Fixture nicht ignorieren darf.
    // match() beantwortet nur den Pfad, und jeder andere Parameter (Filter, Bereich) laesst sich hier
    // gefahrlos uebergehen - die Buchungsliste ist klein genug, um ungefiltert zu zeigen, was gemeint
    // ist. "limit" und "after" nicht: die Seite laedt inzwischen seitenweise nach, und eine Fixture,
    // die immer dieselben sechs Zeilen zurueckgibt, wuerde das Nachladen zwar ausloesen, aber nie
    // etwas anhaengen - nicht kaputt, nur nicht vorfuehrbar. Genauso wichtig ist, dass die Liste
    // laenger als eine Seite ist: sonst kaeme der Ankerpunkt gar nicht erst ins Spiel.
    //
    // Der Cursor ist hier die Id der zuletzt gelieferten Zeile. Fuer die Seite ist er ohnehin
    // undurchsichtig (sie reicht ihn nur zurueck), und einen echten Sortierschluessel nachzubauen
    // hiesse, die Regel des Servers ein zweites Mal aufzuschreiben.
    if (method === 'GET' && /\/api\/transactions$/.test(url.pathname) && Array.isArray(body?.items)) {
      const all = [...body.items, ...TRANSACTION_HISTORY];
      const after = url.searchParams.get('after');
      const limit = Math.max(1, Number(url.searchParams.get('limit')) || 60);
      const start = after ? all.findIndex(item => item.id === after) + 1 : 0;
      const page = all.slice(start, start + limit);
      const hasNext = start + limit < all.length;
      body = {
        ...body,
        items: page,
        total: after ? null : all.length,
        hasNext,
        nextCursor: hasNext && page.length ? page[page.length - 1].id : null
      };
    }
    // #139 Teil 3 (Nachladen): die Fixture selbst kennt keinen Horizont, sie liefert immer dieselben
    // fuenf Eintraege bis 2026-10-27. Ohne diesen Zusatz wuerde "Weiter laden" im Harness zwar feuern,
    // aber sichtbar nichts anhaengen - nicht kaputt, nur nicht vorfuehrbar. horizonDays=180 haengt zwei
    // weitere, um einen Monatszyklus verschobene Eintraege an (derselbe Vertrag/dieselbe Einnahme wie
    // oben, naechste Faelligkeit), damit das Verhalten sich tatsaechlich beobachten laesst.
    if (method === 'GET' && /\/api\/transactions\/forecast$/.test(url.pathname) && url.searchParams.get('horizonDays') === '180' && Array.isArray(body?.items)) {
      body = { ...body, items: [...body.items,
        { kind: 'contract', sourceId: 'k1', date: iso('2026-11-01'), label: 'Stromvertrag', subLabel: 'Stadtwerke', amount: -78.5, currency: 'EUR', isEstimate: true, accountId: 'a1', categoryId: 'c1', categoryIconKey: null },
        { kind: 'income', sourceId: 'is1', date: iso('2026-11-27'), label: 'Gehalt', subLabel: null, amount: null, currency: 'EUR', isEstimate: true, accountId: 'a1', categoryId: null, categoryIconKey: null }
      ] };
    }
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
    // Eine Antwort, die sofort da ist, macht jeden Wartezustand unsichtbar. Die FinTS-Uebernahme
    // dauert echt 20 bis 60 Sekunden, und genau ihr Zwischenschritt (Titel, Hinweis, Skelettzeilen)
    // laesst sich sonst nicht ansehen - eine Dreiviertelsekunde reicht, um ihn zu pruefen.
    if (url.pathname.endsWith('/import')) await new Promise(done => setTimeout(done, 750));
    // Ein Endpunkt, der ein Dokument liefert, liefert kein JSON. Das Kuendigungsschreiben (#135) ist
    // serverseitig Results.Text(..., "text/plain") - als JSON verpackt kaeme im Dialog ein in
    // Anfuehrungszeichen gesetzter Text mit \n-Literalen an. Eine String-Fixture bedeutet hier also
    // genau das, was sie beim echten Server bedeutet: roher Text.
    if (typeof body === 'string')
      return new Response(body, { status, headers: { 'content-type': 'text/plain; charset=utf-8' } });
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
  };

  // The app remembers the active space locally; pre-set it so no picker blocks the first render.
  try { localStorage.setItem('fullworth.spaceId', SPACE); } catch { /* ignore */ }
  window.__harnessReady = true;
})();
