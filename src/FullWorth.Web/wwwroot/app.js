import { money, setMoneyLocale } from './components/money.js';
import { isPrivate, onPrivacyChange } from './components/privacy.js';
import { confirmDialog } from './components/confirm.js';
import { bindIdentityIcons } from './features/ux-kit.js';
import { renderDashboard, bindDashboard, toggleDashboardEdit, invalidateLayout } from './pages/dashboard/page.js';
import { renderDashboardInsights } from './pages/insights/page.js';


// Der Registrierungs-Assistent. Er gehoert zu den Einstellungen, laeuft aber nach dem Registrieren
// auf der Startseite los - und die ist die letzte Ansicht, die diese Huelle noch zeigt. Beide
// Zeilen ziehen mit ihr um, nicht vorher.
import { createAccessSetup } from './pages/settings/access-setup.js';
import { openBankingSetup } from './pages/settings/bank-connections/page.js';
import { createDialog } from './components/dialog.js';
import { apiClient, api, bankApi, i18n, jsonBody } from './core/services.js';
import { state } from './core/state.js';
import { createRouter } from './core/router.js';
import { createFeatureRegistry } from './core/feature-registry.js';
import { installNavigation, navigate } from './core/navigation.js';
import { emitAppEvent, onAppEvent } from './core/event-bus.js';
import { createToast } from './components/toast.js';
import { createShell } from './app/shell.js';
import { loadSpaces as loadSpacesInto } from './app/page-context.js';
import { MENU, QUICK, ENTRIES, VIEWS } from './app/menu.js';
import { SUBPAGES, MIGRATED, pathForView as canonicalPath } from './app/routes.js';
// Der Finanzguru-Import ist nur Formular und Ereignisse - er hat nichts zu laden und deshalb auch
// nichts zu zeichnen.
import { emptyRow } from './components/empty.js';

// GET de-duplication and mutation invalidation are owned by core/api.js.
const get=path=>i18n.get(path);
// Seiten, die unter einer anderen liegen. Sie stehen nicht im Menü — sonst wäre es wieder überfüllt —,
// haben aber eine Adresse, die zeigt, wo sie hingehören, und markieren im Menü ihre Elternseite.
// Eine Ansicht, die schon eine eigene Razor-Seite hat, gehoert dieser Huelle nicht mehr (#154): sie
// steht nicht in ihrer Liste, ihr Link wird nicht abgefangen, und ein Wechsel dorthin ist eine echte
// Navigation. So laeuft die Migration seitenweise, ohne zwei Router nebeneinander.
const ALL_VIEWS=[...VIEWS,...Object.keys(SUBPAGES)].filter(view=>!MIGRATED.has(view));
const SUBPAGE_PATHS=Object.fromEntries(Object.entries(SUBPAGES).map(([view,page])=>[view,page.path]));
const MORE=ENTRIES.filter(entry=>!QUICK.includes(entry.view));
// §3: every screen has a real URL so reload/back/forward/deep-links work (the view is no longer
// only client state). dashboard is the root; the server's MapFallbackToFile serves index.html for
// any of these paths and the app resolves the view from location.pathname on boot.
const router=createRouter({views:ALL_VIEWS,defaultView:'dashboard',paths:SUBPAGE_PATHS});
const pathForView=router.pathForView;
const viewFromPath=router.viewFromPath;
// Contextual primary action per section (UI_UX_SPEC §3.1 header). Maps to the same handler as the
// in-page add control so there is a single code path.
// [messageKey, handler, kind]. `kind` drives the mobile glyph; it used to be guessed by matching a
// regex against the rendered label from a MutationObserver, which a new label or language broke.
// Nur noch die Ansichten, die diese Huelle selbst zeigt. Eine umgezogene Seite bringt ihre
// Hauptaktion in ihrem eigenen entry.js mit - sie gehoert zur Seite und nicht in eine Tabelle.
const PRIMARY_ACTION={dashboard:['dashboard.edit',()=>toggleDashboardEdit(ctx),'edit']};
const media=matchMedia('(prefers-color-scheme: dark)');
const $=s=>document.querySelector(s);const $$=s=>[...document.querySelectorAll(s)];
const root=document.documentElement;
const toastController=createToast($('#toast'));
const toast=(text,duration)=>toastController.show(text,duration);

async function boot(){
  setMoneyLocale(state.lang);
  applyTheme();
  await loadMessages();await loadCapabilities();bind();syncAdminVisibility();syncPrivacyToggle();syncNavToggle();
  const startView=handleConnectRedirect()||viewFromPath(location.pathname);
  try{await loadSpaces()}catch(e){console.error(e);toast(get('common.error'))}
  const canonicalStart=pathForView(startView);
  const startPath=location.pathname===canonicalStart||location.pathname.startsWith(canonicalStart.replace(/\/$/,'')+'/')?location.pathname:undefined;
  await showView(startView,{replace:true,path:startPath});
  // Inactivity lock: covers the app after 10 min idle; unlock re-loads the current screen.
  shell.startLock();
  await createAccessSetup(ctx,(status,options)=>openBankingSetup(ctx,status,options)).maybeOpenRegistrationOnboarding();
}
async function loadCapabilities(){
  try{
    const response=await fetch('/auth/capabilities',{cache:'no-store'});
    if(response.ok)state.capabilities=await response.json();
  }catch{}
}
function syncAdminVisibility(){
  const show=Boolean(state.capabilities?.admin);
  $('[data-entry="admin"]')?.toggleAttribute('hidden',!show);
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
function renderTranslations(){i18n.apply(document);
  // Collapsed sidebar shows icons only — carry each nav label as a tooltip + accessible name.
  $$('.nav-item[data-entry]').forEach(b=>{const t=b.querySelector('span')?.textContent||'';if(t){b.title=t;b.setAttribute('aria-label',t)}})}
// Die Möbel der Topbar stehen in app/shell.js - dieselbe Datei, die jede Razor-Seite benutzt.
const renderPageHeader=()=>shell.renderPageHeader();
// Modus UND abgeleitete Farben kommen aus derselben Engine (app/theme.js, als klassisches <script>
// schon vor diesem Modul geladen - siehe index.html) statt aus einer eigenen Kopie hier: ein
// Hell/Dunkel-Wechsel muss die ganze Akzent-/Neutral-/Datenpalette neu rechnen, nicht nur dataset.theme.
// Dieselbe Abfrage wie auf einer Razor-Seite; was die Huelle zusaetzlich braucht, steht hier.
async function loadSpaces(){await loadSpacesInto();shell.renderUserBlock();invalidateLayout()}
const applyTheme=()=>shell.applyTheme();

function bind(){
  bindIdentityIcons();
  // Sidebar theme toggle: cycles System -> Hell -> Dunkel (same behaviour as the login screen) and keeps the Settings select in sync.
  shell.bind();
  media.addEventListener('change',()=>{if(state.theme==='system')applyTheme()});
  // Jeder Eintrag ist ein echter Link auf seine Adresse. Der Klick wird abgefangen, damit die Seite
  // nicht neu lädt - mit Strg/Cmd oder Mittelklick bleibt er ein Link und öffnet einen neuen Tab.
  $$('.nav-item[data-view]').filter(a=>ALL_VIEWS.includes(a.dataset.view)).forEach(a=>a.addEventListener('click',e=>{
    if(e.metaKey||e.ctrlKey||e.shiftKey||e.button)return;
    e.preventDefault();showView(a.dataset.view,{query:''});
  }));
  // Browser Back/Forward: restore the view from the URL without pushing a new history entry.
  window.addEventListener('popstate',()=>showView(viewFromPath(location.pathname),{fromHistory:true,path:location.pathname}));
  // preventDefault, weil ein Sprung auch ein echter Link sein darf: mit Strg oder Mittelklick
  // öffnet er einen neuen Tab, beim normalen Klick bleibt die Anwendung stehen und wechselt.
  $$('[data-view-jump]').forEach(b=>b.addEventListener('click',event=>{
    if(event.metaKey||event.ctrlKey||event.shiftKey)return;
    event.preventDefault();showView(b.dataset.viewJump);
  }));
  bindDashboard(ctx);
  // Re-render on privacy change so every value on the current screen re-masks via the shared path.
  onPrivacyChange(()=>{syncPrivacyToggle();loadCurrent()});
}
const syncPrivacyToggle=()=>shell.syncPrivacyToggle();
// Die Seitenleiste - Einklappen, Breite, automatisches Einklappen bei wenig Platz - steht seit #154
// in app/shell.js und gilt damit auf JEDER Seite. Vorher stand sie hier, und eine Razor-Seite hatte
// weder den Ziehgriff noch einen funktionierenden Einklapp-Knopf: er war da und tat nichts.
const syncNavToggle=()=>shell.syncNavToggle();

async function showView(view,opts={}){
  // Die Seite gehoert nicht mehr hierher - hin fuehrt die Adresse, nicht ein Ansichtswechsel.
  if(MIGRATED.has(view)){
    const target=canonicalPath(view);
    const query=opts.query!==undefined?String(opts.query).replace(/^\?/,''):'';
    location.assign(query?`${target}?${query}`:target);
    return;
  }
  state.view=view;
  const base=opts.path||pathForView(view);
  const sameBase=location.pathname===base;
  const query=opts.query!==undefined?String(opts.query):(sameBase?location.search.replace(/^\?/,''):'');
  const target=query?`${base}?${query}`:base;
  if(!opts.fromHistory){
    router.write(view,{query,replace:!!opts.replace||location.pathname+location.search===target,state:{view},path:base});
  }
  $$('.view').forEach(v=>v.classList.remove('active'));$(`#view-${view}`)?.classList.add('active');
  const marked=SUBPAGES[view]?.parent||view;
  $$('.nav-item[data-entry]').forEach(b=>{const on=b.dataset.entry===marked;b.classList.toggle('active',on);b.setAttribute('aria-current',on?'page':'false')});
  $('#bottom-more').classList.toggle('active',MORE.some(entry=>entry.view===view));
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
function empty(el,message){el.innerHTML=emptyRow(message||get('common.empty'))}
function skeleton(el,rows=4){el.innerHTML=Array.from({length:rows},()=>`<div class="row skel"><div class="skel-bar shimmer"></div><div class="skel-bar short shimmer"></div></div>`).join('')}
function esc(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function dialog(html,options={}){return createDialog(html,{closeLabel:get('common.close'),...options})}
// §10.5: options show the full path ("Groceries > Supermarket"), not just the leaf name, so a
// category under multiple parents with the same name is still distinguishable at a glance.
async function categoryOptions(selected){const categories=await api('api/categories');const byId=new Map(categories.map(c=>[c.id,c]));const path=c=>{const chain=[];let cur=c;while(cur){chain.unshift(cur.name);cur=cur.parentId?byId.get(cur.parentId):null}return chain.join(' › ')};return categories.map(c=>`<option value="${c.id}"${c.id===selected?' selected':''}>${esc(path(c))}</option>`).join('')}

// "Mehr" auf dem Handy zeigt denselben Baum wie die Seitenleiste - beide entstehen aus MENU. Die
// Fassung davor las die Einträge aus dem Desktop-Markup aus, und genau deshalb fehlten dort Admin
// und Insights, während Händler und Protokoll nur hier standen.
installNavigation((view,options={})=>showView(view,options));
onAppEvent('surface:reload',()=>loadCurrent());

// Global search (§19): groups results from existing scoped endpoints; never touches provider payloads.
// Shared context handed to UI modules (dashboard widgets, transactions detail, …) so they reuse the
// app's single api()/formatting/dialog path instead of duplicating it.
const ctx={$,$,api,bankApi,get,esc,date,dateTime,toast,dialog,money,isPrivate,categoryOptions,jsonBody,empty,skeleton,reload:loadCurrent,confirm:(message,opts)=>confirmDialog(ctx,message,opts),bffUrl:path=>apiClient.backendUrl(path),
  // api() parst jede Antwort als JSON. Ein Endpunkt, der bewusst ein Dokument liefert (das
  // Kuendigungsschreiben ist text/plain), wuerde daran scheitern - deshalb dieselbe Anfrage ueber
  // denselben Client, nur ohne JSON.parse. Kein zweiter Abrufweg, nur die vorhandene Antwort roh.
  apiText:path=>apiClient.backendResponse(path).then(response=>response.text()),
  // Drill-down helper (UX rework §3): open a view with a URL scope, e.g. navScope('transactions','accountId='+id).
  navScope:(view,query)=>navigate(view,{query:query||''}),showView:(view,opts)=>navigate(view,opts||{})};
// Theme, Privatmodus, Ueberschrift, die beiden Ueberlaufmenues, die globale Suche und die
// Sitzungssperre stehen seit #154 in app/shell.js - dieselbe Datei, die jede Razor-Seite benutzt.
// Zwei Fassungen derselben Topbar waeren genau das Muster, das diese Migration aufraeumt. Was die
// Huelle anders macht, steht in den vier Funktionen, die sie mitgibt.
const shell=createShell({
  navigate:(view,options)=>showView(view,options),
  reload:()=>loadCurrent(),
  context:ctx,
  currentView:()=>state.view,
  primaryAction:view=>PRIMARY_ACTION[view]??null
});
const featureRegistry=createFeatureRegistry()
  .register('dashboard',()=>loadDashboard());
async function loadDashboard(){await Promise.all([renderDashboard(ctx),renderDashboardInsights(ctx)])}

boot();
