// Altersvorsorge — the Simulation tab (step 3 of docs/PENSION.md): projection and variant comparison.
//
// Mounted by features/pension.js as a tab, the same way features/pension-documents.js is, so this
// module registers neither a view nor a route of its own. The shared pension copy is handed in as
// `t` / `label` rather than imported, because pension.js already imports this file and importing back
// would make the pair circular.
//
// Four rules decide how this screen looks, and each of them is the reason the feature exists at all —
// they are the same rules PensionProjectionContracts.cs states on the server:
//
//   1. A projection is never presented as a value. What is guaranteed and what is merely projected sit
//      in two separate blocks with two different edges, and every projected figure carries the
//      `returnPercent` it was computed with. A projected number next to the balance is the single most
//      expensive mistake this feature could make; the database refuses the same mix
//      (CK_BavSnapshots_Projection) and the screen must not undo that.
//   2. The comparison must not appear to invent an advantage. With the same money and the same
//      assumptions the capital delta is zero, and then the screen SAYS SO IN WORDS: splitting a
//      contribution across two contracts produces no extra compound interest. A bare "0,00 €" teaches
//      nobody that.
//   3. A delta is attributed. `cause` names costs / guarantee / investment_concept, or
//      `different_assumptions` — in which case the two sides are not comparable as contracts at all,
//      and the screen says that instead of showing a difference as if it came from the contracts.
//   4. An excluded contract is named with its blocker and with what the user can do about it. A total
//      that silently drops a contract looks complete and is not, which is worse than no total.
//
// It is a PAGE, not a dialog: a return scenario is something the user changes repeatedly, and
// re-opening a dialog for each change is exactly the friction docs/UI_AUDIT.md spent six steps
// removing. The form is inline on the tab, and there is no dialog in this flow at all.
import { sectionCard, esc } from '../ui/ux-kit.js';

// --- injected by pension.js ---
let ctx = null;
let host = null;
let contracts = [];
let sharedT = null;
// pension.js owns this area's number formatting. Re-implementing a percent formatter here is how the
// two halves of one screen end up printing the same assumption two different ways.
let percent = value => String(value);

// --- module state ---
// The scenario the user is looking at. It never contains a computed figure: a result is fetched, shown
// and thrown away, so nothing on this screen can be mistaken for something that was stored.
let scenario = { returnPercent: 5, custom: false, retirementDate: '', continueContributions: true };
let result = null;
let resultError = null;
let busy = false;
// Which request the screen is currently waiting for. See runProjection / runComparison: a scenario is
// changed faster than a round trip, so an older answer must not overwrite a newer one.
let runToken = 0;
let compareToken = 0;
// The comparison is opt-in: it only makes sense once two contracts hold capital, and pre-computing it
// would fire a second POST on every scenario change for a panel nobody opened.
let compare = { open: false, leftId: '', rightId: '', leftReturn: 5, rightReturn: 5 };
let comparison = null;
let compareError = null;
let comparing = false;

const RETURN_PRESETS = [3, 5, 7];

const SIM_T = {
  de: {
    scenarioTitle: 'Szenario',
    scenarioHint: 'Eine Prognose wird hier gerechnet und nicht gespeichert. Sie ändert kein Guthaben und zählt in keinem Vermögen mit.',
    returnLabel: 'Angenommene Rendite',
    returnCustom: 'eigene',
    returnCustomLabel: 'Rendite (% p. a.)',
    retirementLabel: 'Rentenbeginn (optional)',
    retirementHint: 'Leer lassen heißt: jeder Vertrag rechnet mit seinem eigenen Rentenbeginn. Ein Vertrag ohne eigenes Datum borgt sich keines.',
    continueLabel: 'Beiträge bis zum Rentenbeginn weiterzahlen',
    continueHint: 'Aus ist die ehrliche Lesart für einen beitragsfreien Vertrag: dann wächst nur das Guthaben.',
    recalc: 'Neu rechnen',
    calculating: 'Wird gerechnet …',
    empty: 'Noch kein Vertrag erfasst. Eine Prognose braucht mindestens einen Vertrag mit einem Stand.',
    todayTitle: 'Heute vorhanden',
    todayHint: 'Guthaben zum jeweiligen Stichtag. Das ist die einzige Zahl auf dieser Seite, die Geld ist.',
    balanceTotal: 'Guthaben gesamt',
    guaranteeTitle: 'Garantiert zum Rentenbeginn',
    guaranteeHint: 'Zugesagte Werte aus den Verträgen. Sie stehen absichtlich getrennt von der Prognose.',
    projectionTitle: 'Prognose zum Rentenbeginn – eine Annahme',
    projectionHint: 'Eine Prognose ist keine Garantie und kein heutiges Vermögen. Sie gilt nur mit der Rendite, die daneben steht.',
    withReturn: '{percent} % p. a. angenommen',
    capital: 'Kapital',
    monthlyAnnuity: 'Monatsrente',
    noAnnuity: 'Keine Monatsrente',
    noAnnuityHint: 'Der Vertrag nennt keinen Rentenfaktor. Aus einem Kapital eine Monatsrente zu erfinden wäre die irreführendste Zahl auf dieser Seite – deshalb steht hier nur das Kapital.',
    noAnnuityFix: 'Trage den garantierten Rentenfaktor im Vertrag nach, dann erscheint die Monatsrente.',
    perContract: 'Je Vertrag',
    monthsAhead: 'Noch {months} Monate',
    ownAhead: 'Eigenanteil bis dahin',
    employerAhead: 'Arbeitgeber bis dahin',
    employerAheadHint: 'Der Arbeitgeberanteil ist ein Zuschuss – er ist keine Ausgabe und kein verfügbares Einkommen.',
    costsApplied: 'Berücksichtigte Kosten',
    costsEstimated: 'darunter geschätzte Kosten',
    costsOnlyKnown: 'Gerechnet wird nur mit den Kosten, die in den Dokumenten stehen. Kosten, die nirgendwo stehen, werden nicht erfunden – die Prognose ist dadurch eher zu hoch als zu niedrig.',
    incomplete: 'Unvollständig',
    incompleteHint: 'Für {currencies} fehlt ein Wechselkurs. Diese Verträge sind NICHT in den Summen enthalten.',
    excludedTitle: 'Nicht gerechnet',
    excludedHint: 'Diese Verträge fehlen in den Summen oben. Sie stehen hier, damit die Summe nicht vollständig aussieht, obwohl sie es nicht ist.',
    blockers: {
      no_retirement_date: 'Kein Rentenbeginn hinterlegt.',
      no_balance: 'Noch kein Stand erfasst – es gibt kein Guthaben, das wachsen könnte.',
      already_due: 'Der Rentenbeginn liegt in der Vergangenheit: der Vertrag ist in oder nach der Auszahlung.',
      missing_rate: 'Für die Währung dieses Vertrags fehlt ein Wechselkurs.'
    },
    blockerFixes: {
      no_retirement_date: 'Trage den Rentenbeginn im Vertrag nach – oder gib oben ein Datum für alle Verträge vor.',
      no_balance: 'Ergänze im Vertrag einen Stand mit Guthaben und Stichtag.',
      already_due: 'Für die Auszahlungsphase rechnet FullWorth keine Prognose. Der Vertrag bleibt mit seinem Guthaben in der Übersicht.',
      missing_rate: 'Hinterlege einen Kurs für diese Währung. Bis dahin fehlt der Vertrag in der Summe und wird nicht mit 1:1 geschätzt.'
    },
    compareTitle: 'Zwei Verträge vergleichen',
    compareHint: 'Gleiches Geld, zwei Verträge: der Vergleich zeigt nur Unterschiede, die es wirklich gibt – und nennt, woher sie kommen.',
    compareOpen: 'Vergleich öffnen',
    compareClose: 'Vergleich schließen',
    compareNeedsTwo: 'Für einen Vergleich braucht es zwei Verträge mit Guthaben.',
    compareLeft: 'Vertrag A',
    compareRight: 'Vertrag B',
    compareReturn: 'Rendite (% p. a.)',
    compareRun: 'Vergleichen',
    compareSame: 'Bitte zwei verschiedene Verträge wählen.',
    comparing: 'Wird verglichen …',
    deltaCapital: 'Unterschied Kapital',
    deltaAnnuity: 'Unterschied Monatsrente',
    sameMoneyTitle: 'Kein Unterschied – und das ist das Ergebnis',
    sameMoneySentence: 'Gleiches Geld, gleiche Annahmen: Ein Beitrag, der auf zwei Verträge verteilt wird, erzeugt keinen zusätzlichen Zinseszins. 50 € + 288 € sind genau 338 € – aufgeteilt wie zusammen.',
    causeTitle: 'Woher der Unterschied kommt',
    causes: {
      costs: 'Kosten – die beiden Verträge nehmen unterschiedlich viel vom selben Beitrag.',
      guarantee: 'Garantie – eine höhere Zusage kostet Rendite, eine niedrigere lässt mehr im Markt.',
      investment_concept: 'Anlagekonzept – die Verträge legen dasselbe Geld unterschiedlich an.',
      none: 'Nichts – gleiches Geld, gleiche Annahmen.',
      different_assumptions: 'Unterschiedliche Annahmen: Die beiden Seiten rechnen mit verschiedenen Renditen.'
    },
    notComparable: 'Mit verschiedenen Renditen sind die beiden als Verträge nicht vergleichbar – der Unterschied kommt aus der Annahme, nicht aus den Verträgen. Stelle beide Seiten auf dieselbe Rendite, um die Verträge selbst zu vergleichen.',
    side: 'Seite',
    error: 'Die Prognose konnte nicht gerechnet werden.'
  },
  en: {
    scenarioTitle: 'Scenario',
    scenarioHint: 'A projection is computed here and never stored. It changes no balance and counts in no total.',
    returnLabel: 'Assumed return',
    returnCustom: 'custom',
    returnCustomLabel: 'Return (% p.a.)',
    retirementLabel: 'Retirement date (optional)',
    retirementHint: 'Empty means every contract uses its own retirement date. A contract without one does not borrow somebody else’s.',
    continueLabel: 'Keep paying contributions until retirement',
    continueHint: 'Off is the honest reading for a paid-up contract: then only the balance grows.',
    recalc: 'Recalculate',
    calculating: 'Calculating …',
    empty: 'No contract yet. A projection needs at least one contract with a value.',
    todayTitle: 'There today',
    todayHint: 'Balance at its own date. It is the only figure on this page that is money.',
    balanceTotal: 'Total balance',
    guaranteeTitle: 'Guaranteed at retirement',
    guaranteeHint: 'Promised figures from the contracts. They are kept apart from the projection on purpose.',
    projectionTitle: 'Projected at retirement — an assumption',
    projectionHint: 'A projection is neither a guarantee nor money you have today. It only holds with the return stated next to it.',
    withReturn: '{percent} % p.a. assumed',
    capital: 'Capital',
    monthlyAnnuity: 'Monthly annuity',
    noAnnuity: 'No monthly annuity',
    noAnnuityHint: 'The contract states no annuity factor. A monthly annuity invented from a capital sum would be the most misleading number on this page, so only the capital is shown.',
    noAnnuityFix: 'Add the guaranteed annuity factor to the contract and the monthly figure appears.',
    perContract: 'Per contract',
    monthsAhead: '{months} months to go',
    ownAhead: 'Own share until then',
    employerAhead: 'Employer until then',
    employerAheadHint: 'The employer share is a benefit — never an expense and never spendable income.',
    costsApplied: 'Costs applied',
    costsEstimated: 'of which estimated',
    costsOnlyKnown: 'Only the costs the documents state are applied. A cost nobody stated is not invented, which makes this projection more likely too high than too low.',
    incomplete: 'Incomplete',
    incompleteHint: 'No exchange rate for {currencies}. Those contracts are NOT in the totals.',
    excludedTitle: 'Not projected',
    excludedHint: 'These contracts are missing from the totals above. They are listed so the total cannot look complete while it is not.',
    blockers: {
      no_retirement_date: 'No retirement date on the contract.',
      no_balance: 'No value recorded — there is no balance that could grow.',
      already_due: 'The retirement date is in the past: this contract is in or past payout.',
      missing_rate: 'No exchange rate for this contract’s currency.'
    },
    blockerFixes: {
      no_retirement_date: 'Add the retirement date to the contract — or set one above for every contract.',
      no_balance: 'Add a value with a balance and a date to the contract.',
      already_due: 'FullWorth projects nothing for the payout phase. The contract keeps its balance in the overview.',
      missing_rate: 'Add a rate for that currency. Until then the contract is missing from the total and is never assumed to be 1:1.'
    },
    compareTitle: 'Compare two contracts',
    compareHint: 'Same money, two contracts: the comparison only shows differences that really exist — and names where they come from.',
    compareOpen: 'Open the comparison',
    compareClose: 'Close the comparison',
    compareNeedsTwo: 'A comparison needs two contracts that hold capital.',
    compareLeft: 'Contract A',
    compareRight: 'Contract B',
    compareReturn: 'Return (% p.a.)',
    compareRun: 'Compare',
    compareSame: 'Please pick two different contracts.',
    comparing: 'Comparing …',
    deltaCapital: 'Capital difference',
    deltaAnnuity: 'Monthly annuity difference',
    sameMoneyTitle: 'No difference — and that is the result',
    sameMoneySentence: 'Same money, same assumptions: splitting a contribution across two contracts produces no extra compound interest. 50 € + 288 € is exactly 338 € — split or together.',
    causeTitle: 'Where the difference comes from',
    causes: {
      costs: 'Costs — the two contracts take a different amount out of the same contribution.',
      guarantee: 'Guarantee — a higher promise costs return, a lower one leaves more in the market.',
      investment_concept: 'Investment concept — the contracts invest the same money differently.',
      none: 'Nothing — same money, same assumptions.',
      different_assumptions: 'Different assumptions: the two sides use different returns.'
    },
    notComparable: 'With different returns the two are not comparable as contracts — the difference comes from the assumption, not from the contracts. Set both sides to the same return to compare the contracts themselves.',
    side: 'Side',
    error: 'The projection could not be computed.'
  }
};

function lang() { return (document.documentElement.lang || '').startsWith('en') ? 'en' : 'de'; }
function d() { return SIM_T[lang()]; }
function fill(template, values) {
  return String(template).replace(/\{(\w+)\}/g, (_, key) => String(values[key] ?? ''));
}

/** Leaving the tab drops the scenario AND its result, so the tab never opens on a stale projection. */
export function resetPensionProjection() {
  result = null;
  resultError = null;
  busy = false;
  comparison = null;
  compareError = null;
  comparing = false;
  compare = { open: false, leftId: '', rightId: '', leftReturn: scenario.returnPercent, rightReturn: scenario.returnPercent };
}

export async function renderPensionProjection(mountHost, injected) {
  host = mountHost;
  ctx = injected.ctx;
  contracts = injected.contracts || [];
  sharedT = injected.t;
  percent = injected.percent;

  if (!contracts.length) {
    host.innerHTML = sectionCard(d().scenarioTitle, `<div class="row-sub">${esc(d().empty)}</div>`);
    return;
  }

  paint();
  // The first projection runs on open: a scenario screen that starts empty asks the user to press a
  // button before it has told them anything.
  if (!result && !resultError) await runProjection();
}

function paint() {
  host.innerHTML = `
    ${scenarioHtml()}
    <div data-sim-result>${resultHtml()}</div>
    ${compareHtml()}`;
  wireScenario();
  wireCompare();
}

// ---- the scenario form, inline on the tab ----

function scenarioHtml() {
  const presets = RETURN_PRESETS.map(value => {
    const active = !scenario.custom && Number(scenario.returnPercent) === value;
    return `<button type="button" class="${active ? 'active' : ''}" aria-pressed="${active}" data-sim-preset="${value}">${percent(value)} %</button>`;
  }).join('');

  const body = `
    <p class="row-sub pension-sim-note">${esc(d().scenarioHint)}</p>
    <div class="pension-sim-scenario">
      <div class="pension-sim-return">
        <span class="pension-sim-return-label" id="pension-sim-return-label">${esc(d().returnLabel)}</span>
        <div class="fw-cycle" role="group" aria-labelledby="pension-sim-return-label">
          ${presets}
          <button type="button" class="${scenario.custom ? 'active' : ''}" aria-pressed="${scenario.custom}" data-sim-preset="custom">${esc(d().returnCustom)}</button>
        </div>
      </div>
      ${scenario.custom ? `<label class="field pension-field pension-sim-custom">
        <span>${esc(d().returnCustomLabel)}</span>
        <input type="number" inputmode="decimal" min="0" max="15" step="0.1" data-sim-return value="${esc(String(scenario.returnPercent))}">
      </label>` : ''}
      <label class="field pension-field">
        <span>${esc(d().retirementLabel)}</span>
        <input type="date" data-sim-retirement value="${esc(scenario.retirementDate)}">
        <small class="row-sub">${esc(d().retirementHint)}</small>
      </label>
      <label class="check pension-check pension-sim-continue">
        <input type="checkbox" data-sim-continue${scenario.continueContributions ? ' checked' : ''}>
        <span>${esc(d().continueLabel)}</span>
      </label>
      <small class="row-sub pension-sim-continue-hint">${esc(d().continueHint)}</small>
      <div class="pension-sim-actions">
        <button type="button" class="btn btn-secondary" data-sim-run>${esc(busy ? d().calculating : d().recalc)}</button>
      </div>
    </div>`;
  return sectionCard(d().scenarioTitle, body, { className: 'pension-sim-card' });
}

function wireScenario() {
  host.querySelectorAll('[data-sim-preset]').forEach(button => button.addEventListener('click', () => {
    const raw = button.dataset.simPreset;
    if (raw === 'custom') scenario = { ...scenario, custom: true };
    else scenario = { ...scenario, custom: false, returnPercent: Number(raw) };
    paint();
    runProjection();
  }));
  host.querySelector('[data-sim-return]')?.addEventListener('change', event => {
    const value = Number(event.target.value);
    if (!Number.isFinite(value) || value < 0) return;
    scenario = { ...scenario, returnPercent: value };
    runProjection();
  });
  host.querySelector('[data-sim-retirement]')?.addEventListener('change', event => {
    scenario = { ...scenario, retirementDate: String(event.target.value || '') };
    runProjection();
  });
  host.querySelector('[data-sim-continue]')?.addEventListener('change', event => {
    scenario = { ...scenario, continueContributions: !!event.target.checked };
    runProjection();
  });
  host.querySelector('[data-sim-run]')?.addEventListener('click', () => runProjection());
}

function requestFor(overrides = {}) {
  const request = {
    returnPercent: Number(scenario.returnPercent),
    continueContributions: scenario.continueContributions,
    ...overrides
  };
  // An empty date field means "each contract's own date", which the contract expresses as null — not
  // as today, and not as somebody else's retirement date.
  if (scenario.retirementDate) request.retirementDate = scenario.retirementDate;
  return request;
}

async function runProjection() {
  // A token rather than a "busy, ignore this" guard: the user clicks 3 / 5 / 7 in a row, so two
  // requests are in flight and the SLOWER one can answer last. Refusing the second click would drop
  // the scenario the user actually chose, and taking whichever answer arrives last would show 5 %
  // figures under a 7 % badge - both are worse than letting the newest request win.
  const token = ++runToken;
  busy = true;
  resultError = null;
  // The previous result stays on screen while the next one is computed and the button says what is
  // happening. Blanking it would make every scenario change look like a failure for a moment.
  paint();
  let answer = null;
  let failure = null;
  try {
    answer = await ctx.api('api/pension/projection', ctx.jsonBody(requestFor()));
  } catch (error) {
    failure = error?.message || d().error;
  }
  if (token !== runToken) return;
  result = failure ? null : answer;
  resultError = failure;
  busy = false;
  paint();
}

// ---- the result ----

function resultHtml() {
  if (resultError) return sectionCard(d().projectionTitle, `<div class="row-sub">${esc(resultError)}</div>`);
  if (!result) return sectionCard(d().projectionTitle, `<div class="row-sub">${esc(d().calculating)}</div>`);

  const currency = result.currency || 'EUR';
  const returnPercent = result.returnPercent;
  const projected = (result.contracts || []).filter(item => !item.blocker);

  const notices = [];
  if (result.isComplete === false) {
    const list = (result.missingCurrencies || []).join(', ');
    notices.push(`<div class="pension-notice pension-notice-warn">${esc(`${d().incomplete}: ${fill(d().incompleteHint, { currencies: list })}`)}</div>`);
  }

  // Three blocks, three edges, and they are siblings rather than rows of one table: today's money,
  // what was promised, and what is only assumed. A single mixed table is how a projection ends up
  // read as a balance.
  const blocks = `
    <div class="pension-sim-blocks">
      <section class="pension-value-block pension-sim-now">
        <div class="pension-value-head">${esc(d().todayTitle)}</div>
        <div class="amount pension-sim-figure">${money(result.totalCurrentBalance, currency)}</div>
        <p class="row-sub">${esc(d().todayHint)}</p>
      </section>
      <section class="pension-value-block pension-sim-guarantee">
        <div class="pension-value-head">${esc(d().guaranteeTitle)}</div>
        <dl class="pension-facts">
          <div><dt>${esc(d().capital)}</dt><dd class="amount">${money(result.totalGuaranteedCapital, currency)}</dd></div>
          <div><dt>${esc(d().monthlyAnnuity)}</dt><dd class="amount">${money(result.totalGuaranteedMonthlyAnnuity, currency)}</dd></div>
        </dl>
        <p class="row-sub">${esc(d().guaranteeHint)}</p>
      </section>
      <section class="pension-value-block pension-value-projected pension-sim-projection">
        <div class="pension-value-head">${esc(d().projectionTitle)} · <span class="pension-badge pension-badge-quiet pension-sim-return-badge">${esc(fill(d().withReturn, { percent: percent(returnPercent) }))}</span></div>
        <dl class="pension-facts">
          <div><dt>${esc(d().capital)}</dt><dd class="amount">${money(result.totalProjectedCapital, currency)}</dd></div>
          <div><dt>${esc(d().monthlyAnnuity)}</dt><dd class="amount">${money(result.totalProjectedMonthlyAnnuity, currency)}</dd></div>
        </dl>
        <p class="row-sub">${esc(d().projectionHint)}</p>
      </section>
    </div>`;

  return `
    ${notices.join('')}
    ${sectionCard('', blocks, { className: 'pension-sim-result' })}
    ${sectionCard(d().perContract, projected.length
      ? `<div class="rows pension-sim-contracts">${projected.map(contractHtml).join('')}</div>
         <p class="pension-footnote">${esc(d().costsOnlyKnown)}</p>
         <p class="pension-footnote">${esc(d().employerAheadHint)}</p>`
      : `<div class="row-sub">${esc(d().excludedHint)}</div>`)}
    ${excludedHtml(result.excluded)}`;
}

function contractHtml(item) {
  const currency = item.currency || 'EUR';
  const balance = item.currentBalance == null
    ? '—'
    : `${money(item.currentBalance, currency)}${item.balanceAsOf ? ` · ${esc(sharedT.asOf)} ${ctx.date(item.balanceAsOf)}` : ''}`;

  const guaranteed = [
    [sharedT.guaranteedCapital, item.guaranteedCapital],
    [sharedT.guaranteedMonthly, item.guaranteedMonthlyAnnuity]
  ].filter(([, value]) => value != null);

  // `projectedMonthlyAnnuity` is null when the contract states no annuity factor. The capital is shown
  // and the gap is explained; blanking the row would hide a projection the user asked for, and filling
  // it with arithmetic of our own would be the invention rule 1 forbids.
  const annuity = item.projectedMonthlyAnnuity == null
    ? `<div><dt>${esc(d().noAnnuity)}</dt><dd class="row-sub">${esc(d().noAnnuityFix)}</dd></div>`
    : `<div><dt>${esc(d().monthlyAnnuity)}</dt><dd class="amount">${money(item.projectedMonthlyAnnuity, currency)}</dd></div>`;

  const costs = `${money(item.costsApplied, currency)}${item.costsIncludeEstimates ? ` · <span class="pension-badge pension-badge-warn">${esc(sharedT.estimateBadge)}</span>` : ''}`;

  return `<article class="pension-sim-contract">
      <div class="pension-sim-contract-head">
        <div class="row-title">${esc(item.providerName || '—')}</div>
        <div class="row-sub">${balance}</div>
        <div class="row-sub">${esc(fill(d().monthsAhead, { months: item.monthsToRetirement }))}</div>
      </div>
      <div class="pension-sim-contract-blocks">
        ${guaranteed.length ? `<div class="pension-value-block pension-sim-guarantee">
          <div class="pension-value-head">${esc(d().guaranteeTitle)}</div>
          <dl class="pension-facts">${guaranteed.map(([key, value]) =>
            `<div><dt>${esc(key)}</dt><dd class="amount">${money(value, currency)}</dd></div>`).join('')}</dl>
        </div>` : ''}
        <div class="pension-value-block pension-value-projected pension-sim-projection">
          <div class="pension-value-head">${esc(d().projectionTitle)} · <span class="pension-badge pension-badge-quiet pension-sim-return-badge">${esc(fill(d().withReturn, { percent: percent(item.returnPercent) }))}</span></div>
          <dl class="pension-facts">
            <div><dt>${esc(d().capital)}</dt><dd class="amount">${money(item.projectedCapital, currency)}</dd></div>
            ${annuity}
          </dl>
          ${item.projectedMonthlyAnnuity == null ? `<p class="row-sub">${esc(d().noAnnuityHint)}</p>` : ''}
        </div>
      </div>
      <dl class="pension-facts pension-sim-contract-facts">
        <div><dt>${esc(d().ownAhead)}</dt><dd class="amount">${money(item.employeeContributionsAhead, currency)}</dd></div>
        <div><dt>${esc(d().employerAhead)}</dt><dd class="amount">${money(item.employerContributionsAhead, currency)}</dd></div>
        <div><dt>${esc(d().costsApplied)}</dt><dd class="amount">${costs}</dd></div>
      </dl>
    </article>`;
}

// A contract that could not be projected is named, with its blocker and with the one thing that
// unblocks it. Silence about it would be the bug: the totals above would look complete.
function excludedHtml(excluded) {
  const rows = excluded || [];
  if (!rows.length) return '';
  const body = `<p class="row-sub">${esc(d().excludedHint)}</p>
    <div class="rows pension-sim-excluded">${rows.map(item => `
      <div class="row pension-sim-excluded-row">
        <div class="row-main">
          <div class="row-title">${esc(item.providerName || '—')}</div>
          <div class="pension-badges"><span class="pension-badge pension-badge-warn" data-sim-blocker="${esc(item.blocker || '')}">${esc(blockerText(item.blocker))}</span></div>
          <div class="row-sub">${esc(blockerFix(item.blocker))}</div>
        </div>
        <div class="pension-row-value">
          <div class="amount">${item.currentBalance == null ? '—' : money(item.currentBalance, item.currency || 'EUR')}</div>
        </div>
      </div>`).join('')}</div>`;
  return sectionCard(d().excludedTitle, body, { className: 'pension-sim-excluded-card' });
}

function blockerText(blocker) {
  // An unknown token is shown as it came rather than relabelled into something that reads better than
  // what actually happened — the same rule the commit result follows.
  return d().blockers[blocker] || blocker || '—';
}

function blockerFix(blocker) {
  return d().blockerFixes[blocker] || '';
}

// ---- the comparison ----

function comparableContracts() {
  return contracts.filter(contract => contract.holdsCapital !== false);
}

function compareHtml() {
  const usable = comparableContracts();
  if (usable.length < 2) {
    return sectionCard(d().compareTitle, `<div class="row-sub">${esc(d().compareNeedsTwo)}</div>`, { className: 'pension-sim-compare-card' });
  }

  const toggleButton = `<button type="button" class="btn btn-secondary" data-sim-compare-toggle aria-expanded="${compare.open}">${esc(compare.open ? d().compareClose : d().compareOpen)}</button>`;
  if (!compare.open) {
    return sectionCard(d().compareTitle,
      `<p class="row-sub pension-sim-note">${esc(d().compareHint)}</p><div class="pension-sim-actions">${toggleButton}</div>`,
      { className: 'pension-sim-compare-card' });
  }

  const options = (selected) => usable.map(contract =>
    `<option value="${esc(contract.id)}"${contract.id === selected ? ' selected' : ''}>${esc(contract.providerName)}</option>`).join('');
  const leftId = compare.leftId || usable[0].id;
  const rightId = compare.rightId || usable[1].id;

  const body = `
    <p class="row-sub pension-sim-note">${esc(d().compareHint)}</p>
    <div class="pension-sim-compare-form">
      <label class="field pension-field">
        <span>${esc(d().compareLeft)}</span>
        <select data-sim-left>${options(leftId)}</select>
      </label>
      <label class="field pension-field">
        <span>${esc(d().compareLeft)} · ${esc(d().compareReturn)}</span>
        <input type="number" inputmode="decimal" min="0" max="15" step="0.1" data-sim-left-return value="${esc(String(compare.leftReturn))}">
      </label>
      <label class="field pension-field">
        <span>${esc(d().compareRight)}</span>
        <select data-sim-right>${options(rightId)}</select>
      </label>
      <label class="field pension-field">
        <span>${esc(d().compareRight)} · ${esc(d().compareReturn)}</span>
        <input type="number" inputmode="decimal" min="0" max="15" step="0.1" data-sim-right-return value="${esc(String(compare.rightReturn))}">
      </label>
    </div>
    <div class="pension-sim-actions">
      ${toggleButton}
      <button type="button" class="btn btn-primary" data-sim-compare-run>${esc(comparing ? d().comparing : d().compareRun)}</button>
    </div>
    <div data-sim-compare-result>${comparisonHtml()}</div>`;
  return sectionCard(d().compareTitle, body, { className: 'pension-sim-compare-card' });
}

function comparisonHtml() {
  if (compareError) return `<p class="row-sub pension-sim-note">${esc(compareError)}</p>`;
  if (!comparison) return '';

  const currency = comparison.left?.currency || 'EUR';
  const causes = comparison.cause || [];
  const differentAssumptions = causes.includes('different_assumptions');

  // Rule 2. A zero delta is the lesson of this whole screen, so it is a sentence and not a "0,00 €".
  const verdict = comparison.sameMoneySameAssumptions
    ? `<div class="pension-value-block pension-sim-same-money">
        <div class="pension-value-head">${esc(d().sameMoneyTitle)}</div>
        <p class="pension-sim-sentence">${esc(d().sameMoneySentence)}</p>
      </div>`
    : `<dl class="pension-facts pension-sim-delta">
        <div><dt>${esc(d().deltaCapital)}</dt><dd class="amount">${money(comparison.capitalDelta, currency)}</dd></div>
        <div><dt>${esc(d().deltaAnnuity)}</dt><dd class="amount">${money(comparison.annuityDelta, currency)}</dd></div>
      </dl>`;

  // Rule 3. A delta without an attribution would be exactly the invented advantage the contract on the
  // server refuses to produce, so `cause` is rendered as the explanation and not as a tag.
  const attribution = causes.length && !comparison.sameMoneySameAssumptions
    ? `<div class="pension-sim-causes">
        <div class="pension-value-head">${esc(d().causeTitle)}</div>
        <ul class="pension-sim-cause-list">${causes.map(cause =>
          `<li data-sim-cause="${esc(cause)}">${esc(d().causes[cause] || cause)}</li>`).join('')}</ul>
        ${differentAssumptions ? `<p class="row-sub pension-sim-not-comparable">${esc(d().notComparable)}</p>` : ''}
      </div>`
    : '';

  const side = (label_, projection) => `<div class="pension-value-block pension-value-projected pension-sim-projection">
      <div class="pension-value-head">${esc(label_)} · <span class="pension-badge pension-badge-quiet pension-sim-return-badge">${esc(fill(d().withReturn, { percent: percent(projection?.returnPercent) }))}</span></div>
      <dl class="pension-facts">
        <div><dt>${esc(d().capital)}</dt><dd class="amount">${money(projection?.totalProjectedCapital, projection?.currency || currency)}</dd></div>
        <div><dt>${esc(d().monthlyAnnuity)}</dt><dd class="amount">${money(projection?.totalProjectedMonthlyAnnuity, projection?.currency || currency)}</dd></div>
      </dl>
    </div>`;

  return `<div class="pension-sim-comparison">
      ${verdict}
      ${attribution}
      <div class="pension-sim-sides">
        ${side(d().compareLeft, comparison.left)}
        ${side(d().compareRight, comparison.right)}
      </div>
    </div>`;
}

function wireCompare() {
  host.querySelector('[data-sim-compare-toggle]')?.addEventListener('click', () => {
    const usable = comparableContracts();
    // Both sides open on the scenario's own return, so the comparison starts on the one setting where
    // a delta can only come from the contracts. Two different returns is a valid question, but it is
    // not the question this panel exists to answer.
    compare = {
      ...compare,
      open: !compare.open,
      leftId: compare.leftId || usable[0]?.id || '',
      rightId: compare.rightId || usable[1]?.id || '',
      leftReturn: Number(scenario.returnPercent),
      rightReturn: Number(scenario.returnPercent)
    };
    comparison = null;
    compareError = null;
    paint();
  });
  host.querySelector('[data-sim-left]')?.addEventListener('change', event => { compare.leftId = event.target.value; });
  host.querySelector('[data-sim-right]')?.addEventListener('change', event => { compare.rightId = event.target.value; });
  host.querySelector('[data-sim-left-return]')?.addEventListener('change', event => { compare.leftReturn = Number(event.target.value); });
  host.querySelector('[data-sim-right-return]')?.addEventListener('change', event => { compare.rightReturn = Number(event.target.value); });
  host.querySelector('[data-sim-compare-run]')?.addEventListener('click', () => runComparison());
}

async function runComparison() {
  if (!compare.leftId || !compare.rightId || compare.leftId === compare.rightId) {
    comparison = null;
    compareError = d().compareSame;
    paint();
    return;
  }
  const token = ++compareToken;
  comparing = true;
  compareError = null;
  paint();
  let answer = null;
  let failure = null;
  try {
    answer = await ctx.api('api/pension/projection/compare', ctx.jsonBody({
      left: requestFor({ contractIds: [compare.leftId], returnPercent: Number(compare.leftReturn) }),
      right: requestFor({ contractIds: [compare.rightId], returnPercent: Number(compare.rightReturn) })
    }));
  } catch (error) {
    failure = error?.message || d().error;
  }
  // Same reason as the projection: a verdict from the previous pair must never be shown beside the
  // pair that is now selected, because a stale "kein Unterschied" would be a claim about two
  // contracts nobody compared.
  if (token !== compareToken) return;
  comparison = failure ? null : answer;
  compareError = failure;
  comparing = false;
  paint();
}

// ---- formatting: the app's own, so a projection reads exactly like every other figure ----

function money(value, currency) {
  if (value == null) return '—';
  return ctx.money(Number(value), currency || 'EUR');
}
