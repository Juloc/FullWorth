// Category picker (§10.5): search across full category paths, show the icon, and create a category
// inline without leaving the current flow. Layers ON TOP of an existing <select> — clicking a row
// just sets selectEl.value and dispatches 'change', so every existing form (FormData reads, plain
// sel.value reads) keeps working unchanged; this only adds a richer way to set that value.

export function attachCategoryPicker(ctx, selectEl) {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'icon-button category-picker-trigger';
  btn.title = ctx.get('categories.pick');
  btn.setAttribute('aria-label', ctx.get('categories.pick'));
  btn.textContent = '⌕';
  selectEl.insertAdjacentElement('afterend', btn);
  btn.addEventListener('click', () => openCategoryPicker(ctx, id => {
    selectEl.value = id;
    selectEl.dispatchEvent(new Event('change', { bubbles: true }));
  }, selectEl));
}

function pathOf(category, byId) {
  const chain = [];
  let current = category;
  while (current) {
    chain.unshift(current.name);
    current = current.parentId ? byId.get(current.parentId) : null;
  }
  return chain.join(' › ');
}

function slugify(name) {
  return name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, '') || `cat-${Date.now()}`;
}

// Opens the picker and calls onSelect(categoryId) with the chosen (or freshly created) id. `selectEl`
// is optional and only used to append a newly-created <option> when the picker layers over a <select>;
// callers without a select (e.g. the transactions list category chip) just pass a callback.
export async function openCategoryPicker(ctx, onSelect, selectEl = null) {
  let categories;
  try { categories = await ctx.api('api/categories'); }
  catch (err) { ctx.toast(err.message || ctx.get('common.error')); return; }
  const byId = new Map(categories.map(c => [c.id, c]));
  let items = categories.map(c => ({ id: c.id, icon: c.icon, label: pathOf(c, byId) })).sort((a, b) => a.label.localeCompare(b.label));

  const dlg = ctx.dialog(`<div class="dialog-card drawer">
    <div class="panel-head"><h2>${ctx.esc(ctx.get('categories.pick'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <input type="search" data-search placeholder="${ctx.esc(ctx.get('categories.pickSearch'))}">
    <div class="refund-candidates" data-list></div>
    <form data-new-form hidden><label>${ctx.esc(ctx.get('categories.new'))}<input name="name" maxlength="120"></label><div class="dialog-actions"><button type="submit">${ctx.esc(ctx.get('common.create'))}</button></div></form>
    <button type="button" class="ghost" data-toggle-new>${ctx.esc(ctx.get('categories.new'))}</button>
  </div>`);

  const select = id => { onSelect(id); dlg.close(); };
  const list = dlg.querySelector('[data-list]');
  const render = filter => {
    const q = filter.trim().toLowerCase();
    const shown = q ? items.filter(i => i.label.toLowerCase().includes(q)) : items;
    list.innerHTML = shown.length
      ? shown.map(i => `<button type="button" class="row candidate-row" data-id="${i.id}"><div class="row-main"><div class="row-title">${i.icon ? ctx.esc(i.icon) + ' ' : ''}${ctx.esc(i.label)}</div></div></button>`).join('')
      : `<div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div>`;
    list.querySelectorAll('[data-id]').forEach(row => row.addEventListener('click', () => select(row.dataset.id)));
  };
  render('');
  dlg.querySelector('[data-search]').addEventListener('input', e => render(e.target.value));
  dlg.querySelector('[data-close]').onclick = () => dlg.close();

  const newForm = dlg.querySelector('[data-new-form]');
  dlg.querySelector('[data-toggle-new]').addEventListener('click', () => {
    newForm.hidden = !newForm.hidden;
    if (!newForm.hidden) newForm.querySelector('input[name="name"]').focus();
  });
  newForm.addEventListener('submit', async e => {
    e.preventDefault();
    const name = new FormData(newForm).get('name').trim();
    if (!name) return;
    try {
      const created = await ctx.api('api/categories', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ key: slugify(name), name, parentId: null, icon: null, sortOrder: null }) });
      if (selectEl) {
        const option = document.createElement('option');
        option.value = created.id;
        option.textContent = name;
        selectEl.appendChild(option);
      }
      select(created.id);
    } catch (err) { ctx.toast(err.message || ctx.get('common.error')); }
  });
  dlg.showModal();
}
