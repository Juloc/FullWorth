// "Dein Verlauf": was aus deinen bestätigten Abrechnungen und Vertragsständen tatsächlich geworden ist, Jahr für
// Jahr - Brutto, Bonus, Auszahlung im Monat, die Veränderung gegenüber dem Vorjahr, die Teuerung im selben Zeitraum
// und was real übrig bleibt. Darunter die Gehaltsänderungen aus den Verträgen. Der Rest des Reiters (gespeicherte
// Planungsstände aus dem Rechner) kommt erst danach und nur, wenn es welche gibt.
import { esc, euro, euro2, fmtDate, spaceId, api } from './shared.js';

const num = value => Number(value) || 0;
const pct = value => `${value >= 0 ? '+' : '−'}${Math.abs(value).toLocaleString('de-DE', { maximumFractionDigits: 1 })} %`;
const average = (list, pick) => list.length ? list.reduce((sum, item) => sum + pick(item), 0) / list.length : null;

/** Zeichnet die Karte in <paramref name="host"/>; gibt zurück, ob es Abrechnungen gab. */
export async function renderActuals(host) {
  const space = spaceId();
  if (!host || !space) return false;
  const [payslips, contracts, inflation] = await Promise.all([
    api(`api/compensation/payslips?fullWorthSpaceId=${space}`),
    api(`api/compensation/contracts?fullWorthSpaceId=${space}`),
    api('api/compensation/inflation/series').catch(() => null)]);
  if (!payslips?.length) { host.innerHTML = ''; host.hidden = true; return false; }

  // Je Monat die zuletzt gespeicherte Abrechnung, je Jahr deren Monate.
  const years = new Map();
  for (const item of payslips) {
    const p = item.payslip, year = Number(p.period.slice(0, 4)), month = Number(p.period.slice(5, 7));
    const months = years.get(year) || new Map();
    if (!months.has(month)) months.set(month, p);
    years.set(year, months);
  }

  // Die Teuerung eines Jahres ist der Durchschnitt des Verbraucherpreisindex ueber die Monate, fuer die es
  // Abrechnungen gibt - so ist sie mit dem Durchschnitt der Auszahlungen vergleichbar, auch in einem Teiljahr.
  const first = inflation ? Number(inflation.firstMonth.slice(0, 4)) * 12 + Number(inflation.firstMonth.slice(5, 7)) - 1 : 0;
  const cpi = inflation?.series.find(s => s.key === 'gesamt');
  const cpiOf = (year, months) => {
    const values = [...months.keys()].map(m => cpi?.values[year * 12 + m - 1 - first]).filter(v => v != null);
    return values.length ? values.reduce((a, b) => a + b, 0) / values.length : null;
  };

  const rows = [...years].sort((a, b) => a[0] - b[0]).map(([year, months]) => {
    const list = [...months.values()], regular = list.filter(p => !num(p.bonus));
    return {
      year, count: list.length,
      gross: list.reduce((sum, p) => sum + num(p.grossPay), 0), bonus: list.reduce((sum, p) => sum + num(p.bonus), 0),
      paid: average(regular.length ? regular : list, p => num(p.payout)), cpi: cpiOf(year, months)
    };
  });
  rows.forEach((row, i) => {
    const before = rows[i - 1];
    row.change = before?.paid ? (row.paid / before.paid - 1) * 100 : null;
    row.inflation = before?.cpi && row.cpi ? (row.cpi / before.cpi - 1) * 100 : null;
    row.real = row.change !== null && row.inflation !== null ? ((1 + row.change / 100) / (1 + row.inflation / 100) - 1) * 100 : null;
  });

  const states = (contracts?.states || []).map(x => x.state).sort((a, b) => a.validFrom.localeCompare(b.validFrom));
  const raises = states.map((s, i) => ({ date: s.validFrom, from: i ? states[i - 1].grossMonthly : null, to: s.grossMonthly }));

  host.hidden = false;
  host.innerHTML = `<div class="panel-head"><div><h2>Dein Verlauf</h2></div></div>
    <ul class="vr-list">${rows.slice().reverse().map(row => `<li class="vr-row"><div class="vr-main"><strong>${row.year}</strong><small>${row.count} Monat${row.count === 1 ? '' : 'e'} · Brutto ${euro.format(row.gross)}${row.bonus ? ` · Bonus ${euro.format(row.bonus)}` : ''}</small>${row.change === null ? '' : `<small>${pct(row.change)} gegenüber dem Vorjahr${row.inflation === null ? '' : ` · Preise ${pct(row.inflation)} · <span class="${row.real >= 0 ? 'positive' : 'negative'}">real ${pct(row.real)}</span>`}</small>`}</div><div class="vr-amount"><strong>${euro.format(row.paid)}</strong><small>ausgezahlt im Monat</small></div></li>`).join('')}</ul>
    ${raises.length ? `<h3 class="vr-sub">Gehaltsänderungen laut Vertrag</h3><ul class="vr-list">${raises.slice().reverse().map(r => `<li class="vr-row"><div class="vr-main"><strong>${fmtDate(r.date)}</strong>${r.from ? `<small>${euro.format(r.from)} → ${euro.format(r.to)}${` (${pct((r.to / r.from - 1) * 100)})`}</small>` : '<small>Start</small>'}</div><div class="vr-amount"><strong>${euro2.format(r.to)}</strong><small>Brutto im Monat</small></div></li>`).join('')}</ul>` : ''}
    <p class="extended-note">Ausgezahlt im Monat: Durchschnitt der Monate ohne Einmalzahlung. Preise: Destatis Verbraucherpreisindex im selben Zeitraum.</p>`;
  return true;
}
