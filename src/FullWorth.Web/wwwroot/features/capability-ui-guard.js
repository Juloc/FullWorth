const $=(s,r=document)=>r.querySelector(s);
const $$=(s,r=document)=>[...r.querySelectorAll(s)];
const sid=()=>localStorage.getItem('finance.space')||'';

const DENIED='capability-ui-denied';
const CAPABILITY_SELECTORS={
  'transactions.write':[
    '#tx-add',
    '#tx-detect',
    '#fp-import-export [data-import]',
    '#fp-import-export [data-upload]',
    '#view-settings [data-upload]'
  ],
  'transactions.categorize':[
    '[data-action="new-category"]',
    '#view-categories .cat-actions',
    '[data-category-order]',
    '[data-cat-merge]',
    '#rules-reapply',
    '[data-action="new-rule"]',
    '#rules-list .rule-actions',
    '.ci-select-tx',
    '#ci-bulkbar',
    '[data-atb-all]',
    '[data-atb-selected]',
    '[data-ci-review]',
    '[data-ci-tags]',
    '[data-ci-learn]'
  ],
  'budgets.manage':[
    '[data-action="new-budget"]',
    '[data-fp-budget]',
    '.budget-detail [data-edit]',
    '.fp-dialog form[data-settings]',
    '.fp-dialog form[data-income]'
  ],
  'contracts.manage':[
    '[data-action="new-contract"]',
    '#contracts-detect',
    '[data-fp-contract]',
    '#contracts-detected [data-accept]',
    '#contracts-detected [data-dismiss]',
    '#contracts-price-changes [data-confirm]',
    '#contracts-price-changes [data-ignore]',
    '.contract-detail [data-edit]',
    '.contract-detail [data-cancel-contract]',
    '.contract-detail [data-reactivate]'
  ],
  'purchases.manage':[
    '#scan-receipt',
    '#amazon-import',
    '[data-smart-review]',
    '[data-product-manager]',
    '.pi-review-dialog [data-save]',
    '.pi-review-dialog [data-diff]',
    '.pi-review-dialog [data-confirm]',
    '.pi-review-dialog [data-add]',
    '.pi-review-dialog [data-add-discount]',
    '.pi-review-dialog .pi-delete',
    '.pi-review-dialog .pi-discount-delete',
    '.pi-review-dialog .pi-category',
    '.pi-review-dialog .pi-product',
    '.pi-review-dialog .pi-name',
    '.pi-review-dialog .pi-qty',
    '.pi-review-dialog .pi-unit',
    '.pi-review-dialog .pi-total',
    '.pi-review-dialog .pi-original',
    '.pi-review-dialog .pi-item-discount',
    '.pi-review-dialog .pi-item-discount-label',
    '.pi-review-dialog .pi-item-deposit',
    '.pi-review-dialog .pi-discount-type',
    '.pi-review-dialog .pi-discount-label',
    '.pi-review-dialog .pi-discount-amount',
    '.pi-review-dialog .pi-discount-item',
    '.pi-review-dialog .pi-discount-percent',
    '.pi-review-dialog .pi-discount-code',
    '.pi-review-dialog .pi-discount-raw',
    '.pi-review-dialog [data-subtotal]',
    '.pi-review-dialog [data-discount-total]',
    '.pi-review-dialog [data-deposit-total]',
    '.pi-review-dialog [data-tax-total]',
    '.pi-review-dialog [data-rounding]'
  ],
  'investments.manage':[
    '[data-fp-invest]',
    '[data-investment-import]',
    '[data-action="new-asset"]',
    '[data-action="new-liability"]',
    '[data-action="new-loan"]',
    '.fp-dialog [data-trade]',
    '.fp-dialog [data-price]'
  ],
  'banking.manage':[
    '#add-account',
    '#add-group',
    '[data-add-bank]',
    '[data-manage-groups]',
    '[data-style-group]',
    '.account-order-actions',
    '#fp-account-experience [data-custom]',
    '#connections-list [data-sync]',
    '#connections-list [data-reconnect]',
    '#connections-list [data-disconnect]',
    '#accounts-view-list [data-move]',
    '#accounts-view-list [data-edit]',
    '#accounts-view-list [data-delete]',
    '#accounts-view-list [data-rename]',
    '#accounts-view-list [data-delgroup]'
  ],
  'export.read':[
    '#export-data',
    '#fp-import-export [data-export]',
    '#fp-import-export [data-xlsx]'
  ],
  'sharing.manage':[
    '#fp-import-export [data-permissions]'
  ]
};

const PRIMARY_CAPABILITY={
  accounts:'banking.manage',
  budgets:'budgets.manage',
  contracts:'contracts.manage',
  networth:'investments.manage',
  categories:'transactions.categorize',
  rules:'transactions.categorize'
};

let cached=null;
let cacheSpace=null;
let lastFetch=0;
let refreshTimer=null;
let applyTimer=null;
let fetchPatched=false;

function ensureStyle(){
  // Production CSP forbids JS-created inline <style> blocks (style-src 'self'); load the module's
  // CSS (the .capability-ui-denied hide rule) as a same-origin linked stylesheet instead, exactly once.
  if($('link[data-feature-css="capability-ui-guard"]'))return;
  const link=document.createElement('link');
  link.rel='stylesheet';
  link.href='/features/capability-ui-guard.css';
  link.dataset.featureCss='capability-ui-guard';
  document.head.appendChild(link);
}

function cleanupLegacyDuplicates(){
  // These controls are installed by the older parity-completion enhancer. Dedicated modules now own
  // the same flows with stronger previews/validation, so keeping both would expose two competing UIs.
  $$('[data-category-tools],[data-select-all-matching],[data-access-panel]').forEach(el=>el.remove());
}

function allowed(capability){return !!cached?.capabilities?.[capability]}
function mark(selector,isAllowed){
  $$(selector).forEach(el=>el.classList.toggle(DENIED,!isAllowed));
}

function currentView(){
  const active=$('.view.active');
  if(active?.id?.startsWith('view-'))return active.id.slice(5);
  const nav=$('.sidebar button[data-view].active');
  return nav?.dataset.view||null;
}

function apply(){
  cleanupLegacyDuplicates();
  if(!cached)return;
  for(const [capability,selectors] of Object.entries(CAPABILITY_SELECTORS)){
    const ok=allowed(capability);
    selectors.forEach(selector=>mark(selector,ok));
  }

  // Rules are not a read-only page: the backend intentionally requires categorization capability even
  // for listing them. Audit likewise requires audit.read. Hide their navigation entries, including the
  // dynamically generated mobile "More" sheet entries.
  mark('.sidebar button[data-view="rules"], #bottom-nav button[data-view="rules"], [data-go="rules"]',allowed('transactions.categorize'));
  mark('.sidebar button[data-view="audit"], #bottom-nav button[data-view="audit"], [data-go="audit"]',allowed('audit.read'));

  // The final access button remains useful without sharing.manage because it shows "My permissions".
  const accessButton=$('#fp-import-export [data-access]');
  accessButton?.classList.remove(DENIED);

  const primary=$('#primary-action');
  const cap=PRIMARY_CAPABILITY[currentView()];
  if(primary&&cap)primary.classList.toggle(DENIED,!allowed(cap));
  else primary?.classList.remove(DENIED);
}

function scheduleApply(){
  clearTimeout(applyTimer);
  applyTimer=setTimeout(()=>{
    if(sid()!==cacheSpace)refresh(true);
    else apply();
  },60);
}

async function refresh(force=false){
  const space=sid();
  if(!space)return;
  if(cacheSpace&&cacheSpace!==space){cached=null;lastFetch=0}
  const now=Date.now();
  if(!force&&cached&&cacheSpace===space&&now-lastFetch<30000){scheduleApply();return}
  try{
    const response=await nativeFetch(`/bff/backend/api/access/effective?fullWorthSpaceId=${encodeURIComponent(space)}`,{headers:{'X-FullWorth-UI-Guard':'1'}});
    if(!response.ok)return;
    cached=await response.json();
    cacheSpace=space;
    lastFetch=Date.now();
    apply();
  }catch{
    // Backend authorization remains authoritative. A failed cosmetic capability refresh must never
    // guess permissions or lock the user out of otherwise valid read-only UI.
  }
}

const nativeFetch=window.fetch.bind(window);
function patchFetch(){
  if(fetchPatched)return;
  fetchPatched=true;
  window.fetch=async(...args)=>{
    const response=await nativeFetch(...args);
    try{
      const input=args[0];
      const url=typeof input==='string'?input:input?.url||'';
      if((response.status===403||response.status===404)&&url.includes('/bff/backend/')&&!url.includes('/api/access/effective')){
        clearTimeout(refreshTimer);
        refreshTimer=setTimeout(()=>refresh(true),80);
      }
    }catch{}
    return response;
  };
}

function boot(){
  ensureStyle();
  cleanupLegacyDuplicates();
  patchFetch();
  refresh(true);
  const observer=new MutationObserver(scheduleApply);
  if(document.body)observer.observe(document.body,{childList:true,subtree:true});
  document.addEventListener('click',event=>{
    if(event.target.closest?.('[data-view],[data-go],[data-parity-bank]'))setTimeout(()=>{apply();refresh(false)},0);
  },true);
  window.addEventListener('popstate',()=>setTimeout(()=>{apply();refresh(false)},0));
  window.addEventListener('focus',()=>refresh(true));
  document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh(true)});
  window.addEventListener('storage',event=>{if(event.key==='finance.space'){cached=null;cacheSpace=null;refresh(true)}});
}

boot();
