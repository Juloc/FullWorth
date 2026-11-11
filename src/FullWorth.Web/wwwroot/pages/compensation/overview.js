// "Mein Gehalt": die Startseite des Bereichs. Alles hier kommt aus den bestätigten Abrechnungen und Vertragsständen -
// nichts davon ist eine Eingabe. Sie zeigt, woraus das Gehalt besteht (Grundgehalt, Bonus, Firmenwagen, bAV), jedes
// Stück einzeln und zusammen, und darunter "Gehalt gegen Preise" und die Wohnkosten. Wer noch keine Abrechnung hat,
// sieht, wie er sie aus Paperless holt.
import { $, esc, euro, euro2, fmtDate, monthLabel, spaceId, api, splitSpecialPay } from './shared.js';
import { renderPurchasingPower, setHousingCosts } from './purchasing-power.js';
import { renderHousingCosts } from './housing-costs.js';

const num = value => Number(value) || 0;
const taxOf = p => num(p.wageTax) + num(p.solidaritySurcharge) + num(p.churchTax);
const socialOf = p => num(p.pensionInsurance) + num(p.unemploymentInsurance) + num(p.healthInsurance) + num(p.careInsurance);

export async function renderMeinGehalt() {
  const root = $('#gehalt-overview'); if (!root) return;
  const space = spaceId(); if (!space) return;
  const [payslips, contracts] = await Promise.all([
    api(`api/compensation/payslips?fullWorthSpaceId=${space}`), api(`api/compensation/contracts?fullWorthSpaceId=${space}`)]);
  const list = payslips || [];
  if (!list.length) {
    root.innerHTML = `<div class="panel comp-card"><h2>Noch keine Abrechnungen</h2><p class="extended-note">Hier steht dein Gehalt, sobald du Lohnabrechnungen hast: woraus es besteht (Grundgehalt, Bonus, Firmenwagen, bAV) und was es gegenüber den Preisen wert ist. Lass sie unter „Lohnabrechnungen“ aus Paperless holen oder lade eine hoch.</p><button type="button" class="btn btn-primary" data-open-tab="payslips">Zu den Lohnabrechnungen</button></div>`;
    return;
  }

  // Der Monat, an dem die Zusammensetzung abgelesen wird: die neueste Abrechnung ohne Einmalzahlung - ein Bonus-Monat
  // wuerde Brutto und Auszahlung aufblaehen und waere kein "normaler" Monat.
  const byNewest = list.slice().sort((a, b) => b.payslip.period.localeCompare(a.payslip.period));
  const regular = byNewest.find(x => !num(x.payslip.bonus)) || byNewest[0];
  const p = regular.payslip;
  const states = (contracts?.states || []).map(x => x.state).sort((a, b) => a.validFrom.localeCompare(b.validFrom));
  const contract = states.filter(s => s.validFrom <= p.period).pop() || null;
  const base = contract ? contract.grossMonthly : num(p.grossPay) - num(p.bonus) - num(p.companyCarTaxableBenefit);
  const car = num(p.companyCarTaxableBenefit), bavOwn = num(p.bavEmployee), bavEmployer = num(p.bavEmployer);
  const net = num(p.netPay), paid = num(p.payout);
  // Was zwischen Netto-Verdienst und Auszahlung liegt und weder bAV noch Dienstwagen ist (Zuzahlungen, Nach- und Rueckzahlungen).
  const other = Math.round((net - bavOwn - car - paid) * 100) / 100;

  const year = byNewest.slice(0, 12);
  const sum = key => year.reduce((total, x) => total + num(x.payslip[key]), 0);
  // Das dreizehnte Gehalt (Urlaubs- und Weihnachtsgeld) und der Bonus stehen auf der Abrechnung in einer Zeile, sind aber
  // zweierlei: das eine ist Teil des Gehalts, das andere kommt dazu. Der Vertrag kennt nur das Monatsgehalt, also ergibt
  // sich die Trennung aus den Abrechnungen (siehe splitSpecialPay).
  const split = x => splitSpecialPay(x.payslip);
  const holidaySum = year.reduce((total, x) => total + split(x).holiday, 0);
  const bonusSum = year.reduce((total, x) => total + split(x).bonus, 0);
  const holidayMonths = year.filter(x => split(x).holiday > 0).sort((a, b) => a.payslip.period.localeCompare(b.payslip.period));
  const bonusMonths = year.filter(x => split(x).bonus > 0).sort((a, b) => a.payslip.period.localeCompare(b.payslip.period));
  const bonusText = [
    holidayMonths.length ? `13. Gehalt: ½ Monatsgehalt im ${holidayMonths.map(x => monthLabel(x.payslip.period).split(' ')[0]).join(' und im ')} (Urlaubs- und Weihnachtsgeld).` : '',
    ...bonusMonths.map(x => `Bonus ${monthLabel(x.payslip.period)} ${euro.format(split(x).bonus)}.`)].filter(Boolean).join(' ');
  const source = `Abrechnung ${monthLabel(p.period)}${contract ? ` und Vertrag ab ${fmtDate(contract.validFrom)}` : ''}`;
  const row = (label, value, note = '', cls = '') => `<div class="po-detail-row${cls}"><dt>${label}</dt><dd>${value}</dd><dd class="gv-note">${note}</dd></div>`;

  root.innerHTML = `
    <div class="panel comp-card">
      <div class="panel-head"><div><h2>Dein Gehalt im ${esc(monthLabel(p.period))}</h2></div></div>
      <dl class="po-rows gv-rows">
        ${row('Grundgehalt', euro2.format(base), contract ? 'laut Vertrag' : 'aus der Abrechnung')}
        ${car ? row('+ Firmenwagen (geldwerter Vorteil)', euro2.format(car), 'steuerpflichtig, kein Geld') : ''}
        ${row('= Brutto laut Abrechnung', euro2.format(num(p.grossPay)), '', ' is-total')}
        ${row('− Steuern', euro2.format(taxOf(p)))}
        ${row('− Sozialabgaben', euro2.format(socialOf(p)))}
        ${row('= Netto-Verdienst', euro2.format(net), '', ' is-total')}
        ${bavOwn ? row('− bAV Eigenbeitrag', euro2.format(bavOwn), 'Entgeltumwandlung') : ''}
        ${car ? row('− Firmenwagen verrechnet', euro2.format(car), 'gleicht den Vorteil aus') : ''}
        ${Math.abs(other) >= 0.5 ? row('± Sonstiges', euro2.format(other), 'Zuzahlungen, Korrekturen') : ''}
        ${row('= Ausgezahlt', euro2.format(paid), '', ' is-total')}
      </dl>
      ${bavEmployer ? `<p class="extended-note gv-extra">Zusätzlich zahlt dein Arbeitgeber <strong>${euro2.format(bavEmployer)}</strong> im Monat in deine Altersvorsorge. Das steht nicht im Brutto.</p>` : ''}
      <div class="po-kpis gv-kpis">
        <div><span>${year.length} Monate Brutto</span><strong>${euro.format(sum('grossPay'))}</strong></div>
        ${holidaySum ? `<div><span>davon 13. Gehalt</span><strong>${euro.format(holidaySum)}</strong></div>` : ''}
        ${bonusSum ? `<div><span>davon Bonus</span><strong>${euro.format(bonusSum)}</strong></div>` : ''}
        <div><span>Firmenwagen-Vorteil</span><strong>${euro.format(sum('companyCarTaxableBenefit'))}</strong></div>
        <div><span>bAV Arbeitgeber</span><strong>${euro.format(sum('bavEmployer'))}</strong></div>
        <div><span>Ausgezahlt</span><strong>${euro.format(sum('payout'))}</strong></div>
      </div>
      <p class="extended-note gv-extra">Quelle: ${esc(source)}.${bonusText ? ` ${esc(bonusText)}` : ''}${year.length < 12 ? ` Es liegen ${year.length} von 12 Monaten vor.` : ''}</p>
    </div>
    <div class="panel comp-card pp-card" id="po-power"></div>
    <div class="panel comp-card hc-card" id="po-housing"></div>`;

  renderPurchasingPower($('#po-power'), list);
  renderHousingCosts($('#po-housing'), setHousingCosts);
}

// Die Knoepfe "zu den Abrechnungen" und "zu den Vertraegen" druecken den Reiter, wie es der Benutzer taete:
// jedes Modul daneben haengt sich selbst an seinen Knopf.
document.addEventListener('click', event => {
  const target = event.target.closest('[data-open-tab]');
  if (!target) return;
  document.querySelector(`.comp-tabs [data-extended-tab="${target.dataset.openTab}"]`)?.click();
});
