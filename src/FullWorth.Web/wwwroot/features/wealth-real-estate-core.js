const COPY = {
  de: {
    overview: 'Übersicht', property: 'Immobilie', financing: 'Finanzierung', history: 'Historie',
    marketValue: 'Marktwert', debt: 'Verknüpfte Schulden', equity: 'Eigenkapital', ltv: 'LTV', valueGain: 'Wertzuwachs',
    acquisitionBasis: 'Anschaffungsbasis', editProperty: 'Immobiliendaten bearbeiten', address: 'Adresse',
    type: 'Objekttyp', usage: 'Nutzung', livingArea: 'Wohnfläche', usableArea: 'Nutzfläche', plotArea: 'Grundstück',
    rooms: 'Zimmer', bedrooms: 'Schlafzimmer', bathrooms: 'Bäder', floor: 'Etage', totalFloors: 'Etagen gesamt',
    yearBuilt: 'Baujahr', modernized: 'Modernisiert', ownership: 'Eigentumsanteil', condition: 'Zustand',
    parkingSpaces: 'Stellplätze', garageSpaces: 'Garagenplätze', constructionType: 'Bauart', heatingType: 'Heizung',
    energySource: 'Energieträger', addressExtra: 'Adresszusatz', notes: 'Notizen',
    purchaseDate: 'Kaufdatum', purchasePrice: 'Kaufpreis', acquisitionCosts: 'Kaufnebenkosten', equityAtPurchase: 'Eigenkapital beim Kauf',
    features: 'Ausstattung', elevator: 'Aufzug', barrierFree: 'Barrierefrei', balcony: 'Balkon/Terrasse', basement: 'Keller', garden: 'Garten',
    save: 'Speichern', cancel: 'Abbrechen', addDebt: 'Finanzierung verknüpfen', allocation: 'Anteil', relation: 'Beziehung',
    mortgage: 'Hypothek', secured_loan: 'Besichertes Darlehen', other: 'Sonstiges', vehicle_finance: 'Fahrzeugfinanzierung',
    remove: 'Entfernen', amortization: 'Tilgungsplan', payoff: 'Voraussichtlich schuldenfrei', expectedInterest: 'Erwartete Restzinsen',
    principal: 'Ursprünglicher Kredit', currentBalance: 'Restschuld', rate: 'Sollzins', payment: 'Rate', noDebt: 'Keine Finanzierung verknüpft.',
    acquisitionBreakdown: 'Kaufkosten-Aufschlüsselung', addCost: 'Kaufkosten hinzufügen', noCosts: 'Keine detaillierten Kaufkosten hinterlegt.',
    valueHistory: 'Werthistorie', updateValue: 'Wert aktualisieren', noHistory: 'Keine Werthistorie vorhanden.', source: 'Quelle',
    fxIncomplete: 'Kennzahlen unvollständig: Wechselkurs fehlt', close: 'Schließen', notAvailable: 'N/A', selectDebt: 'Darlehen/Schuld auswählen',
    areaUnit: 'm²', propertyType_apartment: 'Wohnung', propertyType_detached_house: 'Einfamilienhaus', propertyType_semi_detached: 'Doppelhaushälfte',
    propertyType_row_house: 'Reihenhaus', propertyType_multi_family: 'Mehrfamilienhaus', propertyType_land: 'Grundstück',
    propertyType_commercial: 'Gewerbe', propertyType_mixed: 'Gemischt', propertyType_other: 'Sonstiges',
    usage_owner_occupied: 'Selbst genutzt', usage_rented: 'Vermietet', usage_mixed: 'Gemischt', usage_vacant: 'Leerstand',
    condition_new: 'Neu', condition_renovated: 'Renoviert', condition_good: 'Gut', condition_needs_renovation: 'Renovierungsbedarf',
    condition_major_renovation: 'Starker Renovierungsbedarf', condition_unknown: 'Unbekannt',
    cost_property_price: 'Kaufpreis', cost_transfer_tax: 'Grunderwerbsteuer', cost_notary: 'Notar', cost_land_registry: 'Grundbuch',
    cost_broker: 'Makler', cost_renovation_at_purchase: 'Renovierung beim Kauf', cost_financing_fee: 'Finanzierungskosten', cost_other: 'Sonstiges',
    method_manual: 'Manuell', method_purchase_price: 'Kaufpreis', method_internal_estimate: 'FullWorth-Schätzung',
    method_external_provider: 'Externer Anbieter', method_appraisal: 'Gutachten', method_import: 'Import', method_legacy: 'Übernommen'
  },
  en: {
    overview: 'Overview', property: 'Property', financing: 'Financing', history: 'History',
    marketValue: 'Market value', debt: 'Linked debt', equity: 'Equity', ltv: 'LTV', valueGain: 'Value gain', acquisitionBasis: 'Acquisition basis',
    editProperty: 'Edit property data', address: 'Address', type: 'Property type', usage: 'Usage', livingArea: 'Living area', usableArea: 'Usable area',
    plotArea: 'Plot area', rooms: 'Rooms', bedrooms: 'Bedrooms', bathrooms: 'Bathrooms', floor: 'Floor', totalFloors: 'Total floors',
    yearBuilt: 'Year built', modernized: 'Modernized', ownership: 'Ownership share', condition: 'Condition',
    parkingSpaces: 'Parking spaces', garageSpaces: 'Garage spaces', constructionType: 'Construction type', heatingType: 'Heating',
    energySource: 'Primary energy source', addressExtra: 'Address extra', notes: 'Notes',
    purchaseDate: 'Purchase date', purchasePrice: 'Purchase price', acquisitionCosts: 'Acquisition costs', equityAtPurchase: 'Equity at purchase',
    features: 'Features', elevator: 'Elevator', barrierFree: 'Barrier free', balcony: 'Balcony/terrace', basement: 'Basement', garden: 'Garden',
    save: 'Save', cancel: 'Cancel', addDebt: 'Link financing', allocation: 'Allocation', relation: 'Relation', mortgage: 'Mortgage',
    secured_loan: 'Secured loan', other: 'Other', vehicle_finance: 'Vehicle finance', remove: 'Remove', amortization: 'Amortization',
    payoff: 'Estimated payoff', expectedInterest: 'Expected remaining interest', principal: 'Original principal', currentBalance: 'Current balance',
    rate: 'Interest rate', payment: 'Payment', noDebt: 'No financing linked.', acquisitionBreakdown: 'Acquisition cost breakdown', addCost: 'Add acquisition cost',
    noCosts: 'No detailed acquisition costs stored.', valueHistory: 'Value history', updateValue: 'Update value', noHistory: 'No valuation history.',
    source: 'Source', fxIncomplete: 'Metrics incomplete: missing FX rate', close: 'Close', notAvailable: 'N/A', selectDebt: 'Select loan/debt', areaUnit: 'm²',
    propertyType_apartment: 'Apartment', propertyType_detached_house: 'Detached house', propertyType_semi_detached: 'Semi-detached house',
    propertyType_row_house: 'Row house', propertyType_multi_family: 'Multi-family', propertyType_land: 'Land', propertyType_commercial: 'Commercial',
    propertyType_mixed: 'Mixed', propertyType_other: 'Other', usage_owner_occupied: 'Owner occupied', usage_rented: 'Rented', usage_mixed: 'Mixed', usage_vacant: 'Vacant',
    condition_new: 'New', condition_renovated: 'Renovated', condition_good: 'Good', condition_needs_renovation: 'Needs renovation',
    condition_major_renovation: 'Major renovation', condition_unknown: 'Unknown', cost_property_price: 'Property price', cost_transfer_tax: 'Transfer tax',
    cost_notary: 'Notary', cost_land_registry: 'Land registry', cost_broker: 'Broker', cost_renovation_at_purchase: 'Renovation at purchase',
    cost_financing_fee: 'Financing fee', cost_other: 'Other', method_manual: 'Manual', method_purchase_price: 'Purchase price',
    method_internal_estimate: 'FullWorth estimate', method_external_provider: 'External provider', method_appraisal: 'Appraisal', method_import: 'Import', method_legacy: 'Migrated'
  }
};

const PROPERTY_TYPES = ['apartment','detached_house','semi_detached','row_house','multi_family','land','commercial','mixed','other'];
const USAGE_TYPES = ['owner_occupied','rented','mixed','vacant'];
const CONDITIONS = ['new','renovated','good','needs_renovation','major_renovation','unknown'];
const COST_TYPES = ['property_price','transfer_tax','notary','land_registry','broker','renovation_at_purchase','financing_fee','other'];

function tr(key) {
  const lang = (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? 'de' : 'en';
  return COPY[lang][key] || key;
}

function ensureStyles() {
  if (document.querySelector('link[data-wealth-real-estate-css]')) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = '/features/wealth-real-estate.css';
  link.dataset.wealthRealEstateCss = '1';
  document.head.appendChild(link);
}

export async function openRealEstateDetail(ctx, asset, onChanged) {
  ensureStyles();
  const id = asset.id;
  let data;
  try {
    const [property, metrics, debts, costs, valuations, loans, liabilities] = await Promise.all([
      ctx.api(`api/assets/${id}/real-estate`),
      ctx.api(`api/assets/${id}/real-estate/metrics`),
      ctx.api(`api/assets/${id}/debts`),
      ctx.api(`api/assets/${id}/real-estate/acquisition-costs`),
      ctx.api(`api/assets/${id}/valuations`).catch(() => []),
      ctx.api('api/loans').catch(() => []),
      ctx.api('api/liabilities').catch(() => [])
    ]);
    data = { property, metrics, debts, costs, valuations, loans, liabilities };
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  const dlg = ctx.dialog(`<div class="dialog-card property-dialog">
    <div class="panel-head"><div><h2>${ctx.esc(data.property.name)}</h2><div class="row-sub property-address">${ctx.esc(addressText(ctx, data.property.detail))}</div></div><button type="button" data-close aria-label="${ctx.esc(tr('close'))}">×</button></div>
    <div class="property-tabs" role="tablist">
      ${tabButton('overview', true)}${tabButton('property')}${tabButton('financing')}${tabButton('history')}
    </div>
    <section class="property-pane" data-pane="overview">${overviewHtml(ctx, data)}</section>
    <section class="property-pane" data-pane="property" hidden>${propertyHtml(ctx, data)}</section>
    <section class="property-pane" data-pane="financing" hidden>${financingHtml(ctx, data)}</section>
    <section class="property-pane" data-pane="history" hidden>${historyHtml(ctx, data)}</section>
  </div>`);

  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelectorAll('[data-tab]').forEach(button => button.addEventListener('click', () => selectTab(dlg, button.dataset.tab)));
  wirePropertyForm(ctx, dlg, id, data.property.detail, async () => changedAndClose(dlg, onChanged));
  wireCosts(ctx, dlg, id, async () => changedAndClose(dlg, onChanged));
  wireDebt(ctx, dlg, id, data, async () => changedAndClose(dlg, onChanged));
  wireHistory(ctx, dlg, id, asset, async () => changedAndClose(dlg, onChanged));
  dlg.showModal();
}

function tabButton(name, selected = false) {
  return `<button type="button" role="tab" data-tab="${name}" aria-selected="${selected}">${tr(name)}</button>`;
}

function selectTab(dlg, name) {
  dlg.querySelectorAll('[data-tab]').forEach(button => button.setAttribute('aria-selected', button.dataset.tab === name ? 'true' : 'false'));
  dlg.querySelectorAll('[data-pane]').forEach(pane => { pane.hidden = pane.dataset.pane !== name; });
}

function overviewHtml(ctx, d) {
  const m = d.metrics;
  const moneyOrNa = (value, currency = m.currency) => value == null ? tr('notAvailable') : ctx.money(value, currency);
  const percentOrNa = value => value == null ? tr('notAvailable') : `${(Number(value) * 100).toFixed(1)} %`;
  return `<div class="property-metrics">
    ${metric(tr('marketValue'), ctx.money(m.currentValue, m.currency))}
    ${metric(tr('debt'), moneyOrNa(m.allocatedDebt))}
    ${metric(tr('equity'), moneyOrNa(m.equity))}
    ${metric(tr('ltv'), percentOrNa(m.ltv))}
    ${metric(tr('acquisitionBasis'), moneyOrNa(m.acquisitionBasis))}
    ${metric(tr('valueGain'), moneyOrNa(m.valueGain), m.valueGain)}
    ${metric(tr('ownership'), `${Number(m.ownershipSharePercent).toFixed(2)} %`)}
  </div>
  ${m.isComplete ? '' : `<div class="property-fx-warning">${ctx.esc(tr('fxIncomplete'))}${m.missingCurrencies?.length ? ` (${ctx.esc(m.missingCurrencies.join(', '))})` : ''}</div>`}
  <div class="property-section"><h3>${ctx.esc(tr('property'))}</h3>${factsHtml(ctx, d.property.detail)}</div>
  <div class="property-section"><h3>${ctx.esc(tr('financing'))}</h3>${debtSummaryHtml(ctx, d.debts, m.currency)}</div>`;
}

function metric(label, value, numeric = null) {
  const cls = numeric == null ? '' : Number(numeric) > 0 ? ' positive' : Number(numeric) < 0 ? ' negative' : '';
  return `<div class="property-metric"><span>${label}</span><strong class="${cls}">${value}</strong></div>`;
}

function factsHtml(ctx, detail) {
  if (!detail) return `<div class="row-sub">${ctx.esc(tr('editProperty'))}</div>`;
  const bits = [];
  if (detail.propertyType) bits.push(tr(`propertyType_${detail.propertyType}`));
  if (detail.usageType) bits.push(tr(`usage_${detail.usageType}`));
  if (detail.livingAreaSqm != null) bits.push(`${detail.livingAreaSqm} ${tr('areaUnit')}`);
  if (detail.rooms != null) bits.push(`${detail.rooms} ${tr('rooms')}`);
  if (detail.yearBuilt != null) bits.push(`${tr('yearBuilt')} ${detail.yearBuilt}`);
  if (detail.condition) bits.push(tr(`condition_${detail.condition}`));
  return `<div class="row-sub">${ctx.esc(bits.join(' · ') || '—')}</div>`;
}

function propertyHtml(ctx, d) {
  const p = d.property.detail || {};
  const selected = (list, value, prefix) => list.map(item => `<option value="${item}"${item === value ? ' selected' : ''}>${ctx.esc(tr(prefix + item))}</option>`).join('');
  return `<form data-property-form>
    <div class="property-grid">
      <label>${ctx.esc(tr('type'))}<select name="propertyType">${selected(PROPERTY_TYPES, p.propertyType || 'apartment', 'propertyType_')}</select></label>
      <label>${ctx.esc(tr('usage'))}<select name="usageType">${selected(USAGE_TYPES, p.usageType || 'owner_occupied', 'usage_')}</select></label>
      <label>Land<input name="countryCode" maxlength="2" value="${ctx.esc(p.countryCode || 'DE')}"></label>
      <label>PLZ<input name="postalCode" maxlength="20" value="${ctx.esc(p.postalCode || '')}"></label>
      <label>Ort<input name="city" maxlength="160" value="${ctx.esc(p.city || '')}"></label>
      <label>Straße<input name="street" maxlength="200" value="${ctx.esc(p.street || '')}"></label>
      <label>Hausnummer<input name="houseNumber" maxlength="40" value="${ctx.esc(p.houseNumber || '')}"></label>
      <label>Einheit<input name="unitLabel" maxlength="100" value="${ctx.esc(p.unitLabel || '')}"></label>
      <label class="wide">${ctx.esc(tr('addressExtra'))}<input name="addressExtra" maxlength="200" value="${ctx.esc(p.addressExtra || '')}"></label>
      <label>${ctx.esc(tr('yearBuilt'))}<input name="yearBuilt" type="number" value="${p.yearBuilt ?? ''}"></label>
      <label>${ctx.esc(tr('modernized'))}<input name="lastMajorModernizationYear" type="number" value="${p.lastMajorModernizationYear ?? ''}"></label>
      <label>${ctx.esc(tr('livingArea'))}<input name="livingAreaSqm" type="number" min="0" step="0.01" value="${p.livingAreaSqm ?? ''}"></label>
      <label>${ctx.esc(tr('usableArea'))}<input name="usableAreaSqm" type="number" min="0" step="0.01" value="${p.usableAreaSqm ?? ''}"></label>
      <label>${ctx.esc(tr('plotArea'))}<input name="plotAreaSqm" type="number" min="0" step="0.01" value="${p.plotAreaSqm ?? ''}"></label>
      <label>${ctx.esc(tr('rooms'))}<input name="rooms" type="number" min="0" step="0.5" value="${p.rooms ?? ''}"></label>
      <label>${ctx.esc(tr('bedrooms'))}<input name="bedrooms" type="number" min="0" step="1" value="${p.bedrooms ?? ''}"></label>
      <label>${ctx.esc(tr('bathrooms'))}<input name="bathrooms" type="number" min="0" step="1" value="${p.bathrooms ?? ''}"></label>
      <label>${ctx.esc(tr('floor'))}<input name="floor" type="number" value="${p.floor ?? ''}"></label>
      <label>${ctx.esc(tr('totalFloors'))}<input name="totalFloors" type="number" min="0" value="${p.totalFloors ?? ''}"></label>
      <label>${ctx.esc(tr('parkingSpaces'))}<input name="parkingSpaces" type="number" min="0" value="${p.parkingSpaces ?? ''}"></label>
      <label>${ctx.esc(tr('garageSpaces'))}<input name="garageSpaces" type="number" min="0" value="${p.garageSpaces ?? ''}"></label>
      <label>${ctx.esc(tr('ownership'))}<input name="ownershipSharePercent" type="number" min="0.0001" max="100" step="0.01" value="${p.ownershipSharePercent ?? 100}"></label>
      <label>${ctx.esc(tr('condition'))}<select name="condition"><option value=""></option>${selected(CONDITIONS, p.condition || '', 'condition_')}</select></label>
      <label>${ctx.esc(tr('constructionType'))}<input name="constructionType" maxlength="100" value="${ctx.esc(p.constructionType || '')}"></label>
      <label>${ctx.esc(tr('heatingType'))}<input name="heatingType" maxlength="100" value="${ctx.esc(p.heatingType || '')}"></label>
      <label>${ctx.esc(tr('energySource'))}<input name="primaryEnergySource" maxlength="100" value="${ctx.esc(p.primaryEnergySource || '')}"></label>
    </div>
    <div class="property-section"><h3>${ctx.esc(tr('features'))}</h3><div class="property-actions">
      ${check('elevator', tr('elevator'), p.elevator)}${check('barrierFree', tr('barrierFree'), p.barrierFree)}${check('balconyTerrace', tr('balcony'), p.balconyTerrace)}${check('basement', tr('basement'), p.basement)}${check('garden', tr('garden'), p.garden)}
    </div></div>
    <div class="property-section"><h3>${ctx.esc(tr('acquisitionBasis'))}</h3><div class="property-grid two">
      <label>${ctx.esc(tr('purchaseDate'))}<input name="purchaseDate" type="date" value="${dateValue(p.purchaseDate)}"></label>
      <label>${ctx.esc(tr('purchasePrice'))}<input name="purchasePrice" type="number" min="0" step="0.01" value="${p.purchasePrice ?? ''}"></label>
      <label>Währung<input name="purchaseCurrency" minlength="3" maxlength="3" value="${ctx.esc(p.purchaseCurrency || d.property.currency)}"></label>
      <label>${ctx.esc(tr('acquisitionCosts'))}<input name="acquisitionCosts" type="number" min="0" step="0.01" value="${p.acquisitionCosts ?? ''}"></label>
      <label>${ctx.esc(tr('equityAtPurchase'))}<input name="equityAtPurchase" type="number" min="0" step="0.01" value="${p.equityAtPurchase ?? ''}"></label>
      <label class="wide">${ctx.esc(tr('notes'))}<textarea name="notes" maxlength="4000" rows="3">${ctx.esc(p.notes || '')}</textarea></label>
    </div></div>
    <div class="dialog-actions"><button type="submit">${ctx.esc(tr('save'))}</button></div>
  </form>
  <div class="property-section"><h3>${ctx.esc(tr('acquisitionBreakdown'))}</h3>${costsHtml(ctx, d.costs, d.property.currency)}${costFormHtml(ctx, d.property.currency)}</div>`;
}

function check(name, label, value) {
  return `<label class="check"><input type="checkbox" name="${name}"${value ? ' checked' : ''}> ${label}</label>`;
}

function costsHtml(ctx, costs, fallbackCurrency) {
  if (!costs?.length) return `<div class="row-sub">${ctx.esc(tr('noCosts'))}</div>`;
  return `<div>${costs.map(cost => `<div class="property-cost-row"><div><strong>${ctx.esc(tr(`cost_${cost.type}`))}</strong><div class="property-debt-meta">${ctx.esc(cost.date || '')}</div></div><div class="property-actions"><span class="amount">${ctx.money(cost.amount, cost.currency || fallbackCurrency)}</span><button type="button" data-delete-cost="${cost.id}">${ctx.esc(tr('remove'))}</button></div></div>`).join('')}</div>`;
}

function costFormHtml(ctx, currency) {
  return `<form data-cost-form class="property-grid two property-section">
    <label>Typ<select name="type">${COST_TYPES.map(type => `<option value="${type}">${ctx.esc(tr(`cost_${type}`))}</option>`).join('')}</select></label>
    <label>Betrag<input name="amount" type="number" min="0" step="0.01" required></label>
    <label>Währung<input name="currency" minlength="3" maxlength="3" value="${ctx.esc(currency)}" required></label>
    <label>Datum<input name="date" type="date"></label>
    <div class="dialog-actions wide"><button type="submit">${ctx.esc(tr('addCost'))}</button></div>
  </form>`;
}

function financingHtml(ctx, d) {
  const linkedLoanIds = new Set((d.debts || []).map(item => item.loanId).filter(Boolean));
  const linkedLiabilityIds = new Set((d.debts || []).map(item => item.liabilityId).filter(Boolean));
  const choices = [
    ...(d.loans || []).filter(item => !linkedLoanIds.has(item.id)).map(item => ({ value: `loan:${item.id}`, label: `${item.name} · ${ctx.money(item.currentBalance, item.currency)}` })),
    ...(d.liabilities || []).filter(item => !linkedLiabilityIds.has(item.id)).map(item => ({ value: `liability:${item.id}`, label: `${item.name} · ${ctx.money(item.currentBalance, item.currency)}` }))
  ];
  return `${debtCardsHtml(ctx, d.debts)}
    <form data-debt-form class="property-section">
      <h3>${ctx.esc(tr('addDebt'))}</h3>
      <div class="property-grid">
        <label class="mobile-wide">${ctx.esc(tr('selectDebt'))}<select name="debt" required><option value=""></option>${choices.map(choice => `<option value="${choice.value}">${ctx.esc(choice.label)}</option>`).join('')}</select></label>
        <label>${ctx.esc(tr('relation'))}<select name="relationType"><option value="mortgage">${ctx.esc(tr('mortgage'))}</option><option value="secured_loan">${ctx.esc(tr('secured_loan'))}</option><option value="other">${ctx.esc(tr('other'))}</option></select></label>
        <label>${ctx.esc(tr('allocation'))} %<input name="allocationPercent" type="number" min="0.01" max="100" step="0.01" value="100" required></label>
      </div>
      <div class="dialog-actions"><button type="submit"${choices.length ? '' : ' disabled'}>${ctx.esc(tr('addDebt'))}</button></div>
    </form>`;
}

function debtSummaryHtml(ctx, debts, currency) {
  if (!debts?.length) return `<div class="row-sub">${ctx.esc(tr('noDebt'))}</div>`;
  return debts.map(debt => `<div class="property-debt-card"><div class="property-debt-main"><strong>${ctx.esc(debt.name)}</strong><div class="property-debt-meta">${ctx.esc(debt.relationType)} · ${Number(debt.allocationPercent).toFixed(2)} %</div></div><span class="amount">${ctx.money(debt.currentBalance * (debt.allocationPercent / 100), debt.currency || currency)}</span></div>`).join('');
}

function debtCardsHtml(ctx, debts) {
  if (!debts?.length) return `<div class="row state-empty"><div class="row-sub">${ctx.esc(tr('noDebt'))}</div></div>`;
  return debts.map(debt => `<div class="property-debt-card" data-debt-card="${debt.id}">
    <div class="property-debt-main"><strong>${ctx.esc(debt.name)}</strong><div class="property-debt-meta">${ctx.esc(tr('currentBalance'))}: ${ctx.money(debt.currentBalance, debt.currency)} · ${ctx.esc(tr('allocation'))}: ${Number(debt.allocationPercent).toFixed(2)} %${debt.interestRate != null ? ` · ${ctx.esc(tr('rate'))}: ${Number(debt.interestRate).toFixed(2)} %` : ''}${debt.regularPayment != null ? ` · ${ctx.esc(tr('payment'))}: ${ctx.money(debt.regularPayment, debt.currency)}` : ''}</div><div class="property-amortization" data-amortization hidden></div></div>
    <div class="property-actions">${debt.loanId ? `<button type="button" data-amortization-button="${debt.loanId}">${ctx.esc(tr('amortization'))}</button>` : ''}<button type="button" data-delete-debt="${debt.id}">${ctx.esc(tr('remove'))}</button></div>
  </div>`).join('');
}

function historyHtml(ctx, d) {
  const rows = d.valuations?.length ? d.valuations.map(value => `<div class="property-valuation-row"><div><strong>${ctx.money(value.amount, value.currency)}</strong><div class="property-debt-meta">${ctx.esc(value.valuedAt)} · ${ctx.esc(tr(`method_${value.method}`))}${value.providerDisplayName ? ` · ${ctx.esc(value.providerDisplayName)}` : ''}</div></div>${value.isCurrent ? '<span class="tx-marker">Aktuell</span>' : ''}</div>`).join('') : `<div class="row-sub">${ctx.esc(tr('noHistory'))}</div>`;
  return `${rows}<form data-value-form class="property-section"><h3>${ctx.esc(tr('updateValue'))}</h3><div class="property-grid two"><label>Wert<input name="amount" type="number" min="0" step="0.01" value="${d.property.currentValue}" required></label><label>Währung<input name="currency" minlength="3" maxlength="3" value="${ctx.esc(d.property.currency)}" required></label><label>Datum<input name="valuedAt" type="date" value="${todayValue()}"></label></div><div class="dialog-actions"><button type="submit">${ctx.esc(tr('updateValue'))}</button></div></form>`;
}

function wirePropertyForm(ctx, dlg, id, current, changed) {
  const form = dlg.querySelector('[data-property-form]');
  if (!form) return;
  form.onsubmit = async event => {
    event.preventDefault();
    const fd = new FormData(form);
    const n = key => numberOrNull(fd.get(key));
    const body = {
      propertyType: fd.get('propertyType'), usageType: fd.get('usageType'), countryCode: String(fd.get('countryCode') || 'DE').toUpperCase(),
      postalCode: textOrNull(fd.get('postalCode')), city: textOrNull(fd.get('city')), street: textOrNull(fd.get('street')), houseNumber: textOrNull(fd.get('houseNumber')),
      addressExtra: textOrNull(fd.get('addressExtra')), unitLabel: textOrNull(fd.get('unitLabel')), yearBuilt: n('yearBuilt'), lastMajorModernizationYear: n('lastMajorModernizationYear'),
      livingAreaSqm: n('livingAreaSqm'), usableAreaSqm: n('usableAreaSqm'), plotAreaSqm: n('plotAreaSqm'), rooms: n('rooms'), bedrooms: n('bedrooms'), bathrooms: n('bathrooms'),
      floor: n('floor'), totalFloors: n('totalFloors'), ownershipSharePercent: Number(fd.get('ownershipSharePercent') || 100), parkingSpaces: n('parkingSpaces'), garageSpaces: n('garageSpaces'),
      condition: textOrNull(fd.get('condition')), constructionType: textOrNull(fd.get('constructionType')), heatingType: textOrNull(fd.get('heatingType')),
      primaryEnergySource: textOrNull(fd.get('primaryEnergySource')), elevator: form.elements.elevator.checked, barrierFree: form.elements.barrierFree.checked,
      balconyTerrace: form.elements.balconyTerrace.checked, basement: form.elements.basement.checked, garden: form.elements.garden.checked,
      purchaseDate: fd.get('purchaseDate') || null, purchasePrice: n('purchasePrice'), purchaseCurrency: String(fd.get('purchaseCurrency') || '').toUpperCase() || null,
      acquisitionCosts: n('acquisitionCosts'), equityAtPurchase: n('equityAtPurchase'), notes: textOrNull(fd.get('notes')),
      latitude: current?.latitude ?? null, longitude: current?.longitude ?? null
    };
    try { await ctx.api(`api/assets/${id}/real-estate`, jsonBody(body, 'PUT')); await changed(); } catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
}

function wireCosts(ctx, dlg, id, changed) {
  dlg.querySelectorAll('[data-delete-cost]').forEach(button => button.onclick = async () => {
    try { await ctx.api(`api/assets/${id}/real-estate/acquisition-costs/${button.dataset.deleteCost}`, { method: 'DELETE' }); await changed(); } catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  });
  const form = dlg.querySelector('[data-cost-form]');
  if (!form) return;
  form.onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(form);
    try { await ctx.api(`api/assets/${id}/real-estate/acquisition-costs`, jsonBody({ type: fd.get('type'), amount: Number(fd.get('amount')), currency: String(fd.get('currency')).toUpperCase(), date: fd.get('date') || null, notes: null })); await changed(); } catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
}

function wireDebt(ctx, dlg, id, data, changed) {
  dlg.querySelectorAll('[data-delete-debt]').forEach(button => button.onclick = async () => {
    try { await ctx.api(`api/assets/${id}/debts/${button.dataset.deleteDebt}`, { method: 'DELETE' }); await changed(); } catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  });
  dlg.querySelectorAll('[data-amortization-button]').forEach(button => button.onclick = async () => {
    const target = button.closest('[data-debt-card]').querySelector('[data-amortization]');
    try {
      const result = await ctx.api(`api/loans/${button.dataset.amortizationButton}/amortization`);
      target.hidden = false;
      target.textContent = `${tr('payoff')}: ${result.estimatedPayoffDate || tr('notAvailable')} · ${tr('expectedInterest')}: ${ctx.money(result.totalExpectedInterest, result.currency)}`;
    } catch { target.hidden = false; target.textContent = tr('notAvailable'); }
  });
  const form = dlg.querySelector('[data-debt-form]');
  if (!form) return;
  form.onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(form); const raw = String(fd.get('debt') || ''); const [kind, debtId] = raw.split(':');
    if (!debtId) return;
    try { await ctx.api(`api/assets/${id}/debts`, jsonBody({ loanId: kind === 'loan' ? debtId : null, liabilityId: kind === 'liability' ? debtId : null, relationType: fd.get('relationType'), allocationPercent: Number(fd.get('allocationPercent')) })); await changed(); } catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
}

function wireHistory(ctx, dlg, id, asset, changed) {
  const form = dlg.querySelector('[data-value-form]'); if (!form) return;
  form.onsubmit = async event => {
    event.preventDefault(); const fd = new FormData(form);
    try { await ctx.api(`api/assets/${id}/valuations`, jsonBody({ amount: Number(fd.get('amount')), currency: String(fd.get('currency') || asset.currency).toUpperCase(), valuedAt: fd.get('valuedAt') || null, method: 'manual', isAccepted: true })); await changed(); } catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
  };
}

async function changedAndClose(dlg, onChanged) {
  dlg.close();
  if (onChanged) await onChanged();
}

function addressText(ctx, detail) {
  if (!detail) return tr('editProperty');
  if (ctx.isPrivate()) return '••••••';
  const line = [detail.street, detail.houseNumber].filter(Boolean).join(' ');
  const city = [detail.postalCode, detail.city].filter(Boolean).join(' ');
  return [line, detail.addressExtra, city, detail.unitLabel].filter(Boolean).join(' · ') || tr('editProperty');
}

function jsonBody(body, method = 'POST') { return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }; }
function numberOrNull(value) { const text = String(value ?? '').trim(); return text === '' ? null : Number(text); }
function textOrNull(value) { const text = String(value ?? '').trim(); return text || null; }
function dateValue(value) { return value ? String(value).slice(0, 10) : ''; }
function todayValue() { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`; }
