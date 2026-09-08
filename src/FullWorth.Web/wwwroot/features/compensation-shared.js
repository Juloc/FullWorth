// Shared compensation module. Single source of truth for everything the calculator, history and extended
// (optimizer/payslips) views had triplicated: DOM/format helpers, the profile <-> form mapping, company-car
// factor derivation, benefit rows and the API/toast wrappers. Feature files import from here — no copies.
import { api as backendApi, jsonBody as sharedJsonBody } from '../core/services.js';

export const $ = s => document.querySelector(s);
export const $$ = s => [...document.querySelectorAll(s)];

export const euro = new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR', maximumFractionDigits: 0 });
export const euro2 = new Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR', minimumFractionDigits: 2, maximumFractionDigits: 2 });
export const pct = v => `${Number(v || 0).toLocaleString('de-DE', { minimumFractionDigits: 1, maximumFractionDigits: 2 })} %`;
export const signedEuro = v => { const x = Number(v || 0); return `${x >= 0 ? '+' : '−'}${euro2.format(Math.abs(x))}`; };
export const signedEuro0 = v => { const x = Number(v || 0); return `${x >= 0 ? '+' : '−'}${euro.format(Math.abs(x))}`; };
export const signedPct = v => { const x = Number(v || 0); return `${x >= 0 ? '+' : '−'}${pct(Math.abs(x))}`; };

export const esc = v => String(v ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
export const attr = esc;
export const numAttr = v => { const n = Number(v); return Number.isFinite(n) ? String(n) : '0'; };

export const val = id => $(`#${id}`)?.value ?? '';
export const num = id => Number(val(id)) || 0;
export const setVal = (id, v) => { const el = $(`#${id}`); if (el) el.value = v ?? ''; };
export const checked = id => !!$(`#${id}`)?.checked;
export const setChecked = (id, v) => { const el = $(`#${id}`); if (el) el.checked = !!v; };
export const spaceId = () => $('#space-select')?.value || '';

export const monthLabel = v => v ? new Intl.DateTimeFormat('de-DE', { month: 'long', year: 'numeric' }).format(new Date(`${String(v).slice(0, 10)}T12:00:00`)) : '—';
export const fmtDate = v => v ? new Intl.DateTimeFormat('de-DE').format(new Date(`${String(v).slice(0, 10)}T12:00:00`)) : '—';
export const localIsoDate = (d = new Date()) => { const p = n => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };

export const json = (method, body) => sharedJsonBody(body, method);
export async function api(path, options = {}, allow404 = false) {
  try { return await backendApi(path, options); }
  catch (error) { if (allow404 && error?.status === 404) return null; throw error; }
}
export function notify(message) {
  const toast = $('#comp-error');
  if (!toast) return;
  toast.textContent = message;
  toast.classList.add('show');
  clearTimeout(notify.timer);
  notify.timer = setTimeout(() => toast.classList.remove('show'), 3200);
}

export function hybridMinimumRange() {
  const date = val('car-acquisition-date') || '2026-01-01';
  return date >= '2025-01-01' ? 80 : (date >= '2022-01-01' ? 60 : 40);
}

export function deriveCarFactor() {
  const type = val('car-vehicle-type') || 'manual';
  if (type === 'manual') return num('car-factor') || 1;
  if (type === 'combustion') return 1;
  const price = num('car-list-price');
  const date = val('car-acquisition-date') || '2026-01-01';
  if (type === 'electric') {
    const limit = date >= '2025-07-01' ? 100000 : (date >= '2024-01-01' ? 70000 : 60000);
    return price <= limit ? 0.25 : 0.5;
  }
  if (type === 'hybrid') {
    const byRange = num('car-electric-range') >= hybridMinimumRange();
    const co2 = num('car-co2');
    return byRange || (co2 > 0 && co2 <= 50) ? 0.5 : 1;
  }
  return 1;
}

export function addBenefitRow(benefit = {}) {
  const row = document.createElement('div');
  row.className = 'benefit-row';
  row.innerHTML = `
    <label>Name<input data-benefit="name" value="${attr(benefit.name || '')}" placeholder="z. B. Deutschlandticket"></label>
    <label>AG-Kosten / Monat<input data-benefit="employerCostMonthly" type="number" min="0" step="1" value="${numAttr(benefit.employerCostMonthly)}"></label>
    <label>Dein Wert / Monat<input data-benefit="personalValueMonthly" type="number" min="0" step="1" value="${numAttr(benefit.personalValueMonthly)}"></label>
    <label>Steuerpflichtig / Monat<input data-benefit="taxableBenefitMonthly" type="number" min="0" step="1" value="${numAttr(benefit.taxableBenefitMonthly)}"></label>
    <label>Eigenkosten / Monat<input data-benefit="employeeCostMonthly" type="number" min="0" step="1" value="${numAttr(benefit.employeeCostMonthly)}"></label>
    <button type="button" class="btn btn-danger" aria-label="Benefit entfernen">×</button>`;
  row.querySelector('button').addEventListener('click', () => row.remove());
  $('#benefits-list')?.appendChild(row);
}

export function readBenefits() {
  return $$('.benefit-row').map(row => {
    const input = name => row.querySelector(`[data-benefit="${name}"]`);
    return {
      name: input('name').value.trim() || 'Benefit',
      employerCostMonthly: Number(input('employerCostMonthly').value) || 0,
      personalValueMonthly: Number(input('personalValueMonthly').value) || 0,
      taxableBenefitMonthly: Number(input('taxableBenefitMonthly').value) || 0,
      employeeCostMonthly: Number(input('employeeCostMonthly').value) || 0,
    };
  });
}

// The calculator form is the single input surface; every view reads/writes it through these two functions.
export function readProfile() {
  const payments = Math.min(14, Math.max(12, Math.round(num('salary-payments') || 12)));
  const mode = val('gross-period') === 'monthly' ? 'monthly' : 'annual';
  const taxClass = Math.round(num('tax-class'));
  return {
    name: val('profile-name') || 'Aktuelles Gehalt',
    annualGross: mode === 'monthly' ? num('gross-input') * payments : num('gross-input'),
    annualBonus: num('annual-bonus'),
    grossInputMode: mode,
    salaryPaymentsPerYear: payments,
    taxClass,
    taxClass4Factor: taxClass === 4 ? Math.min(1, Math.max(0.001, num('tax-class4-factor') || 1)) : 1,
    annualTaxAllowance: Math.max(0, num('annual-tax-allowance')),
    childAllowanceUnits: val('child-allowance-units') === '' ? null : Math.max(0, num('child-allowance-units')),
    stateCode: val('state-code'),
    churchTax: checked('church-tax'),
    childrenUnder25: Math.max(0, Math.round(num('children'))),
    age: val('employee-age') === '' ? null : Math.max(0, Math.round(num('employee-age'))),
    childlessCareSurcharge: checked('childless-surcharge'),
    pensionInsuranceEnabled: checked('pension-insurance'),
    unemploymentInsuranceEnabled: checked('unemployment-insurance'),
    healthInsuranceAdditionalRatePercent: num('health-addon'),
    weeklyHours: num('weekly-hours'),
    vacationDays: Math.round(num('vacation-days')),
    spouseAnnualTaxableIncome: 0,
    companyCar: {
      enabled: checked('car-enabled'),
      listPrice: num('car-list-price'),
      taxableListPriceFactor: deriveCarFactor(),
      vehicleType: val('car-vehicle-type') || 'manual',
      acquisitionDate: val('car-acquisition-date') || null,
      electricRangeKm: num('car-electric-range'),
      co2GramsPerKm: num('car-co2'),
      oneWayCommuteKm: num('car-commute'),
      commuteMethod: val('car-commute-method') || 'monthly',
      commuteDaysPerMonth: Math.max(0, Math.min(31, Math.round(num('car-commute-days')))),
      employeeContributionMonthly: num('car-contribution'),
      employerCostMonthly: num('car-employer-cost'),
      privateAlternativeCostMonthly: num('car-private-cost'),
    },
    occupationalPension: {
      employeeContributionMonthly: num('bav-employee'),
      employerContributionMonthly: num('bav-employer'),
      projectionYears: Math.round(num('bav-years')),
      expectedAnnualReturnPercent: num('bav-return'),
    },
    benefits: readBenefits(),
  };
}

export function fillProfile(profile) {
  const p = profile || {};
  const mode = p.grossInputMode === 'monthly' ? 'monthly' : 'annual';
  const payments = Math.min(14, Math.max(12, Number(p.salaryPaymentsPerYear) || 12));
  setVal('profile-name', p.name);
  setVal('gross-period', mode);
  setVal('salary-payments', payments);
  setVal('gross-input', mode === 'monthly' ? (Number(p.annualGross) || 0) / payments : p.annualGross);
  setVal('annual-bonus', p.annualBonus);
  setVal('tax-class', p.taxClass || 1);
  setVal('tax-class4-factor', p.taxClass4Factor || 1);
  setVal('annual-tax-allowance', p.annualTaxAllowance ?? 0);
  setVal('child-allowance-units', p.childAllowanceUnits ?? '');
  setVal('state-code', p.stateCode || 'BW');
  setChecked('church-tax', p.churchTax);
  setVal('children', p.childrenUnder25 ?? 0);
  setVal('employee-age', p.age ?? '');
  setChecked('childless-surcharge', p.childlessCareSurcharge !== false);
  setChecked('pension-insurance', p.pensionInsuranceEnabled !== false);
  setChecked('unemployment-insurance', p.unemploymentInsuranceEnabled !== false);
  setVal('health-addon', p.healthInsuranceAdditionalRatePercent ?? 2.9);
  setVal('weekly-hours', p.weeklyHours ?? 40);
  setVal('vacation-days', p.vacationDays ?? 30);
  const car = p.companyCar || {};
  setChecked('car-enabled', car.enabled);
  setVal('car-list-price', car.listPrice ?? 50000);
  setVal('car-factor', car.taxableListPriceFactor ?? 1);
  setVal('car-vehicle-type', car.vehicleType || 'manual');
  setVal('car-acquisition-date', car.acquisitionDate || '2026-01-01');
  setVal('car-electric-range', car.electricRangeKm ?? 80);
  setVal('car-co2', car.co2GramsPerKm ?? 50);
  setVal('car-commute', car.oneWayCommuteKm ?? 0);
  setVal('car-commute-method', car.commuteMethod || 'monthly');
  setVal('car-commute-days', car.commuteDaysPerMonth ?? 10);
  setVal('car-contribution', car.employeeContributionMonthly ?? 0);
  setVal('car-employer-cost', car.employerCostMonthly ?? 0);
  setVal('car-private-cost', car.privateAlternativeCostMonthly ?? 0);
  const bav = p.occupationalPension || {};
  setVal('bav-employee', bav.employeeContributionMonthly ?? 0);
  setVal('bav-employer', bav.employerContributionMonthly ?? 0);
  setVal('bav-years', bav.projectionYears ?? 30);
  setVal('bav-return', bav.expectedAnnualReturnPercent ?? 3);
  const list = $('#benefits-list');
  if (list) { list.innerHTML = ''; (p.benefits || []).forEach(addBenefitRow); }
  // Trigger the calculator's bound sync handlers (conditional field visibility) without importing them.
  ['gross-period', 'salary-payments', 'tax-class', 'car-enabled', 'car-vehicle-type', 'car-commute-method']
    .forEach(id => $(`#${id}`)?.dispatchEvent(new Event('change')));
}
