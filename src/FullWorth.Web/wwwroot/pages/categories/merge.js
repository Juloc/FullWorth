// Zwei Kategorien zusammenführen (#177).
//
// `GET /api/category-merge/{id}/preview` und `POST /api/category-merge/{id}` standen fertig im Baum
// und hatten keinen Aufrufer. Wer zwei Kategorien für dieselbe Sache hatte — und das passiert bei
// jedem Import — konnte sie nur nacheinander an jeder Buchung von Hand umhängen.
//
// Die Vorschau ist hier nicht Zierde und auch nicht optional: Zusammenführen ändert rückwirkend jede
// Auswertung, jede Regel, jedes Budget und jeden Vertrag, der an der Quelle hing. Deshalb steht die
// Zahl DAVOR auf dem Schirm, und der Knopf bleibt aus, solange der Server sagt, dass es nicht geht.
//
// Zwei Gründe kennt er, und beide sind keine Fehlermeldung, sondern eine Aufgabe:
//
//   aktive Unterkategorien   Sie blieben elternlos zurück. Erst umhängen oder mitführen.
//   Ziel unterhalb der Quelle  Der Baum schlösse einen Kreis.

import { attachCombobox } from '../../components/combobox.js';
import { categoryComboboxItems } from '../../components/category-combobox.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';

const bilingual = (german, english) =>
  (document.documentElement.lang || '').startsWith('en') ? english : german;

/** Die Quelle selbst, alles darunter und jede archivierte Kategorie scheiden als Ziel aus. */
function bannedTargets(source, all) {
  const banned = new Set([source.id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const category of all)
      if (category.parentId && banned.has(category.parentId) && !banned.has(category.id)) {
        banned.add(category.id);
        grew = true;
      }
  }
  for (const category of all) if (category.isArchived) banned.add(category.id);
  return banned;
}

/**
 * @param {object} ctx       Seitenkontext.
 * @param {object} source    Die Kategorie, die aufgeht.
 * @param {Array} all        Alle Kategorien der Seite.
 * @param {Function} onDone  Wird nach einem erfolgreichen Zusammenführen gerufen.
 */
export function openCategoryMerge(ctx, source, all, onDone) {
  const banned = bannedTargets(source, all);
  const options = all.filter(category => !banned.has(category.id))
    .map(category => `<option value="${ctx.esc(category.id)}">${ctx.esc(category.name)}</option>`).join('');

  const dialog = ctx.dialog(`<form class="dialog-card cat-merge">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('categories.merge'))}</h2>
      <button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <p class="row-sub">${ctx.esc(bilingual(
      `Alles, was an „${source.name}" hängt, zählt danach zur Zielkategorie.`,
      `Everything attached to "${source.name}" will count towards the target afterwards.`))}</p>
    <label>${ctx.esc(ctx.get('categories.mergeTarget'))}
      <select name="target"><option value="">—</option>${options}</select></label>
    <div class="cat-merge-preview rows"></div>
    <label class="check cat-merge-delete" hidden>
      <input type="checkbox" name="deleteSource">
      <span>${ctx.esc(ctx.get('categories.mergeDeleteSource'))}</span></label>
    <div class="dialog-actions">
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button>
      <button type="submit" class="${buttonClass(ButtonRole.Primary, 'cat-merge-apply')}" disabled>${ctx.esc(ctx.get('categories.merge'))}</button>
    </div>
  </form>`);

  const select = dialog.querySelector('select[name="target"]');
  const preview = dialog.querySelector('.cat-merge-preview');
  const deleteRow = dialog.querySelector('.cat-merge-delete');
  const apply = dialog.querySelector('.cat-merge-apply');

  attachCombobox(ctx, select, {
    title: ctx.get('categories.mergeTarget'),
    anchored: true,
    items: () => categoryComboboxItems(all, { exclude: banned })
  });

  dialog.querySelector('[data-close]').onclick = () => dialog.close();
  dialog.querySelector('[data-cancel]').onclick = () => dialog.close();
  select.addEventListener('change', () => void load());

  async function load() {
    const target = select.value;
    apply.disabled = true;
    deleteRow.hidden = true;
    if (!target) { preview.replaceChildren(); return; }

    preview.replaceChildren(row(ctx.get('common.loading')));
    let result;
    try {
      result = await ctx.api(
        `api/category-merge/${source.id}/preview?targetCategoryId=${encodeURIComponent(target)}`);
    } catch (error) {
      preview.replaceChildren(row(error.message || ctx.get('common.error'), null, 'bad'));
      return;
    }
    paint(result);
  }

  function paint(result) {
    // Nur was wirklich hängt. Eine Liste aus lauter Nullen sagt nichts und verdeckt die eine Zahl,
    // auf die es ankommt.
    const rows = [
      ['categories.mergeTransactions', result.transactions],
      ['categories.mergeRules', result.rules],
      ['categories.mergeBudgets', result.budgets],
      ['categories.mergeContracts', result.contracts],
      ['categories.mergeSplits', result.splitAllocations],
      ['categories.mergePurchaseItems', result.purchaseItems]
    ].filter(([, count]) => Number(count) > 0)
      .map(([key, count]) => row(`${count} × ${ctx.get(key)}`));

    if (!rows.length) rows.push(row(bilingual('Es hängt nichts daran.', 'Nothing is attached to it.')));

    if (result.activeChildren > 0) {
      rows.push(row(bilingual(
        `${result.activeChildren} aktive Unterkategorie(n) hängen darunter. Erst umhängen oder archivieren.`,
        `${result.activeChildren} active subcategor(ies) below it. Move or archive them first.`), null, 'bad'));
    }
    if (result.targetIsDescendant) {
      rows.push(row(bilingual(
        'Das Ziel liegt unterhalb der Quelle - das würde den Baum in einen Kreis schließen.',
        'The target sits below the source - that would close a cycle in the tree.'), null, 'bad'));
    }

    preview.replaceChildren(...rows);
    apply.disabled = !result.canApply;
    deleteRow.hidden = !result.canDeleteSource;
    if (!result.canDeleteSource) deleteRow.querySelector('input').checked = false;
  }

  function row(text, sub, tone) {
    const element = document.createElement('div');
    element.className = `row${tone ? ` cat-merge-${tone}` : ''}`;
    const main = document.createElement('div');
    main.className = 'row-main';
    const title = document.createElement('div');
    title.className = 'row-title';
    title.textContent = text;
    main.append(title);
    if (sub) {
      const detail = document.createElement('div');
      detail.className = 'row-sub';
      detail.textContent = sub;
      main.append(detail);
    }
    element.append(main);
    return element;
  }

  dialog.querySelector('form').onsubmit = async event => {
    event.preventDefault();
    if (apply.disabled) return;
    apply.disabled = true;

    const target = select.value;
    const deleteSource = dialog.querySelector('[name="deleteSource"]').checked;
    // Eine Rückfrage, weil es rückwirkend ist und nicht rückgängig zu machen.
    const sure = await ctx.confirm(bilingual(
      `„${source.name}" zusammenführen? Das ändert rückwirkend jede Auswertung, in der die Kategorie vorkam.`,
      `Merge "${source.name}"? This retroactively changes every report the category appeared in.`),
      { destructive: true, confirmLabel: ctx.get('categories.merge') });
    if (!sure) { apply.disabled = false; return; }

    try {
      await ctx.api(`api/category-merge/${source.id}`,
        ctx.jsonBody({ targetCategoryId: target, deleteSource }));
      dialog.close();
      ctx.toast(ctx.get('common.saved'));
      await onDone?.();
    } catch (error) {
      apply.disabled = false;
      ctx.toast(error.message || ctx.get('common.error'));
    }
  };

  dialog.showModal();
}
