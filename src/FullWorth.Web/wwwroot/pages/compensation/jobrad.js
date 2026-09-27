// Der Dienstrad-/JobRad-Reiter (#179): eigenes Panel, per insertAdjacentHTML angehaengt, genau wie
// history.js daneben - der Reiter-Knopf selbst steht fest im Markup der Seite (Compensation/Index.cshtml),
// weil seine Position sonst beim ersten Laden noch verrutschen wuerde (#154).
//
// Das Gehalt kommt NICHT aus eigenen Feldern: readProfile() liest dieselben Felder wie der Rechner-Reiter,
// weil "bestehende Compensation-Profile als Datenquelle verwenden" genau das meint - ein zweites
// Gehaltsformular waere die zweite Quelle, die irgendwann von der ersten abweicht.
import {
  $ as J$, $$ as J$$, euro as jeuro, euro2 as jeuro2, signedEuro0 as jsignedEuro,
  esc, api as japi, json as jjson, notify as jnotify, spaceId, readProfile
} from './shared.js';

const jstate = { result: null };

initJobRad();

function initJobRad() {
  const toast = J$('#comp-error');
  if (!toast) return;
  toast.insertAdjacentHTML('beforebegin', jobRadMarkup());
  J$$('[data-jobrad-tab]').forEach(b => b.addEventListener('click', () => openJobRad()));
  J$('#jobrad-calculate').addEventListener('click', () => calculateJobRad().catch(jerror));
  J$('#jobrad-conversion').addEventListener('change', syncJobRadFields);
  J$('#jobrad-takeover-fixed-toggle').addEventListener('change', syncJobRadFields);
  syncJobRadFields();
}

function openJobRad() {
  J$$('.comp-tabs button').forEach(b => b.classList.toggle('active', b.dataset.jobradTab === 'jobrad'));
  J$$('.comp-tab').forEach(tab => tab.classList.toggle('active', tab.id === 'tab-jobrad'));
}

// Gehaltsextra (§ 3 Nr. 37 EStG) hat keinen geldwerten Vorteil und keinen Arbeitgeberzuschuss auf ein
// umgewandeltes Gehalt - es gibt schlicht nichts umzuwandeln. Die Felder bleiben sichtbar (der
// Zuschuss kann trotzdem gezahlt werden), aber optisch gedaempft wie anderswo auf der Seite auch.
function syncJobRadFields() {
  const isConversion = J$('#jobrad-conversion').value === 'true';
  J$('#jobrad-subsidy-field')?.classList.toggle('muted-fields', !isConversion);
  const fixedTakeover = J$('#jobrad-takeover-fixed-toggle')?.checked;
  J$('#jobrad-takeover-percent-field')?.classList.toggle('muted-fields', fixedTakeover);
  J$('#jobrad-takeover-fixed-field')?.classList.toggle('muted-fields', !fixedTakeover);
}

function jval(id) { return J$(`#${id}`)?.value ?? ''; }
function jnum(id) { return Number(jval(id)) || 0; }

function readLeasing() {
  const fixedTakeover = J$('#jobrad-takeover-fixed-toggle')?.checked;
  return {
    listPriceGross: jnum('jobrad-list-price'),
    monthlyLeasingRateGross: jnum('jobrad-rate'),
    termMonths: Math.max(1, Math.round(jnum('jobrad-term')) || 36),
    salaryConversion: jval('jobrad-conversion') === 'true',
    employerSubsidyMonthly: jnum('jobrad-subsidy'),
    employeeInsuranceServiceMonthly: jnum('jobrad-insurance-service'),
    takeoverPricePercentOfList: fixedTakeover ? 0 : jnum('jobrad-takeover-percent'),
    takeoverPriceFixed: fixedTakeover ? jnum('jobrad-takeover-fixed') : null,
    providerCoversTakeoverTax: J$('#jobrad-takeover-tax-covered')?.checked ?? true
  };
}

function readCashAlternative() {
  return {
    actualPurchasePrice: jnum('jobrad-cash-price'),
    annualInsuranceCost: jnum('jobrad-cash-insurance'),
    annualServiceCost: jnum('jobrad-cash-service'),
    financingCostTotal: jnum('jobrad-cash-financing')
  };
}

async function calculateJobRad() {
  if (!spaceId()) { jnotify('Kein Finanzbereich ausgewählt.'); return; }
  const request = { salary: readProfile(), leasing: readLeasing(), cashAlternative: readCashAlternative() };
  jstate.result = await japi('api/compensation/jobrad/compare', jjson('POST', request));
  renderJobRadResult(jstate.result);
}

function renderJobRadResult(r) {
  const box = J$('#jobrad-result');
  if (!box) return;
  if (!r) { box.hidden = true; return; }
  box.hidden = false;

  J$('#jobrad-net-impact').textContent = jsignedEuro(r.netMonthlyImpact);
  J$('#jobrad-lease-total').textContent = jeuro.format(r.totalLeaseCostUntilOwnership);
  J$('#jobrad-cash-total').textContent = jeuro.format(r.comparableCashPurchaseCost);
  const diff = J$('#jobrad-difference');
  diff.textContent = jsignedEuro(r.differenceLeasingVsCash);
  diff.closest('article').querySelector('small').textContent =
    r.differenceLeasingVsCash >= 0 ? 'Leasing ist günstiger' : 'Barkauf wäre günstiger gewesen';
  J$('#jobrad-pension-monthly').textContent = r.pension.aboveContributionCeiling
    ? '0 €' : jsignedEuro(-Math.abs(r.pension.monthlyPensionReductionAtCurrentValue));
  J$('#jobrad-pension-points').textContent = r.pension.aboveContributionCeiling
    ? '—' : `−${r.pension.lostEntgeltpunkteOverTerm.toLocaleString('de-DE', { minimumFractionDigits: 3, maximumFractionDigits: 4 })}`;
  J$('#jobrad-pension-note').textContent = r.pension.aboveContributionCeiling
    ? 'Das Entgelt liegt auch nach der Umwandlung an oder über der Rentenversicherungs-Beitragsbemessungsgrenze - es entsteht keine Rentenwirkung.'
    : `Entgeltpunkt ${r.pension.averageEarningsAnnual.toLocaleString('de-DE')} €/Jahr · aktueller Rentenwert ${r.pension.currentPensionValueMonthly.toLocaleString('de-DE', { minimumFractionDigits: 2 })} €`;

  J$('#jobrad-details-tax').innerHTML = detailRows([
    ['Geldwerter Vorteil (monatlich)', jeuro2.format(r.monthlyTaxableBenefit)],
    ['Netto ohne Dienstrad (normaler Monat)', jeuro.format(r.withoutJobRad.estimatedCashNetMonthly)],
    ['Netto mit Dienstrad (normaler Monat)', jeuro.format(r.withJobRad.estimatedCashNetMonthly)]
  ]);
  J$('#jobrad-details-employer').innerHTML = detailRows([
    ['Eingesparte Arbeitgeber-SV', jeuro2.format(r.employer.monthlySavingsFromLowerContributions)],
    ['Davon als Zuschuss weitergegeben', jeuro2.format(r.employer.monthlySubsidyPassedToEmployee)],
    ['Beim Arbeitgeber verbleibend', jeuro2.format(r.employer.monthlyNetBenefit)]
  ]);
  J$('#jobrad-details-benefits').innerHTML = r.socialBenefitEstimates.length
    ? r.socialBenefitEstimates.map(e => `<div class="field-summary"><strong>${esc(e.label)}: ${jsignedEuro(e.monthlyDeltaEstimate)}/Monat</strong><br>Schätzung · ${esc(e.basis)}</div>`).join('')
    : '<div class="field-summary">Kein Bruttoentgelt-Rückgang durch die Umwandlung - keine dieser Leistungen ist betroffen.</div>';
  J$('#jobrad-assumptions').innerHTML =
    `<div class="field-summary">Steuerjahr ${r.assumptions.taxYear} · ${esc(r.assumptions.taxSource)}<br>${esc(r.assumptions.disclaimer)}</div>`;
}

// .result-row (die einzelne Zeile) ist mit ihrer einzigen Erzeugerfunktion entfernt worden (siehe
// page.css) - .field-summary ist der Baustein, den die Seite seither dafuer wiederverwendet.
function jerror(e) { console.error(e); jnotify(e?.message || 'Unbekannter Fehler.'); }

function detailRows(pairs) {
  return pairs.map(([label, value]) => `<div class="field-summary"><strong>${esc(label)}:</strong> ${esc(value)}</div>`).join('');
}

function jobRadMarkup() { return `
<section id="tab-jobrad" class="comp-tab">
  <div class="comp-layout">
    <div class="comp-form-stack">
      <article class="panel comp-card">
        <div class="panel-head"><div><span class="step">1</span><h2>Leasing</h2></div></div>
        <div class="form-grid">
          <label>Bruttolistenpreis (UVP)<input id="jobrad-list-price" type="number" min="0" step="50" value="3000"></label>
          <label>Monatliche Leasingrate (brutto)<input id="jobrad-rate" type="number" min="0" step="1" value="89"></label>
          <label>Laufzeit<select id="jobrad-term"><option value="24">24 Monate</option><option value="36" selected>36 Monate</option><option value="48">48 Monate</option></select></label>
          <label>Modell<select id="jobrad-conversion"><option value="true">Gehaltsumwandlung</option><option value="false">Gehaltsextra (zusätzlich zum Gehalt)</option></select>
            <small class="field-help">Umwandlung senkt dein Bruttogehalt und wirkt sich auf Steuer, Sozialversicherung und Rente aus. Gehaltsextra ist nach § 3 Nr. 37 EStG steuer- und SV-frei und rührt dein Gehalt nicht an.</small>
          </label>
          <label id="jobrad-subsidy-field">Arbeitgeberzuschuss (monatlich)<input id="jobrad-subsidy" type="number" min="0" step="1" value="0"></label>
          <label>Versicherung/Service extra (monatlich)<input id="jobrad-insurance-service" type="number" min="0" step="1" value="0"></label>
        </div>
        <details class="comp-advanced">
          <summary>Übernahme am Laufzeitende</summary>
          <div class="form-grid">
            <label class="check-row"><input id="jobrad-takeover-fixed-toggle" type="checkbox">Als festen Betrag statt Prozent angeben</label>
            <label id="jobrad-takeover-percent-field">Übernahmepreis (% der UVP)<input id="jobrad-takeover-percent" type="number" min="0" max="100" step="1" value="0"></label>
            <label id="jobrad-takeover-fixed-field">Übernahmepreis (fester Betrag)<input id="jobrad-takeover-fixed" type="number" min="0" step="10" value="0"></label>
            <label class="check-row"><input id="jobrad-takeover-tax-covered" type="checkbox" checked>Anbieter übernimmt die Versteuerung des Preisvorteils</label>
          </div>
        </details>
      </article>

      <article class="panel comp-card">
        <div class="panel-head"><div><span class="step">2</span><h2>Vergleich: Barkauf</h2></div></div>
        <div class="form-grid">
          <label>Tatsächlicher Kaufpreis beim Händler<input id="jobrad-cash-price" type="number" min="0" step="10" value="2600"><small class="field-help">Nicht die UVP - der Preis, den du wirklich zahlen würdest.</small></label>
          <label>Versicherung pro Jahr<input id="jobrad-cash-insurance" type="number" min="0" step="5" value="0"></label>
          <label>Wartung/Service pro Jahr<input id="jobrad-cash-service" type="number" min="0" step="5" value="0"></label>
          <label>Finanzierungskosten insgesamt (falls nicht bar)<input id="jobrad-cash-financing" type="number" min="0" step="10" value="0"></label>
        </div>
      </article>
      <button id="jobrad-calculate" class="btn btn-primary comp-calculate" type="button">Berechnen</button>
    </div>

    <div id="jobrad-result" class="comp-results" hidden>
      <article class="panel comp-card comp-result-primary">
        <span>Netto pro Monat</span>
        <strong id="jobrad-net-impact">—</strong>
        <small>Was ein normaler Monat mit dem Dienstrad weniger Netto bringt.</small>
      </article>
      <div class="metric-grid comp-metrics">
        <article class="metric"><span>Gesamtkosten Leasing bis Eigentum</span><strong id="jobrad-lease-total">—</strong></article>
        <article class="metric"><span>Vergleichbarer Barkauf</span><strong id="jobrad-cash-total">—</strong></article>
        <article class="metric"><span>Differenz Leasing vs. Kauf</span><strong id="jobrad-difference">—</strong><small class="field-help"></small></article>
        <article class="metric"><span>Rentenwirkung pro Monat</span><strong id="jobrad-pension-monthly">—</strong></article>
      </div>
      <article class="panel comp-card">
        <div class="panel-head"><div><h2>Rente</h2></div></div>
        <div class="field-summary"><strong>Verlorene Entgeltpunkte über die Laufzeit:</strong> <span id="jobrad-pension-points">—</span></div>
        <div id="jobrad-pension-note" class="field-summary"></div>
      </article>
      <article class="panel comp-card">
        <details class="comp-advanced" open><summary>Steuer &amp; Sozialversicherung</summary><div id="jobrad-details-tax"></div></details>
        <details class="comp-advanced"><summary>Arbeitgeberanteil</summary><div id="jobrad-details-employer"></div></details>
        <details class="comp-advanced"><summary>Krankengeld / ALG I / Elterngeld (Schätzung)</summary><div id="jobrad-details-benefits"></div></details>
        <details class="comp-advanced"><summary>Annahmen / Rechenweg</summary><div id="jobrad-assumptions"></div></details>
      </article>
    </div>
  </div>
</section>`; }
