const $ = selector => document.querySelector(selector);
const all = selector => [...document.querySelectorAll(selector)];
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
const lang = () => (localStorage.getItem('finance.language') || (navigator.language || 'de')).startsWith('de') ? 'de' : 'en';
const spaceId = () => localStorage.getItem('finance.space');
const isCoachPath = () => location.pathname.replace(/\/+$/, '') === '/coach';
let active = false;
let currentConversationId = null;
let reviews = new Map();
let loading = false;
let responding = false;
let modelCatalog = null;
let selectedModel = '';
let currentConversationSpaceId = null;
let dockOpen = false;
const quickAccessKey = 'finance.coach.quickAccess';
const pageContextKey = 'finance.coach.pageContext';
const pinnedKey = 'finance.coach.pinned';
let currentObjectContext = null;
const excludedContext = new Set();

const reasonLabels = {
  necessary: ['Notwendig', 'Necessary'], good_value: ['Gutes Preis-Leistungs-Verhältnis', 'Good value'], quality_of_life: ['Lebensqualität', 'Quality of life'],
  experience: ['Erlebnis', 'Experience'], health_or_wellbeing: ['Gesundheit / Wohlbefinden', 'Health / wellbeing'], gift_or_relationship: ['Geschenk / Beziehung', 'Gift / relationship'],
  long_term_value: ['Langfristiger Wert', 'Long-term value'], routine: ['Routine', 'Routine'], expected: ['Erwartet', 'Expected'], mixed: ['Gemischt', 'Mixed'], unsure: ['Unsicher', 'Unsure'],
  impulse: ['Impulskauf', 'Impulse'], too_expensive: ['Zu teuer', 'Too expensive'], unused: ['Nicht genutzt', 'Unused'], duplicate: ['Doppelt / unnötig', 'Duplicate'],
  subscription_regret: ['Abo bereut', 'Subscription regret'], convenience_cost: ['Bequemlichkeitskosten', 'Convenience cost'], avoidable_fee: ['Vermeidbare Gebühr', 'Avoidable fee'], poor_value: ['Schlechter Gegenwert', 'Poor value']
};
const reasonsBySentiment = {
  Positive: ['necessary', 'good_value', 'quality_of_life', 'experience', 'health_or_wellbeing', 'gift_or_relationship', 'long_term_value'],
  Neutral: ['routine', 'expected', 'mixed', 'unsure'],
  Negative: ['impulse', 'too_expensive', 'unused', 'duplicate', 'subscription_regret', 'convenience_cost', 'avoidable_fee', 'poor_value']
};

function tr(de, en) { return lang() === 'de' ? de : en; }
function formatMoney(value, currency = 'EUR') { return new Intl.NumberFormat(lang() === 'de' ? 'de-DE' : 'en-US', { style: 'currency', currency }).format(Number(value || 0)); }
function formatPercent(value) { return new Intl.NumberFormat(lang() === 'de' ? 'de-DE' : 'en-US', { style: 'percent', maximumFractionDigits: 0 }).format(Number(value || 0)); }

async function ensureSpace() {
  if (spaceId()) return spaceId();
  const response = await fetch('/bff/backend/api/fullworth-spaces');
  if (!response.ok) throw new Error(String(response.status));
  const spaces = await response.json();
  if (!spaces?.length) throw new Error(tr('Kein FullWorth Space vorhanden.', 'No FullWorth Space exists.'));
  localStorage.setItem('finance.space', spaces[0].id);
  return spaces[0].id;
}

async function api(path, options = {}) {
  await ensureSpace();
  const clean = path.replace(/^\//, '');
  const [base, query = ''] = clean.split('?');
  const params = new URLSearchParams(query);
  if (!params.has('fullWorthSpaceId')) params.set('fullWorthSpaceId', spaceId());
  const response = await fetch(`/bff/backend/${base}?${params}`, options);
  if (!response.ok) {
    let message = String(response.status);
    try { const body = await response.json(); message = body.error || body.message || body.title || message; } catch { }
    throw new Error(message);
  }
  return response.status === 204 ? null : response.json();
}

function installShell() {
  if ($('#coach-nav')) return;
  const nav = $('#nav');
  const separator = nav?.querySelector('.nav-sep');
  const button = document.createElement('button');
  button.id = 'coach-nav';
  button.type = 'button';
  button.dataset.coachView = '1';
  button.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 5.5h14v10H9l-4 3v-13Z"/><path d="M9 9h6m-6 3h4"/></svg><span>Coach</span>';
  button.addEventListener('click', () => activate(true));
  if (separator) nav.insertBefore(button, separator); else nav?.appendChild(button);

  const section = document.createElement('section');
  section.id = 'view-coach';
  section.className = 'view coach-view';
  section.innerHTML = `
    <div class="coach-layout">
      <div class="coach-main">
        <article class="panel coach-hero">
          <div class="coach-identity"><div class="coach-avatar" aria-hidden="true"><img class="brand-logo" src="/branding/fullworth-logo.svg" alt=""></div><div><span class="coach-eyebrow">FullWorth Coach</span><strong id="coach-mascot-label"></strong></div></div>
          <div class="coach-head-actions"><span id="coach-page-context" class="coach-context"></span><button id="coach-new-chat" type="button" class="ghost coach-new-chat">${esc(tr('Neu starten', 'New chat'))}</button><span id="coach-mode" class="coach-mode">${esc(tr('Deterministisch', 'Deterministic'))}</span></div>
        </article>
        <article class="panel coach-chat-panel">
          <div id="coach-starters" class="coach-starters"></div>
          <div id="coach-messages" class="coach-messages" aria-live="polite"></div>
          <form id="coach-form" class="coach-composer">
            <label class="sr-only" for="coach-input">${esc(tr('Frage an FullWorth', 'Question for FullWorth'))}</label>
            <textarea id="coach-input" rows="2" maxlength="2000" placeholder="${esc(tr('Wo ist mein Geld diesen Monat hin?', 'Where did my money go this month?'))}"></textarea>
            <div class="coach-composer-footer">
              <label class="coach-model-picker" for="coach-model">
                <span class="sr-only">${esc(tr('KI-Modell', 'AI model'))}</span>
                <select id="coach-model" aria-label="${esc(tr('KI-Modell', 'AI model'))}"><option value="">${esc(tr('Automatisch', 'Automatic'))}</option></select>
                <svg viewBox="0 0 20 20" aria-hidden="true"><path d="m6.5 8 3.5 3.5L13.5 8"/></svg>
              </label>
              <button id="coach-send" type="submit" class="primary-action coach-send" aria-label="${esc(tr('Senden', 'Send'))}" title="${esc(tr('Senden', 'Send'))}">
                <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 19V5m-5 5 5-5 5 5"/></svg>
              </button>
            </div>
          </form>
        </article>
      </div>
      <aside class="coach-side">
        <article class="panel"><div class="panel-head"><h2>${esc(tr('Ausgaben-Review', 'Spending review'))}</h2><button id="coach-review-refresh" type="button" class="ghost">↻</button></div><div id="coach-summary"></div></article>
        <article class="panel"><div class="panel-head"><h2>${esc(tr('Letzte Ausgaben bewerten', 'Review recent spending'))}</h2></div><div id="coach-review-list" class="coach-review-list"></div></article>
      </aside>
    </div>`;
  $('#main')?.appendChild(section);

  installQuickAccess();
  installSettingsToggle();
  $('#coach-form')?.addEventListener('submit', event => { event.preventDefault(); ask($('#coach-input').value); });
  installComposerKeyboard($('#coach-input'));
  $('#coach-new-chat')?.addEventListener('click', restartConversation);
  $('#coach-model')?.addEventListener('change', event => setSelectedModel(event.target.value || ''));
  $('#coach-review-refresh')?.addEventListener('click', () => loadReviews());
  $('#refresh')?.addEventListener('click', event => {
    if (!active) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    loadAll();
  }, true);

  document.querySelectorAll('.sidebar button[data-view], #bottom-nav button[data-view]').forEach(existing => {
    existing.addEventListener('click', () => { if (active) deactivate(); }, true);
  });
  $('#bottom-more')?.addEventListener('click', () => queueMicrotask(injectMobileMore));
  window.addEventListener('popstate', () => { if (isCoachPath()) activate(false); else if (active) deactivate(); });
  window.addEventListener('load', () => { if (isCoachPath()) activate(false); });
  window.addEventListener('storage', event => {
    if (event.key === quickAccessKey) syncQuickAccess();
    if (event.key === pinnedKey) syncPinButton();
  });
  window.addEventListener('fullworth:view-change', () => {
    currentObjectContext=null;excludedContext.clear();
    if(!dockOpen)return;
    if(isPinned()){renderPageContext();renderStarters();}
    else closeDock();
  });
  window.addEventListener('fullworth:coach-open', event => {
    setObjectContext(event.detail||null);
    if(!dockOpen)openDock();else {renderPageContext();renderStarters();$('#coach-dock-input')?.focus();}
  });
  window.addEventListener('fullworth:layout-reset',()=>{localStorage.removeItem(pinnedKey);currentObjectContext=null;excludedContext.clear();if(dockOpen)closeDock();syncPinButton()});
  document.addEventListener('keydown',event=>{if(event.key==='Escape'&&dockOpen&&!document.querySelector('dialog[open]')){event.preventDefault();closeDock()}});
  document.addEventListener('change', event => { if (dockOpen && event.target.closest('.view.active')) renderPageContext(); });
  document.addEventListener('input', event => { if (dockOpen && event.target.closest('.view.active') && event.target.id === 'tx-query') renderPageContext(); });
  if (document.readyState === 'complete' && isCoachPath()) activate(false);
  syncQuickAccess();
}

function quickAccessEnabled() { return localStorage.getItem(quickAccessKey) !== '0'; }

function installComposerKeyboard(input) {
  if (!input || input.dataset.coachKeyboard === '1') return;
  input.dataset.coachKeyboard = '1';
  input.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || event.isComposing) return;
    if (!event.shiftKey) {
      event.preventDefault();
      input.closest('form')?.requestSubmit();
      return;
    }

    event.preventDefault();
    const start = input.selectionStart ?? input.value.length;
    const end = input.selectionEnd ?? start;
    const before = input.value.slice(0, start);
    const after = input.value.slice(end);
    const lineStart = before.lastIndexOf('\n') + 1;
    const currentLine = before.slice(lineStart);
    const numbered = currentLine.match(/^(\s*)(\d+)([.)])\s*/);
    const bullet = currentLine.match(/^(\s*)([-*+])\s+/);
    const continuation = numbered
      ? `${numbered[1]}${Number(numbered[2]) + 1}${numbered[3]} `
      : bullet ? `${bullet[1]}${bullet[2]} ` : '';
    const insertion = `\n${continuation}`;
    input.setRangeText(insertion, start, end, 'end');
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

function installSettingsToggle() {
  const grid = $('#view-settings .settings-grid');
  if (!grid || $('#coach-quick-access-setting')) return;
  const label = document.createElement('label');
  label.className = 'fw-toggle-row settings-toggle';
  label.innerHTML = `<span>${esc(tr('Coach-Sprechblase', 'Coach chat bubble'))}</span><span class="fw-toggle"><input id="coach-quick-access-setting" type="checkbox"><span class="fw-toggle-track"></span></span>`;
  const input = label.querySelector('input');
  input.checked = quickAccessEnabled();
  input.addEventListener('change', () => {
    localStorage.setItem(quickAccessKey, input.checked ? '1' : '0');
    syncQuickAccess();
  });
  grid.appendChild(label);
}

function installQuickAccess() {
  if ($('#coach-launcher')) return;
  const launcher = document.createElement('button');
  launcher.id = 'coach-launcher';
  launcher.className = 'coach-launcher';
  launcher.type = 'button';
  launcher.setAttribute('aria-label', tr('Coach öffnen', 'Open Coach'));
  launcher.setAttribute('title', tr('Coach öffnen', 'Open Coach'));
  launcher.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 5.5h14v10H9l-4 3v-13Z"/><path d="M9 9h6m-6 3h4"/></svg>';
  launcher.addEventListener('click', openDock);

  const dock = document.createElement('aside');
  dock.id = 'coach-dock';
  dock.className = 'coach-dock';
  dock.hidden = true;
  dock.setAttribute('aria-label', 'FullWorth Coach');
  dock.innerHTML = `
    <div id="coach-dock-resizer" class="coach-dock-resizer" role="separator" aria-orientation="vertical" aria-label="${esc(tr('Coach-Breite ändern', 'Resize Coach'))}" tabindex="0"></div>
    <header class="coach-dock-header">
      <div class="coach-dock-title"><img class="brand-logo" src="/branding/fullworth-logo.svg" alt=""><div><strong>Coach</strong><span id="coach-dock-subtitle"></span></div></div>
      <div class="coach-dock-actions">
        <button id="coach-dock-pin" type="button" class="icon-button coach-dock-pin" aria-label="${esc(tr('Coach anheften', 'Pin Coach'))}" title="${esc(tr('Coach anheften', 'Pin Coach'))}" aria-pressed="false">⌖</button>
        <button id="coach-dock-new" type="button" class="icon-button" aria-label="${esc(tr('Neu starten', 'New chat'))}" title="${esc(tr('Neu starten', 'New chat'))}">↻</button>
        <button id="coach-dock-page" type="button" class="icon-button" aria-label="${esc(tr('Coach-Seite öffnen', 'Open Coach page'))}" title="${esc(tr('Coach-Seite öffnen', 'Open Coach page'))}">↗</button>
        <button id="coach-dock-close" type="button" class="icon-button coach-dock-close" aria-label="${esc(tr('Schließen', 'Close'))}" title="${esc(tr('Schließen', 'Close'))}">×</button>
      </div>
    </header>
    <div class="coach-dock-chat">
      <div id="coach-dock-context" class="coach-context"></div>
      <div id="coach-dock-starters" class="coach-starters"></div>
      <div id="coach-dock-messages" class="coach-messages" aria-live="polite"></div>
      <form id="coach-dock-form" class="coach-composer">
        <label class="sr-only" for="coach-dock-input">${esc(tr('Frage an FullWorth', 'Question for FullWorth'))}</label>
        <textarea id="coach-dock-input" rows="2" maxlength="2000" placeholder="${esc(tr('Frag FullWorth …', 'Ask FullWorth …'))}"></textarea>
        <div class="coach-composer-footer">
          <label class="coach-model-picker" for="coach-dock-model">
            <span class="sr-only">${esc(tr('KI-Modell', 'AI model'))}</span>
            <select id="coach-dock-model" aria-label="${esc(tr('KI-Modell', 'AI model'))}"><option value="">${esc(tr('Automatisch', 'Automatic'))}</option></select>
            <svg viewBox="0 0 20 20" aria-hidden="true"><path d="m6.5 8 3.5 3.5L13.5 8"/></svg>
          </label>
          <button id="coach-dock-send" type="submit" class="primary-action coach-send" aria-label="${esc(tr('Senden', 'Send'))}">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 19V5m-5 5 5-5 5 5"/></svg>
          </button>
        </div>
      </form>
    </div>`;

  document.body.append(launcher, dock);
  $('#coach-dock-close')?.addEventListener('click', closeDock);
  $('#coach-dock-pin')?.addEventListener('click', () => {
    localStorage.setItem(pinnedKey, isPinned() ? '0' : '1');
    syncPinButton();
  });
  $('#coach-dock-page')?.addEventListener('click', () => { closeDock(); activate(true); });
  $('#coach-dock-new')?.addEventListener('click', restartConversation);
  $('#coach-dock-form')?.addEventListener('submit', event => { event.preventDefault(); ask($('#coach-dock-input').value); });
  installComposerKeyboard($('#coach-dock-input'));
  $('#coach-dock-model')?.addEventListener('change', event => setSelectedModel(event.target.value || ''));
  initDockResize();
  initDockSwipe();
  syncPinButton();
}

function dockWidthMode(){return window.innerWidth>=1024?'desktop':'tablet'}
function dockWidthKey(){return `finance.coach.dockWidth.${dockWidthMode()}`}
function isPinned(){return localStorage.getItem(pinnedKey)==='1'}
function syncPinButton(){
  const button=$('#coach-dock-pin');if(!button)return;
  const pinned=isPinned();
  button.setAttribute('aria-pressed',String(pinned));
  button.classList.toggle('active',pinned);
  const label=pinned?tr('Coach lösen','Unpin Coach'):tr('Coach anheften','Pin Coach');
  button.setAttribute('aria-label',label);button.title=label;
}

function initDockResize() {
  const dock = $('#coach-dock');
  const handle = $('#coach-dock-resizer');
  if (!dock || !handle) return;
  const minWidth = 260;
  const defaults={desktop:420,tablet:320};
  const desktopMode = () => window.matchMedia('(min-width:768px)').matches;
  const key=()=>dockWidthKey();
  const defaultWidth=()=>defaults[dockWidthMode()];
  const savedWidth=()=>{
    const scoped=Number(localStorage.getItem(key()));if(scoped>0)return scoped;
    const legacy=Number(localStorage.getItem('finance.coach.dockWidth'));return legacy>0?legacy:defaultWidth();
  };
  const maxWidth = () => {
    const sidebar = document.body.classList.contains('nav-collapsed') || document.body.classList.contains('nav-auto-collapsed')
      ? 72 : document.querySelector('.sidebar')?.getBoundingClientRect().width || 0;
    const minMain = window.innerWidth < 1100 ? 280 : 420;
    return Math.max(minWidth, Math.min(600, window.innerWidth - sidebar - minMain));
  };
  const apply = value => {
    if (!desktopMode()) return;
    const width = Math.max(minWidth, Math.min(maxWidth(), Math.round(Number(value) || savedWidth())));
    document.documentElement.style.setProperty('--coach-dock-w', `${width}px`);
    handle.setAttribute('aria-valuemin', String(minWidth));
    handle.setAttribute('aria-valuemax', String(maxWidth()));
    handle.setAttribute('aria-valuenow', String(width));
    window.dispatchEvent(new CustomEvent('fullworth:coach-resize', { detail: { width } }));
    return width;
  };
  const save=width=>{if(width)localStorage.setItem(key(),String(width))};

  let pointerId = null;
  handle.addEventListener('pointerdown', event => {
    if (!desktopMode()) return;
    pointerId = event.pointerId; handle.setPointerCapture(pointerId);
    handle.classList.add('is-dragging'); document.body.classList.add('coach-dock-resizing'); event.preventDefault();
  });
  handle.addEventListener('pointermove', event => { if (pointerId === event.pointerId) apply(window.innerWidth - event.clientX); });
  const finish = event => {
    if (pointerId === null || event.pointerId !== pointerId) return;
    pointerId = null; handle.classList.remove('is-dragging'); document.body.classList.remove('coach-dock-resizing');
    save(apply(dock.getBoundingClientRect().width));
  };
  handle.addEventListener('pointerup', finish);
  handle.addEventListener('pointercancel', finish);
  handle.addEventListener('dblclick',()=>save(apply(defaultWidth())));
  handle.addEventListener('keydown', event => {
    if (!desktopMode() || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const current = dock.getBoundingClientRect().width;
    const step=event.shiftKey?40:10;
    const next = event.key === 'Home' ? minWidth : event.key === 'End' ? maxWidth() : current + (event.key === 'ArrowLeft' ? step : -step);
    save(apply(next));
  });

  const clamp = () => { if (desktopMode() && dockOpen) save(apply(savedWidth())); };
  window.addEventListener('resize', clamp);
  window.addEventListener('fullworth:sidebar-resize', clamp);
  window.addEventListener('fullworth:layout-reset',()=>apply(defaultWidth()));
  window.fwClampCoachWidth = clamp;
}

function initDockSwipe(){
  const dock=$('#coach-dock'),header=$('.coach-dock-header',dock);if(!dock||!header)return;
  let id=null,startY=0,lastY=0;
  header.addEventListener('pointerdown',event=>{
    if(!window.matchMedia('(max-width:767px)').matches||event.target.closest('button'))return;
    id=event.pointerId;startY=lastY=event.clientY;header.setPointerCapture(id);dock.classList.add('is-swiping');
  });
  header.addEventListener('pointermove',event=>{
    if(event.pointerId!==id)return;lastY=event.clientY;
    const dy=Math.max(0,lastY-startY);dock.style.transform=`translateY(${dy}px)`;
  });
  const end=event=>{
    if(event.pointerId!==id)return;
    const dy=Math.max(0,lastY-startY);id=null;dock.classList.remove('is-swiping');dock.style.transform='';
    if(dy>90)closeDock();
  };
  header.addEventListener('pointerup',end);header.addEventListener('pointercancel',end);
}

function readFilterValue(id) {
  const element = document.getElementById(id);
  if (!element) return null;
  if (element.type === 'checkbox') return element.checked ? 'true' : null;
  const value = String(element.value ?? '').trim();
  return value || null;
}

function safeObjectContext(value){
  if(!value||typeof value!=='object')return null;
  const details={};
  for(const [key,val] of Object.entries(value.details||{})){
    if(val==null)continue;details[String(key).slice(0,40)]=String(val).slice(0,180);
  }
  return {
    entityType:String(value.entityType||'').slice(0,40)||null,
    entityId:value.entityId==null?null:String(value.entityId).slice(0,100),
    entityLabel:value.entityLabel==null?null:String(value.entityLabel).slice(0,160),
    details,
    selectedIds:Array.isArray(value.selectedIds)?value.selectedIds.slice(0,20).map(x=>String(x).slice(0,100)):[],
    selectedItems:Array.isArray(value.selectedItems)?value.selectedItems.slice(0,20).map(item=>({
      id:String(item?.id||'').slice(0,100),
      label:item?.label==null?null:String(item.label).slice(0,160),
      details:Object.fromEntries(Object.entries(item?.details||{}).slice(0,10).map(([key,val])=>[String(key).slice(0,40),String(val??'').slice(0,180)]))
    })).filter(item=>item.id):[]
  };
}

function setObjectContext(value){
  currentObjectContext=safeObjectContext(value);
  excludedContext.clear();
  renderPageContext();renderStarters();
}
function capturePageContext() {
  const activeView = all('.view.active').find(view => view.id !== 'view-coach');
  let base=null;
  if (!activeView) {
    try { base=JSON.parse(sessionStorage.getItem(pageContextKey) || 'null'); } catch { base=null; }
  } else {
    const page = activeView.id.replace(/^view-/, '');
    const filters = {};
    const queryKeys = new Set(['accountId','groupId','categoryId','merchantId','transactionId','direction','flags','period']);
    const params = new URLSearchParams(location.search);
    for (const [key, value] of params) if (queryKeys.has(key) && value) filters[key] = value.slice(0, 160);
    const pageFilters = {
      transactions: [['tx-query','query'],['tx-direction','direction'],['tx-flags','flags']],
      contracts: [['contracts-archived','archived']],
      analytics: [['an-period','period'],['an-measure','measure'],['an-dimension','dimension'],['an-cperiod','comparisonPeriod'],['an-ctype','chartType']],
      audit: [['audit-action','auditAction'],['audit-entity-type','entityType']]
    };
    for (const [id, key] of pageFilters[page] || []) { const value = readFilterValue(id); if (value) filters[key] = value.slice(0, 160); }
    base={page:page.slice(0,40),title:($('#page-title')?.textContent||page).trim().slice(0,100),path:(location.pathname+location.search).slice(0,180),filters};
    sessionStorage.setItem(pageContextKey,JSON.stringify(base));
  }
  if(!base)return null;
  const context={...base,filters:{...(base.filters||{})}};
  for(const key of Object.keys(context.filters))if(excludedContext.has(`filter:${key}`))delete context.filters[key];
  if(currentObjectContext&&!excludedContext.has('entity'))Object.assign(context,currentObjectContext);
  return context;
}

function renderContextRoot(root,context){
  if(!root)return;root.replaceChildren();
  if(!context){root.textContent=tr('Kein Seitenkontext','No page context');return}
  const add=(label,key,removable=true)=>{
    const b=document.createElement(removable?'button':'span');
    b.className='coach-context-chip';b.textContent=label;
    if(removable){b.type='button';b.title=tr('Aus Kontext entfernen','Remove from context');b.addEventListener('click',()=>{excludedContext.add(key);renderPageContext();renderStarters()})}
    root.appendChild(b);
  };
  add(context.title||context.page,'page',false);
  if(context.entityType) add(context.entityLabel||context.entityType,'entity');
  for(const [key,value] of Object.entries(context.filters||{})) add(`${key}: ${value}`,`filter:${key}`);
  if((context.selectedIds||[]).length) add(`${context.selectedIds.length} ${tr('ausgewählt','selected')}`,'entity');
}
function renderPageContext() {
  const context = capturePageContext();
  renderContextRoot($('#coach-page-context'),context);
  renderContextRoot($('#coach-dock-context'),context);
  return context;
}

function setSelectedModel(value) {
  selectedModel = value || '';
  if (selectedModel) localStorage.setItem('finance.coach.model', selectedModel);
  else localStorage.removeItem('finance.coach.model');
  all('#coach-model,#coach-dock-model').forEach(select => { if (select.value !== selectedModel) select.value = selectedModel; });
}

function syncQuickAccess() {
  installSettingsToggle();
  const setting = $('#coach-quick-access-setting');
  if (setting) setting.checked = quickAccessEnabled();
  if (!quickAccessEnabled() && dockOpen) closeDock();
  const launcher = $('#coach-launcher');
  if (launcher) launcher.hidden = !quickAccessEnabled() || active || dockOpen;
}

async function openDock() {
  if (!quickAccessEnabled()) return;
  capturePageContext();
  dockOpen = true;
  const dock = $('#coach-dock');
  if (dock) dock.hidden = false;
  document.body.classList.add('coach-dock-open');
  if (window.matchMedia('(min-width:768px)').matches) {
    window.fwClampCoachWidth?.();
    window.fwClampSidebarWidth?.();
    window.fwClampCoachWidth?.();
    window.fwSyncResponsiveSidebar?.();
  }
  syncQuickAccess();
  setMascotLabel();
  renderPageContext();
  renderStarters();
  try { await Promise.all([loadConversation(), loadModels()]); } catch (error) { renderError(error); }
  queueMicrotask(() => $('#coach-dock-input')?.focus());
}

function closeDock() {
  dockOpen = false;
  document.body.classList.remove('coach-dock-open');
  const dock = $('#coach-dock');
  if (dock) { dock.hidden = true; dock.style.transform=''; }
  window.dispatchEvent(new Event('resize'));
  window.fwSyncResponsiveSidebar?.();
  syncQuickAccess();
}

function injectMobileMore() {
  const list = [...document.querySelectorAll('dialog .more-list')].at(-1);
  if (!list || list.querySelector('[data-coach-mobile]')) return;
  const button = document.createElement('button');
  button.type = 'button';
  button.dataset.coachMobile = '1';
  button.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M5 5.5h14v10H9l-4 3v-13Z"/><path d="M9 9h6m-6 3h4"/></svg><span>Coach</span>';
  button.addEventListener('click', () => { button.closest('dialog')?.close(); activate(true); });
  list.appendChild(button);
}

function activate(push) {
  if (!isCoachPath()) capturePageContext();
  if (dockOpen) closeDock();
  active = true;
  document.querySelectorAll('.view').forEach(view => view.classList.remove('active'));
  $('#view-coach')?.classList.add('active');
  document.querySelectorAll('.sidebar button').forEach(button => button.classList.remove('active'));
  $('#coach-nav')?.classList.add('active');
  $('#coach-nav')?.setAttribute('aria-current', 'page');
  $('#bottom-more')?.classList.add('active');
  $('#page-title').textContent = 'Coach';
  $('#page-subtitle').textContent = tr('Deine Daten erklären, Ausgaben bewerten und Ziele berechnen.', 'Explain your data, review spending and calculate goals.');
  const primary = $('#primary-action'); if (primary) primary.hidden = true;
  if (push && !isCoachPath()) history.pushState({ view: 'coach' }, '', '/coach');
  syncQuickAccess();
  loadAll();
}

function deactivate() {
  active = false;
  $('#view-coach')?.classList.remove('active');
  $('#coach-nav')?.classList.remove('active');
  $('#coach-nav')?.setAttribute('aria-current', 'false');
  syncQuickAccess();
}

async function loadAll() {
  if (loading) return;
  loading = true;
  setMascotLabel();
  renderPageContext();
  renderStarters();
  try { await Promise.all([loadConversation(), loadReviews(), loadModels()]); }
  catch (error) { renderError(error); }
  finally { loading = false; }
}

function setMascotLabel() {
  const mascot = localStorage.getItem('finance.mascot');
  const label = mascot ? `${tr('Maskottchen', 'Mascot')}: ${mascot}` : tr('FullWorth-Daten im sicheren Kontext', 'FullWorth data in secure context');
  if ($('#coach-mascot-label')) $('#coach-mascot-label').textContent = label;
  if ($('#coach-dock-subtitle')) $('#coach-dock-subtitle').textContent = label;
}

async function loadModels() {
  const selects = all('#coach-model,#coach-dock-model');
  if (!selects.length) return;
  try {
    modelCatalog = await api('api/coach/models');
  } catch {
    modelCatalog = { configured: false, models: [] };
  }

  const models = Array.isArray(modelCatalog?.models) ? modelCatalog.models : [];
  const stored = localStorage.getItem('finance.coach.model') || '';
  selectedModel = models.some(model => model.id === stored) ? stored : '';
  if (!selectedModel && stored) localStorage.removeItem('finance.coach.model');

  const configure = select => {
  select.innerHTML = '';
  const auto = document.createElement('option');
  auto.value = '';
  const defaultLabel = modelCatalog?.defaultModel ? ` · ${modelCatalog.defaultModel}` : '';
  auto.textContent = modelCatalog?.configured
    ? `${tr('Automatisch', 'Automatic')}${defaultLabel}`
    : tr('Deterministisch', 'Deterministic');
  select.appendChild(auto);

  models.forEach(model => {
    const option = document.createElement('option');
    option.value = model.id;
    option.textContent = model.label || model.id;
    select.appendChild(option);
  });
  select.value = selectedModel;
  select.disabled = !modelCatalog?.configured;
  };
  selects.forEach(configure);

  const mode = $('#coach-mode');
  if (mode) {
    mode.textContent = modelCatalog?.configured
      ? `${tr('KI', 'AI')} · ${modelCatalog.provider || tr('konfiguriert', 'configured')}`
      : tr('Deterministisch', 'Deterministic');
  }
}

function starterQuestions(){
  const context=capturePageContext(),type=context?.entityType;
  if(type==='transaction')return lang()==='de'
    ? ['Ist diese Buchung ungewöhnlich?','Passt die Kategorie?','Wie wirkt sie sich auf mein Budget aus?']
    : ['Is this transaction unusual?','Does the category fit?','How does it affect my budget?'];
  if(type==='transactions')return lang()==='de'
    ? ['Was fällt bei diesen Buchungen auf?','Welche davon sind vermeidbar?','Wie könnte ich sie sinnvoll gruppieren?']
    : ['What stands out in these transactions?','Which of them may be avoidable?','How should I group them?'];
  if(type==='contract')return lang()==='de'
    ? ['Ist dieser Vertrag auffällig teuer?','Wie hoch sind die Jahreskosten?','Wo könnte ich sparen?']
    : ['Is this contract unusually expensive?','What is the annual cost?','Where could I save?'];
  if(type==='account')return lang()==='de'
    ? ['Was fällt bei diesem Konto auf?','Wie entwickelt sich mein Geldfluss hier?','Welche Ausgaben stechen heraus?']
    : ['What stands out for this account?','How is cash flow developing here?','Which expenses stand out?'];
  if(type==='asset'||type==='liability')return lang()==='de'
    ? ['Wie wirkt sich das auf mein Vermögen aus?','Was sollte ich dabei beobachten?','Wie hat sich der Wert entwickelt?']
    : ['How does this affect my net worth?','What should I watch here?','How has the value developed?'];
  if(type==='budget')return lang()==='de'
    ? ['Bin ich bei diesem Budget auf Kurs?','Welche Ausgaben treiben dieses Budget?','Wie kann ich das Budget besser einhalten?']
    : ['Am I on track with this budget?','Which expenses drive this budget?','How can I stay within this budget?'];
  if(type==='portfolio')return lang()==='de'
    ? ['Wie passt dieses Depot zu meinem Gesamtvermögen?','Was sollte ich bei diesem Depot beobachten?','Wie beeinflusst es meine Vermögensentwicklung?']
    : ['How does this portfolio fit my overall net worth?','What should I watch in this portfolio?','How does it affect my wealth trend?'];
  return lang()==='de'
    ? ['Wo ist mein Geld hin?','Was habe ich bereut?','Was war es wert?','Was könnte ich reduzieren?','Wann erreiche ich 100.000 €?']
    : ['Where did my money go?','What did I regret?','What was worth it?','What could I reduce?','When could I reach €100,000?'];
}
function renderStarters() {
  const starters=starterQuestions();
  all('#coach-starters,#coach-dock-starters').forEach(root => {
    root.innerHTML = '';
    starters.forEach(text => {
      const button = document.createElement('button'); button.type = 'button'; button.className = 'coach-chip'; button.textContent = text;
      button.addEventListener('click', () => ask(text)); root.appendChild(button);
    });
  });
}

async function loadConversation() {
  const sid = spaceId();
  if (currentConversationSpaceId !== sid) {
    currentConversationId = null;
    currentConversationSpaceId = sid;
  }
  const conversations = await api('api/coach/conversations?limit=1');
  currentConversationId = conversations?.[0]?.id || null;
  if (!currentConversationId) { renderMessages([]); return; }
  const detail = await api(`api/coach/conversations/${currentConversationId}`);
  renderMessages(detail.messages || []);
}

async function ensureConversation() {
  if (currentConversationId) return currentConversationId;
  const created = await api('api/coach/conversations', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title: null, mascotId: localStorage.getItem('finance.mascot') || null })
  });
  currentConversationId = created.id;
  currentConversationSpaceId = spaceId();
  return currentConversationId;
}

async function ask(text) {
  const question = String(text || '').trim();
  if (!question || responding) return;
  responding = true;
  const inputs = all('#coach-input,#coach-dock-input');
  const sends = all('#coach-send,#coach-dock-send');
  inputs.forEach(input => { input.value = ''; });
  sends.forEach(send => { send.disabled = true; });
  appendMessage({ role: 'User', text: question, facts: [] });
  setThinking(true);
  try {
    const id = await ensureConversation();
    const uiContext = renderPageContext();
    const response = await api(`api/coach/conversations/${id}/messages`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ text: question, model: selectedModel || null, uiContext })
    });
    setThinking(false);
    appendMessage(response.message);
    renderContextActions();
    const modeText = response.message.mode === 'Ai'
      ? `${tr('KI', 'AI')}${response.message.model ? ` · ${response.message.model}` : ''}`
      : tr('Deterministisch', 'Deterministic');
    if ($('#coach-mode')) $('#coach-mode').textContent = modeText;
    renderFollowUps(response.followUps || []);
    await loadReviews(false);
  } catch (error) {
    setThinking(false);
    appendMessage({ role: 'Assistant', text: `${tr('Fehler', 'Error')}: ${error.message}`, facts: [] });
  }
  finally {
    setThinking(false);
    responding = false;
    sends.forEach(send => { send.disabled = false; });
    (dockOpen ? $('#coach-dock-input') : $('#coach-input'))?.focus();
  }
}

async function restartConversation() {
  const id = currentConversationId;
  if (id) {
    try { await api(`api/coach/conversations/${id}`, { method: 'DELETE' }); }
    catch (error) { renderError(error); return; }
  }
  currentConversationId = null;
  currentConversationSpaceId = spaceId();
  renderMessages([]);
  renderStarters();
  (dockOpen ? $('#coach-dock-input') : $('#coach-input'))?.focus();
}

function messageRoots() { return all('#coach-messages,#coach-dock-messages'); }

function setThinking(visible) {
  messageRoots().forEach(root => {
    root.querySelector('.coach-thinking')?.remove();
    if (!visible) return;
    root.querySelector('.coach-empty')?.remove();
    const article = document.createElement('article');
    article.className = 'coach-message assistant coach-thinking';
    article.setAttribute('role', 'status');
    article.setAttribute('aria-label', tr('Coach denkt nach', 'Coach is thinking'));
    article.innerHTML = `<span class="sr-only">${esc(tr('Coach denkt nach', 'Coach is thinking'))}</span><span class="coach-thinking-dots" aria-hidden="true"><span></span><span></span><span></span></span>`;
    root.appendChild(article);
    root.scrollTop = root.scrollHeight;
  });
}

function renderMessages(messages) {
  messageRoots().forEach(root => {
    root.innerHTML = '';
    if (!messages.length) root.innerHTML = `<div class="coach-empty">${esc(tr('Noch keine Nachrichten.', 'No messages yet.'))}</div>`;
  });
  messages.forEach(appendMessage);
}

function appendMessage(message) {
  messageRoots().forEach(root => {
    root.querySelector('.coach-empty')?.remove();
    const article = document.createElement('article');
    article.className = `coach-message ${String(message.role).toLowerCase() === 'user' ? 'user' : 'assistant'}`;
    const text = document.createElement('div'); text.className = 'coach-message-text'; text.textContent = message.text || '';
    article.appendChild(text);
    if (String(message.role).toLowerCase() !== 'user' && (message.model || message.provider)) {
      const meta = document.createElement('div');
      meta.className = 'coach-message-meta';
      meta.textContent = [message.provider, message.model].filter(Boolean).join(' · ');
      article.appendChild(meta);
    }
    if (message.facts?.length) {
      const details = document.createElement('details'); details.className = 'coach-evidence';
      const summary = document.createElement('summary'); summary.textContent = tr('Verwendete Fakten', 'Evidence'); details.appendChild(summary);
      const facts = document.createElement('div'); facts.className = 'coach-facts';
      message.facts.forEach(fact => { const chip = document.createElement('span'); chip.className = 'coach-fact'; chip.textContent = `${fact.label}: ${fact.value}`; facts.appendChild(chip); });
      details.appendChild(facts); article.appendChild(details);
    }
    root.appendChild(article); root.scrollTop = root.scrollHeight;
  });
}

function renderContextActions(){
  const context=capturePageContext();
  if(!context?.entityType)return;
  const actions=[];
  const add=(label,run)=>actions.push({label,run});
  const openView=view=>document.querySelector(`.sidebar [data-view="${view}"],#bottom-nav [data-view="${view}"]`)?.click();
  if(context.entityType==='transaction'&&context.entityId){
    add(tr('Kategorie ändern','Change category'),()=>window.dispatchEvent(new CustomEvent('fullworth:open-transaction',{detail:{id:context.entityId}})));
    add(tr('Regel erstellen','Create rule'),()=>{openView('rules');setTimeout(()=>$('#primary-action')?.click(),40)});
  }else if(context.entityType==='contract'&&context.entityId){
    add(tr('Vertrag öffnen','Open contract'),()=>window.dispatchEvent(new CustomEvent('fullworth:open-contract',{detail:{id:context.entityId}})));
  }else if(context.entityType==='account'&&context.entityId){
    add(tr('Kontobuchungen öffnen','Open account transactions'),()=>window.fwNavScope?.('transactions','accountId='+encodeURIComponent(context.entityId)));
  }else if(context.entityType==='budget'&&context.entityId){
    add(tr('Budget öffnen','Open budget'),()=>{openView('budgets');setTimeout(()=>window.fwOpenBudget?.(context.entityId),50)});
  }else if(['asset','liability','portfolio'].includes(context.entityType)){
    add(tr('Vermögen öffnen','Open net worth'),()=>openView('networth'));
  }else if(context.entityType==='transactions'){
    add(tr('Auswahl in Buchungen ansehen','View selection in transactions'),()=>openView('transactions'));
  }
  if(!actions.length)return;
  messageRoots().forEach(root=>{
    const article=[...root.querySelectorAll('.coach-message.assistant')].at(-1);if(!article||article.querySelector('.coach-response-actions'))return;
    const bar=document.createElement('div');bar.className='coach-response-actions';
    actions.forEach(action=>{const b=document.createElement('button');b.type='button';b.className='ghost';b.textContent=action.label;b.addEventListener('click',action.run);bar.appendChild(b)});
    article.appendChild(bar);
  });
}

function renderFollowUps(items) {
  all('#coach-starters,#coach-dock-starters').forEach(root => {
    root.innerHTML = '';
    items.slice(0, 3).forEach(text => { const b = document.createElement('button'); b.type = 'button'; b.className = 'coach-chip'; b.textContent = text; b.addEventListener('click', () => ask(text)); root.appendChild(b); });
  });
}

async function loadReviews(renderCandidates = true) {
  const [summary, recent, transactions] = await Promise.all([
    api('api/spending-reviews/summary'),
    api('api/spending-reviews/recent?limit=100'),
    renderCandidates ? api('api/transactions?direction=expense&limit=20') : Promise.resolve(null)
  ]);
  reviews = new Map((recent || []).map(review => [review.transactionId, review]));
  renderSummary(summary);
  if (transactions) renderReviewCandidates((transactions.items || []).filter(tx => !tx.isTransfer && !tx.isIgnored).slice(0, 12));
}

function renderSummary(summary) {
  const root = $('#coach-summary');
  const score = summary.worthItScore == null ? '—' : Number(summary.worthItScore).toFixed(2);
  root.innerHTML = `<div class="coach-summary-grid">
    <div class="coach-metric"><span>${esc(tr('Abdeckung', 'Coverage'))}</span><strong>${esc(formatPercent(summary.reviewCoverage))}</strong></div>
    <div class="coach-metric"><span>${esc(tr('Worth-it', 'Worth it'))}</span><strong>${esc(score)}</strong></div>
    <div class="coach-metric coach-metric--good"><span>${esc(tr('Gut', 'Good'))}</span><strong>${esc(formatMoney(summary.positiveAmount, summary.currency))}</strong></div>
    <div class="coach-metric coach-metric--bad"><span>${esc(tr('Schlecht', 'Bad'))}</span><strong>${esc(formatMoney(summary.negativeAmount, summary.currency))}</strong></div>
  </div>${renderInsightGroups(summary)}`;
}

function renderInsightGroups(summary) {
  const positive = (summary.highSpendPositive || []).slice(0, 2);
  const negative = (summary.negativeOpportunities || []).slice(0, 2);
  if (!positive.length && !negative.length) return `<p class="coach-muted">${esc(tr('Mehr Ausgaben bewerten, damit die Auswertung persönlicher wird.', 'Review more spending to make the analysis personal.'))}</p>`;
  const insightCard = (label, tone, line) => `<div class="coach-insight coach-insight--${tone}"><span class="coach-insight-dot" aria-hidden="true"></span><div class="coach-insight-body"><strong>${esc(label)}</strong><span>${esc(line)}</span></div></div>`;
  const cards = [
    ...positive.map(x => insightCard(x.label, 'good', tr('überwiegend gut bewertet', 'mostly rated good'))),
    ...negative.map(x => insightCard(x.label, 'warn', tr('negatives Signal', 'negative signal')))
  ];
  return `<div class="coach-insight-label">${esc(tr('Signale', 'Signals'))}</div><div class="coach-insight-list">${cards.join('')}</div>`;
}

function renderReviewCandidates(items) {
  const root = $('#coach-review-list'); root.innerHTML = '';
  if (!items.length) { root.innerHTML = `<div class="coach-empty">${esc(tr('Keine Ausgaben gefunden.', 'No spending found.'))}</div>`; return; }
  items.forEach(tx => {
    const review = reviews.get(tx.id);
    const row = document.createElement('div'); row.className = 'coach-review-row'; row.dataset.transactionId = tx.id;
    row.innerHTML = `<div class="coach-review-head"><div><strong>${esc(tx.counterparty || tx.description || tr('Ausgabe', 'Expense'))}</strong><span>${esc(tx.bookingDate || '')}</span></div><strong>${esc(formatMoney(Math.abs(tx.amount), tx.currency || 'EUR'))}</strong></div>
      <div class="coach-sentiments" role="group" aria-label="${esc(tr('Ausgabe bewerten', 'Review spending'))}">
        ${sentimentButton('Positive', tr('Gut', 'Good'), review)}${sentimentButton('Neutral', tr('Neutral', 'Neutral'), review)}${sentimentButton('Negative', tr('Schlecht', 'Bad'), review)}
        <button type="button" class="coach-details" ${review ? '' : 'disabled'}>${esc(tr('Details', 'Details'))}</button>
      </div><div class="coach-review-details" hidden></div>`;
    row.querySelectorAll('[data-sentiment]').forEach(button => button.addEventListener('click', () => quickReview(tx, button.dataset.sentiment, row)));
    row.querySelector('.coach-details').addEventListener('click', () => toggleReviewDetails(tx, row));
    root.appendChild(row);
  });
}

function sentimentButton(sentiment, label, review) {
  const activeClass = review?.sentiment === sentiment ? ' active' : '';
  return `<button type="button" class="coach-sentiment ${sentiment.toLowerCase()}${activeClass}" data-sentiment="${sentiment}" aria-pressed="${review?.sentiment === sentiment}">${esc(label)}</button>`;
}

async function quickReview(tx, sentiment, row) {
  try {
    const saved = await api(`api/spending-reviews/transactions/${tx.id}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sentiment, reasons: [], note: null })
    });
    reviews.set(tx.id, saved); updateReviewRow(row, saved); await refreshSummaryOnly();
  } catch (error) { showInlineError(row, error.message); }
}

function updateReviewRow(row, review) {
  row.querySelectorAll('[data-sentiment]').forEach(button => { const on = button.dataset.sentiment === review.sentiment; button.classList.toggle('active', on); button.setAttribute('aria-pressed', String(on)); });
  row.querySelector('.coach-details').disabled = false;
}

function toggleReviewDetails(tx, row) {
  const panel = row.querySelector('.coach-review-details');
  if (!panel.hidden) { panel.hidden = true; return; }
  const review = reviews.get(tx.id); if (!review) return;
  const allowed = reasonsBySentiment[review.sentiment] || [];
  panel.innerHTML = `<div class="coach-reasons">${allowed.map(reason => `<button type="button" data-reason="${reason}" class="${review.reasons?.includes(reason) ? 'active' : ''}">${esc(reasonLabels[reason]?.[lang() === 'de' ? 0 : 1] || reason)}</button>`).join('')}</div>
    <textarea maxlength="500" rows="2" placeholder="${esc(tr('Optionale Notiz', 'Optional note'))}">${esc(review.note || '')}</textarea>
    <div class="coach-detail-actions"><button type="button" data-clear class="ghost">${esc(tr('Bewertung löschen', 'Clear review'))}</button><button type="button" data-save>${esc(tr('Speichern', 'Save'))}</button></div>`;
  panel.querySelectorAll('[data-reason]').forEach(button => button.addEventListener('click', () => button.classList.toggle('active')));
  panel.querySelector('[data-save]').addEventListener('click', () => saveReviewDetails(tx, row, panel));
  panel.querySelector('[data-clear]').addEventListener('click', () => clearReview(tx, row));
  panel.hidden = false;
}

async function saveReviewDetails(tx, row, panel) {
  const current = reviews.get(tx.id); if (!current) return;
  const reasons = [...panel.querySelectorAll('[data-reason].active')].map(button => button.dataset.reason);
  const note = panel.querySelector('textarea').value.trim() || null;
  try {
    const saved = await api(`api/spending-reviews/transactions/${tx.id}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sentiment: current.sentiment, reasons, note })
    });
    reviews.set(tx.id, saved); panel.hidden = true; await refreshSummaryOnly();
  } catch (error) { showInlineError(row, error.message); }
}

async function clearReview(tx, row) {
  try {
    await api(`api/spending-reviews/transactions/${tx.id}`, { method: 'DELETE' });
    reviews.delete(tx.id); row.querySelectorAll('[data-sentiment]').forEach(button => { button.classList.remove('active'); button.setAttribute('aria-pressed', 'false'); });
    row.querySelector('.coach-details').disabled = true; row.querySelector('.coach-review-details').hidden = true; await refreshSummaryOnly();
  } catch (error) { showInlineError(row, error.message); }
}

async function refreshSummaryOnly() { renderSummary(await api('api/spending-reviews/summary')); }
function showInlineError(row, message) { let error = row.querySelector('.coach-inline-error'); if (!error) { error = document.createElement('div'); error.className = 'coach-inline-error'; row.appendChild(error); } error.textContent = message; }
function renderError(error) { messageRoots().forEach(root => { root.innerHTML = `<div class="coach-empty">${esc(tr('Coach konnte nicht geladen werden: ', 'Coach could not be loaded: ') + error.message)}</div>`; }); }

installShell();
