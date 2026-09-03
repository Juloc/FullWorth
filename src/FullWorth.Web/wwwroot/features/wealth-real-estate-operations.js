const COPY = {
  de: {
    rental: 'Miete', costs: 'Kosten', renovations: 'Renovierungen',
    contractualRent: 'Vertragsmiete p.a.', actualRent: 'Tatsächliche Miete', noi: 'NOI', grossYield: 'Bruttorendite', netYield: 'Nettomietrendite', cashflowBeforeTax: 'Cashflow vor Steuern',
    units: 'Einheiten', mainUnit: 'Haupteinheit', addMainUnit: 'Haupteinheit anlegen', active: 'Aktiv', inactive: 'Inaktiv', ownerOccupied: 'Selbst genutzt',
    leases: 'Mietverträge', addLease: 'Mietvertrag hinzufügen', tenant: 'Mieter-Bezeichnung', start: 'Beginn', end: 'Ende', status: 'Status', coldRent: 'Kaltmiete', utilities: 'Nebenkosten-Vorauszahlung', otherCharges: 'Weitere laufende Kosten', warmRent: 'Warmmiete', deposit: 'Kaution', depositHeld: 'Kaution hinterlegt', cycle: 'Zahlungsrhythmus', endLease: 'Mietvertrag beenden',
    plannedOnly: 'Mietvertrag = Planwert. Als Ist-Einnahme zählt nur eine zugeordnete Zahlung.',
    operatingCosts: 'Nicht umlagefähige Ist-Kosten', debtPayments: 'Kreditzahlungen', cashflows: 'Ist-/Plan-Cashflows', addCashflow: 'Cashflow hinzufügen', transaction: 'Bankbuchung', manual: 'Manuell', planned: 'Geplant', bankLinked: 'Bankbuchung', bankHint: 'Bei Bankbuchungen übernimmt FullWorth Datum, Währung und Richtung aus der echten Buchung.',
    type: 'Typ', amount: 'Betrag', direction: 'Richtung', currency: 'Währung', date: 'Datum', income: 'Einnahme', expense: 'Ausgabe', remove: 'Entfernen',
    recurringCosts: 'Verknüpfte laufende Verträge', addContract: 'Vertrag verknüpfen', role: 'Rolle', noContracts: 'Keine laufenden Verträge verknüpft.',
    improvements: 'Renovierungen & Verbesserungen', addImprovement: 'Renovierung hinzufügen', title: 'Titel', category: 'Kategorie', completed: 'Abgeschlossen', cost: 'Kosten', estimatedAdded: 'Geschätzter Mehrwert', estimatedWarning: 'Geschätzter Mehrwert ist nur informativ und verändert den Immobilienwert nicht automatisch.', description: 'Beschreibung', linkCashflow: 'Ausgabe verknüpfen', linkedCashflows: 'Verknüpfte Ausgaben',
    noUnits: 'Noch keine Einheit vorhanden.', noLeases: 'Keine Mietverträge vorhanden.', noCashflows: 'Keine Cashflows vorhanden.', noImprovements: 'Keine Renovierungen vorhanden.',
    monthly: 'Monatlich', quarterly: 'Quartalsweise', yearly: 'Jährlich', weekly: 'Wöchentlich', activeLease: 'Aktiv', plannedLease: 'Geplant', endedLease: 'Beendet',
    rental_income: 'Mieteinnahme', operating_expense: 'Betriebskosten', capex: 'Investition / Renovierung', debt_payment: 'Kreditzahlung', tax: 'Steuer', insurance: 'Versicherung', fee: 'Gebühr', other: 'Sonstiges', incomeType: 'Sonstige Einnahme',
    apartment: 'Wohnung', commercial: 'Gewerbe', parking: 'Stellplatz', storage: 'Lager',
    hoa: 'Hausgeld', property_tax: 'Grundsteuer', utilitiesRole: 'Versorger', maintenance_plan: 'Wartung',
    windows: 'Fenster', roof: 'Dach', heating: 'Heizung', insulation: 'Dämmung', electrical: 'Elektrik', plumbing: 'Sanitär', bathroom: 'Bad', kitchen: 'Küche', flooring: 'Boden', facade: 'Fassade', solar: 'Solar', structural: 'Bausubstanz',
    save: 'Speichern', link: 'Verknüpfen', notAvailable: 'N/A'
  },
  en: {
    rental: 'Rent', costs: 'Costs', renovations: 'Renovations',
    contractualRent: 'Contract rent p.a.', actualRent: 'Actual rent', noi: 'NOI', grossYield: 'Gross yield', netYield: 'Net rental yield', cashflowBeforeTax: 'Cashflow before tax',
    units: 'Units', mainUnit: 'Main unit', addMainUnit: 'Create main unit', active: 'Active', inactive: 'Inactive', ownerOccupied: 'Owner occupied',
    leases: 'Leases', addLease: 'Add lease', tenant: 'Tenant label', start: 'Start', end: 'End', status: 'Status', coldRent: 'Cold rent', utilities: 'Utilities advance', otherCharges: 'Other recurring charges', warmRent: 'Warm rent', deposit: 'Deposit', depositHeld: 'Deposit held', cycle: 'Payment cycle', endLease: 'End lease',
    plannedOnly: 'A lease is a planned contract value. Only linked actual payments count as received rent.',
    operatingCosts: 'Actual non-recoverable costs', debtPayments: 'Debt payments', cashflows: 'Actual/planned cashflows', addCashflow: 'Add cashflow', transaction: 'Bank transaction', manual: 'Manual', planned: 'Planned', bankLinked: 'Bank transaction', bankHint: 'For linked bank transactions FullWorth uses the real transaction date, currency and direction.',
    type: 'Type', amount: 'Amount', direction: 'Direction', currency: 'Currency', date: 'Date', income: 'Income', expense: 'Expense', remove: 'Remove',
    recurringCosts: 'Linked recurring contracts', addContract: 'Link contract', role: 'Role', noContracts: 'No recurring contracts linked.',
    improvements: 'Renovations & improvements', addImprovement: 'Add improvement', title: 'Title', category: 'Category', completed: 'Completed', cost: 'Cost', estimatedAdded: 'Estimated value added', estimatedWarning: 'Estimated value added is informational and never changes property market value automatically.', description: 'Description', linkCashflow: 'Link expense', linkedCashflows: 'Linked expenses',
    noUnits: 'No unit available.', noLeases: 'No leases available.', noCashflows: 'No cashflows available.', noImprovements: 'No improvements available.',
    monthly: 'Monthly', quarterly: 'Quarterly', yearly: 'Yearly', weekly: 'Weekly', activeLease: 'Active', plannedLease: 'Planned', endedLease: 'Ended',
    rental_income: 'Rental income', operating_expense: 'Operating expense', capex: 'Capex / renovation', debt_payment: 'Debt payment', tax: 'Tax', insurance: 'Insurance', fee: 'Fee', other: 'Other', incomeType: 'Other income',
    apartment: 'Apartment', commercial: 'Commercial', parking: 'Parking', storage: 'Storage',
    hoa: 'HOA', property_tax: 'Property tax', utilitiesRole: 'Utilities', maintenance_plan: 'Maintenance',
    windows: 'Windows', roof: 'Roof', heating: 'Heating', insulation: 'Insulation', electrical: 'Electrical', plumbing: 'Plumbing', bathroom: 'Bathroom', kitchen: 'Kitchen', flooring: 'Flooring', facade: 'Facade', solar: 'Solar', structural: 'Structural',
    save: 'Save', link: 'Link', notAvailable: 'N/A'
  }
};

const CASHFLOW_TYPES = ['rental_income','income','operating_expense','capex','debt_payment','tax','insurance','fee','other'];
const EXPENSE_TYPES = new Set(['operating_expense','capex','debt_payment','tax','insurance','fee']);
const CONTRACT_ROLES = ['hoa','property_tax','insurance','utilities','maintenance_plan','other'];
const IMPROVEMENT_CATEGORIES = ['windows','roof','heating','insulation','electrical','plumbing','bathroom','kitchen','flooring','facade','solar','structural','other'];

function tr(key) {
  const lang = (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? 'de' : 'en';
  return COPY[lang][key] || key;
}

function ensureStyles() {
  if (document.querySelector('link[data-wealth-real-estate-operations-css]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/wealth-real-estate-operations.css';
  link.dataset.wealthRealEstateOperationsCss = '1';
  document.head.appendChild(link);
}

export async function attachRealEstateOperations(ctx, dlg, asset, onChanged) {
  if (!dlg || dlg.dataset.propertyOperationsAttached === '1') return;
  dlg.dataset.propertyOperationsAttached = '1';
  ensureStyles();

  const id = asset.id;
  let data;
  try {
    const [property, metrics, units, leases, cashflows, improvements, contractLinks, contracts, transactionResult] = await Promise.all([
      ctx.api(`api/assets/${id}/real-estate`),
      ctx.api(`api/assets/${id}/real-estate/metrics`),
      ctx.api(`api/assets/${id}/real-estate/units`),
      ctx.api(`api/assets/${id}/real-estate/leases`),
      ctx.api(`api/assets/${id}/cashflows`),
      ctx.api(`api/assets/${id}/real-estate/improvements`),
      ctx.api(`api/assets/${id}/recurring-contracts`),
      ctx.api('api/contracts').catch(() => []),
      ctx.api('api/transactions?limit=200&sort=date&order=desc').catch(() => ({ items: [] }))
    ]);
    data = { property, metrics, units, leases, cashflows, improvements, contractLinks, contracts, transactions: transactionResult?.items || [] };
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  injectTabs(ctx, dlg, data, asset);
  wireRental(ctx, dlg, data, asset, () => changedAndClose(dlg, onChanged));
  wireCosts(ctx, dlg, data, asset, () => changedAndClose(dlg, onChanged));
  wireRenovations(ctx, dlg, data, asset, () => changedAndClose(dlg, onChanged));
}

function injectTabs(ctx, dlg, data, asset) {
  const tabs = dlg.querySelector('.property-tabs');
  if (!tabs) return;
  const historyButton = tabs.querySelector('[data-tab="history"]');
  for (const [name, label] of [['rental', tr('rental')], ['costs', tr('costs')], ['renovations', tr('renovations')]]) {
    const button = document.createElement('button');
    button.type = 'button';
    button.role = 'tab';
    button.dataset.tab = name;
    button.setAttribute('aria-selected', 'false');
    button.textContent = label;
    tabs.insertBefore(button, historyButton || null);
    button.addEventListener('click', () => selectTab(dlg, name));
  }

  const card = dlg.querySelector('.property-dialog');
  const historyPane = dlg.querySelector('[data-pane="history"]');
  const panes = [
    makePane('rental', rentalHtml(ctx, data, asset)),
    makePane('costs', costsHtml(ctx, data, asset)),
    makePane('renovations', renovationsHtml(ctx, data, asset))
  ];
  for (const pane of panes) card?.insertBefore(pane, historyPane || null);
}

function makePane(name, html) {
  const section = document.createElement('section');
  section.className = 'property-pane';
  section.dataset.pane = name;
  section.hidden = true;
  section.innerHTML = html;
  return section;
}

function selectTab(dlg, name) {
  dlg.querySelectorAll('[data-tab]').forEach(button => button.setAttribute('aria-selected', button.dataset.tab === name ? 'true' : 'false'));
  dlg.querySelectorAll('[data-pane]').forEach(pane => { pane.hidden = pane.dataset.pane !== name; });
}

function rentalHtml(ctx, data, asset) {
  const m = data.metrics;
  const money = value => value == null ? tr('notAvailable') : ctx.money(value, m.currency || asset.currency);
  const percent = value => value == null ? tr('notAvailable') : `${(Number(value) * 100).toFixed(2)} %`;
  const activeUnits = data.units.filter(unit => unit.isActive);
  return `<div class="property-metrics property-operation-metrics">
      ${metric(tr('contractualRent'), money(m.annualColdRent))}
      ${metric(tr('actualRent'), money(m.actualRent))}
      ${metric(tr('noi'), money(m.netOperatingIncome), m.netOperatingIncome)}
      ${metric(tr('grossYield'), percent(m.grossYield))}
      ${metric(tr('netYield'), percent(m.netRentalYield))}
      ${metric(tr('cashflowBeforeTax'), money(m.cashflowBeforeTax), m.cashflowBeforeTax)}
    </div>
    <div class="property-operation-note">${ctx.esc(tr('plannedOnly'))}</div>
    <div class="property-section"><div class="property-operation-head"><h3>${ctx.esc(tr('units'))}</h3></div>${unitListHtml(ctx, data.units)}${data.units.length ? '' : mainUnitFormHtml(ctx, data.property.detail, asset)}</div>
    <div class="property-section"><div class="property-operation-head"><h3>${ctx.esc(tr('leases'))}</h3></div>${leaseListHtml(ctx, data.leases)}${activeUnits.length ? leaseFormHtml(ctx, activeUnits, asset.currency) : ''}</div>`;
}

function unitListHtml(ctx, units) {
  if (!units.length) return `<div class="row-sub">${ctx.esc(tr('noUnits'))}</div>`;
  return `<div class="property-operation-list">${units.map(unit => `<div class="property-operation-row">
    <div><strong>${ctx.esc(unit.name)}</strong><div class="row-sub">${ctx.esc(tr(unit.unitType))}${unit.areaSqm != null ? ` · ${ctx.esc(String(unit.areaSqm))} m²` : ''}${unit.rooms != null ? ` · ${ctx.esc(String(unit.rooms))} Zimmer` : ''}${unit.isOwnerOccupied ? ` · ${ctx.esc(tr('ownerOccupied'))}` : ''}</div></div>
    <span class="tx-marker">${ctx.esc(tr(unit.isActive ? 'active' : 'inactive'))}</span>
  </div>`).join('')}</div>`;
}

function mainUnitFormHtml(ctx, detail, asset) {
  const ownerOccupied = detail?.usageType === 'owner_occupied';
  return `<form data-main-unit-form class="property-operation-form">
    <input type="hidden" name="name" value="${ctx.esc(detail?.unitLabel || tr('mainUnit'))}">
    <input type="hidden" name="areaSqm" value="${detail?.livingAreaSqm ?? ''}">
    <input type="hidden" name="rooms" value="${detail?.rooms ?? ''}">
    <input type="hidden" name="ownershipSharePercent" value="${detail?.ownershipSharePercent ?? 100}">
    <input type="hidden" name="isOwnerOccupied" value="${ownerOccupied ? 'true' : 'false'}">
    <button type="submit">${ctx.esc(tr('addMainUnit'))}</button>
  </form>`;
}

function leaseListHtml(ctx, leases) {
  if (!leases.length) return `<div class="row-sub">${ctx.esc(tr('noLeases'))}</div>`;
  return `<div class="property-operation-list">${leases.map(lease => `<div class="property-operation-row">
    <div><strong>${ctx.esc(lease.unitName)}${lease.tenantDisplayLabel ? ` · ${ctx.esc(ctx.isPrivate() ? '••••••' : lease.tenantDisplayLabel)}` : ''}</strong>
      <div class="row-sub">${ctx.esc(tr(lease.status === 'active' ? 'activeLease' : lease.status === 'planned' ? 'plannedLease' : 'endedLease'))} · ${ctx.esc(String(lease.startDate))}${lease.endDate ? ` – ${ctx.esc(String(lease.endDate))}` : ''}</div>
      <div class="row-sub">${ctx.esc(tr('coldRent'))}: ${ctx.money(lease.coldRent, lease.currency)} · ${ctx.esc(tr('warmRent'))}: ${ctx.money(lease.warmRent, lease.currency)}</div>
    </div>
    ${lease.status !== 'ended' ? `<button type="button" data-end-lease="${lease.id}">${ctx.esc(tr('endLease'))}</button>` : ''}
  </div>`).join('')}</div>`;
}

function leaseFormHtml(ctx, units, currency) {
  return `<form data-lease-form class="property-operation-form property-form-grid">
    <label>Einheit<select name="propertyUnitId" required>${units.map(unit => `<option value="${unit.id}">${ctx.esc(unit.name)}</option>`).join('')}</select></label>
    <label>${ctx.esc(tr('tenant'))}<input name="tenantDisplayLabel" maxlength="160"></label>
    <label>${ctx.esc(tr('start'))}<input name="startDate" type="date" required></label>
    <label>${ctx.esc(tr('end'))}<input name="endDate" type="date"></label>
    <label>${ctx.esc(tr('status'))}<select name="status"><option value="active">${ctx.esc(tr('activeLease'))}</option><option value="planned">${ctx.esc(tr('plannedLease'))}</option></select></label>
    <label>${ctx.esc(tr('coldRent'))}<input name="coldRent" type="number" min="0" step="0.01" required></label>
    <label>${ctx.esc(tr('utilities'))}<input name="utilitiesAdvance" type="number" min="0" step="0.01"></label>
    <label>${ctx.esc(tr('otherCharges'))}<input name="otherRecurringCharges" type="number" min="0" step="0.01"></label>
    <label>${ctx.esc(tr('currency'))}<input name="currency" maxlength="3" value="${ctx.esc(currency || 'EUR')}" required></label>
    <label>${ctx.esc(tr('cycle'))}<select name="paymentCycle"><option value="monthly">${ctx.esc(tr('monthly'))}</option><option value="quarterly">${ctx.esc(tr('quarterly'))}</option><option value="yearly">${ctx.esc(tr('yearly'))}</option><option value="weekly">${ctx.esc(tr('weekly'))}</option></select></label>
    <label>${ctx.esc(tr('deposit'))}<input name="depositAmount" type="number" min="0" step="0.01"></label>
    <label class="check"><input name="depositHeld" type="checkbox"> ${ctx.esc(tr('depositHeld'))}</label>
    <div class="dialog-actions wide"><button type="submit">${ctx.esc(tr('addLease'))}</button></div>
  </form>`;
}

function costsHtml(ctx, data, asset) {
  const m = data.metrics;
  const linkedIds = new Set(data.contractLinks.map(link => link.recurringContractId));
  const availableContracts = (data.contracts || []).filter(contract => !linkedIds.has(contract.id));
  return `<div class="property-metrics property-operation-metrics">
      ${metric(tr('operatingCosts'), m.nonRecoverableOperatingCosts == null ? tr('notAvailable') : ctx.money(m.nonRecoverableOperatingCosts, m.currency || asset.currency), m.nonRecoverableOperatingCosts)}
      ${metric(tr('debtPayments'), m.debtPayments == null ? tr('notAvailable') : ctx.money(m.debtPayments, m.currency || asset.currency), -Number(m.debtPayments || 0))}
      ${metric(tr('cashflowBeforeTax'), m.cashflowBeforeTax == null ? tr('notAvailable') : ctx.money(m.cashflowBeforeTax, m.currency || asset.currency), m.cashflowBeforeTax)}
    </div>
    <div class="property-section"><h3>${ctx.esc(tr('cashflows'))}</h3>${cashflowListHtml(ctx, data.cashflows)}${cashflowFormHtml(ctx, data.transactions, asset.currency)}</div>
    <div class="property-section"><h3>${ctx.esc(tr('recurringCosts'))}</h3>${contractListHtml(ctx, data.contractLinks)}${availableContracts.length ? contractFormHtml(ctx, availableContracts) : ''}</div>`;
}

function cashflowListHtml(ctx, cashflows) {
  if (!cashflows.length) return `<div class="row-sub">${ctx.esc(tr('noCashflows'))}</div>`;
  return `<div class="property-operation-list">${cashflows.map(item => `<div class="property-operation-row">
    <div><strong>${ctx.esc(cashflowTypeLabel(item.type))}</strong><div class="row-sub">${ctx.esc(String(item.date))} · ${ctx.esc(item.transactionId ? tr('bankLinked') : tr('manual'))}${item.isPlanned ? ` · ${ctx.esc(tr('planned'))}` : ''}${item.transactionCounterparty && !ctx.isPrivate() ? ` · ${ctx.esc(item.transactionCounterparty)}` : ''}</div></div>
    <div class="property-operation-actions"><span class="amount ${item.direction === 'income' ? 'positive' : 'negative'}">${item.direction === 'income' ? '+' : '−'}${ctx.money(item.amount, item.currency)}</span><button type="button" data-delete-cashflow="${item.id}">${ctx.esc(tr('remove'))}</button></div>
  </div>`).join('')}</div>`;
}

function cashflowFormHtml(ctx, transactions, currency) {
  return `<form data-cashflow-form class="property-operation-form property-form-grid">
    <label>${ctx.esc(tr('type'))}<select name="type">${CASHFLOW_TYPES.map(type => `<option value="${type}">${ctx.esc(cashflowTypeLabel(type))}</option>`).join('')}</select></label>
    <label>${ctx.esc(tr('amount'))}<input name="amount" type="number" min="0.01" step="0.01" required></label>
    <label>${ctx.esc(tr('direction'))}<select name="direction"><option value="income">${ctx.esc(tr('income'))}</option><option value="expense">${ctx.esc(tr('expense'))}</option></select></label>
    <label>${ctx.esc(tr('currency'))}<input name="currency" maxlength="3" value="${ctx.esc(currency || 'EUR')}" required></label>
    <label>${ctx.esc(tr('date'))}<input name="date" type="date" value="${todayValue()}"></label>
    <label>${ctx.esc(tr('transaction'))}<select name="transactionId"><option value="">${ctx.esc(tr('manual'))}</option>${transactions.map(tx => `<option value="${tx.id}">${ctx.esc(String(tx.bookingDate || tx.valueDate || ''))} · ${ctx.esc(ctx.isPrivate() ? '••••••' : (tx.counterparty || tx.description || ''))} · ${ctx.money(tx.amount, tx.currency)}</option>`).join('')}</select></label>
    <label class="check"><input name="isPlanned" type="checkbox"> ${ctx.esc(tr('planned'))}</label>
    <div class="property-operation-note wide">${ctx.esc(tr('bankHint'))}</div>
    <div class="dialog-actions wide"><button type="submit">${ctx.esc(tr('addCashflow'))}</button></div>
  </form>`;
}

function contractListHtml(ctx, links) {
  if (!links.length) return `<div class="row-sub">${ctx.esc(tr('noContracts'))}</div>`;
  return `<div class="property-operation-list">${links.map(link => `<div class="property-operation-row">
    <div><strong>${ctx.esc(link.contractName)}</strong><div class="row-sub">${ctx.esc(contractRoleLabel(link.role))} · ${ctx.esc(link.billingCycle)}${link.nextDueDate ? ` · ${ctx.esc(String(link.nextDueDate))}` : ''}</div></div>
    <div class="property-operation-actions"><span class="amount">${ctx.money(link.amount, link.currency)}</span><button type="button" data-delete-contract="${link.recurringContractId}">${ctx.esc(tr('remove'))}</button></div>
  </div>`).join('')}</div>`;
}

function contractFormHtml(ctx, contracts) {
  return `<form data-contract-form class="property-operation-form property-form-grid two">
    <label>Vertrag<select name="recurringContractId" required>${contracts.map(contract => `<option value="${contract.id}">${ctx.esc(contract.name)} · ${ctx.money(contract.amount, contract.currency)}</option>`).join('')}</select></label>
    <label>${ctx.esc(tr('role'))}<select name="role">${CONTRACT_ROLES.map(role => `<option value="${role}">${ctx.esc(contractRoleLabel(role))}</option>`).join('')}</select></label>
    <div class="dialog-actions wide"><button type="submit">${ctx.esc(tr('addContract'))}</button></div>
  </form>`;
}

function renovationsHtml(ctx, data, asset) {
  return `<div class="property-operation-note">${ctx.esc(tr('estimatedWarning'))}</div>
    <div class="property-section"><h3>${ctx.esc(tr('improvements'))}</h3>${improvementListHtml(ctx, data.improvements, data.cashflows)}${improvementFormHtml(ctx, asset.currency)}</div>`;
}

function improvementListHtml(ctx, improvements, cashflows) {
  if (!improvements.length) return `<div class="row-sub">${ctx.esc(tr('noImprovements'))}</div>`;
  const expenseCashflows = cashflows.filter(item => !item.isPlanned && item.direction === 'expense');
  return `<div class="property-operation-list">${improvements.map(item => {
    const linked = new Set(item.cashflowEntryIds || []);
    const available = expenseCashflows.filter(flow => !linked.has(flow.id));
    return `<div class="property-operation-card">
      <div class="property-operation-row"><div><strong>${ctx.esc(item.title)}</strong><div class="row-sub">${ctx.esc(tr(item.category))}${item.startDate ? ` · ${ctx.esc(String(item.startDate))}` : ''}${item.completedDate ? ` – ${ctx.esc(String(item.completedDate))}` : ''}</div></div><button type="button" data-delete-improvement="${item.id}">${ctx.esc(tr('remove'))}</button></div>
      <div class="property-operation-facts">${item.cost != null ? `<span>${ctx.esc(tr('cost'))}: <strong>${ctx.money(item.cost, item.currency || 'EUR')}</strong></span>` : ''}${item.estimatedValueAdded != null ? `<span>${ctx.esc(tr('estimatedAdded'))}: <strong>${ctx.money(item.estimatedValueAdded, item.currency || 'EUR')}</strong></span>` : ''}<span>${ctx.esc(tr('linkedCashflows'))}: <strong>${(item.cashflowEntryIds || []).length}</strong></span></div>
      ${item.description ? `<div class="row-sub">${ctx.esc(item.description)}</div>` : ''}
      ${available.length ? `<form data-improvement-link-form="${item.id}" class="property-operation-inline"><select name="cashflowEntryId">${available.map(flow => `<option value="${flow.id}">${ctx.esc(String(flow.date))} · ${ctx.esc(cashflowTypeLabel(flow.type))} · ${ctx.money(flow.amount, flow.currency)}</option>`).join('')}</select><button type="submit">${ctx.esc(tr('linkCashflow'))}</button></form>` : ''}
    </div>`;
  }).join('')}</div>`;
}

function improvementFormHtml(ctx, currency) {
  return `<form data-improvement-form class="property-operation-form property-form-grid">
    <label>${ctx.esc(tr('title'))}<input name="title" maxlength="200" required></label>
    <label>${ctx.esc(tr('category'))}<select name="category">${IMPROVEMENT_CATEGORIES.map(category => `<option value="${category}">${ctx.esc(tr(category))}</option>`).join('')}</select></label>
    <label>${ctx.esc(tr('start'))}<input name="startDate" type="date"></label>
    <label>${ctx.esc(tr('completed'))}<input name="completedDate" type="date"></label>
    <label>${ctx.esc(tr('cost'))}<input name="cost" type="number" min="0" step="0.01"></label>
    <label>${ctx.esc(tr('currency'))}<input name="currency" maxlength="3" value="${ctx.esc(currency || 'EUR')}"></label>
    <label>${ctx.esc(tr('estimatedAdded'))}<input name="estimatedValueAdded" type="number" min="0" step="0.01"></label>
    <label class="wide">${ctx.esc(tr('description'))}<textarea name="description" maxlength="4000" rows="3"></textarea></label>
    <div class="dialog-actions wide"><button type="submit">${ctx.esc(tr('addImprovement'))}</button></div>
  </form>`;
}

function wireRental(ctx, dlg, data, asset, changed) {
  const main = dlg.querySelector('[data-main-unit-form]');
  if (main) main.onsubmit = async event => {
    event.preventDefault();
    const fd = new FormData(main);
    await mutate(ctx, `api/assets/${asset.id}/real-estate/units`, {
      name: fd.get('name'), unitType: 'apartment', areaSqm: numberOrNull(fd.get('areaSqm')), rooms: numberOrNull(fd.get('rooms')),
      ownershipSharePercent: numberOrNull(fd.get('ownershipSharePercent')), isOwnerOccupied: fd.get('isOwnerOccupied') === 'true', isActive: true, notes: null
    }, changed);
  };

  const lease = dlg.querySelector('[data-lease-form]');
  if (lease) lease.onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(lease);
    await mutate(ctx, `api/assets/${asset.id}/real-estate/leases`, {
      propertyUnitId: fd.get('propertyUnitId'), tenantDisplayLabel: textOrNull(fd.get('tenantDisplayLabel')), startDate: fd.get('startDate'), endDate: fd.get('endDate') || null,
      status: fd.get('status'), coldRent: Number(fd.get('coldRent')), utilitiesAdvance: numberOrNull(fd.get('utilitiesAdvance')), otherRecurringCharges: numberOrNull(fd.get('otherRecurringCharges')),
      currency: String(fd.get('currency') || asset.currency).toUpperCase(), paymentCycle: fd.get('paymentCycle'), depositAmount: numberOrNull(fd.get('depositAmount')), depositHeld: lease.elements.depositHeld.checked,
      lastRentChangeDate: null, nextReviewDate: null, notes: null
    }, changed);
  };

  dlg.querySelectorAll('[data-end-lease]').forEach(button => button.onclick = async () => mutateDelete(ctx, `api/assets/${asset.id}/real-estate/leases/${button.dataset.endLease}`, changed));
}

function wireCosts(ctx, dlg, data, asset, changed) {
  dlg.querySelectorAll('[data-delete-cashflow]').forEach(button => button.onclick = async () => mutateDelete(ctx, `api/assets/${asset.id}/cashflows/${button.dataset.deleteCashflow}`, changed));
  const cashflow = dlg.querySelector('[data-cashflow-form]');
  if (cashflow) {
    const type = cashflow.elements.type;
    const direction = cashflow.elements.direction;
    type.addEventListener('change', () => {
      if (type.value === 'rental_income') direction.value = 'income';
      else if (EXPENSE_TYPES.has(type.value)) direction.value = 'expense';
    });
    cashflow.onsubmit = async event => {
      event.preventDefault(); const fd = new FormData(cashflow); const transactionId = fd.get('transactionId') || null;
      await mutate(ctx, `api/assets/${asset.id}/cashflows`, {
        transactionId, date: fd.get('date') || null, type: fd.get('type'), amount: Number(fd.get('amount')), direction: fd.get('direction'),
        currency: String(fd.get('currency') || asset.currency).toUpperCase(), isPlanned: transactionId ? false : cashflow.elements.isPlanned.checked, notes: null
      }, changed);
    };
  }

  dlg.querySelectorAll('[data-delete-contract]').forEach(button => button.onclick = async () => mutateDelete(ctx, `api/assets/${asset.id}/recurring-contracts/${button.dataset.deleteContract}`, changed));
  const contract = dlg.querySelector('[data-contract-form]');
  if (contract) contract.onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(contract);
    await mutate(ctx, `api/assets/${asset.id}/recurring-contracts`, { recurringContractId: fd.get('recurringContractId'), role: fd.get('role') }, changed);
  };
}

function wireRenovations(ctx, dlg, data, asset, changed) {
  dlg.querySelectorAll('[data-delete-improvement]').forEach(button => button.onclick = async () => mutateDelete(ctx, `api/assets/${asset.id}/real-estate/improvements/${button.dataset.deleteImprovement}`, changed));
  dlg.querySelectorAll('[data-improvement-link-form]').forEach(form => form.onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(form);
    await mutate(ctx, `api/assets/${asset.id}/real-estate/improvements/${form.dataset.improvementLinkForm}/cashflows`, { cashflowEntryId: fd.get('cashflowEntryId') }, changed);
  });
  const improvement = dlg.querySelector('[data-improvement-form]');
  if (improvement) improvement.onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(improvement); const cost = numberOrNull(fd.get('cost'));
    await mutate(ctx, `api/assets/${asset.id}/real-estate/improvements`, {
      title: fd.get('title'), category: fd.get('category'), startDate: fd.get('startDate') || null, completedDate: fd.get('completedDate') || null,
      cost, currency: cost == null ? null : String(fd.get('currency') || asset.currency).toUpperCase(), estimatedValueAdded: numberOrNull(fd.get('estimatedValueAdded')),
      description: textOrNull(fd.get('description')), documentId: null
    }, changed);
  };
}

async function mutate(ctx, path, body, changed) {
  try { await ctx.api(path, jsonBody(body)); await changed(); }
  catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
}

async function mutateDelete(ctx, path, changed) {
  try { await ctx.api(path, { method: 'DELETE' }); await changed(); }
  catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
}

function metric(label, value, numeric = null) {
  const cls = numeric == null ? '' : Number(numeric) > 0 ? ' positive' : Number(numeric) < 0 ? ' negative' : '';
  return `<div class="property-metric"><span>${label}</span><strong class="${cls}">${value}</strong></div>`;
}

function cashflowTypeLabel(type) {
  if (type === 'income') return tr('incomeType');
  return tr(type);
}

function contractRoleLabel(role) {
  if (role === 'utilities') return tr('utilitiesRole');
  return tr(role);
}

async function changedAndClose(dlg, onChanged) {
  dlg.close();
  if (onChanged) await onChanged();
}

function jsonBody(body, method = 'POST') { return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }; }
function numberOrNull(value) { const text = String(value ?? '').trim(); return text === '' ? null : Number(text); }
function textOrNull(value) { const text = String(value ?? '').trim(); return text || null; }
function todayValue() { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; }
