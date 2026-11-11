import { confirmMessage } from '../../components/confirm.js';
import { createFindsCard, openImportPdf as openPdf } from './paperless-finds.js';
import { openAdd, closeAdd } from './add-dialog.js';
import {
  $, $$, euro2 as euro, euro as euro0, esc, val as value, num as number, setVal as set,
  spaceId, monthLabel as month, signedEuro, api, json, notify as showMessage,
  readProfile, fillProfile
} from './shared.js';

init();

function init(){
  const tabs=$('.comp-tabs');
  const toast=$('#comp-error');
  if(!tabs||!toast)return;


  // Der Reiter steht im Markup der Seite - nichts wird hier mehr in eine bereits gezeichnete Leiste geschoben.
  toast.insertAdjacentHTML('beforebegin',optimizerMarkup());

  $$('[data-extended-tab]').forEach(button=>button.addEventListener('click',()=>openExtendedTab(button.dataset.extendedTab)));
  $('#optimizer-run').addEventListener('click',()=>loadOptimizer().catch(showError));
  // Hochladen, Prüfen und Speichern stehen im Dialog "Hinzufügen" (add-dialog.js).
  $('#payslip-extract').addEventListener('click',()=>extractPayslip().catch(showError));
  $('#payslip-extract-batch').addEventListener('click',()=>extractBatch().catch(showError));
  $('#payslip-save').addEventListener('click',()=>savePayslip().catch(showError));
  // Der Dialog zeigt die Funde erst, wenn er aufgeht - vorher gibt es nichts zu sehen und nichts zu holen.
  document.addEventListener('fw-add-opened',()=>loadPaperless().catch(showError));
  document.addEventListener('fw-gehalt-open',()=>loadPayslips().catch(showError));
  $('#space-select').addEventListener('change',()=>loadPayslips().catch(showError));
}

async function openExtendedTab(name){
  $$('.comp-tabs button').forEach(button=>button.classList.toggle('active',button.dataset.extendedTab===name));
  $$('.comp-tab').forEach(tab=>tab.classList.toggle('active',tab.id===`tab-${name}`));
  if(name==='optimizer')await loadOptimizer().catch(showError);
}

function optimizerMarkup(){return `
<section id="tab-optimizer" class="comp-tab">
  <article class="panel comp-card extended-intro">
    <div><h2>Was lohnt sich mehr?</h2></div>
    <label>Arbeitgeberbudget / Monat<input id="optimizer-budget" type="number" min="0" step="25" value="300"></label>
    <button id="optimizer-run" class="btn btn-primary" type="button">Vergleichen</button>
  </article>
  <div class="optimizer-section"><h2>Gehaltserhöhung</h2><div id="optimizer-raises" class="optimizer-grid"></div></div>
  <div class="optimizer-section"><h2>Teilzeit</h2><div id="optimizer-parttime" class="optimizer-grid"></div></div>
  <div class="optimizer-section"><h2>Arbeitgeberbudget</h2><div id="optimizer-budget-results" class="optimizer-grid"></div><p class="extended-note">„Steuerfreier Benefit“ ist eine Vergleichssimulation. Ob ein konkreter Benefit steuerfrei ist, muss für dessen jeweilige gesetzliche Voraussetzungen geprüft werden.</p></div>
</section>`}

async function loadOptimizer(){
  const profile=readProfile();
  const budget=number('optimizer-budget');
  const result=await api('api/compensation/insights',json('POST',{profile,employerBudgetMonthly:budget}));
  renderOptions('#optimizer-raises',result.salaryRaises);
  renderOptions('#optimizer-parttime',result.partTime);
  renderOptions('#optimizer-budget-results',result.employerBudgetOptions,true);
}

function renderOptions(selector,options,ranked=false){
  const root=$(selector);
  root.innerHTML=(options||[]).map((option,index)=>`<article class="panel optimizer-card ${ranked&&index===0?'best':''}">
    ${ranked&&index===0?'<span class="optimizer-badge">höchster Gesamtwert</span>':''}
    <h3>${esc(option.title)}</h3><p>${esc(option.description)}</p>
    <div class="optimizer-value">${signedEuro(option.fullWorthDeltaAnnual)}</div><small>Gesamtwert / Jahr</small>
    <div class="optimizer-details">
      <span>Netto <strong>${signedEuro(option.cashNetDeltaAnnual)}</strong></span>
      <span>AG-Kosten <strong>${signedEuro(option.employerCostDeltaAnnual)}</strong></span>
      <span>Neues Netto <strong>${euro0.format(option.calculation.estimatedCashNetAnnual)}</strong></span>
    </div>
    <button type="button" class="btn btn-secondary" data-load-profile>In Rechner übernehmen</button>
  </article>`).join('');
  root.querySelectorAll('[data-load-profile]').forEach((button,index)=>button.addEventListener('click',()=>loadProfileIntoCalculator(options[index].profile)));
}

function loadProfileIntoCalculator(profile){
  fillProfile(profile);
  $$('.comp-tabs button').forEach(b=>b.classList.toggle('active',b.dataset.tab==='calculator'));
  $$('.comp-tab').forEach(tab=>tab.classList.toggle('active',tab.id==='tab-calculator'));
  $('#calculate').click();
}

async function extractPayslip(){
  const file=$('#payslip-file').files?.[0];if(!file)throw new Error('Bitte eine Lohnabrechnung auswählen.');
  const data=new FormData();data.append('file',file);
  status('Analyse läuft …');
  const result=await api('api/compensation/payslips/extract',{method:'POST',body:data});
  fillExtraction(result,null);
  status(`Erkennungssicherheit ${Number(result.confidencePercent||0).toLocaleString('de-DE')} %. ${(result.warnings||[]).join(' ')}`);
}

// Die Statuszeile steht nur da, solange sie etwas zu sagen hat.
function status(text){const el=$('#payslip-extraction-status');el.textContent=text;el.hidden=!text}

// Mit importId prueft der Benutzer einen Fund aus Paperless; das Speichern bestaetigt dann genau diesen.
let reviewedImport=null;
function fillExtraction(x,importId,title){
  reviewedImport=importId;
  $('#ps-review-hint').textContent=importId?`Aus Paperless: „${title}“. Werte mit dem Original abgleichen, dann speichern.`:'';
  // Gibt es etwas zu pruefen, zeigt der Dialog "Hinzufuegen" das Formular; geleert wird es still.
  if(importId||x.period||x.grossPay!=null)openAdd('payslip');
  set('ps-period',x.period||'');set('ps-gross',x.grossPay);set('ps-net',x.netPay);set('ps-payout',x.payout);set('ps-tax',x.wageTax);set('ps-soli',x.solidaritySurcharge);set('ps-church',x.churchTax);set('ps-rv',x.pensionInsurance);set('ps-av',x.unemploymentInsurance);set('ps-kv',x.healthInsurance);set('ps-pv',x.careInsurance);set('ps-car',x.companyCarTaxableBenefit);set('ps-bav',x.bavEmployee);set('ps-bav-ag',x.bavEmployer);set('ps-bonus',x.bonus)}

let batchItems=[];
async function extractBatch(){
  const files=[...($('#payslip-file').files||[])];
  if(!files.length)throw new Error('Bitte mindestens eine Lohnabrechnung auswählen.');
  if(files.length>40)throw new Error('Höchstens 40 Abrechnungen pro Durchlauf.');
  const data=new FormData();files.forEach(f=>data.append('files',f));
  status(`Analysiere ${files.length} Abrechnung(en) … mit Codex kann das etwas dauern.`);
  const result=await api('api/compensation/payslips/extract-batch',{method:'POST',body:data});
  batchItems=((result&&result.items)||[]).map(it=>({...it}));
  renderBatch(batchItems);
  const ok=batchItems.filter(it=>it.result).length;
  status(`${ok} von ${batchItems.length} Abrechnung(en) erkannt. Werte prüfen und speichern.`);
}

function renderBatch(items){
  const root=$('#payslip-batch');if(!root)return;
  if(!items.length){root.hidden=true;root.innerHTML='';return}
  root.hidden=false;
  root.innerHTML=`<div class="va-batch-head"><h3 class="va-sub">Mehrere Abrechnungen prüfen</h3><button id="payslip-save-all" class="btn btn-primary" type="button">Alle speichern</button></div>`+
    `<div class="payslip-batch-list">${items.map((it,i)=>batchRow(it,i)).join('')}</div>`;
  root.querySelectorAll('[data-batch-save]').forEach(b=>b.addEventListener('click',()=>saveBatchRow(Number(b.dataset.batchSave)).catch(showError)));
  $('#payslip-save-all')?.addEventListener('click',()=>saveAllBatch().catch(showError));
}

function batchRow(it,i){
  if(it.error||!it.result)return `<div class="payslip-batch-row error"><div class="pb-file">${esc(it.fileName||'Datei')}</div><div class="pb-msg">${esc(it.error||'Nicht erkannt.')}</div></div>`;
  const r=it.result;
  return `<div class="payslip-batch-row" data-batch-index="${i}">`+
    `<div class="pb-file" title="${esc(it.fileName||'')}"><span>${esc(it.fileName||'')}</span><small>${Number(r.confidencePercent||0).toLocaleString('de-DE')} %</small></div>`+
    `<label>Monat<input data-pb="period" type="date" value="${esc(r.period||'')}"></label>`+
    `<label>Brutto<input data-pb="gross" type="number" step="0.01" value="${pbn(r.grossPay)}"></label>`+
    `<label>Netto<input data-pb="net" type="number" step="0.01" value="${pbn(r.netPay)}"></label>`+
    `<label>Auszahlung<input data-pb="payout" type="number" step="0.01" value="${pbn(r.payout??r.netPay)}"></label>`+
    `<button type="button" class="btn btn-secondary" data-batch-save="${i}">Speichern</button>`+
  `</div>`;
}
function pbn(v){const n=Number(v);return Number.isFinite(n)?n:''}

function batchPayload(row,r){
  const g=k=>{const el=row.querySelector(`[data-pb="${k}"]`);return el?el.value:''};
  const num=k=>Number(g(k))||0;
  return{period:g('period'),grossPay:num('gross'),netPay:num('net'),payout:num('payout'),wageTax:Number(r.wageTax)||0,solidaritySurcharge:Number(r.solidaritySurcharge)||0,churchTax:Number(r.churchTax)||0,pensionInsurance:Number(r.pensionInsurance)||0,unemploymentInsurance:Number(r.unemploymentInsurance)||0,healthInsurance:Number(r.healthInsurance)||0,careInsurance:Number(r.careInsurance)||0,companyCarTaxableBenefit:Number(r.companyCarTaxableBenefit)||0,bavEmployee:Number(r.bavEmployee)||0,bavEmployer:Number(r.bavEmployer)||0,bonus:Number(r.bonus)||0,note:null,source:'confirmed-ocr'};
}

async function saveBatchRow(i){
  const space=spaceId();if(!space)throw new Error('Kein Finanzbereich ausgewählt.');
  const row=$(`.payslip-batch-row[data-batch-index="${i}"]`),it=batchItems[i];
  if(!row||!it||!it.result)return;
  const payload=batchPayload(row,it.result);
  if(!payload.period)throw new Error('Abrechnungsmonat fehlt.');
  await api(`api/compensation/payslips?fullWorthSpaceId=${space}`,json('POST',payload));
  row.classList.add('saved');const btn=row.querySelector('[data-batch-save]');if(btn){btn.textContent='Gespeichert';btn.disabled=true}
  await loadPayslips();
}

async function saveAllBatch(){
  const rows=$$('.payslip-batch-row[data-batch-index]');
  for(const row of rows){if(row.classList.contains('saved'))continue;await saveBatchRow(Number(row.dataset.batchIndex)).catch(showError)}
  showMessage('Geprüfte Abrechnungen gespeichert.');
}

async function savePayslip(){
  const space=spaceId();if(!space)throw new Error('Kein Finanzbereich ausgewählt.');if(!value('ps-period'))throw new Error('Abrechnungsdatum fehlt.');
  const payload={period:value('ps-period'),grossPay:number('ps-gross'),netPay:number('ps-net'),payout:number('ps-payout'),wageTax:number('ps-tax'),solidaritySurcharge:number('ps-soli'),churchTax:number('ps-church'),pensionInsurance:number('ps-rv'),unemploymentInsurance:number('ps-av'),healthInsurance:number('ps-kv'),careInsurance:number('ps-pv'),companyCarTaxableBenefit:number('ps-car'),bavEmployee:number('ps-bav'),bavEmployer:number('ps-bav-ag'),bonus:number('ps-bonus'),note:value('ps-note')||null,source:'confirmed-ocr'};
  await api(`api/compensation/payslips?fullWorthSpaceId=${space}${reviewedImport?`&importId=${reviewedImport}`:''}`,json('POST',payload));
  if(reviewedImport)fillExtraction({},null);
  showMessage('Lohnabrechnung gespeichert.');
  closeAdd();
  await Promise.all([loadPayslips(),loadPaperless()]);
}

// Der Verlauf (verlauf.js) liest Abrechnungen und Vertragsstaende selbst; hier steht nur, was er von uns braucht und
// nicht selbst weiss: die offenen Funde aus Paperless und was ein Knopf in seinen Jahren ausloesen darf. Jede
// Aenderung meldet sich als Ereignis, statt dass jemand eine Zeichenfunktion eines anderen Moduls aufruft.
function emitChanged(){
  document.dispatchEvent(new CustomEvent('fw-gehalt-changed',{detail:{
    finds:finds.state.items,
    searching:finds.state.connected&&!!finds.state.preset?.lastCheckedAt&&!finds.state.preset.lastError,
    actions:{openPdf,deletePayslip:id=>deletePayslip(id).catch(showError),review:reviewFind}
  }}));
}
const loadPayslips=async()=>{if(spaceId())emitChanged()};

async function deletePayslip(id){
  if(!await confirmMessage({message:'Gespeicherte Lohnabrechnung löschen?',title:'Lohnabrechnung löschen',confirmLabel:'Löschen',cancelLabel:'Abbrechen',destructive:true}))return;
  await api(`api/compensation/payslips/${id}?fullWorthSpaceId=${spaceId()}`,{method:'DELETE'});await loadPayslips();
}

const finds=createFindsCard({
  kind:'payslip',root:'#paperless-body',isActive:()=>!!$('#verlauf-add')?.open,onError:showError,
  texts:{preset:'Lohnabrechnungen',noun:'Lohnabrechnungen',typePattern:'(lohn|gehalt|entgelt|verdienst)s?abrechnung',empty:'Keine Abrechnungen zur Prüfung.'},
  group:item=>{const x=item.extraction;return x.period?{key:x.period.slice(0,7),signature:`${x.grossPay}|${x.netPay}|${x.payout}`}:null},
  summarize:item=>{const x=item.extraction;return{title:x.period?month(x.period):item.title,detail:`${x.grossPay==null?'—':euro.format(x.grossPay)} brutto · ${x.netPay==null?'—':euro.format(x.netPay)} netto`}},
  onReview:item=>reviewFind(item.id),
  // Wird der Fund verworfen, den die Form gerade zeigt, wird sie geleert.
  onDismiss:id=>{if(reviewedImport===id)fillExtraction({},null)},
  onChange:()=>emitChanged()
});
const loadPaperless=()=>finds.load();

function reviewFind(id){
  const item=finds.find(id);if(!item)return;
  fillExtraction(item.extraction,item.id,item.title);
}

function showError(error){console.error(error);showMessage(error?.message||'Unbekannter Fehler.')}
