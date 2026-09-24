// Budget-Gruppen (#177).
//
// Vier Routen unter /api/budget-groups standen fertig im Baum und hatten keinen Aufrufer - Auflisten,
// Anlegen, Umbenennen, Archivieren. Dazu trug der Geltungsbereich eines Budgets seit jeher ein
// groupId-Feld, das der Dialog treu hin- und herschickte und nie setzen konnte: eine Zuordnung, die
// niemand vergeben konnte.
//
// Hier ist die eine Stelle, an der Gruppen entstehen und vergehen. Die Budgetliste gruppiert danach,
// der Budget-Dialog waehlt daraus - beide lesen, nur dieses Blatt schreibt.
//
// Archivieren statt loeschen ist die Entscheidung des Servers (DELETE setzt IsArchived), und sie ist
// die richtige: ein Budget, das noch auf die Gruppe zeigt, verliert damit seine Zuordnung nicht, es
// sieht sie nur nicht mehr in der Auswahl.

import { openFormDialog, FieldKind } from '../../components/form-dialog.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';

const bilingual = (german, english) =>
  (document.documentElement.lang || '').startsWith('en') ? english : german;

/** Die aktiven Gruppen, nach ihrer Reihenfolge und dann nach Namen. */
export async function loadBudgetGroups(ctx) {
  let rows;
  try { rows = await ctx.api('api/budget-groups'); }
  catch { return []; }
  return (Array.isArray(rows) ? rows : [])
    .filter(row => !row.isArchived)
    .sort((a, b) => (Number(a.sortOrder) || 0) - (Number(b.sortOrder) || 0)
      || String(a.name || '').localeCompare(String(b.name || '')));
}

/**
 * Die Auswahl im Budget-Dialog. Bewusst nur die vorhandenen Gruppen plus "Ohne Gruppe" - wer eine
 * neue braucht, legt sie dort an, wo Gruppen verwaltet werden, und nicht nebenbei in einem Dialog,
 * der von etwas anderem handelt.
 */
export function budgetGroupOptions(ctx, groups, selected) {
  const chosen = String(selected || '');
  return `<option value="">${ctx.esc(ctx.get('accounts.ungrouped'))}</option>`
    + groups.map(group => `<option value="${ctx.esc(group.id)}"${String(group.id) === chosen ? ' selected' : ''}>`
      + `${ctx.esc(group.name)}</option>`).join('');
}

/**
 * Die Verwaltung: eine Liste, je Zeile Umbenennen und Archivieren, darunter das Anlegen.
 *
 * @param {object} ctx      Seitenkontext.
 * @param {Function} after  Wird gerufen, wenn sich etwas geaendert hat - die Liste zeichnet neu.
 * @param {boolean} dirty   Ob eine frueher geoeffnete Runde dieses Blattes schon etwas geaendert hat.
 *                          Nach jeder Aenderung oeffnet sich die Verwaltung neu; ohne dieses Merkmal
 *                          ginge die Tatsache dabei verloren und die Liste dahinter bliebe alt.
 */
export async function openBudgetGroupManager(ctx, after, dirty = false) {
  const groups = await loadBudgetGroups(ctx);

  const rows = groups.length
    ? groups.map(group => `<div class="row budget-group-row" data-group="${ctx.esc(group.id)}">`
        + `<div class="row-main"><div class="row-title">${ctx.esc(group.name)}</div></div>`
        + `<div class="budget-group-row-actions">`
        + `<button type="button" class="${buttonClass(ButtonRole.Secondary, 'budget-group-rename')}" data-rename>`
        + `${ctx.esc(ctx.get('accounts.renameGroup'))}</button>`
        + `<button type="button" class="${buttonClass(ButtonRole.Danger, 'budget-group-archive')}" data-archive>`
        + `${ctx.esc(bilingual('Archivieren', 'Archive'))}</button>`
        + `</div></div>`).join('')
    : `<p class="row-sub">${ctx.esc(bilingual('Noch keine Gruppen.', 'No groups yet.'))}</p>`;

  const dialog = ctx.dialog(`<div class="dialog-card budget-group-manager">
    <div class="panel-head"><h3>${ctx.esc(bilingual('Budget-Gruppen', 'Budget groups'))}</h3></div>
    <div class="budget-group-list">${rows}</div>
    <div class="dialog-actions">
      <button type="button" class="${buttonClass(ButtonRole.Secondary, 'budget-group-close')}" data-close>${ctx.esc(ctx.get('common.close'))}</button>
      <button type="button" class="${buttonClass(ButtonRole.Primary, 'budget-group-add')}" data-add>${ctx.esc(ctx.get('accounts.newGroup'))}</button>
    </div>
  </div>`, { className: 'budget-group-dialog' });

  let changed = dirty;
  const done = () => { dialog.close(); if (changed) after?.(); };

  dialog.querySelector('[data-close]').addEventListener('click', done);
  dialog.querySelector('[data-add]').addEventListener('click', async () => {
    if (await editGroup(ctx, null, groups)) { dialog.close(); openBudgetGroupManager(ctx, after, true); }
  });

  for (const row of dialog.querySelectorAll('.budget-group-row')) {
    const group = groups.find(entry => String(entry.id) === row.dataset.group);
    row.querySelector('[data-rename]').addEventListener('click', async () => {
      if (await editGroup(ctx, group, groups)) { dialog.close(); openBudgetGroupManager(ctx, after, true); }
    });
    row.querySelector('[data-archive]').addEventListener('click', async () => {
      // Die Frage nennt die Folge, nicht die Tat: "archivieren" sagt nichts darueber, was mit den
      // Budgets darin passiert - naemlich nichts ausser dass sie wieder ohne Gruppe dastehen.
      const sure = await ctx.confirm(bilingual(
        `„${group.name}" archivieren? Die Budgets darin bleiben bestehen und stehen danach ohne Gruppe.`,
        `Archive "${group.name}"? Its budgets stay and are ungrouped afterwards.`));
      if (!sure) return;
      try {
        await ctx.api(`api/budget-groups/${group.id}`, { method: 'DELETE' });
        dialog.close();
        openBudgetGroupManager(ctx, after, true);
      } catch (error) { ctx.toast(error.message || ctx.get('common.error')); }
    });
  }

  dialog.showModal();
}

/**
 * Anlegen oder Umbenennen. Die Reihenfolge wird nicht gefragt: eine neue Gruppe geht ans Ende, eine
 * bestehende behaelt ihre - eine Zahl, nach der niemand gefragt hat, ist ein Feld zu viel.
 */
function editGroup(ctx, existing, groups) {
  return new Promise(resolve => {
    let saved = false;
    const handles = openFormDialog({
      title: ctx.get(existing ? 'accounts.renameGroup' : 'accounts.newGroup'),
      closeLabel: ctx.get('common.close'),
      fallbackError: ctx.get('common.error'),
      create: html => ctx.dialog(html),
      fields: [{ name: 'name', kind: FieldKind.Text, label: ctx.get('accounts.groupName'), required: true, maxLength: 120 }],
      values: { name: existing?.name || '' },
      actions: [
        { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close() },
        { name: 'save', label: ctx.get(existing ? 'common.save' : 'common.create'), role: 'primary', submit: true }
      ],
      onSubmit: async ({ values, setFormError, close }) => {
        const name = String(values.name || '').trim();
        if (!name) return setFormError(ctx.get('common.error'));
        const sortOrder = existing
          ? (Number(existing.sortOrder) || 0)
          : Math.max(0, ...groups.map(group => Number(group.sortOrder) || 0)) + 100;
        try {
          await ctx.api(
            existing ? `api/budget-groups/${existing.id}` : 'api/budget-groups',
            ctx.jsonBody({ name, sortOrder }, existing ? 'PUT' : 'POST'));
          saved = true;
          close();
        } catch (error) { setFormError(error.message || ctx.get('common.error')); }
      }
    });
    // form-dialog kennt kein onClose - das Element selbst schon, und zwar unabhaengig davon, ob
    // gespeichert, abgebrochen oder mit Escape geschlossen wurde.
    handles.dialog.addEventListener('close', () => resolve(saved), { once: true });
  });
}
