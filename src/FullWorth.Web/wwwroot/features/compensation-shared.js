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
// Empty means "not specified" rather than zero — the backend then applies its own year-based default.
export const numOrNull = id => { const raw = val(id); return raw === "" || raw === null || raw === undefined ? null : (Number(raw) || 0); };
export const setVal = (id, v) => { const el = $(`#${id}`); if (el) el.value = v ?? ''; };
export const checked = id => !!$(`#${id}`)?.checked;
export const setChecked = (id, v) => { const el = $(`#${id}`); if (el) el.checked = !!v; };
export const spaceId = () => $('#space-select')?.value || '';

export const MONTH_NAMES = Array.from({ length: 12 }, (_, i) => new Intl.DateTimeFormat('de-DE', { month: 'long' }).format(new Date(2021, i, 1)));
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

// ── Steuerjahr / Geburtsdatum ────────────────────────────────────────────────────────────────────────
// The profile carries an optional tax year (which year's law to compute with) and an optional birth date.
// Leaving the year on "Automatisch" keeps it null, so historical snapshots stay stamped with the year they
// took effect in (the backend only fills an unset year). The age derivation mirrors the backend's
// EffectiveAge: age reached during the reference year, clamped to 0…120.

/** The explicitly chosen tax year, or null for "automatisch" (newest law). */
export function selectedTaxYear() {
  const raw = val('tax-year');
  const year = Math.round(Number(raw));
  return raw === '' || !Number.isFinite(year) ? null : Math.min(2200, Math.max(1900, year));
}

/** The reference year the age is derived for: the chosen tax year, else the current one. */
export const ageReferenceYear = () => selectedTaxYear() ?? new Date().getFullYear();

/** Age derived from the birth date for the reference year, or null when no birth date is set. */
export function derivedAge() {
  const birth = val('birth-date');
  const birthYear = Math.round(Number(String(birth).slice(0, 4)));
  if (!birth || !Number.isFinite(birthYear)) return null;
  return Math.min(120, Math.max(0, ageReferenceYear() - birthYear));
}

/**
 * A birth date makes the manual age entry redundant: the field then shows the derived age read-only.
 * Without a birth date the manual field keeps working exactly as before (older saved profiles only have it).
 */
export function syncAgeFields() {
  const input = $('#employee-age');
  if (!input) return;
  const help = $('#employee-age-help');
  const age = derivedAge();
  input.disabled = age !== null;
  if (age !== null) input.value = age;
  if (help) {
    help.textContent = age === null
      ? 'Relevant für den Pflegeversicherungs-Zuschlag. Mit Geburtsdatum wird das Alter automatisch berechnet.'
      : `Automatisch aus dem Geburtsdatum: ${age} Jahre im Steuerjahr ${ageReferenceYear()}.`;
  }
}

// ── Einmalige Sonderzahlungen ────────────────────────────────────────────────────────────────────────
// Same row mechanism as the benefit rows above. Each payment can be taxable and/or social-insurance-liable
// independently, so all four German combinations are expressible. The presets only prefill label, month and
// the two flags — the checkboxes stay visible and editable.
export const ONE_OFF_PRESETS = [
  { label: 'Eigene Sonderzahlung', payment: { taxable: true, socialInsuranceLiable: true } },
  { label: 'Weihnachtsgeld', payment: { label: 'Weihnachtsgeld', month: 11, taxable: true, socialInsuranceLiable: true } },
  { label: 'Urlaubsgeld', payment: { label: 'Urlaubsgeld', month: 6, taxable: true, socialInsuranceLiable: true } },
  { label: 'Einmalprämie / Bonus', payment: { label: 'Einmalprämie', taxable: true, socialInsuranceLiable: true } },
  { label: 'Inflationsausgleichsprämie (steuer- & SV-frei)', payment: { label: 'Inflationsausgleichsprämie', taxable: false, socialInsuranceLiable: false } },
  { label: 'Corona-Prämie (steuer- & SV-frei)', payment: { label: 'Corona-Prämie', taxable: false, socialInsuranceLiable: false } },
  { label: 'Energiepreispauschale (steuerpflichtig, SV-frei)', payment: { label: 'Energiepreispauschale', month: 9, taxable: true, socialInsuranceLiable: false } },
];

export const oneOffHint = (taxable, social) => {
  if (!taxable && !social) return 'Steuer- und SV-frei: wird voll ausgezahlt (z. B. Corona-Prämie, Inflationsausgleichsprämie).';
  if (taxable && !social) return 'Steuerpflichtig, aber SV-frei (z. B. Energiepreispauschale).';
  if (!taxable && social) return 'Steuerfrei, aber SV-pflichtig.';
  return 'Voll steuer- und SV-pflichtig (z. B. Weihnachtsgeld, Urlaubsgeld).';
};

function syncOneOffRow(row) {
  const flag = name => !!row.querySelector(`[data-oneoff="${name}"]`)?.checked;
  const hint = row.querySelector('[data-oneoff="hint"]');
  if (hint) hint.textContent = oneOffHint(flag('taxable'), flag('socialInsuranceLiable'));
}

export function addOneOffRow(payment = {}) {
  const row = document.createElement('div');
  row.className = 'oneoff-row';
  const months = MONTH_NAMES
    .map((name, i) => `<option value="${i + 1}"${Math.round(Number(payment.month)) === i + 1 ? ' selected' : ''}>${name}</option>`)
    .join('');
  row.innerHTML = `
    <label>Bezeichnung<input data-oneoff="label" value="${attr(payment.label || '')}" placeholder="z. B. Weihnachtsgeld"></label>
    <label>Betrag<input data-oneoff="amount" type="number" min="0" step="50" value="${numAttr(payment.amount)}"></label>
    <label>Monat<select data-oneoff="month"><option value="0">ohne Monat</option>${months}</select></label>
    <button type="button" class="btn btn-danger" aria-label="Sonderzahlung entfernen">×</button>
    <div class="oneoff-flags">
      <label class="check"><input data-oneoff="taxable" type="checkbox"${payment.taxable === false ? '' : ' checked'}> steuerpflichtig</label>
      <label class="check"><input data-oneoff="socialInsuranceLiable" type="checkbox"${payment.socialInsuranceLiable === false ? '' : ' checked'}> SV-pflichtig</label>
    </div>
    <small class="field-help oneoff-hint" data-oneoff="hint"></small>`;
  row.querySelector('button').addEventListener('click', () => row.remove());
  row.addEventListener('change', () => syncOneOffRow(row));
  syncOneOffRow(row);
  $('#oneoff-list')?.appendChild(row);
}

export function readOneOffPayments() {
  return $$('.oneoff-row').map(row => {
    const field = name => row.querySelector(`[data-oneoff="${name}"]`);
    return {
      label: field('label').value.trim() || 'Sonderzahlung',
      amount: Math.max(0, Number(field('amount').value) || 0),
      month: Math.min(12, Math.max(0, Math.round(Number(field('month').value) || 0))),
      taxable: !!field('taxable').checked,
      socialInsuranceLiable: !!field('socialInsuranceLiable').checked,
    };
  });
}

/** Fills the preset picker once; the markup ships an empty <select> so the list stays in this module. */
export function fillOneOffPresets() {
  const select = $('#oneoff-preset');
  if (!select || select.options.length) return;
  select.innerHTML = ONE_OFF_PRESETS.map((preset, index) => `<option value="${index}">${esc(preset.label)}</option>`).join('');
}

export function addOneOffFromPreset() {
  const index = Math.round(Number(val('oneoff-preset')));
  const preset = ONE_OFF_PRESETS[Number.isFinite(index) ? index : 0] || ONE_OFF_PRESETS[0];
  addOneOffRow({ ...preset.payment });
}

// The calculator form is the single input surface; every view reads/writes it through these two functions.
export function readProfile() {
  const payments = Math.min(14, Math.max(12, Math.round(num('salary-payments') || 12)));
  const mode = val('gross-period') === 'monthly' ? 'monthly' : 'annual';
  const taxClass = Math.round(num('tax-class'));
  // A birth date wins over the manual age (same precedence as the backend), but the manual field stays the
  // fallback so profiles saved before the birth-date field existed keep calculating identically.
  const birthDate = val('birth-date') || null;
  const manualAge = val('employee-age') === '' ? null : Math.max(0, Math.round(num('employee-age')));
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
    age: birthDate ? derivedAge() : manualAge,
    taxYear: selectedTaxYear(),
    birthDate,
    childlessCareSurcharge: checked('childless-surcharge'),
    pensionInsuranceEnabled: checked('pension-insurance'),
    unemploymentInsuranceEnabled: checked('unemployment-insurance'),
    healthInsuranceAdditionalRatePercent: numOrNull('health-addon'),
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
    oneOffPayments: readOneOffPayments(),
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
  setVal('tax-year', p.taxYear ?? '');
  setVal('birth-date', p.birthDate ? String(p.birthDate).slice(0, 10) : '');
  setVal('employee-age', p.age ?? '');
  setChecked('childless-surcharge', p.childlessCareSurcharge !== false);
  setChecked('pension-insurance', p.pensionInsuranceEnabled !== false);
  setChecked('unemployment-insurance', p.unemploymentInsuranceEnabled !== false);
  setVal('health-addon', p.healthInsuranceAdditionalRatePercent ?? '');
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
  if (list) { list.innerHTML = ''; (p.benefits || []).forEach(benefit => addBenefitRow(benefit)); }
  const oneOffList = $('#oneoff-list');
  if (oneOffList) { oneOffList.innerHTML = ''; (p.oneOffPayments || []).forEach(payment => addOneOffRow(payment)); }
  syncAgeFields();
  // Trigger the calculator's bound sync handlers (conditional field visibility) without importing them.
  ['gross-period', 'salary-payments', 'tax-class', 'car-enabled', 'car-vehicle-type', 'car-commute-method']
    .forEach(id => $(`#${id}`)?.dispatchEvent(new Event('change')));
}
