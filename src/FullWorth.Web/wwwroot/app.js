import { money, converted, maskIdentifier, setMoneyLocale } from './ui/money.js';
import { isPrivate, togglePrivacy, onPrivacyChange, privacyDefault, setPrivacyDefault } from './ui/privacy.js';
import { confirmDialog } from './ui/confirm.js';
import { initLock, openPinDialog } from './ui/lock.js';
import { renderDashboard, bindDashboard, toggleDashboardEdit, invalidateLayout } from './ui/dashboard.js';
import { renderTransactions, bindTransactions } from './features/transactions.js';
import { renderCategories, bindCategories } from './features/categories.js';
import { renderRules, bindRules, newRule } from './features/rules.js';
import { renderContracts, bindContracts, newContract } from './features/contracts.js';
import { renderNetWorth, bindNetWorth, newAsset } from './features/networth.js';
import { renderNotifications } from './features/notifications.js';
import { renderLoans, bindLoans } from './features/loans.js';
import { renderAnalytics, bindAnalytics } from './features/analytics.js';
import { renderPurchases, bindPurchases } from './features/purchases.js';
import { renderMerchants, bindMerchants, newMerchant } from './features/merchants.js';
import { renderAudit, bindAudit } from './features/audit.js';
import { renderSharing, bindSharing } from './features/sharing.js';
import { createAccessSetup } from './features/access-setup.js';
import { createDialog } from './ui/dialog.js';

// Coalesce identical backend GETs at the one choke point every caller shares — window.fetch. The
// feature-parity modules each keep their own fetch wrapper and independently pull the same
// access/overview/category/list data on a shared render, which produced a burst of dozens of
// duplicate requests (and made opening a detail sluggish). Dedupe within a short TTL, handing each
// caller its own response clone; any mutation (non-GET) clears the cache so reads stay fresh.
(function dedupeBackendGets(){
  const real=window.fetch.bind(window);
  const cache=new Map();const TTL=2000;
  const urlOf=i=>{try{return typeof i==='string'?i:(i&&i.url)||'';}catch{return'';}};
  const methodOf=(i,init)=>String((init&&init.method)||(i&&typeof i!=='string'&&i.method)||'GET').toUpperCase();
  window.fetch=function(input,init){
    const url=urlOf(input),method=methodOf(input,init);
    const isBackend=url.includes('/bff/backend/');
    if(method!=='GET'||!isBackend||(init&&init.body)){
      const p=real(input,init);
      if(method!=='GET'&&isBackend)cache.clear();
      return p;
    }
    const hit=cache.get(url);
    if(hit&&Date.now()-hit.t<TTL)return hit.p.then(r=>r.clone());
    if(cache.size>200)cache.clear();
    const p=real(input,init);
    cache.set(url,{t:Date.now(),p});
    p.then(r=>{if(!r.ok){const c=cache.get(url);if(c&&c.p===p)cache.delete(url);}},()=>{const c=cache.get(url);if(c&&c.p===p)cache.delete(url);});
    return p.then(r=>r.clone());
  };
})();
const state={lang:localStorage.getItem('finance.language')||((navigator.language||'de').startsWith('de')?'de':'en'),theme:localStorage.getItem('finance.theme')||'system',messages:{},view:'dashboard',spaces:[],space:null};
// Mobile bottom nav shows exactly these four + "More" (UX rework §2): Übersicht, Verträge, Analysen,
// Vermögen. Transactions is reached by tapping an account/group or the "Alle Buchungen" row (never a
// permanent slot); everything else lives in More.
const MOBILE_PRIMARY=['dashboard','contracts','analytics','networth'];
const ALL_VIEWS=['dashboard','transactions','accounts','budgets','contracts','networth','analytics','purchases','categories','rules','notifications','merchants','audit','settings'];
const MORE_VIEWS=ALL_VIEWS.filter(v=>!MOBILE_PRIMARY.includes(v));
// §3: every screen has a real URL so reload/back/forward/deep-links work (the view is no longer
// only client state). dashboard is the root; the server's MapFallbackToFile serves index.html for
// any of these paths and the app resolves the view from location.pathname on boot.
const VIEW_PATH={dashboard:'/'};ALL_VIEWS.forEach(v=>{if(v!=='dashboard')VIEW_PATH[v]='/'+v});
const pathForView=v=>VIEW_PATH[v]||'/';
function viewFromPath(p){const seg=(p||'/').replace(/^\/+|\/+$/g,'').split('/')[0];return seg&&ALL_VIEWS.includes(seg)?seg:'dashboard'}
// Contextual primary action per section (UI_UX_SPEC §3.1 header). Maps to the same handler as the
// in-page add control so there is a single code path.
const PRIMARY_ACTION={dashboard:['dashboard.edit',()=>toggleDashboardEdit(ctx)],budgets:['budgets.new',()=>openBudgetDialog()],contracts:['contracts.new',()=>newContract(ctx)],rules:['rules.new',()=>newRule(ctx)],categories:['categories.new',()=>openCategoryDialog()],accounts:['accounts.add',()=>openAddAccountDialog()],networth:['networth.newAsset',()=>newAsset(ctx)],merchants:['merchants.new',()=>newMerchant(ctx)]};
const media=matchMedia('(prefers-color-scheme: dark)');
const $=s=>document.querySelector(s);const $$=s=>[...document.querySelectorAll(s)];

async function boot(){
  setMoneyLocale(state.lang);
  $('#theme').value=state.theme;$('#language').value=state.lang;applyTheme();
  await loadMessages();bind();syncPrivacyToggle();syncNavToggle();
  const startView=handleConnectRedirect()||viewFromPath(location.pathname);
  try{await loadSpaces()}catch(e){console.error(e);toast(get('common.error'))}
  await showView(startView,{replace:true});
  // Inactivity lock: covers the app after 10 min idle; unlock re-loads the current screen.
  initLock(ctx,{onUnlock:loadCurrent});
  await accessSetup.maybeOpenRegistrationOnboarding();
}
function handleConnectRedirect(){
  const params=new URLSearchParams(location.search);
  const connected=params.get('bankConnected');const error=params.get('bankError');
  if(!connected&&!error)return null;
  history.replaceState(null,'',location.pathname);
  if(connected){toast(get('accounts.connected').replace('{name}',()=>connected),6000);return'accounts'}
  const known={access_denied:'accounts.connectCancelled',app_invalid_callback:'accounts.connectExpired',app_not_configured:'accounts.notConfigured',app_missing_parameters:'accounts.connectFailed',reauthorization_required:'accounts.connectReauth'};
  toast(get(known[error]||'accounts.connectFailed'),8000);
  return'accounts';
}
function get(path){return path.split('.').reduce((o,k)=>o?.[k],state.messages)||path}
async function loadMessages(){state.messages=await fetch(`/locales/${state.lang}.json`).then(r=>r.json());document.documentElement.lang=state.lang;renderTranslations();renderPageHeader()}
function renderTranslations(){$$('[data-i18n]').forEach(el=>el.textContent=get(el.dataset.i18n));$$('[data-i18n-placeholder]').forEach(el=>el.placeholder=get(el.dataset.i18nPlaceholder));$$('[data-i18n-title]').forEach(el=>el.title=get(el.dataset.i18nTitle));
  // Collapsed sidebar shows icons only — carry each nav label as a tooltip + accessible name.
  $$('.sidebar button[data-view], #bottom-nav button[data-view]').forEach(b=>{const t=b.querySelector('span')?.textContent||'';if(t){b.title=t;b.setAttribute('aria-label',t)}})}
function renderPageHeader(){
  const p=state.messages.pages?.[state.view];
  if(p){$('#page-title').textContent=p.title;$('#page-subtitle').textContent=p.subtitle}
  const action=PRIMARY_ACTION[state.view];const btn=$('#primary-action');
  if(action){btn.hidden=false;btn.textContent=get(action[0]);btn.onclick=action[1]}else{btn.hidden=true;btn.onclick=null}
}
function applyTheme(){const actual=state.theme==='system'?(media.matches?'dark':'light'):state.theme;document.documentElement.dataset.theme=actual;updateThemeToggle()}
function updateThemeToggle(){const b=$('#theme-toggle');if(b)b.dataset.themePref=state.theme}
async function loadSpaces(){
  const spaces=await api('api/fullworth-spaces');state.spaces=spaces||[];
  const saved=localStorage.getItem('finance.space');
  state.space=state.spaces.find(s=>s.id===saved)||state.spaces[0]||null;
  if(state.space)localStorage.setItem('finance.space',state.space.id);
  renderUserBlock();
  invalidateLayout(); // dashboard layout is per space
}
// Sidebar foot: current space name, currency and an avatar initial (§3.1 user block).
function renderUserBlock(){
  const sp=state.space;
  $('#user-space-name').textContent=sp?.name||'';
  $('#user-space-sub').textContent=sp?.baseCurrency||'';
  $('#user-avatar').textContent=(sp?.name||'F').trim().charAt(0).toUpperCase()||'F';
}
function bind(){
  $('#language').addEventListener('change',async e=>{state.lang=e.target.value;localStorage.setItem('finance.language',state.lang);setMoneyLocale(state.lang);await loadMessages();await loadCurrent()});
  $('#theme').addEventListener('change',e=>{state.theme=e.target.value;localStorage.setItem('finance.theme',state.theme);applyTheme()});
  // Sidebar theme toggle: cycles System -> Hell -> Dunkel (same behaviour as the login screen) and keeps the Settings select in sync.
  $('#theme-toggle')?.addEventListener('click',()=>{const order=['system','light','dark'];state.theme=order[(order.indexOf(state.theme)+1)%order.length]||'system';localStorage.setItem('finance.theme',state.theme);applyTheme();const sel=$('#theme');if(sel)sel.value=state.theme});
  media.addEventListener('change',()=>{if(state.theme==='system')applyTheme()});
  // `.sidebar button[data-view]` covers both #nav and the sidebar-foot (Settings) entry, so Settings
  // is reachable on desktop; #bottom-nav is the mobile bar.
  $$('.sidebar button[data-view], #bottom-nav button[data-view]').forEach(b=>b.addEventListener('click',()=>showView(b.dataset.view,{query:''})));
  // Browser Back/Forward: restore the view from the URL without pushing a new history entry.
  window.addEventListener('popstate',()=>showView(viewFromPath(location.pathname),{fromHistory:true}));
  $('#bottom-more').addEventListener('click',openMoreSheet);
  $('#nav-collapse').addEventListener('click',toggleSidebar);
  $('#privacy-toggle').addEventListener('click',()=>togglePrivacy());
  $('#global-search').addEventListener('click',openSearch);
  $$('[data-view-jump]').forEach(b=>b.addEventListener('click',()=>showView(b.dataset.viewJump)));
  $('#refresh').addEventListener('click',loadCurrent);
  bindTransactions(ctx);
  $('#add-account').addEventListener('click',openAddAccountDialog);
  $('#add-group')?.addEventListener('click',()=>openGroupDialog());
  $('[data-action="new-budget"]').addEventListener('click',openBudgetDialog);
  bindContracts(ctx);
  bindNetWorth(ctx);
  bindLoans(ctx);
  bindAnalytics(ctx);
  $('[data-action="new-category"]').addEventListener('click',openCategoryDialog);
  bindCategories(ctx);
  bindRules(ctx);
  bindPurchases(ctx);
  bindMerchants(ctx);
  bindAudit(ctx);
  bindSharing(ctx);
  $('#export-data')?.addEventListener('click',downloadExport);
  bindDashboard(ctx);
  $('#lock-settings')?.addEventListener('click',()=>openPinDialog(ctx));
  $('#privacy-default').addEventListener('change',e=>setPrivacyDefault(e.target.checked));
  // Re-render on privacy change so every value on the current screen re-masks via the shared path.
  onPrivacyChange(()=>{syncPrivacyToggle();loadCurrent()});
  // Desktop keyboard shortcut: "/" opens global search unless typing in a field (§19).
  document.addEventListener('keydown',e=>{if(e.key==='/'&&!/^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)&&!e.target.isContentEditable){e.preventDefault();openSearch()}});
}
function syncPrivacyToggle(){const b=$('#privacy-toggle');b.setAttribute('aria-pressed',String(isPrivate()));b.classList.toggle('active',isPrivate());$('#privacy-default').checked=privacyDefault()}
function toggleSidebar(){const collapsed=!document.body.classList.contains('nav-collapsed');document.body.classList.toggle('nav-collapsed',collapsed);localStorage.setItem('finance.navCollapsed',collapsed?'1':'0');syncNavToggle()}
// Point the chevron the way it will move (‹ collapses, › expands) and label it for its next action.
function syncNavToggle(){const b=$('#nav-collapse');if(!b)return;const collapsed=document.body.classList.contains('nav-collapsed');b.textContent=collapsed?'›':'‹';const label=get(collapsed?'nav.expand':'nav.collapse');b.setAttribute('aria-label',label);b.title=label}
async function showView(view,opts={}){
  state.view=view;
  // Keep the URL in sync so a reload/deep-link lands on this screen and Back/Forward work.
  const base=pathForView(view);
  // Scope query (e.g. /transactions?accountId=… or ?groupId=…): an explicit opts.query wins; otherwise
  // keep the current query when re-entering the same path (boot/deep-link), else clear it on a fresh nav.
  const query=opts.query!==undefined?String(opts.query):(location.pathname===base?location.search.replace(/^\?/,''):'');
  const path=query?`${base}?${query}`:base;
  if(!opts.fromHistory){
    // Replace when re-landing on the exact same URL (or asked to); push a real entry otherwise so
    // drilling into a different account/group is a Back step.
    if(opts.replace||location.pathname+location.search===path)history.replaceState({view},'',path);
    else history.pushState({view},'',path);
  }
  $$('.view').forEach(v=>v.classList.remove('active'));$(`#view-${view}`)?.classList.add('active');
  $$('.sidebar button[data-view]').forEach(b=>{const on=b.dataset.view===view;b.classList.toggle('active',on);b.setAttribute('aria-current',on?'page':'false')});
  $$('#bottom-nav button[data-view]').forEach(b=>{const on=b.dataset.view===view;b.classList.toggle('active',on);b.setAttribute('aria-current',on?'page':'false')});
  $('#bottom-more').classList.toggle('active',MORE_VIEWS.includes(view));
  renderPageHeader();await loadCurrent();
}
async function loadCurrent(){
  try{
    if(!state.space){await loadSpaces();if(!state.space){toast(get('common.error'));return}}
    switch(state.view){
      case'dashboard':return await loadDashboard();
      case'transactions':return await renderTransactions(ctx);
      case'accounts':return await loadAccountsView();
      case'budgets':return await loadBudgets();
      case'contracts':return await renderContracts(ctx);
      case'networth':await renderNetWorth(ctx);return await renderLoans(ctx);
      case'analytics':return await renderAnalytics(ctx);
      case'purchases':return await renderPurchases(ctx);
      case'categories':return await renderCategories(ctx);
      case'rules':return await renderRules(ctx);
      case'notifications':return await renderNotifications(ctx);
      case'merchants':return await renderMerchants(ctx);
      case'audit':return await renderAudit(ctx);
      case'settings':return loadSettings();
    }
  }catch(e){console.error(e);toast(get('common.error'))}
}
async function fail(r){let message=`${r.status}`;try{const body=await r.json();message=body.message||body.error||body.title||message}catch{}throw new Error(message)}
function withSpace(path){
  if(!state.space)return path;
  const [base,query='']=path.split('?');
  const params=new URLSearchParams(query);
  if(params.has('fullWorthSpaceId'))return path;
  params.set('fullWorthSpaceId',state.space.id);
  return `${base}?${params}`;
}
async function api(path,options){const r=await fetch(`/bff/backend/${withSpace(path.replace(/^\//,''))}`,options);if(!r.ok)await fail(r);if(r.status===204)return null;return r.json()}
async function bankApi(path,options){const r=await fetch(`/bff/banking/${withSpace(path.replace(/^\//,''))}`,options);if(!r.ok)await fail(r);if(r.status===204)return null;return r.json()}
const jsonBody=data=>({method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)});
function date(value){if(!value)return'—';return new Intl.DateTimeFormat(state.lang==='de'?'de-DE':'en-US').format(new Date(`${String(value).slice(0,10)}T12:00:00`))}
function empty(el,message){el.innerHTML=`<div class="row state-empty"><div class="row-sub">${esc(message||get('common.empty'))}</div></div>`}
function skeleton(el,rows=4){el.innerHTML=Array.from({length:rows},()=>`<div class="row skel"><div class="skel-bar"></div><div class="skel-bar short"></div></div>`).join('')}
function esc(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function acctId(last4){return last4?` · ${maskIdentifier(last4)}`:''}
function toast(text,duration){const el=$('#toast');el.textContent=text;el.classList.add('show');clearTimeout(toast.timer);toast.timer=setTimeout(()=>el.classList.remove('show'),duration||3200)}
function dialog(html,options={}){return createDialog(html,{closeLabel:get('common.close'),...options})}
// §10.5: options show the full path ("Groceries > Supermarket"), not just the leaf name, so a
// category under multiple parents with the same name is still distinguishable at a glance.
async function categoryOptions(selected){const categories=await api('api/categories');const byId=new Map(categories.map(c=>[c.id,c]));const path=c=>{const chain=[];let cur=c;while(cur){chain.unshift(cur.name);cur=cur.parentId?byId.get(cur.parentId):null}return chain.join(' › ')};return categories.map(c=>`<option value="${c.id}"${c.id===selected?' selected':''}>${esc(path(c))}</option>`).join('')}

function openMoreSheet(){
  const items=MORE_VIEWS.map(view=>{
    const source=$(`.sidebar button[data-view="${view}"]`);
    const icon=source?source.querySelector('svg').outerHTML:'';
    // Prefer the nav label; fall back to the page title when a view has no nav.* key (merchants, audit)
    // so the sheet never shows a raw i18n key.
    const nav=get(`nav.${view}`);
    const label=nav===`nav.${view}`?(state.messages.pages?.[view]?.title||view):nav;
    return `<button type="button" data-go="${view}" class="${state.view===view?'active':''}">${icon}<span>${esc(label)}</span></button>`;
  }).join('');
  const dlg=dialog(`<form method="dialog" class="dialog-card more-sheet"><div class="panel-head"><h2>${esc(get('nav.more'))}</h2><button value="cancel" data-close>×</button></div><div class="more-list">${items}</div></form>`,{mobileMode:'sheet'});
  dlg.classList.add('more-sheet-dialog');
  dlg.querySelectorAll('[data-go]').forEach(b=>b.addEventListener('click',()=>{dlg.close();showView(b.dataset.go)}));
  dlg.showModal();
}

// Global search (§19): groups results from existing scoped endpoints; never touches provider payloads.
async function openSearch(){
  const dlg=dialog(`<form method="dialog" class="dialog-card search-dialog"><div class="panel-head"><h2>${esc(get('search.title'))}</h2><button value="cancel" data-close>×</button></div><input id="search-input" type="search" autocomplete="off" data-i18n-placeholder="search.placeholder" placeholder="${esc(get('search.placeholder'))}"><div id="search-results" class="rows"></div></form>`);
  const input=dlg.querySelector('#search-input');const results=dlg.querySelector('#search-results');
  let timer;
  input.addEventListener('input',()=>{clearTimeout(timer);timer=setTimeout(()=>runSearch(input.value.trim(),results,dlg),220)});
  dlg.addEventListener('close',()=>{});dlg.showModal();input.focus();
}
async function runSearch(query,results,dlg){
  if(query.length<2){results.innerHTML=`<div class="row state-empty"><div class="row-sub">${esc(get('search.hint'))}</div></div>`;return}
  skeleton(results,3);
  try{
    const [tx,accounts,categories,contracts,purchases,assets]=await Promise.all([
      api(`api/transactions?limit=8&query=${encodeURIComponent(query)}`).catch(()=>({items:[]})),
      api('api/accounts').catch(()=>[]),
      api('api/categories').catch(()=>[]),
      api('api/contracts').catch(()=>[]),
      api('api/purchases').catch(()=>[]),
      api('api/assets').catch(()=>[])]);
    const q=query.toLowerCase();
    const groups=[
      [get('search.transactions'),(tx.items||[]).map(x=>({title:x.counterparty||'—',sub:`${date(x.bookingDate)} · ${money(x.amount,x.currency)}`,go:'transactions'}))],
      [get('search.accounts'),(accounts||[]).filter(x=>(x.displayName||x.institutionName||'').toLowerCase().includes(q)).map(x=>({title:x.displayName||x.institutionName,sub:x.institutionName,go:'accounts'}))],
      [get('nav.categories'),(categories||[]).filter(x=>(x.name||'').toLowerCase().includes(q)).slice(0,8).map(x=>({title:x.name,sub:'',go:'categories'}))],
      [get('nav.contracts'),(contracts||[]).filter(x=>(x.name||'').toLowerCase().includes(q)).slice(0,8).map(x=>({title:x.name,sub:money(x.amount,x.currency),go:'contracts'}))],
      [get('nav.purchases'),(purchases||[]).filter(x=>(x.merchant||x.externalOrderId||'').toLowerCase().includes(q)).slice(0,8).map(x=>({title:x.merchant||x.externalOrderId||'—',sub:`${date(x.purchaseDate)} · ${money(x.totalAmount,x.currency)}`,go:'purchases'}))],
      [get('portfolio.assets'),(assets||[]).filter(x=>(x.name||'').toLowerCase().includes(q)).slice(0,8).map(x=>({title:x.name,sub:money(x.currentValue,x.currency),go:'networth'}))],
    ].filter(([,items])=>items.length);
    if(!groups.length){empty(results,get('search.none'));return}
    results.innerHTML=groups.map(([label,items])=>`<div class="search-group">${esc(label)}</div>`+items.map(i=>`<button type="button" class="row search-hit" data-go="${i.go}"><div class="row-main"><div class="row-title">${esc(i.title)}</div>${i.sub?`<div class="row-sub">${esc(i.sub)}</div>`:''}</div></button>`).join('')).join('');
    results.querySelectorAll('[data-go]').forEach(b=>b.addEventListener('click',()=>{dlg.close();showView(b.dataset.go)}));
  }catch(err){empty(results,err.message||get('common.error'))}
}

// Shared context handed to UI modules (dashboard widgets, transactions detail, …) so they reuse the
// app's single api()/formatting/dialog path instead of duplicating it.
const ctx={$,$$,api,bankApi,get,esc,date,toast,dialog,money,isPrivate,categoryOptions,jsonBody,reload:loadCurrent,confirm:(message,opts)=>confirmDialog(ctx,message,opts),bffUrl:path=>`/bff/backend/${withSpace(path.replace(/^\//,''))}`,
  // Drill-down helper (UX rework §3): open a view with a URL scope, e.g. navScope('transactions','accountId='+id).
  navScope:(view,query)=>showView(view,{query:query||''}),showView:(view,opts)=>showView(view,opts)};
const accessSetup=createAccessSetup(ctx,(status,options)=>openEnableBankingWizard(status,options));
// Feature modules loaded as separate <script type="module"> (accounts-ux.js, dashboard widgets) can't
// import app.js internals; expose only the safe scoped-navigation entry point for account/group drill-down.
window.fwNavScope=(view,query)=>showView(view,{query:query||''});
async function loadDashboard(){await renderDashboard(ctx)}

async function loadBudgets(){
  const currency=state.space?.baseCurrency||'EUR';
  const status=await api('api/analytics/budget-status');
  const items=status.items||[];
  const totalBudgeted=items.reduce((s,x)=>s+Number(x.amount||0),0);
  const totalSpent=items.reduce((s,x)=>s+Number(x.spent||0),0);
  $('#budget-total').textContent=money(totalBudgeted,currency);
  $('#budget-spent').textContent=money(totalSpent,currency);
  $('#budget-remaining').textContent=money(totalBudgeted-totalSpent,currency);
  const el=$('#budgets-list');el.innerHTML='';
  if(!items.length){empty(el);return}
  for(const x of items){
    const pct=Math.max(0,Number(x.percent||0));
    const clamped=Math.min(100,pct);
    // Status from usage: over (>100), near (>=85), on track (§12.2).
    const status=pct>100?'over':pct>=85?'near':'ontrack';
    const cycleLabel=x.period&&x.period!=='monthly'?`${esc(get('budgets.period_'+x.period)||x.period)} · ${date(x.periodStart)}–${date(x.periodEnd)} · `:'';
    el.insertAdjacentHTML('beforeend',`<div class="budget-card" role="button" tabindex="0" data-id="${esc(x.budgetId||x.id)}"><div class="budget-card-head"><div class="row-title">${esc(x.name)}</div><span class="budget-status ${status}">${esc(get('budgets.status_'+status))}</span></div><div class="progress ${status}"><span data-w="${clamped}"></span></div><div class="budget-card-foot"><span>${cycleLabel}${money(x.spent,currency)} / ${money(x.amount,currency)}</span><span>${esc(get('budgets.remaining'))}: ${money(x.remaining,currency)}</span></div></div>`);
  }
  // §18: flag when some spend was in a currency with no conversion rate (excluded from the figures).
  if(status.incomplete)el.insertAdjacentHTML('afterbegin',`<div class="fx-incomplete">${esc(get('common.fxIncomplete'))}</div>`);
  // Set bar widths via JS (avoids a source inline style; keeps the CSP inline-style budget at one).
  el.querySelectorAll('.progress > span[data-w]').forEach(s=>{s.style.width=s.dataset.w+'%'});
  // §12: each card opens the budget detail (window, forecast, contributing transactions).
  el.querySelectorAll('.budget-card[data-id]').forEach(card=>{
    const open=()=>openBudgetDetail(card.dataset.id);
    card.addEventListener('click',open);
    card.addEventListener('keydown',ev=>{if(ev.key==='Enter'||ev.key===' '){ev.preventDefault();open()}});
  });
}
// §12 budget detail: cycle window, spend vs. budget, cycle-end forecast, and the transactions
// contributing to this cycle. Reuses the shared api()/money()/dialog() path.
async function openBudgetDetail(id){
  let s;
  try{s=await api(`api/budgets/${id}/status`)}catch(err){toast(err.message||get('common.error'));return}
  if(!s){toast(get('common.error'));return}
  const currency=s.currency||state.space?.baseCurrency||'EUR';
  const pct=Math.max(0,Number(s.percentUsed||0));
  const clamped=Math.min(100,pct);
  const barStatus=pct>100?'over':pct>=85?'near':'ontrack';
  // Hatched forecast segment = projected end-of-cycle spend beyond what's already spent (capped at 100%).
  const projectedPct=Number(s.budgetAmount)>0?(Number(s.projectedEndSpend||0)/Number(s.budgetAmount))*100:0;
  const forecastPct=Math.max(0,Math.min(100,projectedPct)-clamped);
  const trend=(s.trend||'NoData');
  const trendKey='budgets.trend_'+trend.toLowerCase();
  const projOverUnder=Number(s.projectedOverUnder||0);
  // Colour is reserved for money statements (design rule): the forecast figures carry sentiment,
  // the trend text stays neutral (the % pill already signals status at a glance).
  const forecastLine=trend==='NoData'?'':`<div class="budget-detail-forecast"><div class="kv"><span>${esc(get('budgets.projectedEnd'))}</span><strong class="amount">${money(s.projectedEndSpend,currency)}</strong></div><div class="kv"><span>${esc(get(projOverUnder>0?'budgets.projectedOver':'budgets.projectedUnder'))}</span><strong class="amount ${projOverUnder>0?'negative':'positive'}">${money(Math.abs(projOverUnder),currency)}</strong></div></div>`;
  const rows=(s.contributing||[]).map(t=>`<div class="row"><div class="row-main"><div class="row-title">${esc(t.counterparty||'—')}</div><div class="row-sub">${t.bookingDate?date(t.bookingDate):''}${t.category?` · ${esc(t.category)}`:''}</div></div><div class="amount negative">${money(-Math.abs(Number(t.amount||0)),t.currency||currency)}</div></div>`).join('');
  const cycleLabel=s.period&&s.period!=='monthly'?`${esc(get('budgets.period_'+s.period)||s.period)} · `:'';
  const dlg=dialog(`<div class="dialog-card budget-detail">
    <div class="panel-head"><h2>${esc(s.name)}</h2><div class="panel-head-actions"><button type="button" class="ghost" data-edit>${esc(get('common.edit'))}</button><button data-close aria-label="${esc(get('common.close'))}">×</button></div></div>
    <div class="row-sub">${cycleLabel}${date(s.periodStart)}–${date(s.periodEnd)}</div>
    <div class="budget-detail-stats">
      <div class="kv"><span>${esc(get('budgets.spent'))}</span><strong class="amount">${money(s.spent,currency)}</strong></div>
      <div class="kv"><span>${esc(get('budgets.budget'))}</span><strong class="amount">${money(s.budgetAmount,currency)}</strong></div>
      <div class="kv"><span>${esc(get('budgets.remaining'))}</span><strong class="amount${Number(s.remaining)<0?' negative':''}">${money(s.remaining,currency)}</strong></div>
    </div>
    <div class="progress ${barStatus}"><span data-w="${clamped}"></span><span class="forecast" data-w="${forecastPct}"></span></div>
    <div class="budget-detail-trend"><span class="budget-status ${barStatus}">${esc(Math.round(pct))}%</span><span>${esc(get(trendKey))}</span></div>
    ${forecastLine}
    <div class="row-group">${esc(get('budgets.contributing'))}</div>
    <div class="budget-detail-rows">${rows||`<div class="row state-empty"><div class="row-sub">${esc(get('common.empty'))}</div></div>`}</div>
  </div>`);
  dlg.querySelectorAll('.progress > span[data-w]').forEach(s=>{s.style.width=s.dataset.w+'%'});
  dlg.querySelector('[data-close]').addEventListener('click',()=>dlg.close());
  dlg.querySelector('[data-edit]').addEventListener('click',()=>openBudgetEdit(s.budgetId,()=>dlg.close()));
  dlg.showModal();
}
function renderRows(el,rows,map){el.innerHTML='';for(const x of rows||[]){const [title,sub,value]=map(x);el.insertAdjacentHTML('beforeend',`<div class="row"><div class="row-main"><div class="row-title">${esc(title)}</div><div class="row-sub">${esc(sub)}</div></div><div class="amount">${esc(value)}</div></div>`)}if(!(rows||[]).length)empty(el)}


const ACCT_TRASH='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m2 0v12a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V7"/></svg>';
const ACCT_EDIT='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 20h4L18 10l-4-4L4 16v4Z"/><path d="M13.5 6.5 17.5 10.5"/></svg>';
const ACCT_FOLDER='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 6h6l2 2h8v10H4Z"/></svg>';
function accountRow(x,groups){
  const isManual=x.provider==='manual'&&!x.bankConnectionId;
  const kind=[x.product||x.accountType,isManual?get('accounts.manual'):null].filter(Boolean).join(' · ');
  const nativeAmt=x.latestBalance?money(x.latestBalance.amount,x.latestBalance.currency):'—';
  const convertedAmt=x.baseValue!=null?`<div class="amount-converted">${converted(x.baseValue,x.baseCurrency)}</div>`:'';
  const row=document.createElement('div');row.className='row';
  // The "move to group" affordance only appears once at least one group exists (otherwise the dialog
  // would be a dead end offering only "Ungrouped").
  const moveBtn=(groups||[]).length?`<button type="button" class="icon-button" data-move title="${esc(get('accounts.moveToGroup'))}" aria-label="${esc(get('accounts.moveToGroup'))}">${ACCT_FOLDER}</button>`:'';
  const renameBtn=`<button type="button" class="icon-button" data-rename-account title="${esc(get('common.edit'))}: ${esc(get('accounts.name'))}" aria-label="${esc(get('common.edit'))}: ${esc(get('accounts.name'))}">${ACCT_EDIT}</button>`;
  const balanceBtn=isManual?`<button type="button" class="icon-button" data-edit-balance title="${esc(get('accounts.updateBalance'))}" aria-label="${esc(get('accounts.updateBalance'))}">±</button>`:'';
  const deleteBtn=isManual?`<button type="button" class="icon-button" data-delete title="${esc(get('accounts.delete'))}" aria-label="${esc(get('accounts.delete'))}">${ACCT_TRASH}</button>`:'';
  row.innerHTML=`<div class="row-main"><div class="row-title">${esc(x.displayName||x.institutionName)}</div><div class="row-sub">${esc(x.institutionName)}${kind?` · ${esc(kind)}`:''}${acctId(x.ibanLast4)}</div></div><div class="row-end"><div class="amount-stack"><div class="amount">${nativeAmt}</div>${convertedAmt}</div>${moveBtn}${renameBtn}${balanceBtn}${deleteBtn}</div>`;
  row.querySelector('[data-move]')?.addEventListener('click',()=>openMoveToGroupDialog(x,groups));
  row.querySelector('[data-rename-account]')?.addEventListener('click',()=>openAccountNameDialog(x));
  row.querySelector('[data-edit-balance]')?.addEventListener('click',()=>openBalanceDialog(x));
  row.querySelector('[data-delete]')?.addEventListener('click',()=>deleteAccount(x));
  // Drill-down (UX rework §3): the account row itself opens that account's bookings; management
  // controls (edit/move/delete/balance) keep their own click and are excluded here.
  row.dataset.accountId=x.id;row.classList.add('is-drillable');row.setAttribute('role','button');row.tabIndex=0;
  const drill=e=>{if(e.target.closest('button,a,input,select'))return;showView('transactions',{query:'accountId='+encodeURIComponent(x.id)})};
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
  // Group subtotal in the base currency: use the converted baseValue for foreign accounts, the native
  // amount for base-currency accounts, and EXCLUDE a foreign account with no FX rate (baseValue null)
  // rather than adding its foreign figure into a base-currency total.
  const total=accts=>accts.reduce((s,a)=>a.baseValue!=null?s+Number(a.baseValue):(a.latestBalance&&a.latestBalance.currency===baseCur?s+Number(a.latestBalance.amount):s),0);
  // Group header (g=null → the "Ungrouped" bucket). Collapse state persists in localStorage.
  const renderBucket=(g,accts)=>{
    const gid=g?g.id:'';const isCollapsed=collapsed.has(gid);
    const head=document.createElement('div');head.className='row group-head';head.dataset.groupId=gid;
    // The chevron only expands/collapses; the name is a separate drill-down that opens all bookings of
    // the group's accounts (UX rework §3). The name keeps class `group-toggle` for accounts-ux decoration.
    const toggle=()=>{collapsed.has(gid)?collapsed.delete(gid):collapsed.add(gid);localStorage.setItem('finance.groupsCollapsed',JSON.stringify([...collapsed]));loadAccountsView();};
    head.innerHTML=`<div class="row-main"><button type="button" class="group-chevron" data-toggle aria-label="${esc(get(isCollapsed?'nav.expand':'nav.collapse'))}">${isCollapsed?'▸':'▾'}</button><button type="button" class="group-toggle${g?' is-drillable':''}" data-group-open>${esc(g?g.name:get('accounts.ungrouped'))}</button></div><div class="row-side"><span class="amount">${money(total(accts),baseCur)}</span>${g?`<button type="button" class="icon-button" data-rename aria-label="${esc(get('accounts.renameGroup'))}" title="${esc(get('accounts.renameGroup'))}">${ACCT_EDIT}</button><button type="button" class="icon-button" data-delgroup aria-label="${esc(get('accounts.deleteGroup'))}" title="${esc(get('accounts.deleteGroup'))}">${ACCT_TRASH}</button>`:''}</div>`;
    head.querySelector('[data-toggle]').addEventListener('click',toggle);
    head.querySelector('[data-group-open]').addEventListener('click',()=>{if(g)showView('transactions',{query:'groupId='+encodeURIComponent(g.id)});else toggle();});
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
    const expiry=Number.isFinite(x.daysUntilExpiry)&&x.daysUntilExpiry>=0&&health!=='expired'?` · ${get('accounts.expiresIn').replace('{days}',x.daysUntilExpiry)}`:'';
    const row=document.createElement('div');row.className='row';
    row.innerHTML=`<div class="row-main"><div class="row-title">${esc(x.institutionName)}</div><div class="row-sub">${esc(get('accounts.validUntil'))}: ${date(x.validUntil)} · ${esc(get('accounts.lastSync'))}: ${date(x.lastSyncedAt)}${esc(expiry)}</div></div><div class="row-side"><div class="amount${warn?' negative':''}">${esc(label)}</div>${warn?`<button type="button" class="ghost" data-reconnect>${esc(get('accounts.reconnect'))}</button>`:`<button type="button" class="icon-button" data-sync title="${esc(get('accounts.syncNow'))}" aria-label="${esc(get('accounts.syncNow'))}">⟳</button>`}<button type="button" class="ghost danger" data-disconnect>${esc(get('accounts.disconnect'))}</button></div>`;
    row.querySelector('[data-sync]')?.addEventListener('click',ev=>syncConnection(x.id,ev.currentTarget));
    row.querySelector('[data-reconnect]')?.addEventListener('click',ev=>reconnectConnection(x,ev.currentTarget));
    row.querySelector('[data-disconnect]').addEventListener('click',ev=>disconnectConnection(x,ev.currentTarget));
    conns.appendChild(row);
  }
  if(!(connections||[]).length)empty(conns);
}
async function syncConnection(id,button){
  if(button)button.disabled=true;
  try{
    const r=await bankApi(`api/banking/connections/${id}/sync?force=true`,{method:'POST'});
    const status=(r&&r.status)||'started';
    const messages={started:'accounts.syncStarted',completed:'accounts.syncCompleted',partial_history:'accounts.syncPartial',error:'accounts.syncError',already_running:'accounts.syncRunning',cooldown:'accounts.syncCooldown',reauthorization_required:'accounts.syncReauth'};
    toast(get(messages[status]||'accounts.syncStarted'));
    await loadCurrent();
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
  if(button)button.disabled=true;
  try{
    const status=await bankApi('api/banking/status');
    if(!bankingReady(status)){if(button)button.disabled=false;return openEnableBankingWizard(status)}
    const country=(connection.country||'DE').toUpperCase();
    const data=await bankApi(`api/banking/institutions?country=${encodeURIComponent(country)}&psuType=${encodeURIComponent(connection.psuType||'personal')}`);
    const bank=(data.aspsps||[]).find(x=>(x.name||'').toLowerCase()===(connection.institutionName||'').toLowerCase());
    if(button)button.disabled=false;
    if(bank)return openBankConnectionOptions(bank,connection.id,status.profile?.id||null);
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
      await api('api/accounts',jsonBody({fullWorthSpaceId:state.space.id,bankConnectionId:null,displayName:fd.get('name'),currency:fd.get('currency'),includeInNetWorth:true,sortOrder:0,institutionName:fd.get('institution')||null,initialBalance:fd.get('balance')===''?null:Number(fd.get('balance'))}));
      dlg.close();toast(get('accounts.created'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
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
async function openBudgetDialog(existing){
  const currency=existing?.currency||state.space?.baseCurrency||'EUR';
  let options;try{options=await categoryOptions(existing?.categoryId||undefined)}catch(err){toast(err.message||get('common.error'));return}
  const periods=['monthly','weekly','biweekly','paycycle'].map(p=>`<option value="${p}"${existing?.period===p?' selected':''}>${esc(get(`budgets.period_${p}`))}</option>`).join('');
  const dlg=dialog(`<form class="dialog-card"><h2>${esc(get(existing?'budgets.edit':'budgets.new'))}</h2><label>${esc(get('common.name'))}<input name="name" required maxlength="120" value="${esc(existing?.name||'')}"></label><label>${esc(get('transactions.amount'))}<input name="amount" type="number" step="0.01" inputmode="decimal" required value="${existing?esc(String(existing.amount)):''}"></label><label>${esc(get('purchases.currency'))}<input name="currency" value="${esc(currency)}" maxlength="3" required></label><label>${esc(get('budgets.period'))}<select name="period">${periods}</select></label><label>${esc(get('transactions.category'))}<select name="category"><option value="">${esc(get('common.all'))}</option>${options}</select></label><label class="check"><input name="carryOver" type="checkbox"${existing?.carryOver?' checked':''}>${esc(get('budgets.carryOver'))}</label><div class="dialog-actions">${existing?`<button type="button" class="ghost danger" data-delete>${esc(get('common.delete'))}</button>`:''}<button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get(existing?'common.save':'common.create'))}</button></div></form>`);
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('[data-delete]')?.addEventListener('click',async()=>{
    if(!await ctx.confirm(get('budgets.deleteConfirm').replace('{name}',()=>existing.name),{destructive:true,confirmLabel:get('common.delete')}))return;
    try{await api(`api/budgets/${existing.id}`,{method:'DELETE'});dlg.close();toast(get('common.deleted'));await loadBudgets()}catch(err){toast(err.message||get('common.error'))}
  });
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const fd=new FormData(e.currentTarget);
    const body=jsonBody({name:fd.get('name'),categoryId:fd.get('category')||null,amount:Number(fd.get('amount')),currency:fd.get('currency'),period:fd.get('period'),carryOver:fd.get('carryOver')==='on',isActive:true,startDate:null,endDate:null});
    try{await api(existing?`api/budgets/${existing.id}`:'api/budgets',existing?{...body,method:'PUT'}:body);dlg.close();toast(get('common.saved'));await loadBudgets()}catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}
async function openBudgetEdit(id,closeDrawer){
  let budget;try{budget=await api(`api/budgets/${id}`)}catch(err){toast(err.message||get('common.error'));return}
  closeDrawer?.();
  openBudgetDialog(budget);
}
async function openCategoryDialog(){
  let options;try{options=await categoryOptions()}catch(err){toast(err.message||get('common.error'));return}
  const dlg=dialog(`<form class="dialog-card"><h2>${esc(get('categories.new'))}</h2><label>${esc(get('common.name'))}<input name="name" required maxlength="120"></label><label>${esc(get('categories.icon'))}<input name="icon" maxlength="8" placeholder="🏷️"></label><label>${esc(get('categories.parent'))}<select name="parent"><option value="">${esc(get('categories.topLevel'))}</option>${options}</select></label><div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('common.create'))}</button></div></form>`);
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const fd=new FormData(e.currentTarget);
    const name=fd.get('name').trim();const key=name.toLowerCase().replace(/[^a-z0-9]+/g,'-').replace(/(^-|-$)/g,'')||`cat-${Date.now()}`;
    try{await api('api/categories',jsonBody({key,name,parentId:fd.get('parent')||null,icon:fd.get('icon')||null,sortOrder:null}));dlg.close();toast(get('common.saved'));await loadCategories()}catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}


async function loadSettings(){$('#language').value=state.lang;$('#theme').value=state.theme;$('#privacy-default').checked=privacyDefault();await Promise.all([renderSharing(ctx),renderEnableBankingSettings(),accessSetup.renderAiAccessSettings()])}
// Export the space's full data snapshot (§ data portability). The endpoint returns plain JSON, so we
// fetch the raw response as a blob and hand it to a download anchor — api() would parse it to an object,
// which cannot trigger a "save as file". withSpace() supplies the required fullWorthSpaceId.
async function downloadExport(){
  const btn=$('#export-data');if(btn)btn.disabled=true;
  try{
    const r=await fetch(`/bff/backend/${withSpace('api/export/snapshot')}`);
    if(!r.ok)await fail(r);
    const blob=await r.blob();const url=URL.createObjectURL(blob);
    const a=document.createElement('a');a.href=url;a.download=`finance-export-${new Date().toISOString().slice(0,10)}.json`;
    document.body.appendChild(a);a.click();a.remove();URL.revokeObjectURL(url);
    toast(get('export.done'));
  }catch(err){toast(err.message||get('common.error'))}
  finally{if(btn)btn.disabled=false}
}

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

  const poll=async()=>{
    if(closed)return;
    try{
      const current=await bankApi(`api/banking/provider-status?country=${encodeURIComponent(country)}`);
      if(current?.available){
        toast(get('bankingSetup.statusConnected'),5000);
        dlg.close();
        if(onConnected)await onConnected();
        return;
      }
    }catch{}
    pollTimer=setTimeout(poll,2000);
  };

  form.onsubmit=async e=>{
    e.preventDefault();
    const submit=form.querySelector('[type="submit"]'),email=String(new FormData(form).get('email')||'').trim();
    submit.disabled=true;
    try{
      await bankApi('api/banking/provider-status/connect/start',jsonBody({email}));
      form.innerHTML=`<div class="panel-head"><h2>${esc(get('bankingSetup.statusConnect'))}</h2><button type="button" data-close>×</button></div>
        <p>${esc(get('bankingSetup.statusEmailSent'))}</p>
        <p class="row-sub">${esc(get('bankingSetup.waitingForEmail'))}</p>
        <div class="dialog-actions"><button type="button" data-close-bottom>${esc(get('common.close'))}</button></div>`;
      form.querySelector('[data-close]').onclick=()=>dlg.close();
      form.querySelector('[data-close-bottom]').onclick=()=>dlg.close();
      pollTimer=setTimeout(poll,1000);
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

function openBankConnectionOptions(bank,reconnectConnectionId=null,profileId=null){
  const psuTypes=Array.isArray(bank.psu_types)&&bank.psu_types.filter(Boolean).length
    ? bank.psu_types.filter(Boolean)
    : ['personal','business'];
  const health=bank.providerStatusSeverity;
  const healthWarning=health&&health!=='ok'
    ? `<div class="bank-status-warning ${esc(health)}"><strong>${esc(providerStatusLabel(health))}</strong><span>${esc(get('bankingSetup.statusWarning'))}</span><a href="${ENABLE_BANKING_STATUS}" target="_blank" rel="noopener">${esc(get('bankingSetup.statusPage'))} ↗</a></div>`
    : '';
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(bank.name)}</h2><button type="button" data-close>×</button></div>
    ${healthWarning}
    ${psuTypes.length>1?`<label>${esc(get('bankingSetup.accountType'))}<select name="psuType">${psuTypes.map(x=>`<option value="${esc(x)}">${esc(get('bankingSetup.psu_'+x)||x)}</option>`).join('')}</select></label>`:`<input type="hidden" name="psuType" value="${esc(psuTypes[0]||'personal')}">`}
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
    const psuType=form.elements.psuType?.value||psuTypes[0]||'personal';
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
      providerStatusState=null;statusConnect.hidden=true;statusState.textContent=get('bankingSetup.notConfigured');banks=ing;draw(search.value);return;
    }
    try{
      const [data,providerStatus]=await Promise.all([
        bankApi(`api/banking/institutions?country=${encodeURIComponent(country)}`),
        bankApi(`api/banking/provider-status?country=${encodeURIComponent(country)}`).catch(()=>null)
      ]);
      providerStatusState=providerStatus;
      const canConnectStatus=providerStatus&&
        (providerStatus.reason==='control_panel_access_unavailable'||providerStatus.reason==='control_panel_login_expired');
      statusConnect.hidden=!canConnectStatus;
      statusState.textContent=providerStatus?.available
        ?get('bankingSetup.statusActive')
        :(canConnectStatus?get('bankingSetup.statusConnectShort'):get('bankingSetup.statusUnavailable'));
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

if(localStorage.getItem('finance.navCollapsed')==='1')document.body.classList.add('nav-collapsed');
boot();