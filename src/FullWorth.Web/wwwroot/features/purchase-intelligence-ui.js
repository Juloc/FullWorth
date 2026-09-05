import { money, setMoneyLocale } from '../ui/money.js';

const $=(s,r=document)=>r.querySelector(s);
const $$=(s,r=document)=>[...r.querySelectorAll(s)];
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const sid=()=>localStorage.getItem('finance.space')||'';
const en=()=>document.documentElement.lang?.startsWith('en');
const t=(de,enText)=>en()?enText:de;
function withSpace(path){const [base,q='']=path.split('?');const p=new URLSearchParams(q);if(sid()&&!p.has('fullWorthSpaceId'))p.set('fullWorthSpaceId',sid());return `${base}?${p}`}
async function api(path,opt){const r=await fetch(`/bff/backend/${withSpace(path.replace(/^\//,''))}`,opt);if(!r.ok){let m=`${r.status}`;try{const b=await r.json();m=b.error||b.title||m}catch{}const e=new Error(m);e.status=r.status;throw e}if(r.status===204)return null;return r.json()}
const json=(method,body)=>({method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
function toast(message){const el=$('#toast');if(!el)return;el.textContent=message;el.classList.add('show');clearTimeout(toast.timer);toast.timer=setTimeout(()=>el.classList.remove('show'),3500)}
function fmt(v,c='EUR'){setMoneyLocale(en()?'en':'de');return v==null?'—':money(v,c)}
function numberOrNull(value){if(value===''||value==null)return null;const n=Number(value);return Number.isFinite(n)?n:null}
function nonNegative(value){const n=Number(value||0);return Number.isFinite(n)?Math.max(0,n):0}
function modal(title,cls='pi-dialog'){const d=document.createElement('dialog');d.className=cls;d.innerHTML=`<div class="pi-card"><div class="pi-head"><h2>${esc(title)}</h2><button type="button" data-close aria-label="${esc(t('Schließen','Close'))}">×</button></div><div data-body></div></div>`;document.body.appendChild(d);$('[data-close]',d).onclick=()=>d.close();d.addEventListener('close',()=>d.remove());d.showModal();return d}
function ensureCss(){if(document.querySelector('link[data-pi-css]'))return;const l=document.createElement('link');l.rel='stylesheet';l.href='/purchase-intelligence.css';l.dataset.piCss='1';document.head.appendChild(l)}ensureCss();

async function loadBase(){return Promise.all([api('api/categories?includeArchived=false'),api('api/product-identities/'),api('api/access/effective').catch(()=>({capabilities:{}}))])}

export async function openProductManager(){
  try{
    const [categories,products,access]=await loadBase();
    const suggestions=await api('api/product-learning/category-suggestions').catch(()=>[]);
    const canManage=!!access.capabilities?.['purchases.manage'];
    const d=modal(t('Produkte & Lernen','Products & learning'));const body=$('[data-body]',d);
    body.innerHTML=`${suggestions.length?`<section class="pi-section"><div class="pi-section-head"><h3>${esc(t('Lernvorschläge','Learning suggestions'))}</h3><span>${suggestions.length}</span></div><div class="pi-list">${suggestions.map((s,i)=>`<div class="pi-row"><div><strong>${esc(s.text)}</strong><div class="pi-muted">${s.count}× → ${esc(s.category)}</div></div>${canManage?`<button type="button" data-accept="${i}">${esc(t('Übernehmen','Accept'))}</button>`:''}</div>`).join('')}</div></section>`:''}<section class="pi-section"><div class="pi-section-head"><h3>${esc(t('Produktidentitäten','Product identities'))}</h3>${canManage?`<button type="button" data-new>${esc(t('+ Produkt','+ Product'))}</button>`:''}</div><div class="pi-list">${products.map((p,i)=>`<button type="button" class="pi-row pi-click" data-product="${i}"><span><strong>${esc(p.canonicalName)}</strong><small>${esc([p.brand,p.barcode,`${p.aliasCount||0} ${t('Aliase','aliases')}`].filter(Boolean).join(' · '))}</small></span><span>›</span></button>`).join('')||`<div class="pi-muted">${esc(t('Noch keine Produkte angelegt.','No products yet.'))}</div>`}</div></section>`;
    $$('[data-accept]',body).forEach(b=>b.onclick=async()=>{const s=suggestions[Number(b.dataset.accept)];b.disabled=true;try{await api('api/product-learning/category-suggestions/accept',json('POST',{text:s.text,categoryId:s.categoryId,productIdentityId:s.productIdentityId||null,canonicalName:s.productName||s.text}));toast(t('Produktkategorie gelernt.','Product category learned.'));d.close();openProductManager()}catch(e){toast(e.message);b.disabled=false}});
    $('[data-new]',body)?.addEventListener('click',()=>openProductEditor(null,categories,d));
    $$('[data-product]',body).forEach(b=>b.onclick=()=>openProductEditor(products[Number(b.dataset.product)],categories,d));
  }catch(e){toast(e.message)}
}

async function openProductEditor(product,categories,parent){
  const d=modal(product?product.canonicalName:t('Neues Produkt','New product'),'pi-small-dialog');const body=$('[data-body]',d);
  const aliases=product?await api(`api/product-learning/products/${product.id}/aliases`).catch(()=>[]):[];
  const history=product?await api(`api/product-identities/${product.id}/history`).catch(()=>null):null;
  const historyRows=Array.isArray(history)?history:(history?.items||history?.history||[]);
  body.innerHTML=`<form data-form class="pi-form"><label>${esc(t('Name','Name'))}<input name="name" required value="${esc(product?.canonicalName||'')}"></label><div class="pi-grid"><label>${esc(t('Marke','Brand'))}<input name="brand" value="${esc(product?.brand||'')}"></label><label>Barcode<input name="barcode" value="${esc(product?.barcode||'')}"></label><label>${esc(t('Standardkategorie','Default category'))}<select name="category"><option value="">—</option>${categories.filter(c=>!c.isArchived).map(c=>`<option value="${c.id}" ${c.id===product?.defaultCategoryId?'selected':''}>${esc(c.name)}</option>`).join('')}</select></label><label>${esc(t('Einheit','Unit'))}<select name="unit"><option value="">—</option>${['piece','g','kg','ml','l','m','cm'].map(x=>`<option value="${x}" ${x===product?.unitKind?'selected':''}>${x}</option>`).join('')}</select></label><label>${esc(t('Packungsgröße','Package size'))}<input name="size" type="number" step="0.000001" value="${product?.unitSize??''}"></label></div><div class="pi-actions"><button>${esc(t('Speichern','Save'))}</button></div></form>${product?`<section class="pi-section"><div class="pi-section-head"><h3>${esc(t('Aliase','Aliases'))}</h3></div><div class="pi-list">${aliases.map(a=>`<div class="pi-row"><span>${esc(a.normalizedText)}</span><small>${esc(a.source)}</small></div>`).join('')||`<div class="pi-muted">${esc(t('Keine Aliase.','No aliases.'))}</div>`}</div><form data-alias class="pi-inline"><input name="alias" placeholder="${esc(t('Artikelnamen merken','Remember item text'))}" required><button>${esc(t('Hinzufügen','Add'))}</button></form></section><section class="pi-section"><div class="pi-section-head"><h3>${esc(t('Preisverlauf','Price history'))}</h3></div><div class="pi-list">${historyRows.slice(0,20).map(x=>`<div class="pi-row"><span>${esc(x.merchant||'')}</span><span>${x.comparisonSafe?fmt(x.comparableUnitPrice,x.currency||'EUR'):fmt(x.total,x.currency||'EUR')}</span></div>`).join('')||`<div class="pi-muted">${esc(t('Noch keine vergleichbaren Preise.','No comparable prices yet.'))}</div>`}</div></section>`:''}`;
  $('[data-form]',body).onsubmit=async e=>{e.preventDefault();const f=new FormData(e.currentTarget);const payload={canonicalName:f.get('name'),brand:f.get('brand')||null,barcode:f.get('barcode')||null,defaultCategoryId:f.get('category')||null,unitKind:f.get('unit')||null,unitSize:f.get('size')?Number(f.get('size')):null};try{if(product)await api(`api/product-identities/${product.id}`,json('PUT',payload));else await api('api/product-identities/',json('POST',payload));toast(t('Produkt gespeichert.','Product saved.'));d.close();parent?.close();openProductManager()}catch(err){toast(err.message)}};
  $('[data-alias]',body)?.addEventListener('submit',async e=>{e.preventDefault();const f=new FormData(e.currentTarget);try{await api(`api/product-identities/${product.id}/aliases`,json('POST',{text:f.get('alias'),confidence:1,source:'manual'}));toast(t('Alias gespeichert.','Alias saved.'));d.close();openProductEditor(product,categories,parent)}catch(err){toast(err.message)}})
}

export async function openSmartReview(purchaseId=null){
  try{
    if(!purchaseId){const rows=await api('api/purchases');const pending=rows.filter(x=>x.status!=='confirmed'||!x.transactionId);if(!pending.length){toast(t('Keine offenen Käufe.','No purchases need review.'));return}const d=modal(t('Kauf zur Prüfung wählen','Choose purchase to review'),'pi-small-dialog');const body=$('[data-body]',d);body.innerHTML=`<div class="pi-list">${pending.map(x=>`<button type="button" class="pi-row pi-click" data-purchase="${x.id}"><span><strong>${esc(x.merchant||x.externalOrderId||t('Bon','Receipt'))}</strong><small>${esc(x.purchaseDate||'')} · ${x.items?.length||0} ${esc(t('Artikel','items'))}</small></span><span>${fmt(x.totalAmount,x.currency)}</span></button>`).join('')}</div>`;$$('[data-purchase]',body).forEach(b=>b.onclick=()=>{d.close();openSmartReview(b.dataset.purchase)});return}
    await reviewPurchase(purchaseId);
  }catch(e){toast(e.message)}
}

async function reviewPurchase(id){
  const [purchase,categories,products,review,financials]=await Promise.all([
    api(`api/purchases/${id}`),
    api('api/categories?includeArchived=false'),
    api('api/product-identities/'),
    api(`api/purchase-review/${id}`),
    api(`api/purchases/${id}/financials`).catch(()=>null)
  ]);
  const finance=financials||emptyFinancials(purchase);
  const financialByItem=new Map((finance.items||[]).map(x=>[x.purchaseItemId,x]));
  const itemIndexById=new Map((purchase.items||[]).map((x,i)=>[x.id,i]));
  const suggestions=await Promise.all((purchase.items||[]).map(item=>api(`api/product-identities/suggest?text=${encodeURIComponent(item.name)}`).catch(()=>null)));
  const d=modal(purchase.merchant||t('Bon prüfen','Review receipt'),'pi-review-dialog');const body=$('[data-body]',d);
  const categoryOptions=id=>`<option value="">${esc(t('Nicht kategorisiert','Uncategorized'))}</option>${categories.filter(c=>!c.isArchived).map(c=>`<option value="${c.id}" ${c.id===id?'selected':''}>${esc(c.name)}</option>`).join('')}`;
  const productOptions=id=>`<option value="">${esc(t('Kein Produkt','No product'))}</option>${products.map(p=>`<option value="${p.id}" ${p.id===id?'selected':''}>${esc(p.canonicalName)}</option>`).join('')}`;
  const itemRows=(purchase.items||[]).map((item,i)=>itemRow(item,financialByItem.get(item.id),categoryOptions(item.categoryId),productOptions(suggestions[i]?.id))).join('');
  const discounts=(finance.discounts||[]).map(discount=>discountRow(discount,itemIndexById.get(discount.purchaseItemId))).join('');
  body.innerHTML=`<div class="pi-review-layout"><aside class="pi-receipt"><div class="pi-reconcile" data-reconcile>${reconcileHtml(review,purchase.currency)}</div>${purchase.hasReceipt?`<a class="pi-receipt-link" target="_blank" rel="noopener" href="/bff/backend/${withSpace(`api/purchases/${id}/receipt`)}">${esc(t('Originalbon öffnen','Open original receipt'))}</a>`:''}${financialSummary(finance,purchase.currency)}</aside><main class="pi-items"><div class="pi-section-head"><h3>${esc(t('Artikel','Items'))}</h3><button type="button" class="ghost" data-add>${esc(t('+ Artikel','+ Item'))}</button></div><div data-items>${itemRows}</div><section class="pi-section pi-discounts"><div class="pi-section-head"><div><h3>${esc(t('Rabatte & Aktionen','Discounts & promotions'))}</h3><small data-discount-sum class="pi-muted"></small></div><button type="button" class="ghost" data-add-discount>${esc(t('+ Rabatt','+ Discount'))}</button></div><div data-discounts>${discounts||`<div class="pi-muted" data-no-discounts>${esc(t('Keine einzelnen Rabattzeilen erkannt.','No individual discount lines detected.'))}</div>`}</div></section><div class="pi-review-actions"><button type="button" class="ghost" data-save>${esc(t('Änderungen speichern','Save changes'))}</button><button type="button" class="ghost" data-diff ${review.fullyReconciled?'disabled':''}>${esc(review.differenceConfirmed?t('Differenz bestätigt','Difference confirmed'):t('Differenz bestätigen','Confirm difference'))}</button><button type="button" data-confirm>${esc(t('Kauf bestätigen','Confirm purchase'))}</button></div></main></div>`;

  $('[data-add]',body).onclick=()=>{$('[data-items]',body).insertAdjacentHTML('beforeend',itemRow({id:null,name:'',quantity:1,totalPrice:0,unitPrice:null,categoryId:null},null,categoryOptions(null),productOptions(null)));bindReviewControls(body)};
  $('[data-add-discount]',body).onclick=()=>{ $('[data-no-discounts]',body)?.remove(); $('[data-discounts]',body).insertAdjacentHTML('beforeend',discountRow(null,null)); bindReviewControls(body); updateDiscountSum(body,purchase.currency)};
  bindReviewControls(body);
  updateDiscountSum(body,purchase.currency);

  $('[data-save]',body).onclick=async()=>{try{await saveReview(id,body,purchase.currency);toast(t('Kaufdaten gespeichert.','Purchase data saved.'));d.close();reviewPurchase(id)}catch(e){toast(e.message)}};
  $('[data-diff]',body).onclick=async()=>{try{await saveReview(id,body,purchase.currency);await api(`api/purchase-review/${id}/confirm-difference`,{method:'POST'});toast(t('Differenz bestätigt.','Difference confirmed.'));d.close();reviewPurchase(id)}catch(e){toast(e.message)}};
  $('[data-confirm]',body).onclick=async()=>{try{await saveReview(id,body,purchase.currency);await api(`api/purchase-review/${id}/confirm`,{method:'POST'});toast(t('Kauf bestätigt.','Purchase confirmed.'));d.close()}catch(e){toast(e.message)}};
}

function emptyFinancials(purchase){return{purchaseId:purchase.id,currency:purchase.currency,totalAmount:purchase.totalAmount,subtotalAmount:null,discountAmount:0,depositAmount:0,taxAmount:null,roundingAmount:0,items:[],discounts:[]}}

function financialSummary(f,currency){return `<section class="pi-section pi-financials"><div class="pi-section-head"><h3>${esc(t('Bonrechnung','Receipt totals'))}</h3><span>${fmt(f.totalAmount,currency)}</span></div><div class="pi-financial-grid"><label>${esc(t('Zwischensumme','Subtotal'))}<input data-subtotal type="number" step="0.01" value="${f.subtotalAmount??''}"></label><label>${esc(t('Rabatte gesamt','Total discounts'))}<input data-discount-total type="number" min="0" step="0.01" value="${f.discountAmount??0}"></label><label>${esc(t('Pfand','Deposits'))}<input data-deposit-total type="number" min="0" step="0.01" value="${f.depositAmount??0}"></label><label>${esc(t('Steuer enthalten','Tax included'))}<input data-tax-total type="number" step="0.01" value="${f.taxAmount??''}"></label><label>${esc(t('Rundung','Rounding'))}<input data-rounding type="number" step="0.01" value="${f.roundingAmount??0}"></label><div class="pi-financial-result"><span>${esc(t('Berechnet','Calculated'))}</span><strong>${fmt(f.calculatedTotal,currency)}</strong>${f.calculationDifference!=null?`<small>${esc(t('Differenz','Difference'))}: ${fmt(f.calculationDifference,currency)}</small>`:''}</div></div></section>`}

function itemRow(item,financial,categories,products){return `<div class="pi-item" data-item-id="${esc(item.id||'')}"><div class="pi-item-main"><input class="pi-name" value="${esc(item.name||'')}" placeholder="${esc(t('Artikelname','Item name'))}"><input class="pi-qty" type="number" step="0.001" min="0.001" value="${item.quantity??1}" aria-label="${esc(t('Menge','Quantity'))}"><input class="pi-unit" type="number" step="0.01" value="${item.unitPrice??''}" placeholder="${esc(t('Einzelpreis','Unit price'))}"><input class="pi-total" type="number" step="0.01" value="${item.totalPrice??0}" aria-label="${esc(t('Gesamtpreis','Total price'))}"><select class="pi-category">${categories}</select><select class="pi-product">${products}</select><button type="button" class="ghost pi-delete" aria-label="${esc(t('Artikel löschen','Delete item'))}">×</button></div><div class="pi-item-financials"><label>${esc(t('Originalpreis','Original price'))}<input class="pi-original" type="number" step="0.01" value="${financial?.originalUnitPrice??''}"></label><label>${esc(t('Artikelrabatt','Item discount'))}<input class="pi-item-discount" type="number" min="0" step="0.01" value="${financial?.discountAmount??0}"></label><label>${esc(t('Rabatt-Label','Discount label'))}<input class="pi-item-discount-label" value="${esc(financial?.discountLabel||'')}"></label><label>${esc(t('Pfand','Deposit'))}<input class="pi-item-deposit" type="number" min="0" step="0.01" value="${financial?.depositAmount??0}"></label></div></div>`}

const discountTypes=['price_reduction','percentage','coupon','loyalty','multibuy','bundle','employee','promotion','other'];
function discountRow(discount,itemIndex){const type=discount?.type||'other';return `<div class="pi-discount" data-discount-id="${esc(discount?.id||'')}"><select class="pi-discount-type" aria-label="${esc(t('Rabattart','Discount type'))}">${discountTypes.map(x=>`<option value="${x}" ${x===type?'selected':''}>${esc(discountTypeLabel(x))}</option>`).join('')}</select><input class="pi-discount-label" value="${esc(discount?.label||'')}" placeholder="${esc(t('Bezeichnung','Label'))}"><input class="pi-discount-amount" type="number" min="0" step="0.01" value="${discount?.amount??0}" aria-label="${esc(t('Rabattbetrag','Discount amount'))}"><select class="pi-discount-item" aria-label="${esc(t('Artikelbezug','Item relation'))}" data-selected-index="${itemIndex??''}"></select><input class="pi-discount-percent" type="number" min="0" max="100" step="0.01" value="${discount?.percentage??''}" placeholder="%"><input class="pi-discount-code" value="${esc(discount?.couponCode||'')}" placeholder="${esc(t('Coupon-Code','Coupon code'))}"><input class="pi-discount-raw" value="${esc(discount?.rawText||'')}" placeholder="${esc(t('Bontext','Receipt text'))}"><button type="button" class="ghost pi-discount-delete" aria-label="${esc(t('Rabatt löschen','Delete discount'))}">×</button></div>`}
function discountTypeLabel(type){const labels={price_reduction:t('Preisreduktion','Price reduction'),percentage:t('Prozent','Percentage'),coupon:t('Coupon','Coupon'),loyalty:t('Treueprogramm','Loyalty'),multibuy:t('Mehrkauf','Multi-buy'),bundle:t('Bundle','Bundle'),employee:t('Mitarbeiter','Employee'),promotion:t('Aktion','Promotion'),other:t('Sonstiger Rabatt','Other')};return labels[type]||type}

function bindReviewControls(root){
  $$('.pi-delete',root).forEach(b=>b.onclick=()=>{b.closest('.pi-item')?.remove();refreshDiscountItemOptions(root)});
  $$('.pi-discount-delete',root).forEach(b=>b.onclick=()=>{b.closest('.pi-discount')?.remove();updateDiscountSum(root)});
  $$('.pi-discount-amount',root).forEach(input=>input.oninput=()=>updateDiscountSum(root));
  refreshDiscountItemOptions(root);
}
function refreshDiscountItemOptions(root){const items=$$('.pi-item',root);$$('.pi-discount-item',root).forEach(select=>{const previous=select.value||select.dataset.selectedIndex||'';select.innerHTML=`<option value="">${esc(t('Belegebene','Receipt level'))}</option>${items.map((row,i)=>`<option value="${i}">${i+1}. ${esc($('.pi-name',row)?.value||t('Artikel','Item'))}</option>`).join('')}`;if(previous!==''&&Number(previous)<items.length)select.value=previous;select.dataset.selectedIndex=''});$$('.pi-name',root).forEach(input=>{if(input.dataset.discountBound)return;input.dataset.discountBound='1';input.addEventListener('input',()=>refreshDiscountItemOptions(root))})}
function updateDiscountSum(root,currency='EUR'){const sum=$$('.pi-discount-amount',root).reduce((total,input)=>total+nonNegative(input.value),0);const target=$('[data-discount-sum]',root);if(target)target.textContent=`${t('Zeilensumme','Line total')}: ${fmt(sum,currency)}`}

async function saveReview(purchaseId,root,currency){
  const rows=$$('.pi-item',root);
  if(!rows.length)throw new Error(t('Mindestens ein Artikel ist erforderlich.','At least one item is required.'));
  const itemFinancialDraft=[];
  for(const row of rows){
    const name=$('.pi-name',row).value.trim(),productId=$('.pi-product',row).value;
    if(!name)throw new Error(t('Jeder Artikel braucht einen Namen.','Every item needs a name.'));
    if(productId)await api(`api/product-identities/${productId}/aliases`,json('POST',{text:name,confidence:1,source:'manual'}));
    itemFinancialDraft.push({
      originalUnitPrice:numberOrNull($('.pi-original',row).value),
      discountAmount:nonNegative($('.pi-item-discount',row).value),
      discountLabel:$('.pi-item-discount-label',row).value.trim()||null,
      depositAmount:nonNegative($('.pi-item-deposit',row).value)
    });
  }
  const itemPayload=rows.map(row=>({categoryId:$('.pi-category',row).value||null,name:$('.pi-name',row).value.trim(),brand:null,sku:null,asin:null,quantity:Number($('.pi-qty',row).value||1),unitPrice:numberOrNull($('.pi-unit',row).value),totalPrice:Number($('.pi-total',row).value||0),currency,notes:null}));
  const discountDraft=$$('.pi-discount',root).map(row=>({
    id:row.dataset.discountId||null,
    itemIndex:$('.pi-discount-item',row).value===''?null:Number($('.pi-discount-item',row).value),
    type:$('.pi-discount-type',row).value,
    label:$('.pi-discount-label',row).value.trim(),
    amount:nonNegative($('.pi-discount-amount',row).value),
    percentage:numberOrNull($('.pi-discount-percent',row).value),
    couponCode:$('.pi-discount-code',row).value.trim()||null,
    rawText:$('.pi-discount-raw',row).value.trim()||null
  }));
  if(discountDraft.some(x=>!x.label))throw new Error(t('Jeder Rabatt braucht eine Bezeichnung.','Every discount needs a label.'));

  // The historical items endpoint replaces item rows. Save items first, then resolve the fresh item ids
  // and atomically reattach item-level financial metadata and discount relationships by visible row order.
  await api(`api/purchases/${purchaseId}/items`,json('PUT',itemPayload));
  const fresh=await api(`api/purchases/${purchaseId}/financials`);
  if((fresh.items||[]).length!==rows.length)throw new Error(t('Artikel konnten nicht eindeutig für Rabattdaten zugeordnet werden.','Items could not be mapped safely to financial metadata.'));
  const financialItems=(fresh.items||[]).map((item,index)=>({purchaseItemId:item.purchaseItemId,...itemFinancialDraft[index]}));
  const discounts=discountDraft.map(discount=>({
    id:discount.id,
    purchaseItemId:discount.itemIndex==null?null:fresh.items[discount.itemIndex]?.purchaseItemId||null,
    type:discount.type,
    label:discount.label,
    amount:discount.amount,
    percentage:discount.percentage,
    couponCode:discount.couponCode,
    rawText:discount.rawText,
    source:'manual',
    confidence:null
  }));
  const financialPayload={
    subtotalAmount:numberOrNull($('[data-subtotal]',root)?.value),
    discountAmount:nonNegative($('[data-discount-total]',root)?.value),
    depositAmount:nonNegative($('[data-deposit-total]',root)?.value),
    taxAmount:numberOrNull($('[data-tax-total]',root)?.value),
    roundingAmount:Number($('[data-rounding]',root)?.value||0),
    items:financialItems,
    discounts
  };
  if(!Number.isFinite(financialPayload.roundingAmount))financialPayload.roundingAmount=0;
  await api(`api/purchases/${purchaseId}/financials`,json('PUT',financialPayload));
}

function reconcileHtml(r,currency){const financial=r.reconciliationBasis==='receipt_financials';return `<h3>${esc(t('Abgleich','Reconciliation'))}</h3><div class="pi-rec-grid"><div><span>${esc(t('Bank','Bank'))}</span><strong>${fmt(r.transactionAmount==null?null:Math.abs(r.transactionAmount),currency)}</strong></div><div><span>${esc(t('Bon','Receipt'))}</span><strong>${fmt(r.purchaseTotal,currency)}</strong></div><div><span>${esc(financial?t('Bonrechnung','Receipt equation'):t('Artikel','Items'))}</span><strong>${fmt(financial?r.calculatedTotal:r.itemTotal,currency)}</strong></div><div><span>${esc(t('Differenz','Difference'))}</span><strong>${fmt(r.itemDifference,currency)}</strong></div></div>${financial?`<div class="pi-equation"><span>${fmt(r.subtotalAmount,currency)}</span><b>−</b><span>${fmt(r.discountAmount,currency)}</span><b>+</b><span>${fmt(r.depositAmount,currency)}</span>${Number(r.roundingAmount||0)!==0?`<b>${Number(r.roundingAmount)>0?'+':'−'}</b><span>${fmt(Math.abs(Number(r.roundingAmount)),currency)}</span>`:''}</div>`:''}${r.differenceConfirmed?`<div class="pi-ok">✓ ${esc(t('Aktuelle Differenz bestätigt','Current difference confirmed'))}</div>`:''}`}

function install(){const scan=$('#scan-receipt'),amazon=$('#amazon-import');if(scan&&!document.querySelector('[data-smart-review]')){const b=document.createElement('button');b.type='button';b.className='ghost';b.dataset.smartReview='1';b.textContent=t('Smart Review','Smart Review');b.onclick=()=>openSmartReview();scan.insertAdjacentElement('afterend',b)}if(amazon&&!document.querySelector('[data-product-manager]')){const b=document.createElement('button');b.type='button';b.className='ghost';b.dataset.productManager='1';b.textContent=t('Aliase & Lernen','Aliases & learning');b.onclick=openProductManager;amazon.insertAdjacentElement('afterend',b)}}
function boot(){install();const o=new MutationObserver(()=>{clearTimeout(boot.timer);boot.timer=setTimeout(install,100)});document.body&&o.observe(document.body,{subtree:true,childList:true})}boot();
