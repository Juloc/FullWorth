// Altersvorsorge (bAV) — the occupational-pension area, per docs/PENSION.md.
//
// Four tabs behind one registered view, the way features/tax.js does it: core/router.js resolves a
// view from the FIRST path segment only, so /pension, /pension/vertraege, /pension/verlauf and
// /pension/dokumente all land on the 'pension' view and this module reads the full pathname itself to
// pick the active tab. Back and Forward therefore work between the tabs without a second router.
//
// Copy lives in this module (like features/networth.js and features/contracts.js) rather than in the
// shared locale files, so the area ships without touching them.
//
// Two things this screen must never do, because the model is built to prevent exactly them:
//   - present a projected value as today's money, or as a guarantee. Only `balance` is money now;
//     every projected figure is rendered in its own block and labelled with its assumption.
//   - read "beitragsfrei" as cancelled or as cost-free. It is its own state and keeps its costs.
//
// The document import (step 2) lives in features/pension-documents.js and the projection / variant
// comparison (step 3) in features/pension-projection.js; both are mounted as tabs here. The manual
// entry flow in this file stays the supported path on an installation with no Cloud and no AI.
//
// Both mounted modules receive this module's copy (`t`, `label`, `percent`) instead of importing it,
// so neither pair ever becomes a circular import.
import { sectionCard, esc } from '../ui/ux-kit.js';
import { renderPensionDocuments, resetPensionDocuments } from './pension-documents.js';
import { renderPensionProjection, resetPensionProjection } from './pension-projection.js';

let ctx = null;
let contracts = [];
let overview = null;
let loadError = null;

const T = {
  de: {
    title: 'Altersvorsorge',
    tabOverview: 'Übersicht', tabContracts: 'Verträge', tabHistory: 'Verlauf',
    tabSimulation: 'Simulation', tabDocuments: 'Dokumente',
    add: 'Vertrag hinzufügen',
    empty: 'Noch kein Vertrag erfasst. Trage deine betriebliche Altersvorsorge ein, um Guthaben, Beiträge und Kosten an einem Ort zu sehen.',
    emptyHistory: 'Noch kein Stand erfasst. Ein Stand gehört immer zu einem Datum – so bleibt der Verlauf erhalten.',
    balance: 'Guthaben', balanceTotal: 'Guthaben gesamt',
    employeeMonthly: 'Eigenanteil / Monat', employerMonthly: 'Arbeitgeber / Monat',
    guaranteedAnnuity: 'Garantierte Rente / Monat', projectedAnnuity: 'Prognose Rente / Monat',
    incomplete: 'Unvollständig',
    incompleteHint: 'Für {currencies} fehlt ein Wechselkurs. Diese Beträge sind NICHT in der Summe enthalten und stehen unten in ihrer eigenen Währung.',
    noSnapshot: '{count} Vertrag ohne Stand – dort ist noch kein Guthaben bekannt.',
    noSnapshotPlural: '{count} Verträge ohne Stand – dort ist noch kein Guthaben bekannt.',
    estimated: 'Bei {count} Vertrag sind Kosten geschätzt und als Schätzung gekennzeichnet.',
    estimatedPlural: 'Bei {count} Verträgen sind Kosten geschätzt und als Schätzung gekennzeichnet.',
    employerHint: 'Der Arbeitgeberanteil ist ein Zuschuss – er ist keine Ausgabe und kein verfügbares Einkommen. Nur der Eigenanteil verlässt dein Netto.',
    projectionHint: 'Prognosen sind keine Garantie und kein heutiges Vermögen. Als Vermögen zählt nur das Guthaben zum jeweiligen Stichtag.',
    unconverted: 'Ohne Kurs',
    provider: 'Anbieter', tariff: 'Tarif', policyNumber: 'Versicherungsnummer',
    policyNumberHint: 'Wird verschlüsselt gespeichert. Angezeigt werden nur die letzten vier Zeichen.',
    policyStored: 'Nummer hinterlegt (…{last4})',
    route: 'Durchführungsweg', status: 'Status',
    employer: 'Arbeitgeber', policyHolder: 'Versicherungsnehmer', insuredPerson: 'Versicherte Person',
    startDate: 'Beginn', retirementDate: 'Rentenbeginn', endDate: 'Vertragsende',
    currency: 'Währung', guaranteeQuota: 'Garantiequote (%)', annuityFactor: 'Rentenfaktor',
    annuityFactorHint: 'Garantierte Monatsrente je 10.000 Einheiten Kapital.',
    fundChangeable: 'Fondsauswahl änderbar',
    includeInNetWorth: 'In Gesamtvermögen einbeziehen',
    includeHint: 'Aus ist möglich: der Vertrag wird dann nur hier angezeigt und nicht mitgezählt.',
    notes: 'Notiz',
    save: 'Speichern', cancel: 'Abbrechen', close: 'Schließen', del: 'Löschen',
    newSnapshot: 'Stand ergänzen', newContribution: 'Beitrag erfassen',
    newCost: 'Kosten erfassen', newFund: 'Fonds erfassen',
    edit: 'Vertrag bearbeiten', more: 'Weitere Aktionen',
    effectiveDate: 'Stichtag', guaranteedBalance: 'davon garantiert',
    surrenderValue: 'Rückkaufswert', securityAssets: 'Sicherungsvermögen', fundAssets: 'Fondsvermögen',
    guaranteedCapital: 'Garantiertes Kapital zum Rentenbeginn',
    guaranteedMonthly: 'Garantierte Monatsrente',
    projectedCapital: 'Prognostiziertes Kapital zum Rentenbeginn',
    projectedMonthly: 'Prognostizierte Monatsrente',
    projectionReturn: 'Angenommene Rendite (% p. a.)',
    projectionBasis: 'Grundlage der Prognose',
    projectionBasisHint: 'Eine Prognose braucht ihre Annahme, sonst ist sie von einer Garantie nicht zu unterscheiden.',
    valueSource: 'Quelle', note: 'Bemerkung',
    validFrom: 'Gültig ab', validUntil: 'Gültig bis', endReason: 'Grund für das Ende',
    cycle: 'Zahlweise',
    employeeAmount: 'Eigenanteil (Entgeltumwandlung)',
    employerSubsidy: 'AG-Zuschuss', employerAmount: 'Arbeitgeberfinanziert',
    statedTotal: 'Gesamtbeitrag laut Dokument',
    statedTotalHint: 'Optional. Weicht er von der Summe der Anteile ab, wird das angezeigt und nicht korrigiert.',
    mismatch: 'Summe weicht ab',
    taxEffect: 'Steuer- und SV-Wirkung',
    taxEffectHint: 'Nur eintragen, wenn es auf einem Dokument oder einer Abrechnung steht. Eigene Rechnung bitte als Simulation kennzeichnen.',
    taxSaving: 'Steuerersparnis', socialSaving: 'SV-Ersparnis', netEffort: 'Nettoaufwand',
    taxEffectSource: 'Quelle der Wirkung', taxEffectReference: 'Belegbezug',
    simulationBadge: 'Simulation', statedBadge: 'Belegt',
    costKind: 'Kostenart', costBasis: 'Bezugsgröße', costTiming: 'Zeitraum',
    amount: 'Betrag', percent: 'Prozent',
    isEstimated: 'Geschätzt', estimateBasis: 'Grundlage der Schätzung',
    estimateBasisHint: 'Pflicht bei einer Schätzung, damit sie nie als Vertragswert gelesen wird.',
    estimateBadge: 'Schätzung',
    continuesWhenPaidUp: 'Läuft auch beitragsfrei weiter',
    fundName: 'Fonds', isin: 'ISIN', weight: 'Gewichtung (%)',
    ongoingCharges: 'Laufende Kosten (% p. a.)', chargesEstimated: 'Kosten geschätzt',
    assetClass: 'Anlageklasse',
    sections: { snapshots: 'Stände', contributions: 'Beiträge', costs: 'Kosten', funds: 'Fonds & ETFs' },
    noneYet: 'Noch nichts erfasst.',
    deleteConfirm: 'Diesen Vertrag mit allen Ständen, Beiträgen und Kosten löschen?',
    saved: 'Gespeichert', deleted: 'Gelöscht',
    duplicateContract: 'Diese Versicherungsnummer ist bei diesem Anbieter schon erfasst. Ergänze dort einen Stand statt einen zweiten Vertrag anzulegen.',
    duplicateSnapshot: 'Für dieses Datum gibt es bereits einen Stand. Ein Stand wird ergänzt, nie überschrieben.',
    ownerOnly: 'Nur Eigentümer dieses Finanzbereichs können hier etwas ändern.',
    asOf: 'Stand', notCounted: 'Nur Anzeige',
    routes: {
      direct_insurance: 'Direktversicherung', pension_fund: 'Pensionskasse',
      pension_scheme: 'Pensionsfonds', provident_fund: 'Unterstützungskasse',
      direct_commitment: 'Direktzusage', other: 'Sonstiger Weg'
    },
    statuses: {
      active: 'Aktiv', paid_up: 'Beitragsfrei', in_payout: 'In Auszahlung',
      transferred: 'Übertragen', terminated: 'Beendet'
    },
    statusHints: { paid_up: 'Keine Beiträge mehr – Vertrag, Guthaben und Kosten laufen weiter.' },
    cycles: {
      monthly: 'monatlich', quarterly: 'vierteljährlich', semiannual: 'halbjährlich',
      yearly: 'jährlich', one_off: 'Einmalbeitrag'
    },
    endReasons: {
      paid_up: 'beitragsfrei gestellt', employer_change: 'Arbeitgeberwechsel',
      amount_change: 'Beitrag geändert', payout: 'Auszahlung', terminated: 'beendet'
    },
    sources: {
      manual: 'manuell', document: 'Dokument', payslip: 'Abrechnung',
      import: 'Import', provider: 'Anbieter'
    },
    bases: {
      document_guaranteed: 'Garantie aus dem Dokument',
      document_forecast: 'Prognose aus dem Dokument',
      simulation: 'eigene Simulation'
    },
    costKinds: {
      acquisition: 'Abschluss- und Vertriebskosten',
      administration_on_contribution: 'Verwaltung auf den Beitrag',
      administration_on_capital: 'Verwaltung auf das Guthaben',
      administration_fixed: 'Stückkosten',
      fund: 'Fondskosten', guarantee: 'Garantiekosten', risk_premium: 'Risikobeitrag',
      payout: 'Kosten in der Rentenphase',
      // The aggregate, and it says so: it contains the kinds above it rather than joining them.
      effective_cost: 'Effektivkosten (Gesamtwirkung)', other: 'Sonstige Kosten'
    },
    costBases: {
      fixed_amount: 'fester Betrag', percent_of_contribution: '% vom Beitrag',
      percent_of_capital: '% vom Guthaben', percent_of_sum: '% der Beitragssumme',
      percent_of_annuity: '% der Rente'
    },
    costTimings: { incurred: 'bereits angefallen', ongoing: 'laufend', future: 'noch offen' },
    assetClasses: {
      equity: 'Aktien', bond: 'Anleihen', mixed: 'Mischfonds', money_market: 'Geldmarkt',
      real_estate: 'Immobilien', commodity: 'Rohstoffe',
      guarantee_assets: 'Sicherungsvermögen', other: 'Sonstiges'
    },
    taxSources: { document: 'Dokument', payslip: 'Lohnabrechnung', simulation: 'Simulation (eigene Rechnung)' }
  },
  en: {
    title: 'Occupational pension',
    tabOverview: 'Overview', tabContracts: 'Contracts', tabHistory: 'History',
    tabSimulation: 'Simulation', tabDocuments: 'Documents',
    add: 'Add contract',
    empty: 'No contract yet. Add your occupational pension to see balance, contributions and costs in one place.',
    emptyHistory: 'No statement recorded yet. A value always belongs to a date, so the history is kept.',
    balance: 'Balance', balanceTotal: 'Total balance',
    employeeMonthly: 'Own share / month', employerMonthly: 'Employer / month',
    guaranteedAnnuity: 'Guaranteed annuity / month', projectedAnnuity: 'Projected annuity / month',
    incomplete: 'Incomplete',
    incompleteHint: 'No exchange rate for {currencies}. Those amounts are NOT in the total and are listed below in their own currency.',
    noSnapshot: '{count} contract has no value yet.',
    noSnapshotPlural: '{count} contracts have no value yet.',
    estimated: '{count} contract carries estimated costs, marked as estimates.',
    estimatedPlural: '{count} contracts carry estimated costs, marked as estimates.',
    employerHint: 'The employer share is a benefit — never an expense and never spendable income. Only your own share leaves your net pay.',
    projectionHint: 'A projection is neither a guarantee nor money you have today. Only the balance at its own date counts as wealth.',
    unconverted: 'No rate',
    provider: 'Provider', tariff: 'Tariff', policyNumber: 'Policy number',
    policyNumberHint: 'Stored encrypted. Only the last four characters are ever shown.',
    policyStored: 'Number stored (…{last4})',
    route: 'Implementation route', status: 'Status',
    employer: 'Employer', policyHolder: 'Policy holder', insuredPerson: 'Insured person',
    startDate: 'Start', retirementDate: 'Retirement date', endDate: 'Contract end',
    currency: 'Currency', guaranteeQuota: 'Guarantee quota (%)', annuityFactor: 'Annuity factor',
    annuityFactorHint: 'Guaranteed monthly annuity per 10,000 units of capital.',
    fundChangeable: 'Fund selection changeable',
    includeInNetWorth: 'Count in total wealth',
    includeHint: 'Off is fine: the contract is then only shown here and not counted.',
    notes: 'Note',
    save: 'Save', cancel: 'Cancel', close: 'Close', del: 'Delete',
    newSnapshot: 'Add a value', newContribution: 'Add a contribution',
    newCost: 'Add a cost', newFund: 'Add a fund',
    edit: 'Edit contract', more: 'More actions',
    effectiveDate: 'As of', guaranteedBalance: 'of which guaranteed',
    surrenderValue: 'Surrender value', securityAssets: 'Security assets', fundAssets: 'Fund assets',
    guaranteedCapital: 'Guaranteed capital at retirement',
    guaranteedMonthly: 'Guaranteed monthly annuity',
    projectedCapital: 'Projected capital at retirement',
    projectedMonthly: 'Projected monthly annuity',
    projectionReturn: 'Assumed return (% p.a.)',
    projectionBasis: 'Projection basis',
    projectionBasisHint: 'A projection needs its assumption, or it cannot be told apart from a guarantee.',
    valueSource: 'Source', note: 'Remark',
    validFrom: 'Valid from', validUntil: 'Valid until', endReason: 'Reason it ended',
    cycle: 'Cycle',
    employeeAmount: 'Own share (deferred compensation)',
    employerSubsidy: 'Employer subsidy', employerAmount: 'Employer financed',
    statedTotal: 'Total stated on the document',
    statedTotalHint: 'Optional. A total that disagrees with the shares is shown, not corrected.',
    mismatch: 'Total disagrees',
    taxEffect: 'Tax and social-insurance effect',
    taxEffectHint: 'Only enter what a document or a payslip states. Mark your own arithmetic as a simulation.',
    taxSaving: 'Tax saved', socialSaving: 'Social insurance saved', netEffort: 'Net effort',
    taxEffectSource: 'Source of the effect', taxEffectReference: 'Reference',
    simulationBadge: 'Simulation', statedBadge: 'Stated',
    costKind: 'Cost type', costBasis: 'Basis', costTiming: 'Period',
    amount: 'Amount', percent: 'Percent',
    isEstimated: 'Estimated', estimateBasis: 'Basis of the estimate',
    estimateBasisHint: 'Required for an estimate, so it is never read as a contract value.',
    estimateBadge: 'Estimate',
    continuesWhenPaidUp: 'Keeps running when paid up',
    fundName: 'Fund', isin: 'ISIN', weight: 'Weight (%)',
    ongoingCharges: 'Ongoing charges (% p.a.)', chargesEstimated: 'Charges estimated',
    assetClass: 'Asset class',
    sections: { snapshots: 'Values', contributions: 'Contributions', costs: 'Costs', funds: 'Funds & ETFs' },
    noneYet: 'Nothing recorded yet.',
    deleteConfirm: 'Delete this contract with all its values, contributions and costs?',
    saved: 'Saved', deleted: 'Deleted',
    duplicateContract: 'This policy number already exists at this provider. Add a value there instead of a second contract.',
    duplicateSnapshot: 'There is already a value for that date. A value is added, never overwritten.',
    ownerOnly: 'Only owners of this finance space can change anything here.',
    asOf: 'As of', notCounted: 'Display only',
    routes: {
      direct_insurance: 'Direct insurance', pension_fund: 'Pension fund (Pensionskasse)',
      pension_scheme: 'Pension scheme (Pensionsfonds)', provident_fund: 'Provident fund (U-Kasse)',
      direct_commitment: 'Direct commitment', other: 'Other route'
    },
    statuses: {
      active: 'Active', paid_up: 'Paid up', in_payout: 'In payout',
      transferred: 'Transferred', terminated: 'Terminated'
    },
    statusHints: { paid_up: 'No new contributions — contract, balance and costs all continue.' },
    cycles: {
      monthly: 'monthly', quarterly: 'quarterly', semiannual: 'half-yearly',
      yearly: 'yearly', one_off: 'one-off'
    },
    endReasons: {
      paid_up: 'made paid up', employer_change: 'employer change',
      amount_change: 'contribution changed', payout: 'payout', terminated: 'terminated'
    },
    sources: {
      manual: 'manual', document: 'document', payslip: 'payslip',
      import: 'import', provider: 'provider'
    },
    bases: {
      document_guaranteed: 'guarantee from the document',
      document_forecast: 'forecast from the document',
      simulation: 'own simulation'
    },
    costKinds: {
      acquisition: 'Acquisition and distribution',
      administration_on_contribution: 'Administration on the contribution',
      administration_on_capital: 'Administration on the capital',
      administration_fixed: 'Fixed administration',
      fund: 'Fund charges', guarantee: 'Guarantee charges', risk_premium: 'Risk premium',
      payout: 'Payout-phase costs',
      effective_cost: 'Effective cost (total impact)', other: 'Other costs'
    },
    costBases: {
      fixed_amount: 'fixed amount', percent_of_contribution: '% of the contribution',
      percent_of_capital: '% of the capital', percent_of_sum: '% of the total contributions',
      percent_of_annuity: '% of the annuity'
    },
    costTimings: { incurred: 'already charged', ongoing: 'ongoing', future: 'still to come' },
    assetClasses: {
      equity: 'Equity', bond: 'Bonds', mixed: 'Mixed', money_market: 'Money market',
      real_estate: 'Real estate', commodity: 'Commodities',
      guarantee_assets: 'Security assets', other: 'Other'
    },
    taxSources: { document: 'Document', payslip: 'Payslip', simulation: 'Simulation (own arithmetic)' }
  }
};

const ROUTES = ['direct_insurance', 'pension_fund', 'pension_scheme', 'provident_fund', 'direct_commitment', 'other'];
const STATUSES = ['active', 'paid_up', 'in_payout', 'transferred', 'terminated'];
const CYCLES = ['monthly', 'quarterly', 'semiannual', 'yearly', 'one_off'];
const END_REASONS = ['paid_up', 'employer_change', 'amount_change', 'payout', 'terminated'];
const VALUE_SOURCES = ['manual', 'document', 'payslip'];
const PROJECTION_BASES = ['document_forecast', 'document_guaranteed', 'simulation'];
const TAX_SOURCES = ['document', 'payslip', 'simulation'];
const COST_KINDS = [
  'acquisition', 'administration_on_contribution', 'administration_on_capital', 'administration_fixed',
  'fund', 'guarantee', 'risk_premium', 'payout', 'effective_cost', 'other'
];
const COST_BASES = ['fixed_amount', 'percent_of_contribution', 'percent_of_capital', 'percent_of_sum', 'percent_of_annuity'];
const COST_TIMINGS = ['ongoing', 'incurred', 'future'];
const ASSET_CLASSES = ['equity', 'bond', 'mixed', 'money_market', 'real_estate', 'commodity', 'guarantee_assets', 'other'];

const TABS = [
  { key: 'overview', path: '/pension' },
  { key: 'contracts', path: '/pension/vertraege' },
  { key: 'history', path: '/pension/verlauf' },
  { key: 'simulation', path: '/pension/simulation' },
  { key: 'documents', path: '/pension/dokumente' }
];

function lang() { return (document.documentElement.lang || '').startsWith('en') ? 'en' : 'de'; }
function tr() { return T[lang()]; }
function label(group, key) { return tr()[group]?.[key] || key || '—'; }
function today() { return new Date().toISOString().slice(0, 10); }

function activeTab() {
  const path = location.pathname;
  const hit = TABS.slice(1).find(tab => path.startsWith(tab.path));
  return hit ? hit.key : 'overview';
}

function pathFor(key) { return (TABS.find(tab => tab.key === key) || TABS[0]).path; }

export function bindPension(context) {
  ctx = context;
}

export async function renderPension(context) {
  ctx = context;
  const host = ctx.$('#view-pension');
  if (!host) return;

  loadError = null;
  try {
    [overview, contracts] = await Promise.all([
      ctx.api('api/pension/overview'),
      ctx.api('api/pension/contracts')
    ]);
    contracts = contracts || [];
  } catch (error) {
    loadError = error;
    overview = null;
    contracts = [];
  }

  const tab = activeTab();
  host.innerHTML = `
    <div class="pension-toolbar">
      <div class="pension-tabs" role="tablist">
        ${TABS.map(item => `<button type="button" role="tab" aria-selected="${item.key === tab}" class="${item.key === tab ? 'active' : ''}" data-pension-tab="${item.key}">${esc(tr()[tabKey(item.key)])}</button>`).join('')}
      </div>
      <button type="button" class="primary-action" data-pension-add>${esc(tr().add)}</button>
    </div>
    <div class="pension-body">${
      loadError
        ? sectionCard(tr().title, `<div class="row-sub">${esc(loadError.message || ctx.get('common.error'))}</div>`)
        : tab === 'contracts' ? contractsHtml()
          : tab === 'history' ? historyHtml()
            : tab === 'simulation' ? '<div class="pension-simulation" data-pension-simulation></div>'
              : tab === 'documents' ? '<div class="pension-documents" data-pension-documents></div>'
                : overviewHtml()
    }</div>`;

  host.querySelectorAll('[data-pension-tab]').forEach(button =>
    button.addEventListener('click', () => switchTab(button.dataset.pensionTab)));
  host.querySelector('[data-pension-add]')?.addEventListener('click', () => openContractDialog(null));
  host.querySelectorAll('[data-pension-open]').forEach(button =>
    button.addEventListener('click', () => openDetail(button.dataset.pensionOpen)));

  // The Dokumente tab owns its own panel: upload, review and commit all render inside it, so the
  // review form never has to be squeezed into a dialog.
  const documentsHost = host.querySelector('[data-pension-documents]');
  if (documentsHost) await renderPensionDocuments(documentsHost, {
    ctx, contracts, t: tr(), label, reload: () => renderPension(ctx)
  });

  // The Simulation tab owns its own panel for the same reason: a return scenario is changed
  // repeatedly, so its form belongs inline on the tab rather than in a dialog that has to be
  // re-opened for every change. It computes on read only - nothing it shows is ever stored.
  const simulationHost = host.querySelector('[data-pension-simulation]');
  if (simulationHost) await renderPensionProjection(simulationHost, {
    ctx, contracts, t: tr(), label, percent, reload: () => renderPension(ctx)
  });
}

function tabKey(key) {
  return key === 'contracts' ? 'tabContracts'
    : key === 'history' ? 'tabHistory'
      : key === 'simulation' ? 'tabSimulation'
        : key === 'documents' ? 'tabDocuments' : 'tabOverview';
}

function switchTab(key) {
  const path = pathFor(key);
  if (location.pathname !== path) history.pushState({ view: 'pension' }, '', path);
  // Leaving the tab leaves its review screen, so Dokumente always opens on the list rather than on a
  // half-finished review of whatever happened to be open before. Simulation drops its result for the
  // same reason: a projection belongs to the scenario that produced it, never to the next visit.
  resetPensionDocuments();
  resetPensionProjection();
  renderPension(ctx);
}

// ---- Übersicht ----

function overviewHtml() {
  if (!contracts.length) return sectionCard(tr().title, `<div class="row-sub">${esc(tr().empty)}</div>`);

  const currency = overview?.currency || 'EUR';
  const metrics = `
    <div class="metric-grid pension-metrics">
      <article class="metric"><span>${esc(tr().balanceTotal)}</span><strong>${money(overview?.totalBalance, currency)}</strong></article>
      <article class="metric"><span>${esc(tr().employeeMonthly)}</span><strong>${money(overview?.monthlyEmployeeContribution, currency)}</strong></article>
      <article class="metric"><span>${esc(tr().employerMonthly)}</span><strong>${money(overview?.monthlyEmployerContribution, currency)}</strong></article>
      <article class="metric"><span>${esc(tr().guaranteedAnnuity)}</span><strong>${money(overview?.guaranteedMonthlyAnnuity, currency)}</strong></article>
    </div>`;

  const notices = [];
  if (overview && overview.isComplete === false) {
    const list = (overview.missingCurrencies || []).join(', ');
    notices.push(noticeHtml('warn', `${tr().incomplete}: ${tr().incompleteHint.replace('{currencies}', list)}`));
    (overview.unconvertedBalances || []).forEach(item => notices.push(
      `<div class="row pension-unconverted"><div class="row-main"><div class="row-title">${esc(tr().unconverted)} · ${esc(item.currency)}</div></div><div class="amount">${money(item.amount, item.currency)}</div></div>`));
  }
  if (overview?.contractsWithoutSnapshot > 0) {
    const copy = overview.contractsWithoutSnapshot === 1 ? tr().noSnapshot : tr().noSnapshotPlural;
    notices.push(noticeHtml('info', copy.replace('{count}', String(overview.contractsWithoutSnapshot))));
  }
  if (overview?.contractsWithEstimatedCosts > 0) {
    const copy = overview.contractsWithEstimatedCosts === 1 ? tr().estimated : tr().estimatedPlural;
    notices.push(noticeHtml('info', copy.replace('{count}', String(overview.contractsWithEstimatedCosts))));
  }

  const projected = Number(overview?.projectedMonthlyAnnuity || 0);
  const projection = projected > 0
    ? sectionCard(tr().projectedAnnuity,
      `<div class="pension-projection"><div class="amount pension-projection-value">${money(projected, currency)}</div>
       <p class="row-sub">${esc(tr().projectionHint)}</p></div>`, { className: 'pension-projection-card' })
    : '';

  return `${metrics}
    ${notices.length ? `<div class="pension-notices">${notices.join('')}</div>` : ''}
    ${sectionCard(tr().tabContracts, `<div class="rows pension-list">${contracts.map(contractRow).join('')}</div>`)}
    ${projection}
    <p class="pension-footnote">${esc(tr().employerHint)}</p>`;
}

function noticeHtml(kind, text) {
  return `<div class="pension-notice pension-notice-${kind}">${esc(text)}</div>`;
}

// ---- Verträge ----

function contractsHtml() {
  if (!contracts.length) return sectionCard(tr().title, `<div class="row-sub">${esc(tr().empty)}</div>`);
  return sectionCard(tr().tabContracts, `<div class="rows pension-list">${contracts.map(contractRow).join('')}</div>`);
}

function contractRow(contract) {
  const snapshot = contract.currentSnapshot;
  const contribution = contract.currentContribution;
  const badges = [`<span class="pension-badge">${esc(label('routes', contract.implementationRoute))}</span>`];
  badges.push(`<span class="pension-badge pension-status-${esc(contract.status)}">${esc(label('statuses', contract.status))}</span>`);
  if (contract.includeInNetWorth === false) badges.push(`<span class="pension-badge">${esc(tr().notCounted)}</span>`);
  if (contract.hasPolicyNumber && contract.policyNumberLast4)
    badges.push(`<span class="pension-badge pension-badge-quiet">…${esc(contract.policyNumberLast4)}</span>`);

  const sub = [contract.tariffName, contract.employerName].filter(Boolean).map(esc).join(' · ');
  const own = contribution && Number(contribution.employeeAmount) > 0
    ? `<div class="row-sub">${esc(tr().employeeMonthly)}: ${money(contribution.employeeAmount, contribution.currency)}</div>`
    : '';

  return `<button type="button" class="row pension-row" data-pension-open="${esc(contract.id)}">
      <div class="row-main">
        <div class="row-title">${esc(contract.providerName)}</div>
        ${sub ? `<div class="row-sub">${sub}</div>` : ''}
        <div class="pension-badges">${badges.join('')}</div>
        ${own}
      </div>
      <div class="pension-row-value">
        <div class="amount">${snapshot ? money(snapshot.balance, snapshot.currency) : '—'}</div>
        <div class="row-sub">${snapshot ? `${esc(tr().asOf)} ${ctx.date(snapshot.effectiveDate)}` : esc(tr().noneYet)}</div>
      </div>
      <span class="pension-row-chevron" aria-hidden="true">›</span>
    </button>`;
}

// ---- Verlauf ----

function historyHtml() {
  const rows = [];
  contracts.forEach(contract => {
    if (!contract.currentSnapshot) return;
    rows.push({ contract, snapshot: contract.currentSnapshot });
  });
  if (!contracts.length) return sectionCard(tr().title, `<div class="row-sub">${esc(tr().empty)}</div>`);
  if (!rows.length) return sectionCard(tr().tabHistory, `<div class="row-sub">${esc(tr().emptyHistory)}</div>`);

  // The list view shows the current value per contract; the full dated history of one contract is one
  // click away in its detail, where every earlier value is still exactly what it was.
  const body = rows
    .sort((a, b) => String(b.snapshot.effectiveDate).localeCompare(String(a.snapshot.effectiveDate)))
    .map(({ contract, snapshot }) => `<button type="button" class="row pension-row" data-pension-open="${esc(contract.id)}">
        <div class="row-main">
          <div class="row-title">${esc(contract.providerName)}</div>
          <div class="row-sub">${esc(tr().asOf)} ${ctx.date(snapshot.effectiveDate)} · ${esc(label('sources', snapshot.source))}</div>
        </div>
        <div class="pension-row-value">
          <div class="amount">${money(snapshot.balance, snapshot.currency)}</div>
          <div class="row-sub">${esc(tr().tabHistory)}: ${esc(String(contract.snapshotCount || 0))}</div>
        </div>
        <span class="pension-row-chevron" aria-hidden="true">›</span>
      </button>`)
    .join('');
  return sectionCard(tr().tabHistory, `<div class="rows pension-list">${body}</div>`);
}

// ---- detail ----

async function openDetail(contractId) {
  let detail;
  try {
    detail = await ctx.api(`api/pension/contracts/${contractId}`);
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    return;
  }

  const contract = detail.contract;
  const dlg = ctx.dialog(`<div class="dialog-card pension-detail">
      <div class="panel-head">
        <h2>${esc(contract.providerName)}</h2>
        <button type="button" data-close aria-label="${esc(tr().close)}">×</button>
      </div>
      <div class="pension-detail-body">
        ${detailFactsHtml(contract)}
        ${detailValueHtml(contract.currentSnapshot)}
        ${detailListHtml(tr().sections.snapshots, detail.snapshots.map(snapshotRow), 'snapshot')}
        ${detailListHtml(tr().sections.contributions, detail.contributions.map(contributionRow), 'contribution')}
        ${detailListHtml(tr().sections.costs, detail.costs.map(costRow), 'cost')}
        ${detailListHtml(tr().sections.funds, detail.allocations.map(allocationRow), 'fund')}
      </div>
      <div class="dialog-actions pension-detail-actions">
        <button type="button" class="btn btn-secondary" data-pension-edit>${esc(tr().edit)}</button>
        <button type="button" class="btn btn-danger" data-pension-delete>${esc(tr().del)}</button>
      </div>
    </div>`, { mobileMode: 'sheet' });

  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-pension-edit]').onclick = () => { dlg.close(); openContractDialog(contract); };
  dlg.querySelector('[data-pension-delete]').onclick = async () => {
    if (!await ctx.confirm(tr().deleteConfirm, { destructive: true, confirmLabel: tr().del })) return;
    try {
      await ctx.api(`api/pension/contracts/${contract.id}`, { method: 'DELETE' });
      dlg.close();
      ctx.toast(tr().deleted);
      await renderPension(ctx);
    } catch (error) { ctx.toast(errorText(error)); }
  };
  dlg.querySelector('[data-pension-new="snapshot"]').onclick = () => { dlg.close(); openSnapshotDialog(contract); };
  dlg.querySelector('[data-pension-new="contribution"]').onclick = () => { dlg.close(); openContributionDialog(contract); };
  dlg.querySelector('[data-pension-new="cost"]').onclick = () => { dlg.close(); openCostDialog(contract); };
  dlg.querySelector('[data-pension-new="fund"]').onclick = () => { dlg.close(); openAllocationDialog(contract); };
  dlg.showModal();
}

function detailFactsHtml(contract) {
  const facts = [
    [tr().route, label('routes', contract.implementationRoute)],
    [tr().status, label('statuses', contract.status)],
    [tr().employer, contract.employerName],
    [tr().policyHolder, contract.policyHolderName],
    [tr().insuredPerson, contract.insuredPersonName],
    [tr().policyNumber, contract.hasPolicyNumber ? tr().policyStored.replace('{last4}', contract.policyNumberLast4 || '') : null],
    [tr().tariff, contract.tariffName],
    [tr().startDate, contract.startDate ? ctx.date(contract.startDate) : null],
    [tr().retirementDate, contract.retirementDate ? ctx.date(contract.retirementDate) : null],
    [tr().endDate, contract.contractEndDate ? ctx.date(contract.contractEndDate) : null],
    [tr().guaranteeQuota, contract.guaranteeQuotaPercent == null ? null : `${percent(contract.guaranteeQuotaPercent)} %`],
    [tr().annuityFactor, contract.guaranteedAnnuityFactor == null ? null : percent(contract.guaranteedAnnuityFactor)],
    [tr().includeInNetWorth, contract.includeInNetWorth === false ? tr().notCounted : null]
  ].filter(([, value]) => value != null && value !== '');

  const hint = tr().statusHints[contract.status];
  return `<dl class="pension-facts">${facts.map(([key, value]) =>
    `<div><dt>${esc(key)}</dt><dd>${esc(String(value))}</dd></div>`).join('')}</dl>
    ${hint ? `<p class="row-sub pension-status-hint">${esc(hint)}</p>` : ''}
    ${contract.notes ? `<p class="row-sub">${esc(contract.notes)}</p>` : ''}`;
}

function detailValueHtml(snapshot) {
  if (!snapshot) return `<p class="row-sub">${esc(tr().emptyHistory)}</p>`;
  const money2 = (value) => money(value, snapshot.currency);
  const current = [
    [tr().balance, snapshot.balance],
    [tr().guaranteedBalance, snapshot.guaranteedBalance],
    [tr().surrenderValue, snapshot.surrenderValue],
    [tr().securityAssets, snapshot.securityAssetsAmount],
    [tr().fundAssets, snapshot.fundAssetsAmount]
  ].filter(([, value]) => value != null);
  const guaranteed = [
    [tr().guaranteedCapital, snapshot.guaranteedCapitalAtRetirement],
    [tr().guaranteedMonthly, snapshot.guaranteedMonthlyAnnuity]
  ].filter(([, value]) => value != null);
  const projected = [
    [tr().projectedCapital, snapshot.projectedCapitalAtRetirement],
    [tr().projectedMonthly, snapshot.projectedMonthlyAnnuity]
  ].filter(([, value]) => value != null);

  // Three separate blocks on purpose: today's money, what is guaranteed, and what is only projected.
  // A single mixed table is how a projection ends up read as a balance.
  return `
    <div class="pension-value-block">
      <div class="pension-value-head">${esc(tr().asOf)} ${ctx.date(snapshot.effectiveDate)} · ${esc(label('sources', snapshot.source))}</div>
      <dl class="pension-facts">${current.map(([key, value]) => `<div><dt>${esc(key)}</dt><dd class="amount">${money2(value)}</dd></div>`).join('')}</dl>
    </div>
    ${guaranteed.length ? `<div class="pension-value-block">
      <div class="pension-value-head">${esc(tr().guaranteedCapital)}</div>
      <dl class="pension-facts">${guaranteed.map(([key, value]) => `<div><dt>${esc(key)}</dt><dd class="amount">${money2(value)}</dd></div>`).join('')}</dl>
    </div>` : ''}
    ${projected.length ? `<div class="pension-value-block pension-value-projected">
      <div class="pension-value-head">${esc(tr().projectedAnnuity)}${snapshot.projectionIsSimulation ? ` · <span class="pension-badge pension-badge-quiet">${esc(tr().simulationBadge)}</span>` : ''}</div>
      <dl class="pension-facts">${projected.map(([key, value]) => `<div><dt>${esc(key)}</dt><dd class="amount">${money2(value)}</dd></div>`).join('')}</dl>
      <p class="row-sub">${esc(tr().projectionHint)}${snapshot.projectionReturnPercent == null ? '' : ` ${esc(tr().projectionReturn)}: ${percent(snapshot.projectionReturnPercent)} %`}${snapshot.projectionBasis ? ` · ${esc(label('bases', snapshot.projectionBasis))}` : ''}</p>
    </div>` : ''}`;
}

function detailListHtml(title, rows, newKind) {
  const label2 = { snapshot: tr().newSnapshot, contribution: tr().newContribution, cost: tr().newCost, fund: tr().newFund }[newKind];
  return `<section class="pension-section">
      <div class="pension-section-head">
        <h3>${esc(title)}</h3>
        <button type="button" class="btn btn-secondary" data-pension-new="${newKind}">${esc(label2)}</button>
      </div>
      <div class="rows">${rows.length ? rows.join('') : `<div class="row state-empty"><div class="row-sub">${esc(tr().noneYet)}</div></div>`}</div>
    </section>`;
}

function snapshotRow(snapshot) {
  const badges = [`<span class="pension-badge pension-badge-quiet">${esc(label('sources', snapshot.source))}</span>`];
  if (snapshot.isCurrent) badges.push(`<span class="pension-badge">${esc(tr().asOf)}</span>`);
  if (snapshot.projectionIsSimulation) badges.push(`<span class="pension-badge pension-badge-quiet">${esc(tr().simulationBadge)}</span>`);
  return `<div class="row">
      <div class="row-main">
        <div class="row-title">${ctx.date(snapshot.effectiveDate)}</div>
        <div class="pension-badges">${badges.join('')}</div>
        ${snapshot.note ? `<div class="row-sub">${esc(snapshot.note)}</div>` : ''}
      </div>
      <div class="amount">${money(snapshot.balance, snapshot.currency)}</div>
    </div>`;
}

function contributionRow(row) {
  const period = row.validUntil
    ? `${ctx.date(row.validFrom)} – ${ctx.date(row.validUntil)}${row.endReason ? ` · ${label('endReasons', row.endReason)}` : ''}`
    : `${tr().validFrom} ${ctx.date(row.validFrom)}`;
  const badges = [`<span class="pension-badge pension-badge-quiet">${esc(label('cycles', row.cycle))}</span>`];
  if (row.totalMismatch) badges.push(`<span class="pension-badge pension-badge-warn">${esc(tr().mismatch)}</span>`);
  if (row.taxEffectIsSimulation) badges.push(`<span class="pension-badge pension-badge-quiet">${esc(tr().simulationBadge)}</span>`);
  if (row.taxEffectIsStated) badges.push(`<span class="pension-badge">${esc(tr().statedBadge)}</span>`);

  const effect = row.netEffortAmount != null || row.taxSavingAmount != null
    ? `<div class="row-sub">${esc(tr().taxEffect)}: ${[
        row.taxSavingAmount != null ? `${tr().taxSaving} ${plainMoney(row.taxSavingAmount, row.currency)}` : null,
        row.socialSecuritySavingAmount != null ? `${tr().socialSaving} ${plainMoney(row.socialSecuritySavingAmount, row.currency)}` : null,
        row.netEffortAmount != null ? `${tr().netEffort} ${plainMoney(row.netEffortAmount, row.currency)}` : null
      ].filter(Boolean).map(esc).join(' · ')}${row.taxEffectSourceReference ? ` (${esc(row.taxEffectSourceReference)})` : ''}</div>`
    : '';

  return `<div class="row">
      <div class="row-main">
        <div class="row-title">${esc(period)}</div>
        <div class="row-sub">${esc(tr().employeeAmount)}: ${money(row.employeeAmount, row.currency)} · ${esc(tr().employerMonthly)}: ${money(row.employerTotalAmount, row.currency)}</div>
        <div class="pension-badges">${badges.join('')}</div>
        ${effect}
      </div>
      <div class="amount">${money(row.partsTotalAmount, row.currency)}</div>
    </div>`;
}

function costRow(row) {
  const figure = row.basis === 'fixed_amount'
    ? money(row.amount, row.currency)
    : `${percent(row.percent)} %`;
  const badges = [`<span class="pension-badge pension-badge-quiet">${esc(label('costBases', row.basis))}</span>`,
    `<span class="pension-badge pension-badge-quiet">${esc(label('costTimings', row.timing))}</span>`];
  if (row.isEstimated) badges.push(`<span class="pension-badge pension-badge-warn">${esc(tr().estimateBadge)}</span>`);
  if (row.continuesWhenPaidUp) badges.push(`<span class="pension-badge pension-badge-quiet">${esc(tr().continuesWhenPaidUp)}</span>`);
  return `<div class="row">
      <div class="row-main">
        <div class="row-title">${esc(label('costKinds', row.kind))}</div>
        <div class="pension-badges">${badges.join('')}</div>
        ${row.isEstimated && row.estimateBasis ? `<div class="row-sub">${esc(row.estimateBasis)}</div>` : ''}
      </div>
      <div class="amount">${figure}</div>
    </div>`;
}

function allocationRow(row) {
  const badges = [`<span class="pension-badge pension-badge-quiet">${esc(label('assetClasses', row.assetClass))}</span>`];
  if (row.isin) badges.push(`<span class="pension-badge pension-badge-quiet">${esc(row.isin)}</span>`);
  if (row.ongoingChargesPercent != null)
    badges.push(`<span class="pension-badge pension-badge-quiet">${esc(tr().ongoingCharges)}: ${percent(row.ongoingChargesPercent)} %${row.ongoingChargesEstimated ? ` (${esc(tr().estimateBadge)})` : ''}</span>`);
  return `<div class="row">
      <div class="row-main">
        <div class="row-title">${esc(row.fundName)}</div>
        <div class="pension-badges">${badges.join('')}</div>
      </div>
      <div class="amount">${row.weightPercent == null ? money(row.amount, row.currency) : `${percent(row.weightPercent)} %`}</div>
    </div>`;
}

// ---- write dialogs ----

function openContractDialog(contract) {
  const editing = !!contract;
  const body = `
    ${field(tr().provider, `<input name="providerName" type="text" required maxlength="200" value="${esc(contract?.providerName || '')}">`)}
    ${field(tr().tariff, `<input name="tariffName" type="text" maxlength="200" value="${esc(contract?.tariffName || '')}">`)}
    ${field(tr().policyNumber, `<input name="policyNumber" type="text" maxlength="60" autocomplete="off" value="">`, editing && contract.hasPolicyNumber ? `${tr().policyStored.replace('{last4}', contract.policyNumberLast4 || '')} · ${tr().policyNumberHint}` : tr().policyNumberHint)}
    ${field(tr().route, select('implementationRoute', ROUTES, contract?.implementationRoute || 'direct_insurance', key => label('routes', key)))}
    ${field(tr().status, select('status', STATUSES, contract?.status || 'active', key => label('statuses', key)), tr().statusHints.paid_up)}
    ${field(tr().employer, `<input name="employerName" type="text" maxlength="200" value="${esc(contract?.employerName || '')}">`)}
    ${field(tr().policyHolder, `<input name="policyHolderName" type="text" maxlength="200" value="${esc(contract?.policyHolderName || '')}">`)}
    ${field(tr().insuredPerson, `<input name="insuredPersonName" type="text" maxlength="200" value="${esc(contract?.insuredPersonName || '')}">`)}
    ${field(tr().startDate, `<input name="startDate" type="date" value="${esc(contract?.startDate || '')}">`)}
    ${field(tr().retirementDate, `<input name="retirementDate" type="date" value="${esc(contract?.retirementDate || '')}">`)}
    ${field(tr().endDate, `<input name="contractEndDate" type="date" value="${esc(contract?.contractEndDate || '')}">`)}
    ${field(tr().currency, `<input name="currency" type="text" maxlength="3" minlength="3" pattern="[A-Za-z]{3}" required value="${esc(contract?.currency || 'EUR')}">`)}
    ${field(tr().guaranteeQuota, numberInput('guaranteeQuotaPercent', contract?.guaranteeQuotaPercent, { min: 0, max: 100, step: '0.01' }))}
    ${field(tr().annuityFactor, numberInput('guaranteedAnnuityFactor', contract?.guaranteedAnnuityFactor, { min: 0, step: '0.01' }), tr().annuityFactorHint)}
    ${checkbox('fundSelectionChangeable', tr().fundChangeable, !!contract?.fundSelectionChangeable)}
    ${checkbox('includeInNetWorth', tr().includeInNetWorth, contract ? contract.includeInNetWorth !== false : true, tr().includeHint)}
    ${field(tr().notes, `<textarea name="notes" rows="2" maxlength="2000">${esc(contract?.notes || '')}</textarea>`)}`;

  formDialog(editing ? tr().edit : tr().add, body, async form => {
    const payload = {
      providerName: text(form, 'providerName'),
      tariffName: text(form, 'tariffName'),
      policyNumber: text(form, 'policyNumber'),
      implementationRoute: text(form, 'implementationRoute'),
      status: text(form, 'status'),
      employerName: text(form, 'employerName'),
      policyHolderName: text(form, 'policyHolderName'),
      insuredPersonName: text(form, 'insuredPersonName'),
      startDate: text(form, 'startDate'),
      retirementDate: text(form, 'retirementDate'),
      contractEndDate: text(form, 'contractEndDate'),
      currency: (text(form, 'currency') || 'EUR').toUpperCase(),
      guaranteeQuotaPercent: num(form, 'guaranteeQuotaPercent'),
      guaranteedAnnuityFactor: num(form, 'guaranteedAnnuityFactor'),
      fundSelectionChangeable: bool(form, 'fundSelectionChangeable'),
      includeInNetWorth: bool(form, 'includeInNetWorth'),
      notes: text(form, 'notes')
    };
    // Editing keeps the stored number when the field is left blank; typing a new one replaces it.
    if (editing && !payload.policyNumber) delete payload.policyNumber;
    const path = editing ? `api/pension/contracts/${contract.id}` : 'api/pension/contracts';
    await ctx.api(path, ctx.jsonBody(payload, editing ? 'PUT' : 'POST'));
  });
}

function openSnapshotDialog(contract) {
  const cur = contract.currency;
  const body = `
    ${field(tr().effectiveDate, `<input name="effectiveDate" type="date" required max="${today()}" value="${today()}">`)}
    ${field(`${tr().balance} (${cur})`, numberInput('balance', null, { min: 0, step: '0.01' }))}
    ${field(`${tr().guaranteedBalance} (${cur})`, numberInput('guaranteedBalance', null, { min: 0, step: '0.01' }))}
    ${field(`${tr().surrenderValue} (${cur})`, numberInput('surrenderValue', null, { min: 0, step: '0.01' }))}
    ${field(`${tr().securityAssets} (${cur})`, numberInput('securityAssetsAmount', null, { min: 0, step: '0.01' }))}
    ${field(`${tr().fundAssets} (${cur})`, numberInput('fundAssetsAmount', null, { min: 0, step: '0.01' }))}
    ${field(`${tr().guaranteedCapital} (${cur})`, numberInput('guaranteedCapitalAtRetirement', null, { min: 0, step: '0.01' }))}
    ${field(`${tr().guaranteedMonthly} (${cur})`, numberInput('guaranteedMonthlyAnnuity', null, { min: 0, step: '0.01' }))}
    ${field(`${tr().projectedCapital} (${cur})`, numberInput('projectedCapitalAtRetirement', null, { min: 0, step: '0.01' }))}
    ${field(`${tr().projectedMonthly} (${cur})`, numberInput('projectedMonthlyAnnuity', null, { min: 0, step: '0.01' }))}
    ${field(tr().projectionReturn, numberInput('projectionReturnPercent', null, { min: -100, max: 100, step: '0.01' }), tr().projectionBasisHint)}
    ${field(tr().projectionBasis, select('projectionBasis', PROJECTION_BASES, 'document_forecast', key => label('bases', key), true))}
    ${field(tr().valueSource, select('source', VALUE_SOURCES, 'manual', key => label('sources', key)))}
    ${field(tr().note, `<input name="note" type="text" maxlength="500" value="">`)}`;

  formDialog(`${tr().newSnapshot} · ${contract.providerName}`, body, async form => {
    const projectedCapital = num(form, 'projectedCapitalAtRetirement');
    const projectedAnnuity = num(form, 'projectedMonthlyAnnuity');
    const hasProjection = projectedCapital != null || projectedAnnuity != null;
    await ctx.api(`api/pension/contracts/${contract.id}/snapshots`, ctx.jsonBody({
      effectiveDate: text(form, 'effectiveDate'),
      currency: contract.currency,
      balance: num(form, 'balance'),
      guaranteedBalance: num(form, 'guaranteedBalance'),
      surrenderValue: num(form, 'surrenderValue'),
      securityAssetsAmount: num(form, 'securityAssetsAmount'),
      fundAssetsAmount: num(form, 'fundAssetsAmount'),
      guaranteedCapitalAtRetirement: num(form, 'guaranteedCapitalAtRetirement'),
      guaranteedMonthlyAnnuity: num(form, 'guaranteedMonthlyAnnuity'),
      projectedCapitalAtRetirement: projectedCapital,
      projectedMonthlyAnnuity: projectedAnnuity,
      // A projection is only ever sent together with its assumption; without one the server refuses it,
      // and rightly so.
      projectionReturnPercent: hasProjection ? num(form, 'projectionReturnPercent') : null,
      projectionBasis: hasProjection ? (text(form, 'projectionBasis') || null) : null,
      source: text(form, 'source'),
      note: text(form, 'note')
    }));
  });
}

function openContributionDialog(contract) {
  const cur = contract.currency;
  const body = `
    ${field(tr().validFrom, `<input name="validFrom" type="date" required value="${today()}">`)}
    ${field(tr().validUntil, `<input name="validUntil" type="date" value="">`)}
    ${field(tr().endReason, select('endReason', END_REASONS, '', key => label('endReasons', key), true), tr().statusHints.paid_up)}
    ${field(tr().cycle, select('cycle', CYCLES, 'monthly', key => label('cycles', key)))}
    ${field(`${tr().employeeAmount} (${cur})`, numberInput('employeeAmount', 0, { min: 0, step: '0.01' }))}
    ${field(`${tr().employerSubsidy} (${cur})`, numberInput('employerSubsidyAmount', 0, { min: 0, step: '0.01' }))}
    ${field(`${tr().employerAmount} (${cur})`, numberInput('employerAmount', 0, { min: 0, step: '0.01' }), tr().employerHint)}
    ${field(`${tr().statedTotal} (${cur})`, numberInput('statedTotalAmount', null, { min: 0, step: '0.01' }), tr().statedTotalHint)}
    ${field(tr().valueSource, select('source', VALUE_SOURCES, 'manual', key => label('sources', key)))}
    <fieldset class="pension-fieldset">
      <legend>${esc(tr().taxEffect)}</legend>
      <p class="row-sub">${esc(tr().taxEffectHint)}</p>
      ${field(`${tr().taxSaving} (${cur})`, numberInput('taxSavingAmount', null, { min: 0, step: '0.01' }))}
      ${field(`${tr().socialSaving} (${cur})`, numberInput('socialSecuritySavingAmount', null, { min: 0, step: '0.01' }))}
      ${field(`${tr().netEffort} (${cur})`, numberInput('netEffortAmount', null, { min: 0, step: '0.01' }))}
      ${field(tr().taxEffectSource, select('taxEffectSource', TAX_SOURCES, '', key => label('taxSources', key), true))}
      ${field(tr().taxEffectReference, `<input name="taxEffectSourceReference" type="text" maxlength="200" value="">`)}
    </fieldset>
    ${field(tr().note, `<input name="note" type="text" maxlength="500" value="">`)}`;

  formDialog(`${tr().newContribution} · ${contract.providerName}`, body, async form => {
    const tax = num(form, 'taxSavingAmount');
    const social = num(form, 'socialSecuritySavingAmount');
    const net = num(form, 'netEffortAmount');
    const hasEffect = tax != null || social != null || net != null;
    await ctx.api(`api/pension/contracts/${contract.id}/contributions`, ctx.jsonBody({
      validFrom: text(form, 'validFrom'),
      validUntil: text(form, 'validUntil'),
      endReason: text(form, 'validUntil') ? (text(form, 'endReason') || null) : null,
      cycle: text(form, 'cycle'),
      currency: contract.currency,
      employeeAmount: num(form, 'employeeAmount') ?? 0,
      employerSubsidyAmount: num(form, 'employerSubsidyAmount') ?? 0,
      employerAmount: num(form, 'employerAmount') ?? 0,
      statedTotalAmount: num(form, 'statedTotalAmount'),
      source: text(form, 'source'),
      taxSavingAmount: tax,
      socialSecuritySavingAmount: social,
      netEffortAmount: net,
      // Nothing is invented: with no amount there is no source, and with an amount the source is what
      // the owner picked — document, payslip, or an explicit simulation.
      taxEffectSource: hasEffect ? (text(form, 'taxEffectSource') || null) : null,
      taxEffectSourceReference: text(form, 'taxEffectSourceReference'),
      note: text(form, 'note')
    }));
  });
}

function openCostDialog(contract) {
  const cur = contract.currency;
  const body = `
    ${field(tr().effectiveDate, `<input name="effectiveDate" type="date" required value="${today()}">`)}
    ${field(tr().costKind, select('kind', COST_KINDS, 'administration_on_capital', key => label('costKinds', key)))}
    ${field(tr().costBasis, select('basis', COST_BASES, 'percent_of_capital', key => label('costBases', key)))}
    ${field(`${tr().amount} (${cur})`, numberInput('amount', null, { min: 0, step: '0.01' }))}
    ${field(tr().percent, numberInput('percent', null, { min: 0, max: 100, step: '0.001' }))}
    ${field(tr().costTiming, select('timing', COST_TIMINGS, 'ongoing', key => label('costTimings', key)))}
    ${checkbox('isEstimated', tr().isEstimated, false)}
    ${field(tr().estimateBasis, `<input name="estimateBasis" type="text" maxlength="300" value="">`, tr().estimateBasisHint)}
    ${checkbox('continuesWhenPaidUp', tr().continuesWhenPaidUp, true, tr().statusHints.paid_up)}
    ${field(tr().valueSource, select('source', VALUE_SOURCES, 'manual', key => label('sources', key)))}
    ${field(tr().note, `<input name="note" type="text" maxlength="500" value="">`)}`;

  formDialog(`${tr().newCost} · ${contract.providerName}`, body, async form => {
    const basis = text(form, 'basis');
    const estimated = bool(form, 'isEstimated');
    await ctx.api(`api/pension/contracts/${contract.id}/costs`, ctx.jsonBody({
      effectiveDate: text(form, 'effectiveDate'),
      kind: text(form, 'kind'),
      basis,
      // A fixed cost carries an amount and a percentage cost a percentage — never both.
      amount: basis === 'fixed_amount' ? num(form, 'amount') : null,
      percent: basis === 'fixed_amount' ? null : num(form, 'percent'),
      currency: contract.currency,
      timing: text(form, 'timing'),
      isEstimated: estimated,
      estimateBasis: estimated ? text(form, 'estimateBasis') : null,
      continuesWhenPaidUp: bool(form, 'continuesWhenPaidUp'),
      source: text(form, 'source'),
      note: text(form, 'note')
    }));
  });
}

function openAllocationDialog(contract) {
  const cur = contract.currency;
  const body = `
    ${field(tr().effectiveDate, `<input name="effectiveDate" type="date" required value="${today()}">`)}
    ${field(tr().fundName, `<input name="fundName" type="text" required maxlength="300" value="">`)}
    ${field(tr().isin, `<input name="isin" type="text" maxlength="14" autocomplete="off" value="">`)}
    ${field(tr().weight, numberInput('weightPercent', null, { min: 0, max: 100, step: '0.01' }))}
    ${field(`${tr().amount} (${cur})`, numberInput('amount', null, { min: 0, step: '0.01' }))}
    ${field(tr().ongoingCharges, numberInput('ongoingChargesPercent', null, { min: 0, max: 100, step: '0.001' }))}
    ${checkbox('ongoingChargesEstimated', tr().chargesEstimated, false)}
    ${field(tr().assetClass, select('assetClass', ASSET_CLASSES, 'equity', key => label('assetClasses', key)))}
    ${field(tr().valueSource, select('source', VALUE_SOURCES, 'manual', key => label('sources', key)))}
    ${field(tr().note, `<input name="note" type="text" maxlength="500" value="">`)}`;

  formDialog(`${tr().newFund} · ${contract.providerName}`, body, async form => {
    await ctx.api(`api/pension/contracts/${contract.id}/allocations`, ctx.jsonBody({
      effectiveDate: text(form, 'effectiveDate'),
      fundName: text(form, 'fundName'),
      isin: text(form, 'isin'),
      weightPercent: num(form, 'weightPercent'),
      amount: num(form, 'amount'),
      currency: contract.currency,
      ongoingChargesPercent: num(form, 'ongoingChargesPercent'),
      ongoingChargesEstimated: bool(form, 'ongoingChargesEstimated'),
      assetClass: text(form, 'assetClass'),
      source: text(form, 'source'),
      note: text(form, 'note')
    }));
  });
}

// ---- form plumbing ----

function formDialog(title, body, submit) {
  const dlg = ctx.dialog(`<form class="dialog-card pension-form">
      <div class="panel-head"><h2>${esc(title)}</h2><button type="button" data-close aria-label="${esc(tr().close)}">×</button></div>
      <div class="pension-form-body">${body}</div>
      <div class="dialog-actions">
        <button type="button" class="btn btn-secondary" data-cancel>${esc(tr().cancel)}</button>
        <button type="submit" class="btn btn-primary">${esc(tr().save)}</button>
      </div>
    </form>`, { mobileMode: 'sheet' });

  dlg.querySelector('[data-close]').onclick = () => dlg.close();
  dlg.querySelector('[data-cancel]').onclick = () => dlg.close();
  dlg.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const button = form.querySelector('[type="submit"]');
    button.disabled = true;
    try {
      await submit(form);
      dlg.close();
      ctx.toast(tr().saved);
      await renderPension(ctx);
    } catch (error) {
      ctx.toast(errorText(error));
    } finally {
      button.disabled = false;
    }
  };
  dlg.showModal();
}

// A 409 is the model doing its job: the contract or the date already exists, and history is added to
// rather than rewritten. Say that in words instead of showing a status code.
function errorText(error) {
  if (error?.status === 403) return tr().ownerOnly;
  if (error?.status === 409) {
    if (error.detail?.existingContractId) return tr().duplicateContract;
    if (error.detail?.existingSnapshotId) return tr().duplicateSnapshot;
  }
  return error?.message || ctx.get('common.error');
}

function field(labelText, control, hint) {
  return `<label class="field pension-field"><span>${esc(labelText)}</span>${control}${hint ? `<small class="row-sub">${esc(hint)}</small>` : ''}</label>`;
}

function checkbox(name, labelText, checked, hint) {
  return `<label class="check pension-check"><input name="${name}" type="checkbox"${checked ? ' checked' : ''}><span>${esc(labelText)}</span></label>${hint ? `<small class="row-sub pension-check-hint">${esc(hint)}</small>` : ''}`;
}

function select(name, keys, selected, labelOf, allowEmpty = false) {
  const empty = allowEmpty ? `<option value=""${selected ? '' : ' selected'}>—</option>` : '';
  return `<select name="${name}">${empty}${keys.map(key =>
    `<option value="${esc(key)}"${key === selected ? ' selected' : ''}>${esc(labelOf(key))}</option>`).join('')}</select>`;
}

function numberInput(name, value, opts = {}) {
  const attrs = [
    opts.min != null ? `min="${opts.min}"` : '',
    opts.max != null ? `max="${opts.max}"` : '',
    opts.step ? `step="${opts.step}"` : 'step="any"'
  ].filter(Boolean).join(' ');
  return `<input name="${name}" type="number" inputmode="decimal" ${attrs} value="${value == null ? '' : esc(String(value))}">`;
}

function text(form, name) {
  const value = form.elements[name];
  if (!value) return null;
  const raw = String(value.value ?? '').trim();
  return raw === '' ? null : raw;
}

function num(form, name) {
  const raw = text(form, name);
  if (raw == null) return null;
  // A number input reports a machine-readable value, so there is no locale parsing here at all.
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : null;
}

function bool(form, name) {
  return !!form.elements[name]?.checked;
}

function money(value, currency) {
  if (value == null) return '—';
  return ctx.money(Number(value), currency || 'EUR');
}

function plainMoney(value, currency) {
  return value == null ? '—' : String(ctx.money(Number(value), currency || 'EUR'));
}

function percent(value) {
  if (value == null) return '—';
  const number = Number(value);
  if (!Number.isFinite(number)) return '—';
  return new Intl.NumberFormat(lang() === 'en' ? 'en-US' : 'de-DE', { maximumFractionDigits: 3 }).format(number);
}
