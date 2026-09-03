const $=(s,r=document)=>r.querySelector(s);
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const en=()=>document.documentElement.lang?.startsWith('en');
const t=(de,enText)=>en()?enText:de;
async function api(path){const r=await fetch(`/bff/backend/${path}`);if(!r.ok)throw new Error(`${r.status}`);return r.json()}
function ensureCss(){if(document.querySelector('link[data-space-switch-css]'))return;const l=document.createElement('link');l.rel='stylesheet';l.href='/fullworth-space-switcher.css';l.dataset.spaceSwitchCss='1';document.head.appendChild(l)}
ensureCss();
let spaces=[];
function currentId(){return localStorage.getItem('finance.space')||''}
function current(){return spaces.find(x=>x.id===currentId())||spaces[0]||null}
function clearSpaceScopedState(){
  const prefixes=['finance.selected.','finance.selection.','finance.space-cache.','finance.tx-selection.','finance.bulk.'];
  for(const store of [localStorage,sessionStorage]){
    for(let i=store.length-1;i>=0;i--){const key=store.key(i);if(key&&prefixes.some(p=>key.startsWith(p)))store.removeItem(key)}
  }
}
function switchTo(id){if(!id||id===currentId())return;clearSpaceScopedState();localStorage.setItem('finance.space',id);location.assign('/')}
function dialog(){const d=document.createElement('dialog');d.className='fs-dialog';d.innerHTML=`<div class="fs-card"><div class="fs-head"><div><h2>${esc(t('FullWorth Space wechseln','Switch finance space'))}</h2><p>${esc(t('Dashboard, Filter und Ansichten werden für den gewählten Space neu geladen.','Dashboard, filters and views reload for the selected space.'))}</p></div><button type="button" data-close aria-label="${esc(t('Schließen','Close'))}">×</button></div><div class="fs-list">${spaces.map(s=>`<button type="button" data-space="${s.id}" class="${s.id===currentId()?'active':''}"><span class="fs-avatar">${esc((s.name||'F').trim().charAt(0).toUpperCase())}</span><span><strong>${esc(s.name||'FullWorth Space')}</strong><small>${esc(s.baseCurrency||'EUR')}</small></span><span class="fs-check">${s.id===currentId()?'✓':'›'}</span></button>`).join('')}</div></div>`;document.body.appendChild(d);$('[data-close]',d).onclick=()=>d.close();d.querySelectorAll('[data-space]').forEach(b=>b.onclick=()=>switchTo(b.dataset.space));d.addEventListener('close',()=>d.remove());d.showModal()}
function install(){if(spaces.length<2)return;const cur=current();const user=$('.sidebar-user');if(user&&!user.querySelector('[data-space-switch]')){const b=document.createElement('button');b.type='button';b.className='fs-switch';b.dataset.spaceSwitch='desktop';b.title=t('FullWorth Space wechseln','Switch finance space');b.setAttribute('aria-label',b.title);b.innerHTML=`<span>${esc(cur?.name||'FullWorth Space')}</span><span aria-hidden="true">⌄</span>`;b.onclick=dialog;const theme=$('#theme-toggle',user);user.insertBefore(b,theme||null)}const actions=$('.topbar-actions');if(actions&&!actions.querySelector('[data-space-switch="mobile"]')){const b=document.createElement('button');b.type='button';b.className='icon-button fs-switch-mobile';b.dataset.spaceSwitch='mobile';b.title=t('FullWorth Space wechseln','Switch finance space');b.setAttribute('aria-label',b.title);b.textContent=(cur?.name||'F').trim().charAt(0).toUpperCase();b.onclick=dialog;actions.insertBefore(b,actions.firstChild)}}
async function boot(){try{spaces=await api('api/fullworth-spaces');install()}catch{}const o=new MutationObserver(()=>{if(spaces.length>1)install()});document.body&&o.observe(document.body,{childList:true,subtree:true})}
boot();