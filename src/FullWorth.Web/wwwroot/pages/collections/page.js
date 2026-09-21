// Sammlungen: „wofür gehörte diese Buchung zusammen?" (#124)
//
// Die Kategorie beantwortet „was wurde gekauft", die Sammlung „zu welchem Vorhaben gehört es".
// Beide Achsen sind unabhängig; eine Sammlung ändert nie Kategorie, Betrag oder Buchung.
//
// Gerechnet wird ausschließlich im Backend. Diese Datei summiert nichts: Sammlungen überschneiden
// sich — dieselbe Bauhaus-Buchung gehört zu „Wohnung" UND zu „Badrenovierung" —, und eine Summe über
// mehrere wäre doppelt gezählt. Deshalb steht hier auch bewusst keine Gesamtzeile über der Liste.

import { emptyRow } from '../../components/empty.js';
import { categoryIconInner, categoryIconPicker, selectedIconKey } from '../../components/icons.js';
import { openFormDialog, FieldKind } from '../../components/form-dialog.js';
import { keepListPosition } from '../../components/list-position.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { selectionListHtml, createSelectionList } from '../../components/selection-list.js';

const STATUSES = ['active', 'completed', 'archived'];

let ctx = null;
let state = { rows: [], openId: null, status: '', query: '' };

const t = key => ctx.get('collections.' + key);

export async function renderCollections(pageContext) {
  ctx = pageContext;
  await refresh();
}

export function bindCollections(pageContext) {
  ctx = pageContext;
  ctx.$('#col-status')?.addEventListener('change', event => { state.status = event.target.value; void refresh(); });
  // Die Suche filtert die bereits geladene Liste - sie ist kurz, und eine Runde zum Server für ein
  // Wort wäre langsamer als das Tippen.
  ctx.$('#col-search')?.addEventListener('input', event => { state.query = event.target.value.trim().toLowerCase(); renderList(); });
  ctx.$('#col-close')?.addEventListener('click', () => { state.openId = null; renderDetail(); });
}

/** Der Eintrag „Hinzufügen" der Kopfzeile - dieselbe Aktion wie auf anderen Seiten. */
export function newCollection(pageContext) {
  ctx = pageContext;
  return openEditor(null);
}

async function refresh() {
  try {
    state.rows = (await ctx.api(`api/collections${state.status ? `?status=${encodeURIComponent(state.status)}` : ''}`)) || [];
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
    state.rows = [];
  }
  renderList();
  await renderDetail();
}

function visibleRows() {
  if (!state.query) return state.rows;
  return state.rows.filter(row => `${row.name} ${row.description || ''}`.toLowerCase().includes(state.query));
}

function renderList() {
  const list = ctx.$('#collections-list');
  if (!list) return;
  const rows = visibleRows();
  if (!rows.length) { list.innerHTML = emptyRow(ctx.get('common.empty')); return; }

  const fragment = document.createDocumentFragment();
  for (const row of rows) fragment.appendChild(collectionRow(row));
  list.innerHTML = '';
  list.appendChild(fragment);
}

function periodLabel(row) {
  if (!row.startDate && !row.endDate) return t('ongoing');
  if (row.startDate && row.endDate) return `${ctx.date(row.startDate)} – ${ctx.date(row.endDate)}`;
  return row.startDate ? `${t('since')} ${ctx.date(row.startDate)}` : `${t('until')} ${ctx.date(row.endDate)}`;
}

function collectionRow(row) {
  const element = document.createElement('div');
  element.className = 'row collection-row';
  element.dataset.collectionId = row.id;
  // Ein unvollständiger Betrag sagt es hier, statt eine exakte Summe vorzutäuschen.
  const amount = row.isComplete
    ? ctx.money(row.expenses, row.currency)
    : `${ctx.money(row.expenses, row.currency)} <span class="col-incomplete">${ctx.esc(t('incomplete'))}</span>`;

  element.innerHTML = `<div class="col-row-icon" aria-hidden="true">${categoryIconInner(row.icon)}</div>
    <div class="row-main">
      <div class="row-title">${ctx.esc(row.name)} <span class="col-status is-${ctx.esc(row.status)}">${ctx.esc(t('status_' + row.status))}</span></div>
      <div class="row-sub">${ctx.esc(periodLabel(row))} · ${row.transactionCount} ${ctx.esc(ctx.get('nav.transactions'))}</div>
    </div>
    <div class="row-side"><span class="amount">${amount}</span></div>`;
  element.onclick = () => { state.openId = row.id; void renderDetail(); };
  return element;
}

async function remove(row) {
  // Was verschwindet, ist die Sammlung - nicht eine einzige Buchung. Das steht in der Frage.
  if (!await ctx.confirm(t('deleteConfirm').replace('{name}', row.name),
    { title: t('delete'), destructive: true, confirmLabel: ctx.get('common.delete') })) return;
  try {
    await ctx.api(`api/collections/${row.id}`, { method: 'DELETE' });
    if (state.openId === row.id) state.openId = null;
    ctx.toast(ctx.get('common.deleted'));
    await refresh();
  } catch (error) {
    ctx.toast(error.message || ctx.get('common.error'));
  }
}

async function openEditor(existing) {
  const iconHost = document.createElement('span');
  const handles = openFormDialog({
    title: existing ? t('edit') : t('new'),
    closeLabel: ctx.get('common.close'),
    advancedLabel: ctx.get('common.more') || 'Mehr',
    fallbackError: ctx.get('common.error'),
    create: html => ctx.dialog(html),
    fields: [
      { name: 'name', kind: FieldKind.Text, label: ctx.get('common.name'), required: true, maxLength: 100 },
      { name: 'description', kind: FieldKind.Text, label: t('description'), maxLength: 500 },
      // Ein Zeitraum ist optional: „Wohnung" läuft dauerhaft. Er hilft beim Vorschlagen, er grenzt nicht ein.
      { name: 'startDate', kind: FieldKind.Date, label: t('start'), group: 'period', hint: t('periodHint') },
      { name: 'endDate', kind: FieldKind.Date, label: t('end'), group: 'period' },
      { name: 'status', kind: FieldKind.Select, label: t('status'),
        rawOptions: STATUSES.map(status =>
          `<option value="${status}">${ctx.esc(t('status_' + status))}</option>`).join('') }
    ],
    values: {
      name: existing?.name || '',
      description: existing?.description || '',
      startDate: existing?.startDate || '',
      endDate: existing?.endDate || '',
      status: existing?.status || 'active'
    },
    actions: [
      { name: 'cancel', label: ctx.get('common.cancel'), role: 'secondary', onClick: ({ close }) => close() },
      { name: 'save', label: ctx.get(existing ? 'common.save' : 'common.create'), role: 'primary', submit: true }
    ],
    onSubmit: async ({ values, setFormError, close }) => {
      const body = {
        name: values.name,
        description: values.description || null,
        icon: selectedIconKey(iconHost),
        color: existing?.color || null,
        startDate: values.startDate || null,
        endDate: values.endDate || null,
        status: values.status
      };
      try {
        await ctx.api(existing ? `api/collections/${existing.id}` : 'api/collections',
          ctx.jsonBody(body, existing ? 'PUT' : 'POST'));
      } catch (error) {
        setFormError(error.message || ctx.get('common.error'));
        return;
      }
      close();
      await refresh();
    }
  });

  // Das Symbol ist derselbe Wähler wie bei Kategorien - eine Sammlung ist auch nur etwas mit einem Namen.
  const nameField = handles.form.querySelector('[name="name"]')?.closest('label');
  if (nameField) {
    const wrapper = document.createElement('label');
    wrapper.innerHTML = `<span>${ctx.esc(t('icon'))}</span>`;
    iconHost.append(categoryIconPicker(existing?.icon || null, { none: ctx.get('common.empty') }));
    wrapper.append(iconHost);
    nameField.insertAdjacentElement('afterend', wrapper);
  }
  return handles;
}

async function renderDetail() {
  const panel = ctx.$('#collection-detail');
  if (!panel) return;
  if (!state.openId) { panel.hidden = true; return; }

  let detail = null;
  try { detail = await ctx.api(`api/collections/${state.openId}`); }
  catch (error) { ctx.toast(error.message || ctx.get('common.error')); panel.hidden = true; return; }

  panel.hidden = false;
  const row = detail.collection;
  ctx.$('#collection-detail-title').textContent = row.name;

  const total = detail.categories.reduce((sum, share) => sum + Number(share.expenses || 0), 0);
  const split = detail.categories.length
    ? `<div class="col-split">${detail.categories.map(share => `
        <div class="col-split-row">
          <span>${ctx.esc(share.categoryName || ctx.get('common.uncategorized'))}</span>
          <span class="amount">${ctx.money(share.expenses, row.currency)}</span>
          <span class="col-split-bar"><span style="width:${total > 0 ? Math.round((share.expenses / total) * 100) : 0}%"></span></span>
        </div>`).join('')}</div>`
    : `<div class="row-sub">${ctx.esc(ctx.get('common.empty'))}</div>`;

  ctx.$('#collection-detail-body').innerHTML = `
    ${row.description ? `<p class="row-sub">${ctx.esc(row.description)}</p>` : ''}
    <div class="col-metrics">
      ${metric(t('expenses'), ctx.money(row.expenses, row.currency))}
      ${metric(t('income'), ctx.money(row.income, row.currency))}
      ${metric(t('net'), ctx.money(row.net, row.currency))}
      ${metric(ctx.get('nav.transactions'), String(row.transactionCount))}
    </div>
    ${row.isComplete ? '' : `<p class="col-incomplete">${ctx.esc(t('incompleteHint').replace('{currencies}', row.missingCurrencies.join(', ')))}</p>`}
    <h3>${ctx.esc(t('byCategory'))}</h3>
    ${split}
    <div id="collection-candidates"></div>`;

  ctx.$('#col-edit').onclick = () => openEditor(row);
  ctx.$('#col-delete').onclick = () => remove(row);
  ctx.$('#col-add-transactions').onclick = () => openCandidates(row);
  // Zwei Wege, weil es zwei verschiedene Fragen sind: "was schlaegst du vor" und "ich weiss, was ich
  // suche". Ein Dialog mit Umschalter haette beide schlechter beantwortet.
  ctx.$('#col-search-transactions').onclick = () => openTransactionSearch(row, detail.transactionIds || []);
}

function metric(label, value) {
  return `<div class="col-metric"><span class="col-metric-label">${ctx.esc(label)}</span><span class="col-metric-value">${value}</span></div>`;
}

/**
 * Buchungen suchen und zuordnen (#124).
 *
 * Die Vorschlagsliste daneben beantwortet "was koennte dazugehoeren". Diese hier beantwortet die
 * andere Frage: jemand WEISS, was er sucht - die Tankstelle auf der Rueckfahrt, alle Baumarktkaeufe
 * im Maerz - und der Vorschlag findet sie nicht, weil kein Haendler und keine Kategorie passt.
 *
 * Kein neuer Endpunkt: gesucht wird ueber /api/transactions mit seinen vorhandenen Filtern, und
 * welche Buchungen schon in dieser Sammlung stecken, steht im Sammlungsdetail (transactionIds).
 * Eine zweite Suchimplementierung neben der Buchungsseite waere eine zweite Stelle, an der ein
 * Filter anders bedeutet.
 */
async function openTransactionSearch(row, assignedIds) {
  const assigned = new Set(assignedIds);
  let accounts = [], categories = [];
  try {
    [accounts, categories] = await Promise.all([
      ctx.api('api/accounts').catch(() => []),
      ctx.api('api/categories').catch(() => [])
    ]);
  } catch { /* Die Suche geht auch ohne die beiden Auswahlfelder. */ }

  const options = (rows, label) => (rows || [])
    .map(item => `<option value="${ctx.esc(item.id)}">${ctx.esc(label(item))}</option>`).join('');

  const dlg = ctx.dialog(`<form class="dialog-card col-search-dialog" method="dialog">
    <div class="panel-head"><div><h2>${ctx.esc(t('searchTransactions'))}</h2><div class="row-sub">${ctx.esc(row.name)}</div></div><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <div class="col-search-filters">
      <label>${ctx.esc(t('searchAction'))}<input name="query" type="search" maxlength="120"></label>
      <label>${ctx.esc(t('start'))}<input name="from" type="date" value="${ctx.esc(row.startDate || '')}"></label>
      <label>${ctx.esc(t('end'))}<input name="to" type="date" value="${ctx.esc(row.endDate || '')}"></label>
      <label>${ctx.esc(ctx.get('transactions.account'))}<select name="accountId"><option value="">${ctx.esc(ctx.get('common.all'))}</option>${options(accounts, a => a.displayName || a.institutionName || '')}</select></label>
      <label>${ctx.esc(ctx.get('transactions.category'))}<select name="categoryId"><option value="">${ctx.esc(ctx.get('common.all'))}</option>${options(categories, c => c.name || '')}</select></label>
      <label>${ctx.esc(t('minAmount'))}<input name="minAmount" type="number" step="0.01"></label>
      <label>${ctx.esc(t('maxAmount'))}<input name="maxAmount" type="number" step="0.01"></label>
    </div>
    <label class="check"><input type="checkbox" name="unassignedOnly" checked><span>${ctx.esc(t('onlyUnassigned'))}</span></label>
    <div class="dialog-actions">
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button>
      <button type="submit" class="${buttonClass(ButtonRole.Primary)}">${ctx.esc(t('searchAction'))}</button>
    </div>
    <div data-results></div>
  </form>`);

  const form = dlg.querySelector('form');
  const results = dlg.querySelector('[data-results]');
  const close = () => dlg.close();
  dlg.querySelector('[data-close]').onclick = close;
  dlg.querySelector('[data-cancel]').onclick = close;

  form.onsubmit = async event => {
    event.preventDefault();
    const values = new FormData(form);
    const query = new URLSearchParams({ pageSize: '100' });
    for (const [key, param] of [['query', 'query'], ['from', 'from'], ['to', 'to'],
                                ['accountId', 'accountId'], ['categoryId', 'categoryId'],
                                ['minAmount', 'minAmount'], ['maxAmount', 'maxAmount']]) {
      const value = String(values.get(key) || '').trim();
      if (value) query.set(param, value);
    }

    let found;
    try { found = await ctx.api(`api/transactions?${query}`); }
    catch (error) { ctx.toast(error.message || ctx.get('common.error')); return; }

    const unassignedOnly = values.get('unassignedOnly') === 'on';
    const items = (found?.items || [])
      .filter(item => !unassignedOnly || !assigned.has(item.id))
      .map(item => ({
        id: ctx.esc(item.id),
        // Schon zugeordnete werden nicht versteckt, sondern gekennzeichnet: sie wegzulassen liesse
        // den Benutzer raten, ob die Suche sie nicht gefunden hat oder sie schon drin sind.
        rowClass: assigned.has(item.id) ? 'col-search-assigned' : '',
        html: `<div class="row-main">
            <div class="row-title">${ctx.esc(item.merchantDisplayName || item.counterparty || ctx.get('common.empty'))}</div>
            <div class="row-sub">${item.bookingDate ? ctx.date(item.bookingDate) : ''}${assigned.has(item.id) ? ` · ${ctx.esc(t('alreadyAssigned'))}` : ''}</div>
          </div>
          <span class="amount">${ctx.money(item.amount, item.currency)}</span>`
      }));

    if (!items.length) {
      results.innerHTML = `<div class="row-sub">${ctx.esc(t('noCandidates'))}</div>`;
      return;
    }

    const list = createSelectionList();
    results.innerHTML = selectionListHtml(items, { rowClass: 'row check-row', selectAllLabel: ctx.esc(t('candidateSelectAll')) })
      + `<div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Primary)}" data-assign>${ctx.esc(t('addTransactions'))}</button></div>`;
    list.mount(results, { counterFormat: (n, total) => t('candidateSelectedCount').replace('{n}', String(n)).replace('{total}', String(total)) });

    results.querySelector('[data-assign]').onclick = async event2 => {
      const chosen = list.getSelectedIds();
      if (!chosen.length) return;
      event2.currentTarget.disabled = true;
      try {
        await ctx.api(`api/collections/${row.id}/transactions`, ctx.jsonBody({ transactionIds: chosen }));
        close();
        await keepListPosition(() => refresh());
      } catch (error) {
        ctx.toast(error.message || ctx.get('common.error'));
        event2.currentTarget.disabled = false;
      }
    };
  };
  dlg.showModal();
}

/**
 * Buchungen hinzufügen: der Server schlägt vor, der Mensch entscheidet.
 *
 * Jeder Vorschlag trägt seinen Grund — Händler, Kategorie, Zeitraum. Das ist nicht Zierde: der
 * Zeitraum allein bedeutet ausdrücklich nicht, dass eine Buchung dazugehört (während einer Reise
 * läuft die Miete weiter), und nur mit dem Grund daneben kann das jemand beurteilen.
 */
async function openCandidates(row) {
  let candidates = [];
  try { candidates = (await ctx.api(`api/collections/${row.id}/candidates`)) || []; }
  catch (error) { ctx.toast(error.message || ctx.get('common.error')); return; }

  if (!candidates.length) { ctx.toast(t('noCandidates')); return; }

  // Vorher gab es hier weder "Alle auswaehlen" noch einen Zaehler - bei mehr als ein paar Kandidaten
  // musste man jede Zeile einzeln pruefen, um zu sehen, wie viele schon angehakt sind. Derselbe
  // Baustein wie bei der ING-Kontoauswahl schliesst diese Luecke, statt sie eigens nachzubauen.
  const items = candidates.map(candidate => ({
    id: ctx.esc(candidate.transactionId),
    selected: true,
    html: `<div class="row-main">
        <div class="row-title">${ctx.esc(candidate.counterparty || ctx.get('common.empty'))}</div>
        <div class="row-sub">${candidate.date ? ctx.date(candidate.date) : ''}${candidate.categoryName ? ` · ${ctx.esc(candidate.categoryName)}` : ''}${candidate.accountName ? ` · ${ctx.esc(candidate.accountName)}` : ''}</div>
        <div class="col-candidate-reasons">${candidate.reasons.map(reason => `<span class="col-reason">${ctx.esc(t('reason_' + reason))}</span>`).join('')}</div>
      </div>
      <span class="amount">${ctx.money(candidate.amount, candidate.currency)}</span>`
  }));
  const list = createSelectionList();

  const dialog = ctx.dialog(`<div class="dialog-card">
    <div class="panel-head"><h2>${ctx.esc(t('addTransactions'))}</h2><button type="button" data-close aria-label="${ctx.esc(ctx.get('common.close'))}">×</button></div>
    <p class="row-sub">${ctx.esc(t('candidateHint').replace('{count}', String(candidates.length)))}</p>
    ${selectionListHtml(items, { rowClass: 'row check-row', selectAllLabel: ctx.esc(t('candidateSelectAll')) })}
    <div class="dialog-actions">
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${ctx.esc(ctx.get('common.cancel'))}</button>
      <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-apply>${ctx.esc(ctx.get('common.apply'))}</button>
    </div>
  </div>`);
  list.mount(dialog, { counterFormat: (n, total) => t('candidateSelectedCount').replace('{n}', String(n)).replace('{total}', String(total)) });

  const close = () => dialog.close();
  dialog.querySelector('[data-close]').onclick = close;
  dialog.querySelector('[data-cancel]').onclick = close;
  dialog.querySelector('[data-apply]').onclick = async event => {
    const chosen = list.getSelectedIds();
    if (!chosen.length) { close(); return; }
    event.currentTarget.disabled = true;
    try {
      // Eine Anweisung für alle Ausgewählten - nicht eine Runde je Buchung.
      await ctx.api(`api/collections/${row.id}/transactions`, ctx.jsonBody({ transactionIds: chosen }));
      close();
      await keepListPosition(() => refresh());
    } catch (error) {
      ctx.toast(error.message || ctx.get('common.error'));
      event.currentTarget.disabled = false;
    }
  };
  dialog.showModal();
}
