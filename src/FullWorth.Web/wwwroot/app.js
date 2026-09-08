import { money, setMoneyLocale } from './ui/money.js';
import { isPrivate, togglePrivacy, onPrivacyChange, privacyDefault } from './ui/privacy.js';
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
import { renderDashboardInsights, mountInsights } from './features/insights.js';

import { createAccessSetup } from './features/access-setup.js';
import { bindAccounts, renderAccounts, openAddAccount, openBankingSetup, renderBankingSettings } from './features/accounts.js';
import { bindSettings, renderSettings } from './features/settings.js';
import { renderBudgets, newBudget, openBudgetDetail } from './features/budgets.js';

import { createDialog } from './ui/dialog.js';
import { apiClient, api, bankApi, i18n, jsonBody } from './core/services.js';
import { state } from './core/state.js';
import { createRouter } from './core/router.js';
import { createFeatureRegistry } from './core/feature-registry.js';
import { installNavigation, navigate } from './core/navigation.js';
import { emitAppEvent, onAppEvent } from './core/event-bus.js';
import { createToast } from './ui/toast.js';
import { openGlobalSearch } from './ui/global-search.js';

// GET de-duplication and mutation invalidation are owned by core/api.js.
const get=path=>i18n.get(path);
// Mobile bottom nav shows exactly these four + "More" (UX rework §2): Übersicht, Verträge, Analysen,
// Vermögen. Transactions is reached by tapping an account/group or the "Alle Buchungen" row (never a
// permanent slot); everything else lives in More.
const MOBILE_PRIMARY=['dashboard','contracts','analytics','networth'];
const ALL_VIEWS=['dashboard','insights','transactions','accounts','budgets','contracts','networth','analytics','purchases','tax','categories','rules','notifications','merchants','audit','settings'];
const MORE_VIEWS=ALL_VIEWS.filter(v=>!MOBILE_PRIMARY.includes(v)&&v!=='insights');
// §3: every screen has a real URL so reload/back/forward/deep-links work (the view is no longer
// only client state). dashboard is the root; the server's MapFallbackToFile serves index.html for
// any of these paths and the app resolves the view from location.pathname on boot.
const router=createRouter({views:ALL_VIEWS,defaultView:'dashboard'});
const pathForView=router.pathForView;
const viewFromPath=router.viewFromPath;
// Contextual primary action per section (UI_UX_SPEC §3.1 header). Maps to the same handler as the
// in-page add control so there is a single code path.
const PRIMARY_ACTION={dashboard:['dashboard.edit',()=>toggleDashboardEdit(ctx)],budgets:['budgets.new',()=>newBudget(ctx)],contracts:['contracts.new',()=>newContract(ctx)],rules:['rules.new',()=>newRule(ctx)],categories:['categories.new',()=>newCategory(ctx)],accounts:['accounts.add',()=>openAddAccount(ctx)],networth:['networth.newAsset',()=>newAsset(ctx)],merchants:['merchants.new',()=>newMerchant(ctx)]};
const media=matchMedia('(prefers-color-scheme: dark)');
const $=s=>document.querySelector(s);const $$=s=>[...document.querySelectorAll(s)];
const toastController=createToast($('#toast'));
const toast=(text,duration)=>toastController.show(text,duration);

async function boot(){
  setMoneyLocale(state.lang);
  $('#theme').value=state.theme;$('#language').value=state.lang;applyTheme();
  await loadMessages();await loadCapabilities();bind();syncAdminVisibility();syncPrivacyToggle();syncNavToggle();
  const startView=handleConnectRedirect()||viewFromPath(location.pathname);
  try{await loadSpaces()}catch(e){console.error(e);toast(get('common.error'))}
  const canonicalStart=pathForView(startView);
  const startPath=location.pathname===canonicalStart||location.pathname.startsWith(canonicalStart.replace(/\/$/,'')+'/')?location.pathname:undefined;
  await showView(startView,{replace:true,path:startPath});
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
  router.write(viewFromPath(location.pathname),{replace:true,state:null,path:location.pathname});
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
  window.addEventListener('popstate',()=>showView(viewFromPath(location.pathname),{fromHistory:true,path:location.pathname}));
  $('#bottom-more').addEventListener('click',openMoreSheet);
  $('#admin-nav')?.addEventListener('click',()=>location.assign('/admin'));
  $('[data-compensation-link]')?.addEventListener('click',()=>location.assign('/compensation.html'));
  $('#nav-collapse').addEventListener('click',toggleSidebar);
  $('#privacy-toggle').addEventListener('click',()=>togglePrivacy());
  $('#global-search').addEventListener('click',()=>openGlobalSearch(ctx));
  $$('[data-view-jump]').forEach(b=>b.addEventListener('click',()=>showView(b.dataset.viewJump)));
  $('#refresh').addEventListener('click',loadCurrent);
  bindTransactions(ctx);
  bindAccounts(ctx);
  bindSettings(ctx);
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
  bindDashboard(ctx);
  $('#layout-reset')?.addEventListener('click',resetLayout);
  // Re-render on privacy change so every value on the current screen re-masks via the shared path.
  onPrivacyChange(()=>{syncPrivacyToggle();loadCurrent()});
  // Desktop keyboard shortcut: "/" opens global search unless typing in a field (§19).
  document.addEventListener('keydown',e=>{if(e.key==='/'&&!/^(INPUT|TEXTAREA|SELECT)$/.test(e.target.tagName)&&!e.target.isContentEditable){e.preventDefault();openSearch()}});
}
function syncPrivacyToggle(){const b=$('#privacy-toggle');b.setAttribute('aria-pressed',String(isPrivate()));b.classList.toggle('active',isPrivate());$('#privacy-default').checked=privacyDefault()}
function toggleSidebar(){
  const collapsed=!document.body.classList.contains('nav-collapsed');
  document.body.classList.toggle('nav-collapsed',collapsed);
  localStorage.setItem('finance.navCollapsed',collapsed?'1':'0');
  if(collapsed)document.body.classList.remove('nav-auto-collapsed');
  syncResponsiveSidebar();
}
function sidebarEffectivelyCollapsed(){return document.body.classList.contains('nav-collapsed')||document.body.classList.contains('nav-auto-collapsed')}
// Point the chevron the way it will move (‹ collapses, › expands) and label it for its next action.
function syncNavToggle(){
  const b=$('#nav-collapse');if(!b)return;
  const manual=document.body.classList.contains('nav-collapsed');
  const autoOnly=document.body.classList.contains('nav-auto-collapsed')&&!manual;
  const collapsed=manual||autoOnly;
  b.textContent=collapsed?'›':'‹';
  b.disabled=autoOnly;
  const label=autoOnly
    ? (state.lang==='de'?'Navigation wegen Platz automatisch eingeklappt':'Navigation automatically collapsed for available space')
    : get(collapsed?'nav.expand':'nav.collapse');
  b.setAttribute('aria-label',label);b.title=label;
}
function syncResponsiveSidebar(){
  const desktop=window.matchMedia('(min-width:768px)').matches;
  const manual=document.body.classList.contains('nav-collapsed');
  if(!desktop||manual){
    const changed=document.body.classList.contains('nav-auto-collapsed');
    document.body.classList.remove('nav-auto-collapsed');
    syncNavToggle();
    if(changed)queueMicrotask(()=>emitAppEvent('layout:clamp-coach'));
    return;
  }
  const desiredSidebar=Math.max(176,Number(localStorage.getItem(sidebarWidthKey()))||Number(localStorage.getItem('finance.sidebar.width'))||228);
  const coachKey=`finance.coach.dockWidth.${layoutWidthMode()}`;
  const coach=document.body.classList.contains('coach-dock-open')?($('#coach-dock')?.getBoundingClientRect().width||Number(localStorage.getItem(coachKey))||Number(localStorage.getItem('finance.coach.dockWidth'))||0):0;
  const minMain=window.innerWidth<1100?420:520;
  const shouldCollapse=window.innerWidth-desiredSidebar-coach<minMain;
  const changed=document.body.classList.contains('nav-auto-collapsed')!==shouldCollapse;
  document.body.classList.toggle('nav-auto-collapsed',shouldCollapse);
  syncNavToggle();
  if(changed)queueMicrotask(()=>emitAppEvent('layout:clamp-coach'));
}
onAppEvent('layout:sync-sidebar',syncResponsiveSidebar);

function layoutWidthMode(){return window.innerWidth>=1024?'desktop':'tablet'}
function sidebarWidthKey(){return `finance.sidebar.width.${layoutWidthMode()}`}
function resetLayout(){
  ['finance.sidebar.width','finance.sidebar.width.desktop','finance.sidebar.width.tablet','finance.coach.dockWidth','finance.coach.dockWidth.desktop','finance.coach.dockWidth.tablet'].forEach(key=>localStorage.removeItem(key));
  localStorage.setItem('finance.navCollapsed','0');
  document.body.classList.remove('nav-collapsed','nav-auto-collapsed');
  document.documentElement.style.removeProperty('--sidebar-w');
  document.documentElement.style.removeProperty('--coach-dock-w');
  window.dispatchEvent(new CustomEvent('fullworth:layout-reset'));
  window.dispatchEvent(new Event('resize'));
  syncResponsiveSidebar();
  toast(state.lang==='de'?'Layout zurückgesetzt':'Layout reset');
}

function initResizableSidebar(){
  const sidebar=$('.sidebar');
  if(!sidebar)return;
  const minWidth=176;
  const defaults={desktop:228,tablet:196};
  const desktopMode=()=>window.matchMedia('(min-width:768px)').matches;
  const key=()=>sidebarWidthKey();
  const defaultWidth=()=>defaults[layoutWidthMode()];
  const savedWidth=()=>{
    const scoped=Number(localStorage.getItem(key()));
    if(scoped>0)return scoped;
    const legacy=Number(localStorage.getItem('finance.sidebar.width'));
    return legacy>0?legacy:defaultWidth();
  };
  const maxWidth=()=>{
    const coach=document.body.classList.contains('coach-dock-open')?$('#coach-dock')?.getBoundingClientRect().width||0:0;
    const minMain=window.innerWidth<1100?280:420;
    return Math.max(minWidth,Math.min(360,window.innerWidth-coach-minMain));
  };
  const apply=value=>{
    if(!desktopMode())return;
    const width=Math.max(minWidth,Math.min(maxWidth(),Math.round(Number(value)||savedWidth())));
    document.documentElement.style.setProperty('--sidebar-w',`${width}px`);
    handle.setAttribute('aria-valuemax',String(maxWidth()));
    handle.setAttribute('aria-valuenow',String(width));
    window.dispatchEvent(new CustomEvent('fullworth:sidebar-resize',{detail:{width}}));
    syncResponsiveSidebar();
    return width;
  };
  const save=width=>{if(width)localStorage.setItem(key(),String(width))};
  const handle=document.createElement('div');
  handle.className='sidebar-resizer';
  handle.setAttribute('role','separator');
  handle.setAttribute('aria-orientation','vertical');
  handle.setAttribute('aria-label','Navigation width');
  handle.setAttribute('aria-valuemin',String(minWidth));
  handle.tabIndex=0;
  sidebar.appendChild(handle);
  apply(savedWidth());

  let pointerId=null;
  handle.addEventListener('pointerdown',event=>{
    if(!desktopMode()||sidebarEffectivelyCollapsed())return;
    pointerId=event.pointerId;handle.setPointerCapture(pointerId);
    handle.classList.add('is-dragging');document.body.classList.add('sidebar-resizing');event.preventDefault();
  });
  handle.addEventListener('pointermove',event=>{if(pointerId===event.pointerId)apply(event.clientX)});
  const finish=event=>{
    if(pointerId===null||event.pointerId!==pointerId)return;
    pointerId=null;handle.classList.remove('is-dragging');document.body.classList.remove('sidebar-resizing');
    save(apply(sidebar.getBoundingClientRect().width));
  };
  handle.addEventListener('pointerup',finish);
  handle.addEventListener('pointercancel',finish);
  handle.addEventListener('dblclick',()=>save(apply(defaultWidth())));
  handle.addEventListener('keydown',event=>{
    if(!desktopMode()||sidebarEffectivelyCollapsed()||!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
    event.preventDefault();
    const current=sidebar.getBoundingClientRect().width;
    const step=event.shiftKey?40:10;
    const next=event.key==='Home'?minWidth:event.key==='End'?maxWidth():current+(event.key==='ArrowRight'?step:-step);
    save(apply(next));
  });
  window.addEventListener('resize',()=>{if(!desktopMode()){syncResponsiveSidebar();return}apply(savedWidth());syncResponsiveSidebar()});
  window.addEventListener('fullworth:coach-resize',syncResponsiveSidebar);
  window.addEventListener('fullworth:layout-reset',()=>apply(defaultWidth()));
  onAppEvent('layout:clamp-sidebar',()=>apply(savedWidth()));
}
async function showView(view,opts={}){
  state.view=view;
  const base=opts.path||pathForView(view);
  const sameBase=location.pathname===base;
  const query=opts.query!==undefined?String(opts.query):(sameBase?location.search.replace(/^\?/,''):'');
  const target=query?`${base}?${query}`:base;
  if(!opts.fromHistory){
    router.write(view,{query,replace:!!opts.replace||location.pathname+location.search===target,state:{view},path:base});
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
    await featureRegistry.activate(state.view,ctx);
    emitAppEvent('surface:rendered',{view:state.view,path:location.pathname+location.search});
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
  }).join('');
  const compensation=`<button type="button" data-compensation-more><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 18V8m5 10V5m5 13v-7m5 7V9"/><path d="M3 21h18"/></svg><span>Gehalt &amp; Benefits</span></button>`;
  const dlg=dialog(`<form method="dialog" class="dialog-card more-sheet"><div class="panel-head"><h2>${esc(get('nav.more'))}</h2><button value="cancel" data-close>×</button></div><div class="more-list">${items}${compensation}</div></form>`,{mobileMode:'sheet'});
  dlg.classList.add('more-sheet-dialog');
  dlg.querySelectorAll('[data-go]').forEach(b=>b.addEventListener('click',()=>{dlg.close();showView(b.dataset.go)}));
  dlg.querySelector('[data-compensation-more]')?.addEventListener('click',()=>{dlg.close();location.assign('/compensation.html')});
  dlg.showModal();
}

installNavigation((view,options={})=>showView(view,options));
onAppEvent('budget:open',detail=>{if(detail?.id)openBudgetDetail(ctx,detail.id)});
onAppEvent('rules:new',()=>newRule(ctx));

// Global search (§19): groups results from existing scoped endpoints; never touches provider payloads.
// Shared context handed to UI modules (dashboard widgets, transactions detail, …) so they reuse the
// app's single api()/formatting/dialog path instead of duplicating it.
const ctx={$,$,api,bankApi,get,esc,date,dateTime,toast,dialog,money,isPrivate,categoryOptions,jsonBody,empty,skeleton,reload:loadCurrent,confirm:(message,opts)=>confirmDialog(ctx,message,opts),bffUrl:path=>apiClient.backendUrl(path),
  // Drill-down helper (UX rework §3): open a view with a URL scope, e.g. navScope('transactions','accountId='+id).
  navScope:(view,query)=>navigate(view,{query:query||''}),showView:(view,opts)=>navigate(view,opts||{})};
const accessSetup=createAccessSetup(ctx,(status,options)=>openBankingSetup(ctx,status,options));
const featureRegistry=createFeatureRegistry()
  .register('dashboard',()=>loadDashboard())
  .register('insights',()=>mountInsights(ctx))
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
  .register('settings',()=>renderSettings(ctx,{accessSetup,renderBankingSettings}));
async function loadDashboard(){await Promise.all([renderDashboard(ctx),renderDashboardInsights(ctx)])}

if(localStorage.getItem('finance.navCollapsed')==='1')document.body.classList.add('nav-collapsed');
initResizableSidebar();
syncResponsiveSidebar();
boot();
