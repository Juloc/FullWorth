const $=s=>document.querySelector(s);
const $$=s=>[...document.querySelectorAll(s)];
const euro=new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR',maximumFractionDigits:2});
const euro0=new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR',maximumFractionDigits:0});

init();

function init(){
  const tabs=$('.comp-tabs');
  const toast=$('#comp-error');
  if(!tabs||!toast)return;

  const css=document.createElement('link');css.rel='stylesheet';css.href='/compensation-extended.css';document.head.appendChild(css);

  tabs.insertAdjacentHTML('beforeend','<button data-extended-tab="optimizer" type="button">Optimierer</button><button data-extended-tab="payslips" type="button">Lohnabrechnungen</button>');
  toast.insertAdjacentHTML('beforebegin',optimizerMarkup()+payslipMarkup());

  $$('[data-extended-tab]').forEach(button=>button.addEventListener('click',()=>openExtendedTab(button.dataset.extendedTab)));
  $('#optimizer-run').addEventListener('click',()=>loadOptimizer().catch(showError));
  $('#payslip-extract').addEventListener('click',()=>extractPayslip().catch(showError));
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
    <button id="optimizer-run" class="primary-action" type="button">Vergleichen</button>
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
        <div class="upload-row"><input id="payslip-file" type="file" accept="application/pdf,image/jpeg,image/png,image/webp,image/tiff,image/bmp"><button id="payslip-extract" class="primary-action" type="button">Analysieren</button></div>
        <div id="payslip-extraction-status" class="extended-note">Werte werden erst nach deiner Prüfung gespeichert.</div>
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
        <button id="payslip-save" class="primary-action comp-calculate" type="button">Bestätigte Werte speichern</button>
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
    ${ranked&&index===0?'<span class="optimizer-badge">höchster FullWorth-Wert</span>':''}
    <h3>${esc(option.title)}</h3><p>${esc(option.description)}</p>
    <div class="optimizer-value">${signedEuro(option.fullWorthDeltaAnnual)}</div><small>FullWorth / Jahr</small>
    <div class="optimizer-details">
      <span>Netto <strong>${signedEuro(option.cashNetDeltaAnnual)}</strong></span>
      <span>AG-Kosten <strong>${signedEuro(option.employerCostDeltaAnnual)}</strong></span>
      <span>Neues Netto <strong>${euro0.format(option.calculation.estimatedCashNetAnnual)}</strong></span>
    </div>
    <button type="button" data-load-profile>In Rechner übernehmen</button>
  </article>`).join('');
  root.querySelectorAll('[data-load-profile]').forEach((button,index)=>button.addEventListener('click',()=>loadProfileIntoCalculator(options[index].profile)));
}

function loadProfileIntoCalculator(profile){
  set('profile-name',profile.name);set('annual-gross',profile.annualGross);set('annual-bonus',profile.annualBonus);set('tax-class',profile.taxClass||1);set('state-code',profile.stateCode||'BW');
  $('#church-tax').checked=!!profile.churchTax;set('children',profile.childrenUnder25??0);$('#childless-surcharge').checked=!!profile.childlessCareSurcharge;set('health-addon',profile.healthInsuranceAdditionalRatePercent??2.9);set('weekly-hours',profile.weeklyHours??40);set('vacation-days',profile.vacationDays??30);
  const car=profile.companyCar||{};$('#car-enabled').checked=!!car.enabled;set('car-list-price',car.listPrice??0);set('car-factor',car.taxableListPriceFactor??1);set('car-commute',car.oneWayCommuteKm??0);set('car-contribution',car.employeeContributionMonthly??0);set('car-employer-cost',car.employerCostMonthly??0);set('car-private-cost',car.privateAlternativeCostMonthly??0);
  const bav=profile.occupationalPension||{};set('bav-employee',bav.employeeContributionMonthly??0);set('bav-employer',bav.employerContributionMonthly??0);set('bav-years',bav.projectionYears??30);set('bav-return',bav.expectedAnnualReturnPercent??3);
  const list=$('#benefits-list');list.innerHTML='';(profile.benefits||[]).forEach(addBenefitRow);
  $$('.comp-tabs button').forEach(b=>b.classList.toggle('active',b.dataset.tab==='calculator'));$$('.comp-tab').forEach(tab=>tab.classList.toggle('active',tab.id==='tab-calculator'));
  $('#car-fields').classList.toggle('enabled',!!car.enabled);
  $('#calculate').click();
}

function addBenefitRow(benefit){
  const row=document.createElement('div');row.className='benefit-row';
  row.innerHTML=`<label>Name<input data-benefit="name" value="${attr(benefit.name||'')}"></label><label>AG-Kosten / Monat<input data-benefit="employerCostMonthly" type="number" min="0" value="${n(benefit.employerCostMonthly)}"></label><label>Dein Wert / Monat<input data-benefit="personalValueMonthly" type="number" min="0" value="${n(benefit.personalValueMonthly)}"></label><label>Steuerpflichtig / Monat<input data-benefit="taxableBenefitMonthly" type="number" min="0" value="${n(benefit.taxableBenefitMonthly)}"></label><label>Eigenkosten / Monat<input data-benefit="employeeCostMonthly" type="number" min="0" value="${n(benefit.employeeCostMonthly)}"></label><button type="button">×</button>`;
  row.querySelector('button').addEventListener('click',()=>row.remove());$('#benefits-list').appendChild(row);
}

function readProfile(){
  return{name:value('profile-name')||'Aktuelles Gehalt',annualGross:number('annual-gross'),annualBonus:number('annual-bonus'),taxClass:Math.round(number('tax-class')),stateCode:value('state-code'),churchTax:$('#church-tax').checked,childrenUnder25:Math.max(0,Math.round(number('children'))),childlessCareSurcharge:$('#childless-surcharge').checked,healthInsuranceAdditionalRatePercent:number('health-addon'),weeklyHours:number('weekly-hours'),vacationDays:Math.round(number('vacation-days')),spouseAnnualTaxableIncome:0,companyCar:{enabled:$('#car-enabled').checked,listPrice:number('car-list-price'),taxableListPriceFactor:number('car-factor'),oneWayCommuteKm:number('car-commute'),employeeContributionMonthly:number('car-contribution'),employerCostMonthly:number('car-employer-cost'),privateAlternativeCostMonthly:number('car-private-cost')},occupationalPension:{employeeContributionMonthly:number('bav-employee'),employerContributionMonthly:number('bav-employer'),projectionYears:Math.round(number('bav-years')),expectedAnnualReturnPercent:number('bav-return')},benefits:$$('.benefit-row').map(row=>{const g=x=>row.querySelector(`[data-benefit="${x}"]`);return{name:g('name').value.trim()||'Benefit',employerCostMonthly:Number(g('employerCostMonthly').value)||0,personalValueMonthly:Number(g('personalValueMonthly').value)||0,taxableBenefitMonthly:Number(g('taxableBenefitMonthly').value)||0,employeeCostMonthly:Number(g('employeeCostMonthly').value)||0}})};
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
  root.innerHTML=(list||[]).length?(list||[]).map(item=>`<div class="payslip-row"><div><strong>${month(item.payslip.period)}</strong><small>${euro.format(item.payslip.grossPay)} brutto · ${euro.format(item.payslip.netPay)} netto</small></div><div><strong>${euro.format(item.payslip.payout)}</strong><button type="button" data-delete-payslip="${item.id}">×</button></div></div>`).join(''):'<p class="extended-note">Noch keine Lohnabrechnungen gespeichert.</p>';
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
  if(!confirm('Gespeicherte Lohnabrechnung löschen?'))return;
  await api(`api/compensation/payslips/${id}?fullWorthSpaceId=${spaceId()}`,{method:'DELETE'});await loadPayslips();
}

function spaceId(){return $('#space-select')?.value||''}
function value(id){return $(`#${id}`)?.value??''}
function number(id){return Number(value(id))||0}
function set(id,v){const el=$(`#${id}`);if(el)el.value=v??''}
function json(method,body){return{method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}}
async function api(path,options={},allow204=false){const response=await fetch(`/bff/backend/${String(path).replace(/^\//,'')}`,options);if(allow204&&response.status===204)return null;if(!response.ok){let message=`Fehler ${response.status}`;try{const body=await response.json();message=body.error||body.title||body.message||message}catch{}throw new Error(message)}if(response.status===204)return null;return response.json()}
function month(v){if(!v)return'—';return new Intl.DateTimeFormat('de-DE',{month:'long',year:'numeric'}).format(new Date(`${String(v).slice(0,10)}T12:00:00`))}
function signedEuro(v){const x=Number(v||0);return `${x>=0?'+':'−'}${euro.format(Math.abs(x))}`}
function showMessage(text){const toast=$('#comp-error');toast.textContent=text;toast.classList.add('show');clearTimeout(showMessage.timer);showMessage.timer=setTimeout(()=>toast.classList.remove('show'),3200)}
function showError(error){console.error(error);showMessage(error?.message||'Unbekannter Fehler.')}
function esc(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function attr(v){return esc(v)}
function n(v){const x=Number(v);return Number.isFinite(x)?x:0}
