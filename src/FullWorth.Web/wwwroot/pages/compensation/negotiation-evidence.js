// Die zwei Karten unter dem Gehaltsgespraech: was du im Jahr bekommst, und wie sich deine Erhoehungen zur
// Inflation verhalten. Beides sind Argumente fuer das Gespraech und beides kommt aus den bestaetigten
// Abrechnungen und Vertragsstaenden - nichts davon ist eine Eingabe.
import { $, esc, euro, euro2, fmtDate, pct, signedPct, api, json, splitSpecialPay, detailRow as row } from './shared.js';

const num = value => Number(value) || 0;

/** Zeichnet beide Karten. `states` sind die Vertragsstaende nach Datum sortiert, `payslips` die bestaetigten Abrechnungen. */
export async function renderNegotiationEvidence(payslips, states) {
  const host = $('#neg-evidence');
  if (!host) return;
  host.innerHTML = [totalCard(payslips), await raisesCard(states)].join('');
}

// Brutto und Netto stehen nebeneinander: mit dem Brutto geht man ins Gespraech, das Netto hat viele Einfluesse
// (Steuerklasse, bAV, Firmenwagen). "Netto" heisst die Auszahlung, was aufs Konto kam. Die bAV vom Arbeitgeber
// steht nicht im Brutto, gehoert aber zur Verguetung.
function totalCard(payslips) {
  const year = (payslips || []).slice().sort((a, b) => b.payslip.period.localeCompare(a.payslip.period)).slice(0, 12);
  if (!year.length) return '';
  const sum = pick => year.reduce((total, x) => total + pick(x.payslip), 0);
  const gross = sum(p => num(p.grossPay));
  const holiday = sum(p => splitSpecialPay(p).holiday);
  const bonus = sum(p => splitSpecialPay(p).bonus);
  const car = sum(p => num(p.companyCarTaxableBenefit));
  const bav = sum(p => num(p.bavEmployer));
  const paid = sum(p => num(p.payout));
  return `
    <div class="panel comp-card">
      <div class="panel-head"><div><h2>Deine Gesamtvergütung</h2></div></div>
      <dl class="po-rows gv-rows">
        ${row('Grundgehalt', euro.format(gross - holiday - bonus - car), `${year.length} Monate laut Abrechnung`)}
        ${holiday ? row('+ 13. Gehalt', euro.format(holiday), 'Urlaubs- und Weihnachtsgeld') : ''}
        ${bonus ? row('+ Bonus', euro.format(bonus)) : ''}
        ${car ? row('+ Firmenwagen (geldwerter Vorteil)', euro.format(car), 'steuerpflichtig, kein Geld') : ''}
        ${row('= Brutto', euro.format(gross), '', ' is-total')}
        ${bav ? row('+ bAV Arbeitgeber', euro.format(bav), 'steht nicht im Brutto') : ''}
        ${row('= Gesamtvergütung', euro.format(gross + bav), 'Brutto und bAV Arbeitgeber', ' is-total')}
        ${row('Netto', euro.format(paid), 'was aufs Konto kam')}
      </dl>
      ${year.length < 12 ? `<p class="extended-note gv-extra">Es liegen ${year.length} von 12 Monaten vor.</p>` : ''}
    </div>`;
}

// Eine Erhoehung ist ein Wechsel des Monatsgehalts. Gleiche Betraege hintereinander (ein Nachtrag ohne
// Erhoehung) sind ein Stand und zaehlen ab dem ersten. Die Inflation laeuft von dem Tag, an dem das alte
// Gehalt galt, bis zu dem der neue Stand beginnt - die Frage ist, ob die Erhoehung sie ausgeglichen hat.
async function raisesCard(states) {
  const levels = [];
  for (const state of states || []) {
    if (levels.at(-1)?.grossMonthly !== state.grossMonthly) levels.push({ grossMonthly: state.grossMonthly, validFrom: state.validFrom });
  }
  const raises = levels.slice(1).map((level, index) => ({ from: levels[index], to: level })).slice(-6).reverse();
  if (!raises.length) return '';
  const inflation = await Promise.all(raises.map(({ from, to }) => api('api/compensation/negotiation', json('POST', {
    previousAnnualGross: from.grossMonthly * 12,
    previousDate: from.validFrom,
    currentAnnualGross: to.grossMonthly * 12,
    desiredAnnualGross: to.grossMonthly * 12,
    additionalRealAdjustmentPercent: 0,
    comparisonDate: to.validFrom
  })).catch(() => null)));
  const rows = raises.map(({ from, to }, index) => {
    const nominal = (to.grossMonthly / from.grossMonthly - 1) * 100;
    const result = inflation[index];
    const note = result
      ? `Inflation ${pct(result.cumulativeInflationPercent)} seit ${fmtDate(from.validFrom)} · real ${signedPct(result.currentRealChangePercent)}`
      : '';
    return row(esc(fmtDate(to.validFrom)), `${euro2.format(from.grossMonthly)} → ${euro2.format(to.grossMonthly)} (${signedPct(nominal)})`, esc(note));
  }).join('');
  return `
    <div class="panel comp-card">
      <div class="panel-head"><div><h2>Entwicklung deiner Erhöhungen</h2></div></div>
      <dl class="po-rows gv-rows">${rows}</dl>
      <p class="extended-note gv-extra">Monatsgehalt laut Vertrag. „Real" ist die Erhöhung nach Abzug der Inflation seit dem vorherigen Stand.</p>
    </div>`;
}
