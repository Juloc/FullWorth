// The forward preview's model: what the surplus is, how each part of it grows, and how the curve
// compounds.
//
// It used to be one flat number taken from the measured net-worth curve — last value minus first,
// divided by the months. That is backwards twice over: it extrapolates from the past, and it bundles
// market movement in with actual saving, so a good year on the markets read as a high savings rate and
// then compounded on top of itself.
//
// Now the surplus is composed from `GET /api/wealth/preview-basis`: configured income, the contracts
// marked as fixed costs, and what is actually spent besides those. Each line can carry its own expected
// annual increase, because a salary and a rent do not rise at the same rate and pretending they do is
// the thing that makes a thirty-year preview meaningless.

/** Lines are keyed by kind+id, so a growth rate survives a reload and follows its own contract. */
export function lineKey(line) {
  return line.id ? `${line.kind}:${line.id}` : line.kind;
}

/**
 * Monthly compounding from an ANNUAL rate is the twelfth root, not `annual / 12`.
 *
 * `1 + r/12` compounded twelve times is more than `1 + r`: at 7 % it comes out as 7.229 %, and over
 * thirty years that is a number in the owner's favour on screen and not in their account. The bAV
 * projection made the same call for the same reason; these two must not disagree about what 7 % means.
 */
export function monthlyFactor(annualPercent) {
  const annual = Number(annualPercent) || 0;
  return Math.pow(1 + annual / 100, 1 / 12);
}

/**
 * One line's amount after `months`, grown at its own annual rate. Growth is compounded on the same
 * scale as the return, so a 2 % raise means 2 % a year and not 2 % a month.
 */
export function lineAmountAt(line, growthPercent, months) {
  const amount = Number(line.monthlyAmount) || 0;
  return amount * Math.pow(monthlyFactor(growthPercent), months);
}

/**
 * The composed monthly surplus at month `months`: income minus fixed costs minus variable spend, each
 * line grown at its own rate. Income is the only positive kind — the sign lives in the kind so a line
 * cannot be added to the wrong side by accident.
 */
export function surplusAt(basis, growth, months) {
  if (!basis || !Array.isArray(basis.lines)) return 0;
  let surplus = 0;
  for (const line of basis.lines) {
    const rate = growthFor(growth, line);
    const amount = lineAmountAt(line, rate, months);
    surplus += line.kind === 'income' ? amount : -amount;
  }
  return surplus;
}

/** A line's rate: its own if set, otherwise the default for its kind, otherwise nothing. */
export function growthFor(growth, line) {
  const stored = growth?.[lineKey(line)];
  if (Number.isFinite(Number(stored))) return Number(stored);
  const kindDefault = growth?.[line.kind];
  return Number.isFinite(Number(kindDefault)) ? Number(kindDefault) : 0;
}

/**
 * The curve. `value(m+1) = value(m) * factor + surplus(m)`: the surplus arrives at the start of the
 * month, which is when a salary lands and a rent leaves, and it is the surplus *of that month* rather
 * than of the first one — that is the whole point of per-line growth.
 *
 * A negative surplus is kept as it is. A preview that quietly floors the shortfall at zero would tell
 * somebody spending more than they earn that their wealth is flat.
 */
export function projectSeries(start, { basis, growth, returnPercent, months, flatSurplus = null }) {
  const factor = monthlyFactor(returnPercent);
  const series = [Number(start) || 0];
  let value = series[0];
  for (let month = 0; month < months; month++) {
    const surplus = flatSurplus === null ? surplusAt(basis, growth, month) : Number(flatSurplus) || 0;
    value = value * factor + surplus;
    series.push(value);
  }
  return series;
}

/**
 * What the composition is today, for the screen. `budgetLimit` is reported beside the variable average
 * and never inside it: a budget is an intention, the preview is about what is likely.
 */
export function basisSummary(basis) {
  if (!basis) return null;
  return {
    currency: basis.currency,
    income: Number(basis.monthlyIncome) || 0,
    fixedCosts: Number(basis.monthlyFixedCosts) || 0,
    variableSpend: Number(basis.monthlyVariableSpend) || 0,
    budgetLimit: Number(basis.monthlyBudgetLimit) || 0,
    surplus: Number(basis.monthlySurplus) || 0,
    observedMonths: Number(basis.observedMonths) || 0,
    isComplete: basis.isComplete !== false,
    missingCurrencies: Array.isArray(basis.missingCurrencies) ? basis.missingCurrencies : [],
    // A basis with no income cannot produce a surplus worth showing, and falling back to the old
    // backward estimate is more honest than a confident -1.200 € a month.
    usable: (Number(basis.monthlyIncome) || 0) > 0
  };
}

/**
 * Purchasing power: the end value divided by the inflation over the same span. This is the number the
 * per-line growth actually makes answerable — if every line rises 2 % and the return is 5 %, the
 * nominal end value says less than what it buys.
 */
export function realValue(nominal, inflationPercent, months) {
  const factor = monthlyFactor(inflationPercent);
  return factor <= 0 ? nominal : nominal / Math.pow(factor, months);
}
