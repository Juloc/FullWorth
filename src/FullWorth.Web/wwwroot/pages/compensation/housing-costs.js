// "Heiz- und Nebenkosten": Betraege aus Abrechnungen (Heizkosten, Wasser, Nebenkosten, Hausgeld, Strom) mit ihrem
// Zeitraum. Sie stehen hier als Liste und gehen als Linien in "Gehalt gegen Preise" ein - umgerechnet auf Euro
// je Monat, weil eine Abrechnung oft nur vier oder fuenf Monate deckt.
import { $, esc, attr, euro2, fmtDate, spaceId, api, json, notify } from './shared.js';

export const KINDS = { heating: 'Heizung / Wärme', water: 'Wasser', ancillary: 'Nebenkosten / Hausgeld', electricity: 'Strom', other: 'Sonstiges' };

/** Die Monate eines Zeitraums (von und bis einschliesslich), auf zehntel genau. */
export const monthsOf = entry => Math.max(0.1, Math.round((((new Date(entry.periodTo) - new Date(entry.periodFrom)) / 86400000) + 1) / 30.4375 * 10) / 10);

let host = null, onChange = null, entries = [];

export async function renderHousingCosts(element, callback) {
  host = element; onChange = callback;
  if (!host) return;
  if (!host.dataset.bound) { host.dataset.bound = '1'; host.addEventListener('click', event => act(event).catch(fail)); host.addEventListener('submit', event => { event.preventDefault(); add().catch(fail); }); }
  await load();
}

const fail = error => notify(error?.message || 'Das hat nicht geklappt.');

async function load() {
  const space = spaceId(); if (!space) return;
  entries = await api(`api/compensation/housing-costs?fullWorthSpaceId=${space}`) || [];
  draw();
  onChange?.(entries);
}

function draw() {
  const rows = entries.map(e => `<li class="hc-row"><div class="hc-main"><strong>${esc(KINDS[e.kind] || e.kind)}</strong><small>${fmtDate(e.periodFrom)} – ${fmtDate(e.periodTo)}${e.note ? ` · ${esc(e.note)}` : ''}</small></div>`
    + `<div class="hc-amount"><strong>${euro2.format(e.amount)}</strong><small>${euro2.format(e.amount / monthsOf(e))} / Monat</small></div>`
    + `<button type="button" class="btn btn-danger" data-hc-delete="${attr(e.id)}" aria-label="Eintrag löschen" title="Löschen">×</button></li>`).join('');
  host.innerHTML = `<div class="panel-head"><div><h2>Heiz- und Nebenkosten</h2><p>Beträge aus deinen Abrechnungen mit ihrem Zeitraum. Sie erscheinen als Linien in „Gehalt gegen Preise“, umgerechnet auf Euro im Monat - sobald für ein Jahr mindestens elf Monate erfasst sind, denn ein Heizsaison-Ausschnitt ist mit einem anderen nicht vergleichbar.</p></div></div>`
    + (rows ? `<ul class="hc-list">${rows}</ul>` : '<p class="extended-note">Noch nichts erfasst.</p>')
    + `<details class="hc-add"><summary>Betrag erfassen</summary><form class="form-grid" data-hc-form>
        <label>Kostenart<select name="kind">${Object.entries(KINDS).map(([k, v]) => `<option value="${k}">${v}</option>`).join('')}</select></label>
        <label>Betrag in €<input name="amount" type="number" min="0.01" step="0.01" required inputmode="decimal"></label>
        <label>Zeitraum von<input name="from" type="date" required></label>
        <label>Zeitraum bis<input name="to" type="date" required></label>
        <label class="hc-wide">Notiz<input name="note" maxlength="500" placeholder="z. B. Wärme- und Wasserkostenabrechnung 2024"></label>
        <button type="submit" class="btn btn-primary hc-wide">Speichern</button></form></details>`;
}

async function add() {
  const form = host.querySelector('[data-hc-form]'), data = new FormData(form);
  await api(`api/compensation/housing-costs?fullWorthSpaceId=${spaceId()}`, json('POST', {
    kind: data.get('kind'), amount: Number(data.get('amount')), periodFrom: data.get('from'), periodTo: data.get('to'), note: String(data.get('note') || '').trim() || null }));
  notify('Gespeichert.');
  await load();
}

async function act(event) {
  const button = event.target.closest('[data-hc-delete]'); if (!button) return;
  await api(`api/compensation/housing-costs/${button.dataset.hcDelete}?fullWorthSpaceId=${spaceId()}`, { method: 'DELETE' });
  await load();
}
