// Die Reihenfolge der Kategorien (#177).
//
// PUT /api/category-order stand fertig im Baum und hatte keinen Aufrufer - dabei sortiert die
// Kategorienliste seit jeher nach sortOrder. Der Wert war also wirksam und unveraenderbar: die
// Reihenfolge, die jemand sah, war die, die der Seeder einmal vergeben hatte.
//
// Bewegt wird mit zwei Knoepfen, nicht mit der Maus. Ziehen waere hier die schlechtere Wahl: der Baum
// ist eingerueckt, ein Ziehen muesste zwischen "eine Position hoeher" und "unter die Kategorie
// darueber" unterscheiden, und das Verschieben in eine andere Eltern-Kategorie hat laengst einen
// eigenen, benannten Weg im Bearbeiten-Dialog. Zwei Pfeile koennen nur das eine, sind mit der
// Tastatur bedienbar und brauchen keine zweite Erklaerung.
//
// Geschrieben wird EIN Aufruf mit allen Geschwistern, die sich verschoben haben. Der Server prueft
// die ganze Menge in einer Transaktion; eine Anfrage je Zeile koennte auf halbem Weg stehenbleiben
// und eine Reihenfolge hinterlassen, die niemand gewaehlt hat.

import { ButtonRole, buttonClass } from '../../components/buttons.js';

const STEP = 100;

const bilingual = (german, english) =>
  (document.documentElement.lang || '').startsWith('en') ? english : german;

/**
 * Zeichnet den Baum im Sortiermodus.
 *
 * @param {object} ctx        Seitenkontext.
 * @param {Element} host      Wohin.
 * @param {Array} rows        Alle Kategorien, so wie der Server sie geliefert hat.
 * @param {Function} onDone   Wird gerufen, wenn der Modus endet (gespeichert oder abgebrochen).
 */
export function renderCategoryArrange(ctx, host, rows, onDone) {
  // Archivierte bleiben draussen: sie stehen nicht in der Liste, die jemand ordnen will, und ihre
  // Position mitzuschicken hiesse, sie beim naechsten Wiederherstellen an eine Stelle zu setzen,
  // die niemand fuer sie gewaehlt hat.
  const active = rows.filter(row => !row.isArchived);
  const byParent = new Map();
  for (const row of active) {
    const key = row.parentId || '__root';
    if (!byParent.has(key)) byParent.set(key, []);
    byParent.get(key).push(row);
  }
  for (const list of byParent.values())
    list.sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0) || a.name.localeCompare(b.name));

  host.innerHTML = '';
  const list = document.createElement('div');
  list.className = 'cat-arrange';
  host.append(list);
  paint();

  const bar = document.createElement('div');
  bar.className = 'cat-arrange-bar';
  bar.innerHTML = `<span class="row-sub">${ctx.esc(bilingual(
    'Mit den Pfeilen verschieben. Eine Kategorie bleibt dabei in ihrer Eltern-Kategorie.',
    'Move with the arrows. A category stays inside its parent.'))}</span>`;
  const actions = document.createElement('div');
  const cancel = document.createElement('button');
  cancel.type = 'button';
  cancel.className = buttonClass(ButtonRole.Secondary, 'cat-arrange-cancel');
  cancel.textContent = ctx.get('common.cancel');
  cancel.addEventListener('click', () => onDone(false));
  const save = document.createElement('button');
  save.type = 'button';
  save.className = buttonClass(ButtonRole.Primary, 'cat-arrange-save');
  save.textContent = ctx.get('common.save');
  save.addEventListener('click', () => apply());
  actions.append(cancel, save);
  bar.append(actions);
  host.append(bar);

  function paint() {
    list.innerHTML = '';
    for (const root of byParent.get('__root') || []) row(root, 0);
  }

  function row(node, depth) {
    const siblings = byParent.get(node.parentId || '__root');
    const index = siblings.indexOf(node);
    const element = document.createElement('div');
    element.className = 'cat-arrange-row';
    element.dataset.categoryId = node.id;
    element.style.setProperty('--depth', depth);
    element.innerHTML = `<span class="cat-arrange-name">${ctx.esc(node.name)}</span>`;

    const move = (direction, label) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = buttonClass(ButtonRole.Icon);
      button.textContent = direction < 0 ? '↑' : '↓';
      button.title = label;
      button.setAttribute('aria-label', `${node.name}: ${label}`);
      button.disabled = direction < 0 ? index === 0 : index === siblings.length - 1;
      button.addEventListener('click', () => {
        const target = index + direction;
        [siblings[index], siblings[target]] = [siblings[target], siblings[index]];
        paint();
        // Den Fokus mitnehmen: wer mit der Tastatur zweimal hintereinander verschiebt, soll nicht
        // nach jedem Schritt neu suchen muessen, wo der Knopf hin ist.
        list.querySelector(`[data-category-id="${CSS.escape(node.id)}"] button:${direction < 0 ? 'first' : 'last'}-of-type`)?.focus();
      });
      return button;
    };

    const group = document.createElement('span');
    group.className = 'cat-arrange-actions';
    group.append(
      move(-1, bilingual('nach oben', 'move up')),
      move(1, bilingual('nach unten', 'move down')));
    element.append(group);
    list.append(element);

    for (const child of byParent.get(node.id) || []) row(child, depth + 1);
  }

  async function apply() {
    if (save.disabled) return;
    save.disabled = true;
    // Nur was sich wirklich bewegt hat. Die ganze Liste zu schicken waere einfacher und wuerde jede
    // Kategorie als geaendert protokollieren, auch die, die niemand angefasst hat.
    const items = [];
    for (const [key, siblings] of byParent) {
      siblings.forEach((node, index) => {
        const order = (index + 1) * STEP;
        if ((node.sortOrder ?? 0) !== order)
          items.push({ id: node.id, parentId: key === '__root' ? null : key, sortOrder: order });
      });
    }
    if (!items.length) return onDone(false);

    try {
      await ctx.api('api/category-order', ctx.jsonBody({ items }, 'PUT'));
      ctx.toast(ctx.get('common.saved'));
      onDone(true);
    } catch (error) {
      save.disabled = false;
      ctx.toast(error.message || ctx.get('common.error'));
    }
  }
}
