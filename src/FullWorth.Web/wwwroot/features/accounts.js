import { money, converted, maskIdentifier } from '../ui/money.js';
import { state } from '../core/state.js';
import {
  bindAccountsPresentation,
  enhanceAccountsPresentation,
  toggleAccountGroupEditing,
  decorateManualAccountDialog,
  applyManualAccountVisual,
  editAccountVisualById
} from './accounts-presentation.js';

let ctx = null;
let bound = false;

function use(context) {
  if (context) ctx = context;
  if (!ctx) throw new Error('Accounts feature context is not initialized.');
  return ctx;
}

const $ = selector => document.querySelector(selector);
const api = (path, options) => ctx.api(path, options);
const bankApi = (path, options) => ctx.bankApi(path, options);
const get = key => ctx.get(key);
const esc = value => ctx.esc(value);
const date = value => ctx.date(value);
const dateTime = value => ctx.dateTime(value);
const toast = (...args) => ctx.toast(...args);
const jsonBody = (...args) => ctx.jsonBody(...args);
const dialog = (html, options = {}) => ctx.dialog(html, options);
const empty = (el, message) => ctx.empty(el, message);
const acctId = last4 => last4 ? ` · ${maskIdentifier(last4)}` : '';

const ACCT_TRASH='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m2 0v12a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V7"/></svg>';
const ACCT_EDIT='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20h4L18 10l-4-4L4 16v4Z"/><path d="M13.5 6.5 17.5 10.5"/></svg>';
const ACCT_FOLDER='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 6h6l2 2h8v10H4Z"/></svg>';

function openAccountActionsDialog(account, groups) {
  const isManual = account.provider === 'manual' && !account.bankConnectionId;
  const actions = [
    ['coach', get('accounts.askCoach'), false],
    ['visual', get('accounts.editVisual'), false],
    ...(groups || []).length ? [['move', get('accounts.moveToGroup'), false]] : [],
    ['rename', get('accounts.rename'), false],
    ...(isManual ? [['balance', get('accounts.updateBalance'), false], ['delete', get('accounts.delete'), true]] : [])
  ];

  const dlg = dialog(`<div class="dialog-card more-sheet account-actions-sheet">
    <div class="panel-head"><div><h2>${esc(account.displayName || account.institutionName)}</h2><div class="row-sub">${esc(account.institutionName || '')}</div></div><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div>
    <div class="more-list">${actions.map(([key, label, danger]) => `<button type="button" data-account-action="${key}" class="${danger ? 'danger' : ''}"><span>${esc(label)}</span></button>`).join('')}</div>
  </div>`, { mobileMode: 'sheet' });

  dlg.querySelector('[data-close]')?.addEventListener('click', () => dlg.close());
  dlg.querySelectorAll('[data-account-action]').forEach(button => button.addEventListener('click', () => {
    const action = button.dataset.accountAction;
    dlg.close();

    if (action === 'coach') {
      window.dispatchEvent(new CustomEvent('fullworth:coach-open', { detail: {
        entityType: 'account',
        entityId: account.id,
        entityLabel: account.displayName || account.institutionName || get('accounts.title'),
        details: {
          balance: String(account.latestBalance?.amount ?? ''),
          currency: account.latestBalance?.currency || account.currency || '',
          kind: account.accountType || account.product || ''
        }
      }}));
    } else if (action === 'visual') {
      editAccountVisualById(account.id).catch(console.error);
    } else if (action === 'move') {
      openMoveToGroupDialog(account, groups);
    } else if (action === 'rename') {
      openAccountNameDialog(account);
    } else if (action === 'balance') {
      openBalanceDialog(account);
    } else if (action === 'delete') {
      deleteAccount(account);
    }
  }));
  dlg.showModal();
}
function accountRow(x,groups){
  const isManual=x.provider==='manual'&&!x.bankConnectionId;
  const kind=[x.product||x.accountType,isManual?get('accounts.manual'):null].filter(Boolean).join(' · ');
  const nativeAmt=x.latestBalance?money(x.latestBalance.amount,x.latestBalance.currency):'—';
  const convertedAmt=x.baseValue!=null?`<div class="amount-converted">${converted(x.baseValue,x.baseCurrency)}</div>`:'';
  // A wallet-per-currency account (PayPal, Wise, Revolut) holds money in more than one currency. The
  // headline shows one of them, so the others are listed here - they used to be invisible entirely.
  const otherWallets=(x.balances||[]).slice(1);
  const walletsLine=otherWallets.length
    ? `<div class="amount-wallets">${otherWallets.map(b=>esc(money(b.amount,b.currency))).join(' · ')}</div>`
    : '';
  // The same bank account reached through a second provider stays visible - it brings data the other
  // connection does not - but it is out of the totals, and the row has to say why.
  const duplicateNote=x.duplicateOfDisplayName
    ? ` · ${esc(get('accounts.duplicateOf').replace('{name}',x.duplicateOfDisplayName))}`
    : '';
  const dataAsOf=x.latestBalance?.capturedAt
    ? ` · ${esc(get('accounts.dataAsOf'))}: ${esc(dateTime(x.latestBalance.capturedAt))}`
    : '';
  const row=document.createElement('div');row.className='row';
  // The "move to group" affordance only appears once at least one group exists (otherwise the dialog
  // would be a dead end offering only "Ungrouped").
  const moveBtn=(groups||[]).length?`<button type="button" class="icon-button" data-move title="${esc(get('accounts.moveToGroup'))}" aria-label="${esc(get('accounts.moveToGroup'))}">${ACCT_FOLDER}</button>`:'';
  const renameBtn=`<button type="button" class="icon-button" data-rename-account title="${esc(get('common.edit'))}: ${esc(get('accounts.name'))}" aria-label="${esc(get('common.edit'))}: ${esc(get('accounts.name'))}">${ACCT_EDIT}</button>`;
  const balanceBtn=isManual?`<button type="button" class="icon-button" data-edit-balance title="${esc(get('accounts.updateBalance'))}" aria-label="${esc(get('accounts.updateBalance'))}">±</button>`:'';
  const deleteBtn=isManual?`<button type="button" class="icon-button" data-delete title="${esc(get('accounts.delete'))}" aria-label="${esc(get('accounts.delete'))}">${ACCT_TRASH}</button>`:'';
  const moreBtn=`<button type="button" class="icon-button account-more" data-account-more title="${esc(get('accounts.moreActions'))}" aria-label="${esc(get('accounts.moreActions'))}">⋯</button>`;
  row.innerHTML=`<div class="row-main"><div class="row-title">${esc(x.displayName||x.institutionName)}</div><div class="row-sub">${esc(x.institutionName)}${kind?` · ${esc(kind)}`:''}${acctId(x.ibanLast4)}${dataAsOf}${duplicateNote}</div></div><div class="row-end"><div class="amount-stack"><div class="amount">${nativeAmt}</div>${walletsLine}${convertedAmt}</div>${moveBtn}${renameBtn}${balanceBtn}${deleteBtn}${moreBtn}</div>`;
  row.querySelector('[data-account-more]')?.addEventListener('click',()=>openAccountActionsDialog(x,groups));
  row.querySelector('[data-move]')?.addEventListener('click',()=>openMoveToGroupDialog(x,groups));
  row.querySelector('[data-rename-account]')?.addEventListener('click',()=>openAccountNameDialog(x));
  row.querySelector('[data-edit-balance]')?.addEventListener('click',()=>openBalanceDialog(x));
  row.querySelector('[data-delete]')?.addEventListener('click',()=>deleteAccount(x));
  // Drill-down (UX rework §3): the account row itself opens that account's bookings; management
  // controls (edit/move/delete/balance) keep their own click and are excluded here.
  row.dataset.accountId=x.id;row.classList.add('is-drillable');row.setAttribute('role','button');row.tabIndex=0;
  const drill=e=>{if(e.target.closest('button,a,input,select'))return;ctx.showView('transactions',{query:'accountId='+encodeURIComponent(x.id)})};
  row.addEventListener('click',drill);
  row.addEventListener('keydown',e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();drill(e)}});
  return row;
}
async function loadAccountsView(){
  const [accounts,connections,groups]=await Promise.all([api('api/accounts'),api('api/bank-connections'),api('api/account-groups').catch(()=>[])]);
  const list=$('#accounts-view-list');list.innerHTML='';
  // Archived accounts (IsActive=false, e.g. a deleted manual account) are hidden from the list.
  const visibleAccounts=(accounts||[]).filter(a=>a.isActive!==false);
  const groupList=(groups||[]).slice().sort((a,b)=>(a.sortOrder-b.sortOrder)||a.name.localeCompare(b.name));
  const baseCur=state.space?.baseCurrency||'EUR';
  const collapsed=new Set(JSON.parse(localStorage.getItem('finance.groupsCollapsed')||'[]'));
  const byGroup=new Map();
  for(const a of visibleAccounts){const k=a.groupId||'';if(!byGroup.has(k))byGroup.set(k,[]);byGroup.get(k).push(a);}
  // Group subtotal in the base currency: the converted baseValue for foreign accounts, the native
  // amount for base-currency accounts. An account whose money could NOT be converted is left out -
  // adding a foreign figure into a base-currency total would be arithmetic across units - but leaving
  // it out silently printed a confident number that was missing real money, so the subtotal now says
  // so. (An account with no balance at all is not incomplete; it simply has no value yet.)
  const total=accts=>{
    let sum=0,incomplete=false;
    for(const a of accts){
      if(a.baseValue!=null){sum+=Number(a.baseValue);continue}
      if(a.latestBalance&&a.latestBalance.currency===baseCur){sum+=Number(a.latestBalance.amount);continue}
      if(a.latestBalance)incomplete=true;
    }
    return{sum,incomplete};
  };
  const totalMarkup=accts=>{
    const t=total(accts);
    const mark=t.incomplete?`<span class="amount-incomplete" title="${esc(get('common.fxIncomplete'))}" aria-label="${esc(get('common.fxIncomplete'))}">*</span>`:'';
    return `${money(t.sum,baseCur)}${mark}`;
  };
  // Group header (g=null → the "Ungrouped" bucket). Collapse state persists in localStorage.
  const renderBucket=(g,accts)=>{
    const gid=g?g.id:'';const isCollapsed=collapsed.has(gid);
    const head=document.createElement('div');head.className='row group-head';head.dataset.groupId=gid;
    // The chevron only expands/collapses; the name is a separate drill-down that opens all bookings of
    // the group's accounts (UX rework §3). The name keeps class `group-toggle` for accounts-ux decoration.
    const toggle=()=>{collapsed.has(gid)?collapsed.delete(gid):collapsed.add(gid);localStorage.setItem('finance.groupsCollapsed',JSON.stringify([...collapsed]));loadAccountsView();};
    head.innerHTML=`<div class="row-main"><button type="button" class="group-chevron" data-toggle aria-label="${esc(get(isCollapsed?'nav.expand':'nav.collapse'))}">${isCollapsed?'▸':'▾'}</button><button type="button" class="group-toggle${g?' is-drillable':''}" data-group-open>${esc(g?g.name:get('accounts.ungrouped'))}</button></div><div class="row-side"><span class="amount">${totalMarkup(accts)}</span>${g?`<button type="button" class="icon-button" data-rename aria-label="${esc(get('accounts.renameGroup'))}" title="${esc(get('accounts.renameGroup'))}">${ACCT_EDIT}</button><button type="button" class="icon-button" data-delgroup aria-label="${esc(get('accounts.deleteGroup'))}" title="${esc(get('accounts.deleteGroup'))}">${ACCT_TRASH}</button>`:''}</div>`;
    head.querySelector('[data-toggle]').addEventListener('click',toggle);
    head.querySelector('[data-group-open]').addEventListener('click',()=>{if(g)ctx.showView('transactions',{query:'groupId='+encodeURIComponent(g.id)});else toggle();});
    head.querySelector('[data-rename]')?.addEventListener('click',()=>openGroupDialog(g));
    head.querySelector('[data-delgroup]')?.addEventListener('click',()=>deleteGroup(g));
    list.appendChild(head);
    if(!isCollapsed)for(const a of accts)list.appendChild(accountRow(a,groupList));
  };
  if(!groupList.length){
    // No groups defined: keep the flat list (unchanged for users who don't use groups).
    for(const x of visibleAccounts)list.appendChild(accountRow(x,groupList));
  }else{
    for(const g of groupList)renderBucket(g,byGroup.get(g.id)||[]);
    const ungrouped=byGroup.get('')||[];
    if(ungrouped.length)renderBucket(null,ungrouped);
  }
  if(!visibleAccounts.length)empty(list);
  const conns=$('#connections-list');conns.innerHTML='';
  for(const x of connections||[]){
    const health=x.healthStatus||'authorized';
    const label=get(`accounts.health_${health}`);
    const warn=['reauthorization_required','expired','revoked','closed','error','partial_history'].includes(health);
    // A parked TAN is not a broken connection: Reconnect would start a fresh authorization and throw
    // the pending challenge away. The only action that helps is answering the TAN.
    const needsTan=health==='tan_required';
    const expiry=Number.isFinite(x.daysUntilExpiry)&&x.daysUntilExpiry>=0&&health!=='expired'?` · ${get('accounts.expiresIn').replace('{days}',x.daysUntilExpiry)}`:'';
    const nextSync=x.nextSyncAllowedAt?` · ${get('accounts.nextSyncAllowed')}: ${dateTime(x.nextSyncAllowedAt)}`:'';
    const row=document.createElement('div');row.className='row';row.dataset.connectionId=x.id;
    row.innerHTML=`<div class="row-main"><div class="row-title">${esc(x.institutionName)}</div><div class="row-sub">${esc(get('accounts.validUntil'))}: ${dateTime(x.validUntil)} · ${esc(get('accounts.lastSync'))}: ${dateTime(x.lastSyncedAt)}${esc(expiry)}${esc(nextSync)}</div></div><div class="row-side"><div class="amount${warn?' negative':''}">${esc(label)}</div><button type="button" class="ghost" data-sync-history>${esc(get('accounts.syncHistory'))}</button>${needsTan?`<button type="button" class="ghost" data-enter-tan>${esc(get('accounts.enterTan'))}</button>`:warn?`<button type="button" class="ghost" data-reconnect>${esc(get('accounts.reconnect'))}</button>`:`<button type="button" class="icon-button" data-sync title="${esc(get('accounts.syncNow'))}" aria-label="${esc(get('accounts.syncNow'))}">⟳</button>`}<button type="button" class="ghost danger" data-disconnect>${esc(get('accounts.disconnect'))}</button></div>`;
    row.querySelector('[data-sync-history]')?.addEventListener('click',()=>openSyncHistory(x));
    row.querySelector('[data-sync]')?.addEventListener('click',ev=>syncConnection(x.id,ev.currentTarget));
    row.querySelector('[data-reconnect]')?.addEventListener('click',ev=>reconnectConnection(x,ev.currentTarget));
    row.querySelector('[data-enter-tan]')?.addEventListener('click',ev=>openPendingTanDialog(x,ev.currentTarget));
    row.querySelector('[data-disconnect]').addEventListener('click',ev=>disconnectConnection(x,ev.currentTarget));
    conns.appendChild(row);
  }
  if(!(connections||[]).length)empty(conns);
  await enhanceAccountsPresentation();
}
async function openSyncHistory(connection){
  let history;
  try{
    history=await api(`api/bank-connections/${connection.id}/sync-history?limit=10`);
  }catch(err){toast(err.message||get('common.error'));return}
  const locale=state.lang==='de'?'de-DE':'en-US';
  const rows=(history||[]).map(item=>{
    const resultKey={success:'accounts.syncResultSuccess',partial:'accounts.syncResultPartial',error:'accounts.syncResultError'}[item.result]||'accounts.syncResultError';
    const seconds=Math.max(0,Number(item.durationMs)||0)/1000;
    const duration=new Intl.NumberFormat(locale,{maximumFractionDigits:2}).format(seconds)+' s';
    const error=item.errorCode?` · ${esc(item.errorCode)}`:'';
    return `<div class="row"><div class="row-main"><div class="row-title">${esc(get(resultKey))}${error}</div><div class="row-sub">${esc(get('accounts.syncStartedAt'))}: ${esc(dateTime(item.startedAt))} · ${esc(get('accounts.syncFinishedAt'))}: ${esc(dateTime(item.completedAt))}</div></div><div class="row-side"><span class="row-sub">${esc(get('accounts.syncDuration'))}: ${esc(duration)}</span></div></div>`;
  }).join('');
  const dlg=dialog(`<div class="dialog-card"><div class="panel-head"><div><h2>${esc(get('accounts.syncHistory'))}</h2><div class="row-sub">${esc(connection.institutionName||'')}</div></div><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><div class="rows">${rows||`<div class="row state-empty"><div class="row-sub">${esc(get('accounts.syncHistoryEmpty'))}</div></div>`}</div></div>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.showModal();
}
async function syncConnection(id,button){
  if(button)button.disabled=true;
  try{
    const r=await bankApi(`api/banking/connections/${id}/sync?force=true`,{method:'POST'});
    const status=(r&&r.status)||'started';
    const messages={started:'accounts.syncStarted',completed:'accounts.syncCompleted',partial_history:'accounts.syncPartial',error:'accounts.syncError',already_running:'accounts.syncRunning',cooldown:'accounts.syncCooldown',reauthorization_required:'accounts.syncReauth',tan_required:'accounts.syncTanRequired'};
    const detail=status==='cooldown'&&r?.nextSyncAllowedAt?` · ${get('accounts.nextSyncAllowed')}: ${dateTime(r.nextSyncAllowedAt)}`:'';
    toast(`${get(messages[status]||'accounts.syncStarted')}${detail}`);
    // A TAN is waiting: open it straight away instead of leaving the user with a toast and no way in.
    if(status==='tan_required'){await openPendingTanDialog({id},null);return}
    await ctx.reload();
  }catch(err){toast(err.message||get('common.error'));if(button)button.disabled=false}
}
function openAccountNameDialog(account){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('common.edit'))}: ${esc(get('accounts.name'))}</h2><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><label>${esc(get('accounts.name'))}<input name="name" required maxlength="120" value="${esc(account.displayName||account.institutionName||'')}"></label><div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('common.save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const displayName=String(new FormData(e.currentTarget).get('name')||'').trim();
    try{
      await api(`api/accounts/${account.id}`,{...jsonBody({displayName,isActive:null,includeInNetWorth:null,sortOrder:null}),method:'PATCH'});
      dlg.close();toast(get('common.saved'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}
// Delete (archive) a manual account; it then disappears from the list (archived accounts are hidden).
async function deleteAccount(account){
  const name=account.displayName||account.institutionName;
  if(!await ctx.confirm(get('accounts.deleteConfirm').replace('{name}',()=>name),{destructive:true,confirmLabel:get('accounts.delete')}))return;
  try{await api(`api/accounts/${account.id}`,{method:'DELETE'});toast(get('accounts.deleted'));await loadAccountsView()}
  catch(err){toast(err.message||get('common.error'))}
}
// Create or rename an account group (§8.1).
async function openGroupDialog(existing){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get(existing?'accounts.renameGroup':'accounts.newGroup'))}</h2><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><label>${esc(get('accounts.groupName'))}<input name="name" required maxlength="120" value="${esc(existing?.name||'')}"></label><div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get(existing?'common.save':'common.create'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const name=new FormData(e.currentTarget).get('name');
    try{
      if(existing)await api(`api/account-groups/${existing.id}`,{...jsonBody({name,sortOrder:existing.sortOrder}),method:'PUT'});
      else await api('api/account-groups',jsonBody({name,sortOrder:null}));
      dlg.close();toast(get('common.saved'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}
async function deleteGroup(g){
  if(!await ctx.confirm(get('accounts.deleteGroupConfirm').replace(/\{name\}/g,()=>g.name),{destructive:true,confirmLabel:get('accounts.deleteGroup')}))return;
  try{await api(`api/account-groups/${g.id}`,{method:'DELETE'});toast(get('accounts.groupDeleted'));await loadAccountsView()}
  catch(err){toast(err.message||get('common.error'))}
}
// Move an account into a group (or "Ungrouped" = clear). Owner-gated server-side.
function openMoveToGroupDialog(account,groups){
  const opts=[`<option value="">${esc(get('accounts.ungrouped'))}</option>`].concat((groups||[]).map(g=>`<option value="${g.id}"${account.groupId===g.id?' selected':''}>${esc(g.name)}</option>`)).join('');
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('accounts.moveToGroup'))}</h2><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><label>${esc(get('accounts.groups'))}<select name="group">${opts}</select></label><div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('common.save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const groupId=new FormData(e.currentTarget).get('group')||null;
    try{await api(`api/accounts/${account.id}/group`,{...jsonBody({groupId}),method:'PUT'});dlg.close();await loadAccountsView()}
    catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}
// Disconnect a bank: permanently deletes the connection and all of its synced accounts + data.
async function disconnectConnection(connection,button){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('accounts.disconnect'))}: ${esc(connection.institutionName)}</h2><button type="button" data-close>×</button></div>
    <p class="row-sub">${esc(get('accounts.disconnectProviderHint'))}</p>
    <label class="check"><input type="radio" name="policy" value="keep" checked> <span>${esc(get('accounts.disconnectKeep'))}</span></label>
    <label class="check"><input type="radio" name="policy" value="delete"> <span>${esc(get('accounts.disconnectDelete'))}</span></label>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="danger">${esc(get('accounts.disconnect'))}</button></div></form>`);
  const form=dlg.querySelector('form');
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  form.onsubmit=async e=>{
    e.preventDefault();
    const deleteLocalData=new FormData(form).get('policy')==='delete';
    if(deleteLocalData&&!await ctx.confirm(get('accounts.disconnectConfirm').replace('{name}',()=>connection.institutionName),{destructive:true,confirmLabel:get('accounts.disconnect')}))return;
    if(button)button.disabled=true;form.querySelector('[type="submit"]').disabled=true;
    try{
      await bankApi(`api/banking/connections/${connection.id}?deleteLocalData=${deleteLocalData}`,{method:'DELETE'});
      dlg.close();toast(get(deleteLocalData?'accounts.disconnected':'accounts.disconnectedKept'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'));if(button)button.disabled=false;form.querySelector('[type="submit"]').disabled=false}
  };
  dlg.showModal();
}
// §17: re-authorizes an expired/errored connection IN PLACE (reconnectConnectionId) instead of
// creating a duplicate connection for the same institution.
async function reconnectConnection(connection,button){
  // FinTS connections are re-authorized with their own login, not through Enable Banking. Without
  // this check the Enable Banking flow ran with the FinTS connection id, which rewrote the
  // connection provider - or, with no Enable Banking profile, opened its setup wizard for a bank
  // that never needed one.
  if(String(connection?.provider||'').toLowerCase()==='fints'){
    if(button)button.disabled=false;
    return openIngConnectionOptions(connection);
  }
  if(button)button.disabled=true;
  try{
    const status=await bankApi('api/banking/status');
    if(!bankingReady(status)){if(button)button.disabled=false;return openEnableBankingWizard(status)}
    const country=(connection.country||'DE').toUpperCase();
    const data=await bankApi(`api/banking/institutions?country=${encodeURIComponent(country)}&psuType=${encodeURIComponent(connection.psuType||'personal')}`);
    const bank=(data.aspsps||[]).find(x=>(x.name||'').toLowerCase()===(connection.institutionName||'').toLowerCase());
    if(button)button.disabled=false;
    if(bank)return openBankConnectionOptions(bank,connection.id,status.profile?.id||null,connection.psuType||'personal');
    // ASPSP names can change. Fall back to the fresh provider list and let the user select the
    // renamed successor while keeping the existing FullWorth connection id.
    toast(get('bankingSetup.bankRenamedHint'));
    return openBankDialog(connection,country);
  }catch(err){toast(err.message||get('common.error'));if(button)button.disabled=false}
}
function openAddAccountDialog(){
  const dlg=dialog(`<form method="dialog" class="dialog-card"><div class="panel-head"><h2>${esc(get('accounts.add'))}</h2><button value="cancel" data-close>×</button></div><div class="choice-grid"><button type="button" data-choice="bank"><strong>${esc(get('accounts.addBank'))}</strong><span>${esc(get('accounts.addBankHint'))}</span></button><button type="button" data-choice="manual"><strong>${esc(get('accounts.addManual'))}</strong><span>${esc(get('accounts.addManualHint'))}</span></button></div></form>`);
  dlg.querySelector('[data-choice="bank"]').addEventListener('click',async()=>{dlg.close();await openBankDialog()});
  dlg.querySelector('[data-choice="manual"]').addEventListener('click',()=>{dlg.close();openManualAccountDialog()});
  dlg.showModal();
}
function openManualAccountDialog(){
  const currency=state.space?.baseCurrency||'EUR';
  const dlg=dialog(`<form class="dialog-card"><h2>${esc(get('accounts.addManual'))}</h2><label>${esc(get('accounts.name'))}<input name="name" required maxlength="120" placeholder="${esc(get('accounts.namePlaceholder'))}"></label><label>${esc(get('accounts.institution'))}<input name="institution" maxlength="120" placeholder="${esc(get('accounts.institutionPlaceholder'))}"></label><label>${esc(get('purchases.currency'))}<input name="currency" value="${esc(currency)}" maxlength="3" required></label><label>${esc(get('accounts.startBalance'))}<input name="balance" type="number" step="0.01" inputmode="decimal" placeholder="0,00"></label><div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('common.create'))}</button></div></form>`);
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const fd=new FormData(e.currentTarget);
    if(!state.space){toast(get('common.error'));return}
    try{
      const created=await api('api/accounts',jsonBody({fullWorthSpaceId:state.space.id,bankConnectionId:null,displayName:fd.get('name'),currency:fd.get('currency'),includeInNetWorth:true,sortOrder:0,institutionName:fd.get('institution')||null,initialBalance:fd.get('balance')===''?null:Number(fd.get('balance'))}));
      await applyManualAccountVisual(created);
      dlg.close();toast(get('accounts.created'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
  decorateManualAccountDialog(dlg);
}
function openBalanceDialog(account){
  const current=account.latestBalance?account.latestBalance.amount:'';
  const dlg=dialog(`<form class="dialog-card"><h2>${esc(get('accounts.updateBalance'))}</h2><div class="row-sub">${esc(account.displayName||account.institutionName)}</div><label>${esc(get('accounts.newBalance'))} (${esc(account.currency)})<input name="amount" type="number" step="0.01" inputmode="decimal" value="${current}" required></label><div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('common.apply'))}</button></div></form>`);
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const fd=new FormData(e.currentTarget);
    try{await api(`api/accounts/${account.id}/balance`,{...jsonBody({amount:Number(fd.get('amount')),currency:null}),method:'PUT'});dlg.close();toast(get('accounts.balanceUpdated'));await loadAccountsView()}catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}

// Create OR edit a budget: pass the existing budget object to pre-fill + switch to PUT, with a delete
// action. Called with no argument for the "+ new budget" flow.
const ENABLE_BANKING_SIGN_IN='https://enablebanking.com/sign-in/';
const ENABLE_BANKING_APPS='https://enablebanking.com/cp/applications';
const ENABLE_BANKING_LINKED='https://enablebanking.com/docs/api/linked-accounts';
const ENABLE_BANKING_STATUS='https://enablebanking.com/cp/aspsps';

function bankingReady(status){
  if(status?.profile)return status.profile.environment==='SANDBOX'||status.profile.active===true;
  return false;
}

async function renderEnableBankingSettings(){
  const row=$('#enable-banking-settings'),sub=$('#enable-banking-status');
  if(!row||!sub)return;
  sub.textContent=get('bankingSetup.loading');
  try{
    const status=await bankApi('api/banking/status');
    if(status.profile){
      sub.textContent=status.profile.active||status.profile.environment==='SANDBOX'
        ?get('bankingSetup.ready').replace('{name}',status.profile.applicationName||status.profile.applicationId)
        :get('bankingSetup.activationRequired');
    }else if(status.legacyConfigured)sub.textContent=get('bankingSetup.legacy');
    else sub.textContent=get('bankingSetup.notConfigured');
    row.onclick=()=>openEnableBankingWizard(status);
  }catch(err){
    sub.textContent=err.message||get('common.error');
    row.onclick=()=>openEnableBankingWizard(null);
  }
}

function openEnableBankingWizard(initialStatus,options={}){
  let status=initialStatus;
  let autoPoll=null;
  let autoRegistrationId=null;
  const dlg=dialog('<div class="dialog-card banking-setup"><div class="panel-head"><h2></h2><button type="button" data-close aria-label="Close">×</button></div><div data-step></div></div>');
  const step=dlg.querySelector('[data-step]');
  dlg.querySelector('h2').textContent=get('bankingSetup.title');
  const stopAutoPoll=()=>{if(autoPoll){clearTimeout(autoPoll);autoPoll=null}};
  const cancelAutoRegistration=()=>{
    stopAutoPoll();
    const id=autoRegistrationId;
    autoRegistrationId=null;
    if(id)bankApi(`api/banking/profile/register/${encodeURIComponent(id)}`,{method:'DELETE'}).catch(()=>{});
  };
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.addEventListener('close',()=>{cancelAutoRegistration();options.onClose?.()},{once:true});

  const showIntro=()=>{
    stopAutoPoll();
    step.innerHTML=`<p>${esc(get('bankingSetup.privateIntro'))}</p>
      <p class="row-sub">${esc(get('bankingSetup.privateBoundary'))}</p>
      <label class="check"><input type="checkbox" data-ack> ${esc(get('bankingSetup.ack'))}</label>
      <div class="dialog-actions"><button type="button" data-next disabled>${esc(get('auth.continue'))}</button></div>`;
    const ack=step.querySelector('[data-ack]'),next=step.querySelector('[data-next]');
    ack.onchange=()=>next.disabled=!ack.checked;
    next.onclick=showSetupChoice;
  };

  const showSetupChoice=()=>{
    stopAutoPoll();
    step.innerHTML=`<p class="row-sub">${esc(get('bankingSetup.setupChoiceHint'))}</p>
      <div class="setup-choice-grid">
        <button type="button" class="setup-choice" data-auto><strong>${esc(get('bankingSetup.automaticTitle'))}</strong><span>${esc(get('bankingSetup.automaticHint'))}</span></button>
        <button type="button" class="setup-choice" data-manual><strong>${esc(get('bankingSetup.manualTitle'))}</strong><span>${esc(get('bankingSetup.manualHint'))}</span></button>
      </div>
      <div class="dialog-actions"><button type="button" data-back>${esc(get('onboarding.back'))}</button></div>`;
    step.querySelector('[data-auto]').onclick=showAutomatic;
    step.querySelector('[data-manual]').onclick=showCredentials;
    step.querySelector('[data-back]').onclick=showIntro;
  };

  const showAutomatic=()=>{
    stopAutoPoll();
    const callback=status?.callbackUrl||'';
    step.innerHTML=`<form data-auto-form>
      <p class="row-sub">${esc(get('bankingSetup.automaticExplain'))}</p>
      <label>${esc(get('bankingSetup.email'))}<input name="email" type="email" autocomplete="email" required maxlength="254"></label>
      <label>${esc(get('bankingSetup.environment'))}<select name="environment"><option value="PRODUCTION">${esc(get('bankingSetup.environmentProduction'))}</option><option value="SANDBOX">${esc(get('bankingSetup.environmentSandbox'))}</option></select></label>
      <label>${esc(get('bankingSetup.callback'))}<input readonly value="${esc(callback)}"></label>
      <p class="row-sub">${esc(get('bankingSetup.automaticSecurity'))}</p>
      <div class="dialog-actions"><button type="button" data-back>${esc(get('onboarding.back'))}</button><button type="submit">${esc(get('bankingSetup.sendLogin'))}</button></div>
    </form>`;
    const form=step.querySelector('form');
    step.querySelector('[data-back]').onclick=showSetupChoice;
    form.onsubmit=async e=>{
      e.preventDefault();
      const button=form.querySelector('[type="submit"]');
      const fd=new FormData(form);
      button.disabled=true;
      try{
        const started=await bankApi('api/banking/profile/register/start',jsonBody({
          email:String(fd.get('email')||'').trim(),
          environment:String(fd.get('environment')||'PRODUCTION')
        }));
        autoRegistrationId=started.id;
        showAutomaticWaiting(started);
      }catch(err){
        toast(err.message||get('common.error'));
        button.disabled=false;
      }
    };
  };

  const automaticStatusText=value=>{
    if(value==='waiting_for_email')return get('bankingSetup.waitingForEmail');
    if(value==='registering')return get('bankingSetup.registeringApp');
    if(value==='verifying')return get('bankingSetup.verifyingApp');
    return get('bankingSetup.loading');
  };

  const showAutomaticFailure=registration=>{
    stopAutoPoll();
    const retry=registration?.canRetryVerification
      ? `<button type="button" data-retry>${esc(get('bankingSetup.retryVerification'))}</button>`
      : `<button type="button" data-again>${esc(get('bankingSetup.tryAgain'))}</button>`;
    step.innerHTML=`<p>${esc(registration?.status==='expired'?get('bankingSetup.autoExpired'):get('bankingSetup.autoFailed'))}</p>
      <div class="dialog-actions"><button type="button" class="ghost" data-manual>${esc(get('bankingSetup.useManual'))}</button>${retry}</div>`;
    step.querySelector('[data-manual]').onclick=()=>{cancelAutoRegistration();showCredentials()};
    step.querySelector('[data-again]')?.addEventListener('click',()=>{cancelAutoRegistration();showAutomatic()});
    step.querySelector('[data-retry]')?.addEventListener('click',async e=>{
      e.currentTarget.disabled=true;
      try{
        const next=await bankApi(`api/banking/profile/register/${encodeURIComponent(autoRegistrationId)}/retry`,jsonBody({}));
        if(next.status==='completed'){
          status=await bankApi('api/banking/status');
          await renderEnableBankingSettings();
          showProfile();
          toast(get('bankingSetup.autoComplete'));
          return;
        }
        showAutomaticFailure(next);
      }catch(err){toast(err.message||get('common.error'));e.currentTarget.disabled=false}
    });
  };

  const pollAutomatic=async id=>{
    if(id!==autoRegistrationId)return;
    try{
      const next=await bankApi(`api/banking/profile/register/${encodeURIComponent(id)}`);
      if(id!==autoRegistrationId)return;
      if(next.status==='completed'){
        stopAutoPoll();
        status=await bankApi('api/banking/status');
        await renderEnableBankingSettings();
        showProfile();
        toast(get('bankingSetup.autoComplete'));
        return;
      }
      if(next.status==='failed'||next.status==='expired'){
        showAutomaticFailure(next);
        return;
      }
      const statusEl=step.querySelector('[data-auto-status]');
      if(statusEl)statusEl.textContent=automaticStatusText(next.status);
    }catch(err){
      const statusEl=step.querySelector('[data-auto-status]');
      if(statusEl)statusEl.textContent=err.message||get('common.error');
    }
    autoPoll=setTimeout(()=>pollAutomatic(id),1500);
  };

  const showAutomaticWaiting=started=>{
    stopAutoPoll();
    step.innerHTML=`<p>${esc(get('bankingSetup.emailSent'))}</p>
      <p class="row-sub" data-auto-status>${esc(get('bankingSetup.waitingForEmail'))}</p>
      <div class="setup-meta">
        <div><span>${esc(get('bankingSetup.privacyUrl'))}</span><a href="${esc(started.privacyUrl||'https://fullworth.de/privacy/')}" target="_blank" rel="noopener">${esc(started.privacyUrl||'https://fullworth.de/privacy/')} ↗</a></div>
        <div><span>${esc(get('bankingSetup.termsUrl'))}</span><a href="${esc(started.termsUrl||'https://fullworth.de/terms/')}" target="_blank" rel="noopener">${esc(started.termsUrl||'https://fullworth.de/terms/')} ↗</a></div>
      </div>
      <div class="dialog-actions"><button type="button" class="ghost" data-manual>${esc(get('bankingSetup.useManual'))}</button></div>`;
    step.querySelector('[data-manual]').onclick=()=>{cancelAutoRegistration();showCredentials()};
    autoPoll=setTimeout(()=>pollAutomatic(started.id),800);
  };

  const showCredentials=()=>{
    stopAutoPoll();
    const callback=status?.callbackUrl||'';
    step.innerHTML=`<p class="row-sub">${esc(get('bankingSetup.createApp'))}</p>
      <p><a href="${ENABLE_BANKING_APPS}" target="_blank" rel="noopener">${esc(get('bankingSetup.apiApplications'))} ↗</a></p>
      <label>${esc(get('bankingSetup.callback'))}<input data-callback readonly value="${esc(callback)}"></label>
      <label>${esc(get('bankingSetup.applicationId'))}<input data-app-id required autocomplete="off"></label>
      <label>${esc(get('bankingSetup.privateKey'))}<input data-key type="file" accept=".pem,text/plain" required></label>
      <p class="row-sub">${esc(get('bankingSetup.keyHint'))}</p>
      <div class="dialog-actions"><button type="button" data-back>${esc(get('onboarding.back'))}</button><button type="button" data-verify>${esc(get('bankingSetup.verify'))}</button></div>`;
    step.querySelector('[data-back]').onclick=showSetupChoice;
    step.querySelector('[data-verify]').onclick=async e=>{
      const button=e.currentTarget,appId=step.querySelector('[data-app-id]').value.trim(),file=step.querySelector('[data-key]').files?.[0];
      if(!appId||!file){toast(get('bankingSetup.missingCredentials'));return}
      button.disabled=true;
      try{
        const privateKeyPem=await file.text();
        await bankApi('api/banking/profile/verify',jsonBody({applicationId:appId,privateKeyPem}));
        status=await bankApi('api/banking/status');
        showProfile();
        await renderEnableBankingSettings();
      }catch(err){toast(err.message||get('common.error'));button.disabled=false}
    };
  };

  const showProfile=()=>{
    stopAutoPoll();
    autoRegistrationId=null;
    const p=status?.profile;
    if(!p){showIntro();return}
    const ready=p.environment==='SANDBOX'||p.active;
    step.innerHTML=`<div class="row"><div class="row-main"><div class="row-title">${esc(p.applicationName||p.applicationId)}</div>
      <div class="row-sub">${esc(p.environment)} · ${ready?esc(get('bankingSetup.active')):esc(get('bankingSetup.inactive'))}</div></div></div>
      <p class="row-sub">${esc(ready?get('bankingSetup.complete'):get('bankingSetup.activateRestricted'))}</p>
      ${!ready&&p.environment==='PRODUCTION'?`<p><a href="${ENABLE_BANKING_APPS}" target="_blank" rel="noopener">${esc(get('bankingSetup.activateAccounts'))} ↗</a> · <a href="${ENABLE_BANKING_LINKED}" target="_blank" rel="noopener">${esc(get('bankingSetup.instructions'))} ↗</a></p>`:''}
      <div class="dialog-actions"><button type="button" class="ghost danger" data-remove>${esc(get('bankingSetup.remove'))}</button><button type="button" data-recheck>${esc(get('bankingSetup.recheck'))}</button><button type="button" data-done>${esc(get('common.close'))}</button></div>`;
    step.querySelector('[data-done]').onclick=()=>dlg.close();
    step.querySelector('[data-recheck]').onclick=async e=>{
      e.currentTarget.disabled=true;
      try{await bankApi('api/banking/profile/recheck',jsonBody({}));status=await bankApi('api/banking/status');showProfile();await renderEnableBankingSettings()}
      catch(err){toast(err.message||get('common.error'));e.currentTarget.disabled=false}
    };
    step.querySelector('[data-remove]').onclick=async e=>{
      if(!await confirmDialog(ctx,get('bankingSetup.removeConfirm'),{destructive:true,confirmLabel:get('bankingSetup.remove')}))return;
      e.currentTarget.disabled=true;
      try{await bankApi('api/banking/profile',{method:'DELETE'});status=await bankApi('api/banking/status');showIntro();await renderEnableBankingSettings()}
      catch(err){toast(err.message||get('common.error'));e.currentTarget.disabled=false}
    };
  };

  if(status?.profile)showProfile();else showIntro();
  dlg.showModal();
}

function authMethodsFor(bank,psuType){
  return (bank.auth_methods||[])
    .filter(m=>!(m&&typeof m==='object'&&m.hidden_method))
    .map(m=>{
      if(typeof m==='string')return{name:m,label:m,psuType:null,approach:null,credentials:[]};
      return{
        name:m.name||m.id||'',
        label:m.title||m.name||m.id||'',
        psuType:m.psu_type||null,
        approach:m.approach||null,
        credentials:Array.isArray(m.credentials)?m.credentials:[]
      };
    })
    .filter(m=>m.name&&(!m.psuType||!psuType||String(m.psuType).toLowerCase()===String(psuType).toLowerCase()));
}

function bankNameKey(value){
  return String(value||'').normalize('NFKD').toLowerCase().replace(/[^a-z0-9]+/g,' ').trim();
}

function mergeBankOptions(rows){
  const merged=new Map();
  for(const row of Array.isArray(rows)?rows:[]){
    const name=bankNameKey(row?.name),country=String(row?.country||'').toUpperCase();
    const key=`${country}|${name}`;
    if(!name||!country)continue;
    const existing=merged.get(key);
    if(!existing){
      merged.set(key,{...row,psu_types:[...(row.psu_types||[])],auth_methods:[...(row.auth_methods||[])]});
      continue;
    }
    existing.psu_types=[...new Set([...(existing.psu_types||[]),...(row.psu_types||[])])];
    const seen=new Set((existing.auth_methods||[]).map(x=>JSON.stringify(x)));
    for(const method of row.auth_methods||[]){
      const signature=JSON.stringify(method);
      if(!seen.has(signature)){existing.auth_methods.push(method);seen.add(signature)}
    }
    existing.beta=Boolean(existing.beta||row.beta);
    if(!existing.logo&&row.logo)existing.logo=row.logo;
    if(!existing.group&&row.group)existing.group=row.group;
  }
  return [...merged.values()];
}

function providerStatusSeverity(value){
  const status=String(value||'').toLowerCase().replace(/[_-]+/g,' ');
  if(!status)return null;
  if(status.includes('major')||status.includes('disruption')||status.includes('critical')||status==='down')return'major';
  if(status.includes('possible')||status.includes('problem')||status.includes('warning')||status.includes('degraded'))return'possible';
  if(status.includes('no problems')||status.includes('healthy')||status==='ok'||status.includes('available'))return'ok';
  return'unknown';
}

function providerStatusLabel(severity){
  if(severity==='major')return get('bankingSetup.statusMajor');
  if(severity==='possible')return get('bankingSetup.statusPossible');
  if(severity==='ok')return get('bankingSetup.statusOk');
  return get('bankingSetup.statusUnknown');
}

function bankStatusMatchKeys(value){
  const full=bankNameKey(value),keys=new Set(full?[full]:[]);
  const first=full.split(' ')[0]||'';
  if(first.length>=3&&first.length<=5)keys.add(first);
  return keys;
}

function applyProviderStatuses(banks,statusView){
  if(!statusView?.available||!Array.isArray(statusView.statuses))return banks;
  const rank={major:3,possible:2,unknown:1,ok:0};
  for(const bank of banks){
    const names=bankStatusMatchKeys(bank.name);
    const groupName=typeof bank.group==='string'?bank.group:(bank.group?.name||bank.group?.title||'');
    for(const key of bankStatusMatchKeys(groupName))names.add(key);
    const matches=statusView.statuses.filter(s=>{
      if(String(s.country||'').toUpperCase()!==String(bank.country||'').toUpperCase())return false;
      const brandKeys=bankStatusMatchKeys(s.brand);
      return [...brandKeys].some(key=>names.has(key));
    });
    if(!matches.length)continue;
    const best=matches.map(s=>({raw:s.status,severity:providerStatusSeverity(s.status)}))
      .sort((a,b)=>(rank[b.severity]??-1)-(rank[a.severity]??-1))[0];
    bank.providerStatus=best.raw;
    bank.providerStatusSeverity=best.severity;
  }
  return banks;
}

function openProviderStatusConnection(country,onConnected){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('bankingSetup.statusConnect'))}</h2><button type="button" data-close>×</button></div>
    <p class="row-sub">${esc(get('bankingSetup.statusConnectHint'))}</p>
    <label>${esc(get('bankingSetup.email'))}<input name="email" type="email" autocomplete="email" required maxlength="254"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('bankingSetup.sendLogin'))}</button></div></form>`);
  const form=dlg.querySelector('form');let pollTimer=null,closed=false;
  const stop=()=>{closed=true;if(pollTimer)clearTimeout(pollTimer)};
  dlg.addEventListener('close',stop);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();

  const connected=async()=>{
    toast(get('bankingSetup.statusConnected'),5000);
    dlg.close();
    if(onConnected)await onConnected();
  };
  const poll=async()=>{
    if(closed)return;
    try{
      const current=await bankApi(`api/banking/provider-status?country=${encodeURIComponent(country)}`);
      if(current?.available){await connected();return}
    }catch{}
    pollTimer=setTimeout(poll,2000);
  };

  form.onsubmit=async e=>{
    e.preventDefault();
    const submit=form.querySelector('[type="submit"]'),email=String(new FormData(form).get('email')||'').trim();
    submit.disabled=true;
    try{
      const started=await bankApi('api/banking/provider-status/connect/start',jsonBody({email}));
      const manual=started?.manualCompletionRequired===true;
      form.innerHTML=`<div class="panel-head"><h2>${esc(get('bankingSetup.statusConnect'))}</h2><button type="button" data-close>×</button></div>
        <p>${esc(get(manual?'bankingSetup.statusEmailSentManual':'bankingSetup.statusEmailSent'))}</p>
        ${manual?`<label>${esc(get('bankingSetup.statusLoginLink'))}<input name="loginLink" type="text" autocomplete="off" required placeholder="http://localhost:8888/?oobCode=…"></label>
          <p class="row-sub">${esc(get('bankingSetup.statusManualHint'))}</p>`:`<p class="row-sub">${esc(get('bankingSetup.waitingForEmail'))}</p>`}
        <div class="dialog-actions"><button type="button" data-close-bottom>${esc(get('common.close'))}</button>${manual?`<button type="button" data-complete>${esc(get('bankingSetup.statusCompleteLogin'))}</button>`:''}</div>`;
      form.querySelector('[data-close]').onclick=()=>dlg.close();
      form.querySelector('[data-close-bottom]').onclick=()=>dlg.close();
      if(manual){
        const complete=form.querySelector('[data-complete]'),input=form.querySelector('[name="loginLink"]');
        complete.onclick=async()=>{
          complete.disabled=true;
          try{
            const result=await bankApi('api/banking/provider-status/connect/complete',jsonBody({
              id:started.id,
              loginLinkOrCode:String(input.value||'').trim()
            }));
            if(!result?.success){
              toast(get(result?.errorCode==='missing_oob_code'?'bankingSetup.statusManualInvalid':'common.error'));
              complete.disabled=false;
              return;
            }
            await connected();
          }catch(err){toast(err.message||get('common.error'));complete.disabled=false}
        };
        input.focus();
      }else pollTimer=setTimeout(poll,1000);
    }catch(err){toast(err.message||get('common.error'));submit.disabled=false}
  };
  dlg.showModal();
}

function isIngEnableBank(bank){
  const key=bankNameKey(bank?.name);
  return key==='ing'||key==='ing diba'||key.startsWith('ing ')||key.includes('ing diba');
}

function ingFinTsBankOption(){
  return{name:'ING',country:'DE',group:'FinTS',fullworthProvider:'fints',psu_types:['personal'],auth_methods:[]};
}

async function openIngConnectionOptions(reconnectConnection=null){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('bankingSetup.ingFinTsTitle'))}</h2><button type="button" data-close>&times;</button></div>
    <label>${esc(get('bankingSetup.ingFinTsMode'))}<select name="mode"><option value="fints" selected>${esc(get('bankingSetup.ingFinTsFull'))}</option><option value="enable">${esc(get('bankingSetup.ingEnableBankingOnly'))}</option></select></label>
    <p class="row-sub" data-mode-hint></p>
    <div data-fints-fields><label>${esc(get('bankingSetup.ingUserId'))}<input name="userId" autocomplete="username" required></label><label>${esc(get('bankingSetup.ingPin'))}<input name="pin" type="password" autocomplete="current-password" required></label></div>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('bankingSetup.connect'))}</button></div></form>`);
  const form=dlg.querySelector('form'),mode=form.elements.mode,fields=dlg.querySelector('[data-fints-fields]'),hint=dlg.querySelector('[data-mode-hint]');
  const draw=()=>{
    const useFinTs=mode.value==='fints';
    fields.hidden=!useFinTs;
    form.elements.userId.required=useFinTs;
    form.elements.pin.required=useFinTs;
    hint.textContent=get(useFinTs?'bankingSetup.ingFinTsFullHint':'bankingSetup.ingEnableBankingHint');
  };
  draw();mode.onchange=draw;
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  form.onsubmit=async e=>{
    e.preventDefault();
    const submit=form.querySelector('[type="submit"]');submit.disabled=true;
    try{
      if(mode.value==='enable'){
        const status=await bankApi('api/banking/status');
        if(!bankingReady(status)){
          dlg.close();
          openEnableBankingWizard(status,{onClose:()=>openBankDialog(reconnectConnection,'DE')});
          return;
        }
        const data=await bankApi('api/banking/institutions?country=DE');
        const bank=mergeBankOptions(data.aspsps||[]).find(isIngEnableBank);
        if(!bank)throw new Error(get('common.error'));
        dlg.close();
        openBankConnectionOptions(bank,reconnectConnection?.id||null,status.profile?.id||null);
        return;
      }
      const fd=new FormData(form);
      const result=await bankApi('api/banking/fints/ing/connect',jsonBody({
        userId:String(fd.get('userId')||'').trim(),
        pin:String(fd.get('pin')||''),
        reconnectConnectionId:reconnectConnection?.id||null
      }));
      dlg.close();
      if(result.status==='TAN_REQUIRED')openIngTanDialog(result);
      else{toast(get('bankingSetup.ingConnected'));await loadAccountsView()}
    }catch(err){toast(err.message||get('common.error'));submit.disabled=false}
  };
  dlg.showModal();
}

// Reopens the challenge a connection is already waiting on. The sync that hit the TAN parked it on the
// connection; before this there was no way to answer it outside the dialog that first triggered it, so
// the connection stayed stuck forever.
async function openPendingTanDialog(connection,button){
  if(button)button.disabled=true;
  try{
    const pending=await bankApi(`api/banking/fints/connections/${encodeURIComponent(connection.id)}/challenge`);
    if(!pending||!pending.challenge){
      toast(get('accounts.tanUnavailable'));
      await loadAccountsView();
      return;
    }
    openIngTanDialog(pending);
  }catch(err){toast(err.message||get('common.error'))}
  finally{if(button)button.disabled=false}
}

function openIngTanDialog(initial){
  let current=initial;
  const dlg=dialog('<div class="dialog-card"><div data-tan-content></div></div>');
  const root=dlg.querySelector('[data-tan-content]');
  const complete=async result=>{
    current=result;
    if(result.status!=='TAN_REQUIRED'){
      dlg.close();toast(get('bankingSetup.ingConnected'));await loadAccountsView();return;
    }
    render();
  };
  const render=()=>{
    const challenge=current.challenge||{};
    const decoupled=challenge.isDecoupled===true;
    root.innerHTML=`<div class="panel-head"><h2>${esc(get('bankingSetup.ingTan'))}</h2><button type="button" data-close>&times;</button></div>
      ${challenge.challenge?`<p>${esc(challenge.challenge)}</p>`:''}<p class="row-sub">${esc(get(decoupled?'bankingSetup.ingDecoupledHint':'bankingSetup.ingTanHint'))}</p>
      ${decoupled?'':`<label>${esc(get('bankingSetup.ingTan'))}<input name="tan" inputmode="numeric" autocomplete="one-time-code" required></label>`}
      <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="button" data-submit>${esc(get(decoupled?'bankingSetup.ingPoll':'bankingSetup.connect'))}</button></div>`;
    root.querySelector('[data-close]').onclick=()=>dlg.close();root.querySelector('[data-cancel]').onclick=()=>dlg.close();
    root.querySelector('[data-submit]').onclick=async e=>{
      const button=e.currentTarget;button.disabled=true;
      try{
        const path=decoupled?`api/banking/fints/connections/${encodeURIComponent(current.connectionId)}/poll`:`api/banking/fints/connections/${encodeURIComponent(current.connectionId)}/tan`;
        const body=decoupled?jsonBody({}):jsonBody({tan:String(root.querySelector('[name="tan"]')?.value||'').trim()});
        await complete(await bankApi(path,body));
      }catch(err){toast(err.message||get('common.error'));button.disabled=false}
    };
  };
  render();dlg.showModal();
}

function openBankConnectionOptions(bank,reconnectConnectionId=null,profileId=null,preferredPsuType=null){
  const rawPsuTypes=Array.isArray(bank.psu_types)?bank.psu_types.filter(Boolean):[];
  const psuTypes=[...new Set((rawPsuTypes.length?rawPsuTypes:['personal','business']).map(x=>String(x).toLowerCase()))]
    .sort((a,b)=>(a==='personal'?0:a==='business'?1:2)-(b==='personal'?0:b==='business'?1:2));
  const requestedDefault=String(preferredPsuType||'personal').toLowerCase();
  const defaultPsuType=psuTypes.includes(requestedDefault)
    ? requestedDefault
    : (psuTypes.includes('personal')?'personal':psuTypes[0]||'personal');
  const health=bank.providerStatusSeverity;
  const healthWarning=health&&health!=='ok'
    ? `<div class="bank-status-warning ${esc(health)}"><strong>${esc(providerStatusLabel(health))}</strong><span>${esc(get('bankingSetup.statusWarning'))}</span><a href="${ENABLE_BANKING_STATUS}" target="_blank" rel="noopener">${esc(get('bankingSetup.statusPage'))} ↗</a></div>`
    : '';
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(bank.name)}</h2><button type="button" data-close>×</button></div>
    ${healthWarning}
    ${psuTypes.length>1?`<label>${esc(get('bankingSetup.accountType'))}<select name="psuType">${psuTypes.map(x=>`<option value="${esc(x)}"${x===defaultPsuType?' selected':''}>${esc(get('bankingSetup.psu_'+x)||x)}</option>`).join('')}</select></label>`:`<input type="hidden" name="psuType" value="${esc(defaultPsuType)}">`}
    <p class="row-sub" data-business-notice hidden>${esc(get('bankingSetup.businessNotice'))}</p>
    <label class="check"><input type="checkbox" data-limit-accounts> <span>${esc(get('bankingSetup.limitAccounts'))}</span></label>
    <label data-account-access hidden>${esc(get('bankingSetup.accountIdentifiers'))}<textarea name="accountAccess" rows="3" autocomplete="off" placeholder="DE89370400440532013000&#10;BBAN|123456|Optional issuer"></textarea><span class="row-sub">${esc(get('bankingSetup.accountIdentifiersHint'))}</span></label>
    <div data-auth-method></div>
    <div data-credentials></div>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('bankingSetup.connect'))}</button></div></form>`);
  const form=dlg.querySelector('form'),methodRoot=dlg.querySelector('[data-auth-method]'),credentialRoot=dlg.querySelector('[data-credentials]'),businessNotice=dlg.querySelector('[data-business-notice]');
  const limitAccounts=dlg.querySelector('[data-limit-accounts]'),accountAccess=dlg.querySelector('[data-account-access]');
  limitAccounts.onchange=()=>{accountAccess.hidden=!limitAccounts.checked;if(limitAccounts.checked)form.elements.accountAccess.focus()};
  let methods=[];

  const drawCredentials=()=>{
    credentialRoot.innerHTML='';
    const methodSelect=form.elements.authMethod;
    const method=methods.find(m=>m.name===methodSelect?.value);
    for(const field of method?.credentials||[]){
      const name=field.name||field.id;if(!name)continue;
      const label=document.createElement('label');
      label.append(document.createTextNode(field.title||name));
      const input=document.createElement('input');input.name='credential:'+name;input.autocomplete='off';
      input.type=/password|pin|secret/i.test(name+' '+(field.title||''))?'password':'text';
      if(field.template)try{new RegExp(field.template);input.pattern=field.template}catch{}
      input.required=field.required===true;
      label.append(input);
      if(field.description){
        const hint=document.createElement('span');hint.className='row-sub';hint.textContent=field.description;label.append(hint);
      }
      credentialRoot.append(label);
    }
  };

  const drawMethods=()=>{
    const psuType=form.elements.psuType?.value||defaultPsuType;
    if(businessNotice)businessNotice.hidden=String(psuType).toLowerCase()!=='business';
    methods=authMethodsFor(bank,psuType);
    methodRoot.innerHTML='';
    credentialRoot.innerHTML='';
    if(!methods.length)return;
    const label=document.createElement('label');label.append(document.createTextNode(get('bankingSetup.authMethod')));
    const select=document.createElement('select');select.name='authMethod';
    const defaultOption=document.createElement('option');defaultOption.value='';defaultOption.textContent=get('bankingSetup.bankDefault');select.append(defaultOption);
    for(const method of methods){
      const option=document.createElement('option');option.value=method.name;
      option.textContent=method.label+(method.approach?' · '+method.approach:'');
      select.append(option);
    }
    select.onchange=drawCredentials;label.append(select);methodRoot.append(label);drawCredentials();
  };

  form.elements.psuType?.addEventListener('change',drawMethods);drawMethods();
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  form.onsubmit=async e=>{
    e.preventDefault();
    const fd=new FormData(form),authMethod=fd.get('authMethod')||null,credentials={};
    const selectedPsuType=fd.get('psuType')||'personal';
    const selectedMethod=authMethod?authMethodsFor(bank,selectedPsuType).find(m=>m.name===authMethod):null;
    if(authMethod&&!selectedMethod){toast(get('common.error'));return}
    for(const[k,v]of fd.entries())if(k.startsWith('credential:')&&String(v).length)credentials[k.slice(11)]=String(v);
    let accounts=null;
    if(limitAccounts.checked){
      const lines=String(fd.get('accountAccess')||'').split(/\r?\n/).map(x=>x.trim()).filter(Boolean);
      if(!lines.length){toast(get('bankingSetup.accountIdentifiersMissing'));return}
      accounts=[];
      for(const line of lines){
        if(!line.includes('|')){
          const iban=line.replace(/\s+/g,'').toUpperCase();
          if(!/^[A-Z]{2}[A-Z0-9]{13,32}$/.test(iban)){toast(get('bankingSetup.accountIdentifiersMissing'));return}
          accounts.push({iban});continue;
        }
        const parts=line.split('|').map(x=>x.trim()),schemeName=(parts[0]||'').toUpperCase(),identification=parts[1]||'',issuer=parts.slice(2).join('|').trim();
        if(!schemeName||!identification){toast(get('bankingSetup.accountIdentifiersMissing'));return}
        accounts.push({other:{identification,schemeName,issuer:issuer||null}});
      }
    }
    const body={
      institutionName:bank.name,country:bank.country||'DE',validDays:365,
      authMethod,credentials:Object.keys(credentials).length?credentials:null,
      reconnectConnectionId,enableBankingProfileId:profileId,
      psuType:selectedPsuType,language:state.lang,
      credentialsAutosubmit:Object.keys(credentials).length?true:null,
      accounts
    };
    const submit=form.querySelector('[type="submit"]');submit.disabled=true;
    try{const result=await bankApi('api/banking/connect',jsonBody(body));location.href=result.authorizationUrl}
    catch(err){toast(err.message||get('common.error'));submit.disabled=false}
  };
  dlg.showModal();
}

async function openBankDialog(reconnectConnection=null,initialCountry='DE'){
  let status=null;
  try{status=await bankApi('api/banking/status')}catch{}

  const dlg=dialog(`<form method="dialog" class="dialog-card"><div class="panel-head"><h2>${esc(reconnectConnection?get('accounts.reconnect'):get('accounts.addBank'))}</h2><button value="cancel">×</button></div>
    <label>${esc(get('bankingSetup.country'))}<input id="bank-country" value="${esc(String(initialCountry||'DE').toUpperCase())}" maxlength="2" minlength="2" pattern="[A-Za-z]{2}" autocapitalize="characters"></label>
    <input id="bank-search" type="search" placeholder="Bank">
    <div id="bank-status-tools" class="bank-status-tools"><a href="${ENABLE_BANKING_STATUS}" target="_blank" rel="noopener">${esc(get('bankingSetup.statusPage'))} ↗</a><span data-status-state></span><button type="button" data-status-connect hidden>${esc(get('bankingSetup.statusConnect'))}</button></div>
    <div id="bank-options" class="bank-options"></div></form>`);
  const box=dlg.querySelector('#bank-options'),search=dlg.querySelector('#bank-search'),countryInput=dlg.querySelector('#bank-country');
  const statusTools=dlg.querySelector('#bank-status-tools'),statusState=statusTools.querySelector('[data-status-state]'),statusConnect=statusTools.querySelector('[data-status-connect]');
  let banks=[],providerStatusState=null;

  const draw=filter=>{
    box.innerHTML='';
    for(const bank of banks.filter(x=>!filter||(x.name||'').toLowerCase().includes(filter.toLowerCase())).slice(0,100)){
      const b=document.createElement('button');b.type='button';b.className='bank-option';
      if(bank.logo){
        const logo=document.createElement('img');logo.className='bank-option-logo';logo.src=bank.logo;logo.alt='';
        logo.loading='lazy';logo.referrerPolicy='no-referrer';logo.onerror=()=>logo.remove();b.appendChild(logo);
      }
      const text=document.createElement('span');text.className='bank-option-text';
      const title=document.createElement('strong');title.textContent=bank.name||'';text.appendChild(title);
      const groupName=typeof bank.group==='string'?bank.group:(bank.group?.name||bank.group?.title||'');
      const types=(bank.psu_types||[]).map(type=>get('bankingSetup.psu_'+type)||type).join(' / ');
      const health=bank.providerStatusSeverity;
      const issue=health&&health!=='ok'?providerStatusLabel(health):null;
      const meta=[bank.country||countryInput.value.toUpperCase(),groupName,types,bank.beta?get('bankingSetup.beta'):null,issue].filter(Boolean);
      const sub=document.createElement('span');sub.className='row-sub';sub.textContent=meta.join(' · ');text.appendChild(sub);
      if(health&&health!=='ok')b.classList.add('bank-option-status-'+health);
      b.appendChild(text);
      if(health&&health!=='ok'){
        const chip=document.createElement('span');
        chip.className='bank-status-chip '+health;
        const dot=document.createElement('span');dot.className='bank-status-dot';dot.setAttribute('aria-hidden','true');
        chip.append(dot,document.createTextNode(providerStatusLabel(health)));
        b.appendChild(chip);
      }
      b.onclick=()=>{dlg.close();if(bank.fullworthProvider==='fints')openIngConnectionOptions(reconnectConnection);else openBankConnectionOptions(bank,reconnectConnection?.id||null,status?.profile?.id||null)};
      box.appendChild(b);
    }
    if(!banks.length)box.innerHTML=`<div class="row-sub">${esc(get('common.empty'))}</div>`;
  };

  const loadCountry=async()=>{
    const country=countryInput.value.trim().toUpperCase();
    if(!/^[A-Z]{2}$/.test(country))return;
    countryInput.value=country;box.innerHTML=`<div class="row-sub">${esc(get('bankingSetup.loading'))}</div>`;
    const ing=country==='DE'?[ingFinTsBankOption()]:[];
    if(!bankingReady(status)){
      providerStatusState=null;
      statusConnect.hidden=true;
      statusState.textContent=get('bankingSetup.notConfigured');
      statusTools.classList.add('status-attention');
      statusTools.classList.remove('status-active');
      banks=ing;draw(search.value);return;
    }
    try{
      const [data,providerStatus]=await Promise.all([
        bankApi(`api/banking/institutions?country=${encodeURIComponent(country)}`),
        bankApi(`api/banking/provider-status?country=${encodeURIComponent(country)}`).catch(()=>({available:false,reason:'provider_status_request_failed',statuses:[]}))
      ]);
      providerStatusState=providerStatus;
      const canConnectStatus=providerStatus&&
        (providerStatus.reason==='control_panel_access_unavailable'||providerStatus.reason==='control_panel_login_expired');
      statusConnect.hidden=!canConnectStatus;
      statusState.textContent=providerStatus?.available
        ?get('bankingSetup.statusActive')
        :(canConnectStatus?get('bankingSetup.statusConnectShort'):get('bankingSetup.statusUnavailable'));
      statusTools.classList.toggle('status-attention',providerStatus?.available!==true);
      statusTools.classList.toggle('status-active',providerStatus?.available===true);
      const enableBanks=mergeBankOptions(data.aspsps||[]).filter(bank=>country!=='DE'||!isIngEnableBank(bank)).sort((a,b)=>(a.name||'').localeCompare(b.name||''));
      banks=[...ing,...applyProviderStatuses(enableBanks,providerStatusState)];
      draw(search.value);
    }catch(err){banks=ing;if(ing.length)draw(search.value);else box.innerHTML=`<div class="row-sub">${esc(err.message||get('common.error'))}</div>`}
  };
  statusConnect.onclick=()=>openProviderStatusConnection(
    countryInput.value.trim().toUpperCase()||'DE',
    loadCountry);
  search.oninput=e=>draw(e.target.value);
  countryInput.addEventListener('change',loadCountry);
  countryInput.addEventListener('input',()=>{if(countryInput.value.trim().length===2)loadCountry()});
  dlg.showModal();
  await loadCountry();
}


export function bindAccounts(context) {
  use(context);
  if (bound) return;
  bound = true;
  bindAccountsPresentation({
    openAdd: () => openAddAccountDialog(),
    openBank: () => openBankDialog()
  });
  $('#add-account')?.addEventListener('click', () => openAddAccountDialog());
  $('#add-group')?.addEventListener('click', () => toggleAccountGroupEditing().catch(console.error));
}

export async function renderAccounts(context) {
  use(context);
  return loadAccountsView();
}

export function openAddAccount(context) {
  use(context);
  return openAddAccountDialog();
}

export function openBankConnection(context, reconnectConnection = null, country = 'DE') {
  use(context);
  return openBankDialog(reconnectConnection, country);
}

export function openBankingSetup(context, status, options = {}) {
  use(context);
  return openEnableBankingWizard(status, options);
}

export async function renderBankingSettings(context) {
  use(context);
  return renderEnableBankingSettings();
}
