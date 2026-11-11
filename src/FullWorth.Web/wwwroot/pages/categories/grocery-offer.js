import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { confirmDialog } from '../../components/confirm.js';

const t = (de, en) => (document.documentElement.lang || 'de').toLowerCase().startsWith('de') ? de : en;

// Der Supermarkt-Baum (Obst & Gemüse, Milch & Eier, Alkohol ...) entsteht in neuen Spaces von selbst. Ein Space, der
// schon Kategorien hat, bekommt ihn nur auf Wunsch - sonst käme jede Standardkategorie nach dem Löschen wieder.
// Der Hinweis steht deshalb nur da, solange "Lebensmittel" noch keine Unterkategorie hat, und verschwindet danach.
export function renderGroceryOffer(ctx, rows, host, reload) {
  const active = rows.filter(row => !row.isArchived);
  const offered = active.some(row => row.key === 'food.groceries') && !active.some(row => String(row.key).startsWith('food.groceries.'));
  host.innerHTML = offered
    ? `<div class="cat-offer">
        <div><strong>${ctx.esc(t('Lebensmittel genauer aufteilen', 'Split groceries up'))}</strong>
          <p>${ctx.esc(t('Obst & Gemüse, Milch & Eier, Fleisch, Getränke, Alkohol, Süßigkeiten … Was du schon angelegt hast (Alkohol, Süßigkeiten, Schokolade), wird einsortiert. Positionen deiner Belege, die nur „Lebensmittel“ sind, ordnet FullWorth danach ihrem Namen zu.', 'Fruit & vegetables, dairy, meat, drinks, alcohol, sweets … What you already created is sorted in. Receipt items that are only "Groceries" are then sorted by name.'))}</p></div>
        <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-grocery-setup>${ctx.esc(t('Anlegen', 'Create'))}</button>
      </div>`
    : '';
  host.querySelector('[data-grocery-setup]')?.addEventListener('click', async event => {
    const button = event.currentTarget;
    button.disabled = true;
    try {
      const tree = await ctx.api('api/categories/grocery-tree', { method: 'POST' });
      const preview = await ctx.api('api/purchases/items/refine-grocery-categories', { method: 'POST' });
      ctx.toast(t(`${tree.created} Kategorien angelegt, ${tree.adopted} einsortiert.`, `${tree.created} categories created, ${tree.adopted} sorted in.`));
      if (preview?.changed > 0 && await confirmDialog(ctx,
        t(`${preview.changed} von ${preview.evaluated} Positionen, die nur „Lebensmittel“ sind, lassen sich nach ihrem Namen genauer einordnen. Jetzt einordnen?`,
          `${preview.changed} of ${preview.evaluated} items that are only "Groceries" can be sorted by name. Sort them now?`),
        { title: t('Positionen einordnen', 'Sort items'), confirmLabel: t('Einordnen', 'Sort') })) {
        const done = await ctx.api('api/purchases/items/refine-grocery-categories?apply=true', { method: 'POST' });
        ctx.toast(t(`${done.changed} Positionen eingeordnet.`, `${done.changed} items sorted.`));
      }
      await reload();
    } catch (error) {
      ctx.toast(error.message || ctx.get('common.error'));
      button.disabled = false;
    }
  });
}
