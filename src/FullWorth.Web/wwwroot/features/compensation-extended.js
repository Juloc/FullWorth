import { confirmMessage } from '../ui/confirm.js';
import {
  $, $$, euro2 as euro, euro as euro0, esc, attr, val as value, num as number, setVal as set,
  spaceId, monthLabel as month, signedEuro, api, json, notify as showMessage,
  readProfile, fillProfile
} from './compensation-shared.js';

init();

function init(){
  const tabs=$('.comp-tabs');
  const toast=$('#comp-error');
  if(!tabs||!toast)return;

  const css=document.createElement('link');css.rel='stylesheet';css.href='/styles/features/compensation-extended.css';document.head.appendChild(css);

  tabs.insertAdjacentHTML('beforeend','<button data-extended-tab="optimizer" type="button">Optimierer</button><button data-extended-tab="payslips" type="button">Lohnabrechnungen</button>');
  toast.insertAdjacentHTML('beforebegin',optimizerMarkup()+payslipMarkup());

  $$('[data-extended-tab]').forEach(button=>button.addEventListener('click',()=>openExtendedTab(button.dataset.extendedTab)));
  $('#optimizer-run').addEventListener('click',()=>loadOptimizer().catch(showError));
  $('#payslip-extract').addEventListener('click',()=>extractPayslip().catch(showError));
  $('#payslip-extract-batch').addEventListener('click',()=>extractBatch().catch(showError));
  $('#payslip-save').addEventListener('click',()=>savePayslip().catch(showError));
  $('#space-select').addEventListener('change',()=>loadPayslips().catch(showError));
}

async function openExtendedTab(name){
  $$('.comp-tabs button').forEach(button=>button.classList.toggle('active',button.dataset.extendedTab===name));
  $$('.comp-tab').forEach(tab=>tab.classList.toggle('active',tab.id===`tab-${name}`));
  if(name==='optimizer')await loadOptimizer().catch(showError);
  if(name==='payslips')await loadPayslips().catch(showError);
}

function optimizerMarkup(){return `
<section id="tab-optimizer" class="comp-tab">
  <article class="panel comp-card extended-intro">
    <div><h2>Was lohnt sich mehr?</h2><p>Vergleicht Gehaltserhöhungen, Teilzeit und den Einsatz eines festen Arbeitgeberbudgets auf Basis der aktuellen Rechnerdaten.</p></div>
    <label>Arbeitgeberbudget / Monat<input id="optimizer-budget" type="number" min="0" step="25" value="300"></label>
    <button id="optimizer-run" class="btn btn-primary" type="button">Vergleichen</button>
  </article>
  <div class="optimizer-section"><h2>Gehaltserhöhung</h2><div id="optimizer-raises" class="optimizer-grid"></div></div>
  <div class="optimizer-section"><h2>Teilzeit</h2><div id="optimizer-parttime" class="optimizer-grid"></div></div>
  <div class="optimizer-section"><h2>Arbeitgeberbudget</h2><div id="optimizer-budget-results" class="optimizer-grid"></div><p class="extended-note">„Steuerfreier Benefit“ ist eine Vergleichssimulation. Ob ein konkreter Benefit steuerfrei ist, muss für dessen jeweilige gesetzliche Voraussetzungen geprüft werden.</p></div>
</section>`}

function payslipMarkup(){return `
<section id="tab-payslips" class="comp-tab">
  <div class="payslip-layout">
    <div class="payslip-stack">
      <article class="panel comp-card">
        <div class="panel-head"><div><h2>Lohnabrechnung analysieren</h2><p>PDF oder Bild wird lokal im Backend verarbeitet. Die Originaldatei wird nicht gespeichert.</p></div></div>
        <div class="upload-row"><input id="payslip-file" type="file" multiple accept="application/pdf,image/jpeg,image/png,image/webp,image/tiff,image/bmp"><button id="payslip-extract" class="btn btn-primary" type="button">Analysieren</button><button id="payslip-extract-batch" class="btn btn-secondary" type="button">Alle analysieren</button></div>
        <div id="payslip-extraction-status" class="extended-note">Eine Datei füllt das Formular unten. Mehrere Dateien werden als Liste zur Prüfung angezeigt. Werte werden erst nach deiner Bestätigung gespeichert.</div>
        <div id="payslip-batch" class="payslip-batch" hidden></div>
      </article>
      <article class="panel comp-card">
        <div class="panel-head"><div><h2>Erkannte Werte prüfen</h2><p>Alle Felder können vor dem Speichern korrigiert werden.</p></div></div>
        <div class="form-grid payslip-fields">
          <label>Abrechnungsdatum<input id="ps-period" type="date"></label>
          <label>Brutto<input id="ps-gross" type="number" min="0" step="0.01"></label>
          <label>Netto<input id="ps-net" type="number" min="0" step="0.01"></label>
          <label>Auszahlung<input id="ps-payout" type="number" min="0" step="0.01"></label>
          <label>Lohnsteuer<input id="ps-tax" type="number" min="0" step="0.01"></label>
          <label>Soli<input id="ps-soli" type="number" min="0" step="0.01"></label>
          <label>Kirchensteuer<input id="ps-church" type="number" min="0" step="0.01"></label>
          <label>Rentenversicherung<input id="ps-rv" type="number" min="0" step="0.01"></label>
          <label>Arbeitslosenversicherung<input id="ps-av" type="number" min="0" step="0.01"></label>
          <label>Krankenversicherung<input id="ps-kv" type="number" min="0" step="0.01"></label>
          <label>Pflegeversicherung<input id="ps-pv" type="number" min="0" step="0.01"></label>
          <label>Firmenwagen Sachbezug<input id="ps-car" type="number" min="0" step="0.01"></label>
          <label>bAV Arbeitnehmer<input id="ps-bav" type="number" min="0" step="0.01"></label>
          <label>bAV Arbeitgeber<input id="ps-bav-ag" type="number" min="0" step="0.01"></label>
          <label>Bonus / Sonderzahlung<input id="ps-bonus" type="number" min="0" step="0.01"></label>
          <label>Notiz<input id="ps-note" maxlength="500"></label>
        </div>
        <button id="payslip-save" class="btn btn-primary comp-calculate" type="button">Bestätigte Werte speichern</button>
      </article>
    </div>
    <aside class="payslip-stack">
      <article class="panel comp-card"><h2>Warum ist mein Netto anders?</h2><div id="payslip-delta" class="delta-list"><p class="extended-note">Für den Vergleich werden mindestens zwei gespeicherte Monate benötigt.</p></div></article>
      <article class="panel comp-card"><div class="panel-head"><div><h2>Verlauf</h2><p>Gespeicherte Monatswerte.</p></div></div><div id="payslip-list" class="payslip-list"></div></article>
    </aside>
  </div>
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
  $('#payslip-extraction-status').textContent='Analyse läuft …';
  const result=await api('api/compensation/payslips/extract',{method:'POST',body:data});
  fillExtraction(result);
  $('#payslip-extraction-status').textContent=`Erkennungssicherheit ${Number(result.confidencePercent||0).toLocaleString('de-DE')} %. ${(result.warnings||[]).join(' ')}`;
}

function fillExtraction(x){set('ps-period',x.period||'');set('ps-gross',x.grossPay);set('ps-net',x.netPay);set('ps-payout',x.payout);set('ps-tax',x.wageTax);set('ps-soli',x.solidaritySurcharge);set('ps-church',x.churchTax);set('ps-rv',x.pensionInsurance);set('ps-av',x.unemploymentInsurance);set('ps-kv',x.healthInsurance);set('ps-pv',x.careInsurance);set('ps-car',x.companyCarTaxableBenefit);set('ps-bav',x.bavEmployee);set('ps-bav-ag',x.bavEmployer);set('ps-bonus',x.bonus)}

let batchItems=[];
async function extractBatch(){
  const files=[...($('#payslip-file').files||[])];
  if(!files.length)throw new Error('Bitte mindestens eine Lohnabrechnung auswählen.');
  if(files.length>40)throw new Error('Höchstens 40 Abrechnungen pro Durchlauf.');
  const data=new FormData();files.forEach(f=>data.append('files',f));
  $('#payslip-extraction-status').textContent=`Analysiere ${files.length} Abrechnung(en) … mit Codex kann das etwas dauern.`;
  const result=await api('api/compensation/payslips/extract-batch',{method:'POST',body:data});
  batchItems=((result&&result.items)||[]).map(it=>({...it}));
  renderBatch(batchItems);
  const ok=batchItems.filter(it=>it.result).length;
  $('#payslip-extraction-status').textContent=`${ok} von ${batchItems.length} Abrechnung(en) erkannt. Werte prüfen und speichern.`;
}

function renderBatch(items){
  const root=$('#payslip-batch');if(!root)return;
  if(!items.length){root.hidden=true;root.innerHTML='';return}
  root.hidden=false;
  root.innerHTML=`<div class="panel comp-card"><div class="panel-head"><div><h2>Mehrere Abrechnungen prüfen</h2><p>Jede Zeile wird als Monatswert gespeichert. Korrigiere bei Bedarf, dann speichern.</p></div><button id="payslip-save-all" class="btn btn-primary" type="button">Alle speichern</button></div>`+
    `<div class="payslip-batch-list">${items.map((it,i)=>batchRow(it,i)).join('')}</div></div>`;
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
  await api(`api/compensation/payslips?fullWorthSpaceId=${space}`,json('POST',payload));
  showMessage('Lohnabrechnung gespeichert.');await loadPayslips();
}

async function loadPayslips(){
  const space=spaceId();if(!space)return;
  const list=await api(`api/compensation/payslips?fullWorthSpaceId=${space}`);
  const root=$('#payslip-list');if(!root)return;
  root.innerHTML=(list||[]).length?(list||[]).map(item=>`<div class="payslip-row"><div><strong>${month(item.payslip.period)}</strong><small>${euro.format(item.payslip.grossPay)} brutto · ${euro.format(item.payslip.netPay)} netto</small></div><div><strong>${euro.format(item.payslip.payout)}</strong><button type="button" class="btn btn-danger" data-delete-payslip="${item.id}">×</button></div></div>`).join(''):'<p class="extended-note">Noch keine Lohnabrechnungen gespeichert.</p>';
  root.querySelectorAll('[data-delete-payslip]').forEach(button=>button.addEventListener('click',()=>deletePayslip(button.dataset.deletePayslip).catch(showError)));
  const delta=await api(`api/compensation/payslips/latest-delta?fullWorthSpaceId=${space}`,{},true);
  renderDelta(delta);
}

function renderDelta(delta){
  const root=$('#payslip-delta');if(!root)return;
  if(!delta){root.innerHTML='<p class="extended-note">Für den Vergleich werden mindestens zwei gespeicherte Monate benötigt.</p>';return}
  root.innerHTML=`<div class="delta-head"><strong>${month(delta.previous.payslip.period)} → ${month(delta.current.payslip.period)}</strong><span class="${delta.netDelta>=0?'positive':'negative'}">Netto ${signedEuro(delta.netDelta)}</span></div>${(delta.explanations||[]).map(text=>`<div class="neg-line">${esc(text)}</div>`).join('')}`;
}

async function deletePayslip(id){
  if(!await confirmMessage({message:'Gespeicherte Lohnabrechnung löschen?',title:'Lohnabrechnung löschen',confirmLabel:'Löschen',cancelLabel:'Abbrechen',destructive:true}))return;
  await api(`api/compensation/payslips/${id}?fullWorthSpaceId=${spaceId()}`,{method:'DELETE'});await loadPayslips();
}

function showError(error){console.error(error);showMessage(error?.message||'Unbekannter Fehler.')}
