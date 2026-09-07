import { money, setMoneyLocale } from './ui/money.js';
import { isPrivate, togglePrivacy, onPrivacyChange, setPrivacyDefault } from './ui/privacy.js';
import { confirmDialog } from './ui/confirm.js';
import { initLock } from './ui/lock.js';
import { renderDashboard, bindDashboard, toggleDashboardEdit, invalidateLayout } from './ui/dashboard.js';
import { renderTransactions, bindTransactions } from './features/transactions.js';
import { renderCategories, bindCategories, newCategory } from './features/categories.js';
import { renderRules, bindRules, newRule } from './features/rules.js';
import { renderContracts, bindContracts, newContract } from './features/contracts.js';
import { renderNetWorth, bindNetWorth, newAsset } from './features/networth.js';
import { renderNotifications } from './features/notifications.js';
import { renderLoans, bindLoans } from './features/loans.js';
import { renderAnalytics, bindAnalytics } from './features/analytics.js';
import { renderPurchases, bindPurchases } from './features/purchases.js';
import { renderTax, bindTax } from './features/tax.js';
import { renderMerchants, bindMerchants, newMerchant } from './features/merchants.js';
import { renderAudit, bindAudit } from './features/audit.js';
import { renderSharing, bindSharing } from './features/sharing.js';
import { bindSettings, renderSettings } from './features/settings.js';
import { createAccessSetup } from './features/access-setup.js';
import { renderBudgets, newBudget, openBudgetDetail } from './features/budgets.js';
import {
  bindAccounts,
  newAccount,
  openEnableBankingWizard,
  refreshAccountPresentation,
  renderAccounts,
  renderEnableBankingSettings
} from './features/accounts.js';
import { downloadWealthBackup } from './features/wealth-portability.js';
import { createDialog } from './ui/dialog.js';
import { apiClient, api, bankApi, i18n, jsonBody } from './core/services.js';
import { state } from './core/state.js';
import { createRouter } from './core/router.js';
import { createFeatureRegistry } from './core/feature-registry.js';
import { createToast } from './ui/toast.js';
import { openGlobalSearch } from './ui/global-search.js';
import { bindCompensationNavigation, compensationMoreButton } from './ui/compensation-navigation.js';
import { createLayoutShell } from './ui/layout-shell.js';

// GET de-duplication and mutation invalidation are owned by core/api.js.
const get=path=>i18n.get(path);
// Mobile bottom nav shows exactly these four + "More" (UX rework §2): Übersicht, Verträge, Analysen,
// Vermögen. Transactions is reached by tapping an account/group or the "Alle Buchungen" row (never a
// permanent slot); everything else lives in More.
const MOBILE_PRIMARY=['dashboard','contracts','analytics','networth'];
const ALL_VIEWS=['dashboard','transactions','accounts','budgets','contracts','networth','analytics','purchases','tax','categories','rules','notifications','merchants','audit','settings'];
const MORE_VIEWS=ALL_VIEWS.filter(v=>!MOBILE_PRIMARY.includes(v));
// §3: every screen has a real URL so reload/back/forward/deep-links work (the view is no longer
// only client state). dashboard is the root; the server's MapFallbackToFile serves index.html for
// any of these paths and the app resolves the view from location.pathname on boot.
const router=createRouter({views:ALL_VIEWS,defaultView:'dashboard'});
const pathForView=router.pathForView;
const viewFromPath=router.viewFromPath;
// Contextual primary action per section (UI_UX_SPEC §3.1 header). Maps to the same handler as the
// in-page add control so there is a single code path.
const PRIMARY_ACTION={dashboard:['dashboard.edit',()=>toggleDashboardEdit(ctx)],budgets:['budgets.new',()=>newBudget(ctx)],contracts:['contracts.new',()=>newContract(ctx)],rules:['rules.new',()=>newRule(ctx)],categories:['categories.new',()=>newCategory(ctx)],accounts:['accounts.add',()=>newAccount(ctx)],networth:['networth.newAsset',()=>newAsset(ctx)],merchants:['merchants.new',()=>newMerchant(ctx)]};
const media=matchMedia('(prefers-color-scheme: dark)');
const $=s=>document.querySelector(s);const $$=s=>[...document.querySelectorAll(s)];
const toastController=createToast($('#toast'));
const toast=(text,duration)=>toastController.show(text,duration);
const {initResizableSidebar,resetLayout,syncNavToggle,syncResponsiveSidebar,toggleSidebar}=createLayoutShell({get,toast});

async function boot(){
  setMoneyLocale(state.lang);
  $('#theme').value=state.theme;$('#language').value=state.lang;applyTheme();
  await loadMessages();await loadCapabilities();bind();syncAdminVisibility();syncPrivacyToggle();syncNavToggle();
  const startView=handleConnectRedirect()||viewFromPath(location.pathname);
  try{await loadSpaces()}catch(e){console.error(e);toast(get('common.error'))}
  await showView(startView,{replace:true});
  // Inactivity lock: covers the app after 10 min idle; unlock re-loads the current screen.
  initLock(ctx,{onUnlock:loadCurrent});
  await accessSetup.maybeOpenRegistrationOnboarding();
}
async function loadCapabilities(){
  try{
    const response=await fetch('/auth/capabilities',{cache:'no-store'});
    if(response.ok)state.capabilities=await response.json();
  }catch{}
}
function syncAdminVisibility(){
  const show=Boolean(state.capabilities?.admin);
  $('#admin-nav')?.toggleAttribute('hidden',!show);
  $('#admin-settings-link')?.toggleAttribute('hidden',!show);
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
async function loadMessages(){await i18n.load(state.lang);renderTranslations();renderPageHeader()}
function renderTranslations(){i18n.apply(document);const lr=$('#layout-reset');if(lr){lr.querySelector('span').textContent=state.lang==='de'?'Layout zurücksetzen':'Reset layout';lr.querySelector('small').textContent=state.lang==='de'?'Seitenleisten, Breiten und Panel-Zustand':'Sidebars, widths and panel state'};
  // Collapsed sidebar shows icons only — carry each nav label as a tooltip + accessible name.
  $$('.sidebar button[data-view], #bottom-nav button[data-view]').forEach(b=>{const t=b.querySelector('span')?.textContent||'';if(t){b.title=t;b.setAttribute('aria-label',t)}})}
function renderPageHeader(){
  const p=state.messages.pages?.[state.view];
  if(p){$('#page-title').textContent=p.title;$('#page-subtitle').textContent=p.subtitle}
  const action=PRIMARY_ACTION[state.view];const btn=$('#primary-action');
  if(action){btn.hidden=false;btn.textContent=get(action[0]);btn.onclick=action[1]}else{btn.hidden=true;btn.onclick=null}
}
function applyTheme(){const actual=state.theme==='system'?(media.matches?'dark':'light'):state.theme;document.documentElement.dataset.theme=actual;const meta=document.querySelector('meta[name="theme-color"]');if(meta)meta.setAttribute('content',actual==='dark'?'#121416':'#f5f6f7');updateThemeToggle()}
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
  bindCompensationNavigation();
  $('#nav-collapse').addEventListener('click',toggleSidebar);
  $('#privacy-toggle').addEventListener('click',()=>togglePrivacy());
  $('#global-search').addEventListener('click',()=>openGlobalSearch(ctx));
  $$('[data-view-jump]').forEach(b=>b.addEventListener('click',()=>showView(b.dataset.viewJump)));
  $('#refresh').addEventListener('click',loadCurrent);
  bindTransactions(ctx);
  bindAccounts(ctx);
  $('[data-action="new-budget"]').addEventListener('click',()=>newBudget(ctx));
  bindContracts(ctx);
  bindNetWorth(ctx);
  bindLoans(ctx);
  bindAnalytics(ctx);
  $('[data-action="new-category"]').addEventListener('click',()=>newCategory(ctx));
  bindCategories(ctx);
  bindRules(ctx);
  bindPurchases(ctx);
  bindTax(ctx);
  bindMerchants(ctx);
  bindAudit(ctx);
  bindSharing(ctx);
  bindSettings(ctx,{accessSetup,renderEnableBankingSettings});
  $('#export-data')?.addEventListener('click',event=>downloadWealthBackup(ctx,event.currentTarget));
  bindDashboard(ctx);
  $('#privacy-default').addEventListener('change',e=>setPrivacyDefault(e.target.checked));
  $('#layout-reset')?.addEventListener('click',resetLayout);
  // Re-render on privacy change so every value on the current screen re-masks via the shared path.
  onPrivacyChange(()=>{syncPrivacyToggle();loadCurrent()});
  // Desktop keyboard shortcut: "/" opens global search unless typing in a field (§19).
  document.addEventListener('keydown',e=>{if(e.key==='/'&&!/^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)&&!e.target.isContentEditable){e.preventDefault();openSearch()}});
}
function syncPrivacyToggle(){const b=$('#privacy-toggle');b.setAttribute('aria-pressed',String(isPrivate()));b.classList.toggle('active',isPrivate());$('#privacy-default').checked=privacyDefault()}
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
  renderPageHeader();
  window.dispatchEvent(new CustomEvent('fullworth:view-change',{detail:{view,path:location.pathname+location.search}}));
  await loadCurrent();
}
async function loadCurrent(){
  try{
    if(!state.space){await loadSpaces();if(!state.space){toast(get('common.error'));return}}
    await featureRegistry.refresh(state.view,ctx);
    await refreshAccountPresentation(ctx);
  }catch(e){console.error(e);toast(get('common.error'))}
}
function date(value){if(!value)return'—';return new Intl.DateTimeFormat(state.lang==='de'?'de-DE':'en-US').format(new Date(`${String(value).slice(0,10)}T12:00:00`))}
function dateTime(value){if(!value)return'—';const raw=String(value);if(!/[T ]\d{2}:\d{2}/.test(raw))return date(value);const parsed=new Date(raw);if(Number.isNaN(parsed.getTime()))return date(value);return new Intl.DateTimeFormat(state.lang==='de'?'de-DE':'en-US',{dateStyle:'medium',timeStyle:'medium'}).format(parsed)}
function empty(el,message){el.innerHTML=`<div class="row state-empty"><div class="row-sub">${esc(message||get('common.empty'))}</div></div>`}
function skeleton(el,rows=4){el.innerHTML=Array.from({length:rows},()=>`<div class="row skel"><div class="skel-bar"></div><div class="skel-bar short"></div></div>`).join('')}
function esc(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
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
    const label=view==='transactions'
      ? get('transactions.allTx')
      : (nav===`nav.${view}`?(state.messages.pages?.[view]?.title||view):nav);
    return `<button type="button" data-go="${view}" class="${state.view===view?'active':''}">${icon}<span>${esc(label)}</span></button>`;
  }).join('') + compensationMoreButton(esc(get('nav.compensation')));
  const dlg=dialog(`<form method="dialog" class="dialog-card more-sheet"><div class="panel-head"><h2>${esc(get('nav.more'))}</h2><button value="cancel" data-close>×</button></div><div class="more-list">${items}</div></form>`,{mobileMode:'sheet'});
  dlg.classList.add('more-sheet-dialog');
  dlg.querySelectorAll('[data-go]').forEach(b=>b.addEventListener('click',()=>{dlg.close();showView(b.dataset.go)}));
  bindCompensationNavigation(dlg);
  dlg.showModal();
}

// Global search (§19): groups results from existing scoped endpoints; never touches provider payloads.
// Shared context handed to UI modules (dashboard widgets, transactions detail, …) so they reuse the
// app's single api()/formatting/dialog path instead of duplicating it.
const ctx={$,$,api,bankApi,get,esc,date,dateTime,toast,dialog,money,isPrivate,categoryOptions,jsonBody,empty,skeleton,reload:loadCurrent,confirm:(message,opts)=>confirmDialog(ctx,message,opts),bffUrl:path=>apiClient.backendUrl(path),
  // Drill-down helper (UX rework §3): open a view with a URL scope, e.g. navScope('transactions','accountId='+id).
  navScope:(view,query)=>showView(view,{query:query||''}),showView:(view,opts)=>showView(view,opts)};
const accessSetup=createAccessSetup(ctx,(status,options)=>openEnableBankingWizard(status,options));
const featureRegistry=createFeatureRegistry()
  .register('dashboard',()=>loadDashboard())
  .register('transactions',()=>renderTransactions(ctx))
  .register('accounts',()=>renderAccounts(ctx))
  .register('budgets',()=>renderBudgets(ctx))
  .register('contracts',()=>renderContracts(ctx))
  .register('networth',async()=>{await renderNetWorth(ctx);await renderLoans(ctx)})
  .register('analytics',()=>renderAnalytics(ctx))
  .register('purchases',()=>renderPurchases(ctx))
  .register('tax',()=>renderTax(ctx))
  .register('categories',()=>renderCategories(ctx))
  .register('rules',()=>renderRules(ctx))
  .register('notifications',()=>renderNotifications(ctx))
  .register('merchants',()=>renderMerchants(ctx))
  .register('audit',()=>renderAudit(ctx))
  .register('settings',()=>renderSettings(ctx));
// Feature modules loaded as separate <script type="module"> (accounts-ux.js, dashboard widgets) can't
// import app.js internals; expose only the safe scoped-navigation entry point for account/group drill-down.
window.fwNavScope=(view,query)=>showView(view,{query:query||''});
window.fwOpenBudget=id=>openBudgetDetail(ctx,id);
async function loadDashboard(){await renderDashboard(ctx)}


if(localStorage.getItem('finance.navCollapsed')==='1')document.body.classList.add('nav-collapsed');
initResizableSidebar();
syncResponsiveSidebar();
boot();