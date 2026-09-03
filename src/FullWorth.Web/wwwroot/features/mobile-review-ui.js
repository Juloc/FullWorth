import { money, setMoneyLocale } from '../ui/money.js';
import { onPrivacyChange } from '../ui/privacy.js';

const $=(s,r=document)=>r.querySelector(s);
const $$=(s,r=document)=>[...r.querySelectorAll(s)];
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const sid=()=>localStorage.getItem('finance.space')||'';
const en=()=>document.documentElement.lang?.startsWith('en');
const t=(de,enText)=>en()?enText:de;
function withSpace(path){const [base,q='']=path.split('?');const p=new URLSearchParams(q);if(sid()&&!p.has('fullWorthSpaceId'))p.set('fullWorthSpaceId',sid());return `${base}?${p}`}
async function api(path,opt){const r=await fetch(`/bff/backend/${withSpace(path.replace(/^\//,''))}`,opt);if(!r.ok){let m=`${r.status}`;try{const b=await r.json();m=b.error||b.title||m}catch{}throw new Error(m)}if(r.status===204)return null;return r.json()}
const json=(method,body)=>({method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
function toast(message){const el=$('#toast');if(!el)return;el.textContent=message;el.classList.add('show');clearTimeout(toast.timer);toast.timer=setTimeout(()=>el.classList.remove('show'),3200)}
function ensureCss(){if(document.querySelector('link[data-review-mode-css]'))return;const l=document.createElement('link');l.rel='stylesheet';l.href='/mobile-review.css';l.dataset.reviewModeCss='1';document.head.appendChild(l)}
ensureCss();

let state=null;
onPrivacyChange(()=>state?.render?.());

function formatMoney(value,currency){setMoneyLocale(en()?'en':'de');return money(value,currency)}
function dateText(v){return v?new Intl.DateTimeFormat(en()?'en-US':'de-DE').format(new Date(`${String(v).slice(0,10)}T12:00:00`)):'—'}
function reasonLabel(item){const map={manual:t('Manuell','Manual'),rule:t('Regel','Rule'),merchant:t('Händler erkannt','Merchant match'),text:t('Buchungstext','Transaction text'),mcc:'MCC',imported:t('Importiert','Imported'),catalog:t('Katalog','Catalog'),unclassified:t('Nicht erkannt','Unclassified')};return map[item.reasonCode]||item.reasonCode||''}

async function loadReviewData(){
  const [overview,txResponse,categories,access]=await Promise.all([
    api('api/category-intelligence/overview'),
    api('api/transactions?limit=5000&includeIgnored=true'),
    api('api/categories?includeArchived=false'),
    api('api/access/effective').catch(()=>({capabilities:{}}))
  ]);
  const txItems=Array.isArray(txResponse)?txResponse:(txResponse.items||[]);
  const byTx=new Map(txItems.map(x=>[x.id,x]));
  const review=(overview.items||[]).filter(x=>x.needsReview).map(x=>({...x,transaction:byTx.get(x.id)})).filter(x=>x.transaction);
  return {overview,review,categories:categories.filter(x=>!x.isArchived),canCategorize:!!access.capabilities?.['transactions.categorize']};
}

async function refreshLauncher(){
  const toolbar=$('#view-transactions .toolbar');if(!toolbar)return;
  let data;try{data=await loadReviewData()}catch{return}
  let button=toolbar.querySelector('[data-review-mode]');
  if(!data.canCategorize||data.review.length===0){button?.remove();return}
  if(!button){button=document.createElement('button');button.type='button';button.className='ghost mr-launch';button.dataset.reviewMode='1';toolbar.appendChild(button);button.onclick=()=>openReviewMode()}
  button.textContent=`${t('Prüfen','Review')} · ${data.review.length}`;
  button.setAttribute('aria-label',t(`${data.review.length} ungeprüfte Buchungen prüfen`, `Review ${data.review.length} unreviewed transactions`));
}

export async function openReviewMode(){
  try{
    const data=await loadReviewData();
    if(!data.canCategorize){toast(t('Keine Berechtigung zum Kategorisieren.','No permission to categorize.'));return}
    if(!data.review.length){toast(t('Keine ungeprüften Buchungen.','No unreviewed transactions.'));return}
    const d=document.createElement('dialog');d.className='mr-dialog';d.innerHTML=`<div class="mr-shell"><header class="mr-head"><div><strong>${esc(t('Buchungen prüfen','Review transactions'))}</strong><span data-progress></span></div><button type="button" data-close aria-label="${esc(t('Schließen','Close'))}">×</button></header><main data-card></main></div>`;document.body.appendChild(d);$('[data-close]',d).onclick=()=>d.close();d.addEventListener('close',()=>{state=null;d.remove();refreshLauncher()});d.showModal();
    state={dialog:d,items:data.review,categories:data.categories,index:0,render:null};
    state.render=()=>renderCurrent(state);
    state.render();
  }catch(error){toast(error.message)}
}

function renderCurrent(s){
  if(!s.dialog?.open)return;
  const host=$('[data-card]',s.dialog),progress=$('[data-progress]',s.dialog);
  if(s.index>=s.items.length){progress.textContent='';host.innerHTML=`<section class="mr-finished"><div class="mr-check">✓</div><h2>${esc(t('Alles geprüft','Review complete'))}</h2><p>${esc(t('Alle Buchungen in dieser Review-Liste wurden bearbeitet oder übersprungen.','All transactions in this review list were handled or skipped.'))}</p><button type="button" data-done>${esc(t('Fertig','Done'))}</button></section>`;$('[data-done]',host).onclick=()=>s.dialog.close();return}
  const item=s.items[s.index],tx=item.transaction;progress.textContent=`${s.index+1} / ${s.items.length}`;
  const confidence=Math.round(Number(item.confidence||0)*100);
  host.innerHTML=`<section class="mr-card" data-swipe><div class="mr-date">${esc(dateText(tx.bookingDate||tx.valueDate))} · ${esc(tx.account||'')}</div><div class="mr-main"><h2>${esc(tx.counterparty||tx.description||t('Unbekannte Buchung','Unknown transaction'))}</h2><div class="mr-amount">${formatMoney(tx.amount,tx.currency)}</div></div><div class="mr-category"><span>${esc(t('Aktuelle Kategorie','Current category'))}</span><strong>${esc(tx.category||t('Nicht kategorisiert','Uncategorized'))}</strong></div><div class="mr-confidence"><span>${confidence}% · ${esc(reasonLabel(item))}${item.detail?` · ${esc(item.detail)}`:''}</span><div><i style="width:${Math.max(0,Math.min(100,confidence))}%"></i></div></div>${tx.description&&tx.description!==tx.counterparty?`<p class="mr-description">${esc(tx.description)}</p>`:''}<div data-change hidden></div><div class="mr-actions"><button type="button" class="ghost" data-skip>${esc(t('Überspringen','Skip'))}</button><button type="button" class="ghost" data-change-btn>${esc(t('Kategorie ändern','Change category'))}</button><button type="button" data-confirm>${esc(t('Bestätigen','Confirm'))}</button></div><div class="mr-hint">${esc(t('Optional: nach rechts wischen = bestätigen, nach links = überspringen.','Optional: swipe right to confirm, left to skip.'))}</div></section>`;
  $('[data-confirm]',host).onclick=()=>confirmCurrent(s,item);
  $('[data-skip]',host).onclick=()=>advance(s);
  $('[data-change-btn]',host).onclick=()=>showCategoryChange(s,item,host);
  bindSwipe($('[data-swipe]',host),()=>confirmCurrent(s,item),()=>advance(s));
}

function showCategoryChange(s,item,host){
  const box=$('[data-change]',host);box.hidden=false;box.innerHTML=`<div class="mr-change"><label>${esc(t('Neue Kategorie','New category'))}<select data-category><option value="">—</option>${s.categories.map(c=>`<option value="${c.id}" ${c.id===item.categoryId?'selected':''}>${esc(c.name)}</option>`).join('')}</select></label><div class="mr-learn"><button type="button" data-scope="one">${esc(t('Nur diese','Only this'))}</button><button type="button" class="ghost" data-scope="existing">${esc(t('Alle bisherigen gleichen','All existing matches'))}</button><button type="button" class="ghost" data-scope="future">${esc(t('Auch zukünftig','Future too'))}</button></div></div>`;
  $$('[data-scope]',box).forEach(button=>button.onclick=async()=>{const categoryId=$('[data-category]',box).value;if(!categoryId){toast(t('Bitte Kategorie wählen.','Choose a category.'));return}$$('button',box).forEach(b=>b.disabled=true);try{await api('api/category-intelligence/learn',json('POST',{transactionId:item.id,categoryId,scope:button.dataset.scope}));item.categoryId=categoryId;advance(s)}catch(error){toast(error.message);$$('button',box).forEach(b=>b.disabled=false)}})
}

async function confirmCurrent(s,item){
  const host=$('[data-card]',s.dialog);$$('button',host).forEach(b=>b.disabled=true);
  try{await api('api/category-intelligence/review',json('POST',{transactionIds:[item.id],isReviewed:true}));advance(s)}catch(error){toast(error.message);$$('button',host).forEach(b=>b.disabled=false)}
}
function advance(s){s.index++;s.render()}
function bindSwipe(el,onRight,onLeft){let start=null;el.addEventListener('pointerdown',e=>{start={x:e.clientX,y:e.clientY,id:e.pointerId};el.setPointerCapture?.(e.pointerId)});el.addEventListener('pointerup',e=>{if(!start||start.id!==e.pointerId)return;const dx=e.clientX-start.x,dy=e.clientY-start.y;start=null;if(Math.abs(dx)<70||Math.abs(dx)<Math.abs(dy)*1.4)return;if(dx>0)onRight();else onLeft()});el.addEventListener('pointercancel',()=>{start=null})}

function boot(){refreshLauncher();const observer=new MutationObserver(()=>{clearTimeout(boot.timer);boot.timer=setTimeout(refreshLauncher,140)});document.body&&observer.observe(document.body,{subtree:true,childList:true});document.addEventListener('click',e=>{if(e.target.closest('[data-view="transactions"]'))setTimeout(refreshLauncher,250)})}
boot();