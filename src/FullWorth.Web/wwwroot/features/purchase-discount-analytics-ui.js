import { money, setMoneyLocale } from '../ui/money.js';

const $=(s,r=document)=>r.querySelector(s);
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const en=()=>document.documentElement.lang?.toLowerCase().startsWith('en');
const t=(de,enText)=>en()?enText:de;
const sid=()=>localStorage.getItem('finance.space')||'';
const fmt=(value,currency='EUR')=>{setMoneyLocale(en()?'en':'de');return money(Number(value||0),currency)};

function withSpace(path){const [base,q='']=path.split('?');const p=new URLSearchParams(q);if(sid()&&!p.has('fullWorthSpaceId'))p.set('fullWorthSpaceId',sid());return `${base}?${p}`}
async function api(path){const response=await fetch(`/bff/backend/${withSpace(path)}`);if(!response.ok){let message=String(response.status);try{const body=await response.json();message=body.error||body.title||message}catch{}throw new Error(message)}return response.json()}

export async function openPurchaseDiscountAnalytics(){
  const now=new Date();
  const from=`${now.getFullYear()}-01-01`;
  const to=`${now.getFullYear()}-${String(now.getMonth()+1).padStart(2,'0')}-${String(now.getDate()).padStart(2,'0')}`;
  const dialog=document.createElement('dialog');
  dialog.className='pi-dialog pi-discount-analytics-dialog';
  dialog.innerHTML=`<div class="pi-card"><div class="pi-head"><div><span class="pi-muted">FullWorth</span><h2>${esc(t('Rabatte & Ersparnisse','Discounts & savings'))}</h2></div><button type="button" data-close aria-label="${esc(t('Schließen','Close'))}">×</button></div><form class="pi-analytics-filter" data-filter><label>${esc(t('Von','From'))}<input name="from" type="date" value="${from}"></label><label>${esc(t('Bis','To'))}<input name="to" type="date" value="${to}"></label><button>${esc(t('Auswerten','Apply'))}</button></form><div data-results class="pi-analytics-results"><div class="pi-muted">${esc(t('Auswertung wird geladen …','Loading analytics …'))}</div></div></div>`;
  document.body.appendChild(dialog);
  dialog.querySelector('[data-close]').onclick=()=>dialog.close();
  dialog.addEventListener('close',()=>dialog.remove());
  dialog.querySelector('[data-filter]').onsubmit=event=>{event.preventDefault();load(dialog)};
  ensureCss();
  dialog.showModal();
  await load(dialog);
}

async function load(dialog){
  const form=dialog.querySelector('[data-filter]');
  const data=new FormData(form);
  const from=String(data.get('from')||'');
  const to=String(data.get('to')||'');
  const target=dialog.querySelector('[data-results]');
  target.innerHTML=`<div class="pi-muted">${esc(t('Auswertung wird geladen …','Loading analytics …'))}</div>`;
  try{
    const result=await api(`api/purchases/discount-analytics?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`);
    target.innerHTML=analyticsHtml(result);
  }catch(error){target.innerHTML=`<div class="pi-muted">${esc(error.message||String(error))}</div>`}
}

function analyticsHtml(result){
  const currency=result.baseCurrency||'EUR';
  const cards=[
    [t('Erkannte Rabatte','Detected discounts'),fmt(result.totalDiscountAmount,currency)],
    [t('Käufe mit Rabatt','Purchases with discount'),`${result.purchasesWithDiscount||0} / ${result.purchaseCount||0}`],
    [t('Artikelrabatte','Item discounts'),fmt(result.itemDiscountAmount,currency)],
    [t('Warenkorb / nicht zugeordnet','Basket / unallocated'),fmt(result.basketOrUnallocatedDiscountAmount,currency)]
  ];
  const lineMismatch=Math.abs(Number(result.discountLineAmount||0)-Number(result.totalDiscountAmount||0))>.01;
  const incomplete=result.incomplete?`<div class="pi-muted pi-analytics-note">${esc(t(`Unvollständig: Mindestens ein Fremdwährungsrabatt konnte wegen eines fehlenden historischen FX-Kurses nicht in ${currency} umgerechnet werden.`,`Incomplete: at least one foreign-currency discount could not be converted to ${currency} because a historical FX rate is missing.`))}</div>`:'';
  return `<div class="pi-muted pi-analytics-note">${esc(t(`Alle Geldwerte in Space-Basiswährung ${currency}.`,`All monetary values in the FullWorth Space base currency ${currency}.`))}</div>${incomplete}<div class="pi-analytics-cards">${cards.map(([label,value])=>`<div><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`).join('')}</div>${lineMismatch?`<div class="pi-muted pi-analytics-note">${esc(t('Hinweis: Die Summe einzelner Rabattzeilen weicht von der kanonischen Beleg-Rabattsumme ab. Die Gesamtkennzahl zählt deshalb nur die Beleg-Rabattsumme einmal.','Note: individual discount lines differ from the canonical receipt discount total. The headline total therefore counts only the canonical purchase discount once.'))}</div>`:''}<div class="pi-analytics-grid">${breakdown(t('Nach Händler','By merchant'),result.byMerchant,currency)}${breakdown(t('Nach Rabattart','By discount type'),(result.byType||[]).map(x=>({...x,name:typeLabel(x.name)})),currency)}${breakdown(t('Nach Produkt','By product'),result.byProduct,currency)}${breakdown(t('Nach Kategorie','By category'),result.byCategory,currency)}</div>`;
}
function breakdown(title,rows,currency){return `<section class="pi-section"><div class="pi-section-head"><h3>${esc(title)}</h3><span>${rows?.length||0}</span></div><div class="pi-list">${(rows||[]).map(row=>`<div class="pi-row"><span>${esc(row.name)}</span><strong>${esc(fmt(row.amount,currency))}</strong></div>`).join('')||`<div class="pi-muted">${esc(t('Keine Rabatte im Zeitraum.','No discounts in this period.'))}</div>`}</div></section>`}
function typeLabel(type){return({price_reduction:t('Preisreduktion','Price reduction'),percentage:t('Prozent','Percentage'),coupon:t('Coupon','Coupon'),loyalty:t('Treueprogramm','Loyalty'),multibuy:t('Mehrkauf','Multi-buy'),bundle:t('Bundle','Bundle'),employee:t('Mitarbeiter','Employee'),promotion:t('Aktion','Promotion'),other:t('Sonstiger Rabatt','Other')})[type]||type}

function install(){
  const anchor=document.querySelector('[data-product-manager]')||document.getElementById('amazon-import')||document.getElementById('scan-receipt');
  if(!anchor||document.querySelector('[data-discount-analytics]'))return;
  const button=document.createElement('button');
  button.type='button';button.className='ghost';button.dataset.discountAnalytics='1';button.textContent=t('Rabatte','Discounts');button.onclick=openPurchaseDiscountAnalytics;
  anchor.insertAdjacentElement('afterend',button);
}
function ensureCss(){
  // Production CSP forbids JS-created inline <style> blocks (style-src 'self'); load the module's
  // CSS as a same-origin linked stylesheet instead, exactly once.
  if(document.querySelector('link[data-feature-css="purchase-discount-analytics-ui"]'))return;
  const link=document.createElement('link');link.rel='stylesheet';link.href='/features/purchase-discount-analytics-ui.css';link.dataset.featureCss='purchase-discount-analytics-ui';document.head.appendChild(link);
}
install();
const observer=new MutationObserver(()=>{clearTimeout(install.timer);install.timer=setTimeout(install,100)});if(document.body)observer.observe(document.body,{subtree:true,childList:true});
