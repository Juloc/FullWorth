const $=s=>document.querySelector(s);
const $$=s=>[...document.querySelectorAll(s)];
const state={spaces:[],space:null,result:null,regularMonthResult:null,scenarios:[],selected:[]};
const money=new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR',maximumFractionDigits:0});
const money2=new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR',minimumFractionDigits:2,maximumFractionDigits:2});
const pct=v=>`${Number(v||0).toLocaleString('de-DE',{minimumFractionDigits:1,maximumFractionDigits:2})} %`;

boot();

async function boot(){
  bind();
  try{
    state.spaces=await api('api/fullworth-spaces');
    const saved=localStorage.getItem('finance.space');
    state.space=state.spaces.find(s=>s.id===saved)||state.spaces[0]||null;
    renderSpaces();
    if(!state.space){notify('Kein Finanzbereich vorhanden.');return}
    await loadProfile();
    await Promise.all([calculate(),loadScenarios(),loadInflationMetadata()]);
  }catch(error){handle(error)}
}

function bind(){
  $$('.comp-tabs button').forEach(button=>button.addEventListener('click',()=>showTab(button.dataset.tab)));
  $('#space-select').addEventListener('change',async event=>{
    state.space=state.spaces.find(s=>s.id===event.target.value)||null;
    if(state.space)localStorage.setItem('finance.space',state.space.id);
    state.selected=[];
    await loadProfile();
    await Promise.all([calculate(),loadScenarios()]);
  });
  $('#car-enabled').addEventListener('change',syncCarFields);
  $('#gross-period').addEventListener('change',syncGrossFields);
  $('#salary-payments').addEventListener('change',syncGrossFields);
  $('#gross-input').addEventListener('input',syncGrossFields);
  $('#tax-class').addEventListener('change',syncTaxFactor);
  ['car-vehicle-type','car-acquisition-date','car-list-price','car-electric-range','car-co2'].forEach(id=>$(`#${id}`)?.addEventListener('change',syncCarRuleFields));
  $('#car-commute-method').addEventListener('change',syncCarCommuteFields);
  $('#calculate').addEventListener('click',()=>calculate().catch(handle));
  $('#save-profile').addEventListener('click',()=>saveProfile().catch(handle));
  $('#add-benefit').addEventListener('click',()=>addBenefit());
  $('#analyze-negotiation').addEventListener('click',()=>analyzeNegotiation().catch(handle));
  $('#save-scenario').addEventListener('click',()=>saveScenario().catch(handle));
  $('#clear-comparison').addEventListener('click',()=>{state.selected=[];renderScenarios();renderComparison()});
  $('#children').addEventListener('change',()=>{if(number('children')>0)$('#childless-surcharge').checked=false});
  syncGrossFields();syncTaxFactor();syncCarRuleFields();syncCarCommuteFields();
}

function showTab(name){
  $$('.comp-tabs button').forEach(b=>b.classList.toggle('active',b.dataset.tab===name));
  $$('.comp-tab').forEach(tab=>tab.classList.toggle('active',tab.id===`tab-${name}`));
  if(name==='scenarios')loadScenarios().catch(handle);
  if(name==='negotiation')analyzeNegotiation().catch(handle);
}

function renderSpaces(){
  $('#space-select').innerHTML=state.spaces.map(space=>`<option value="${space.id}"${space.id===state.space?.id?' selected':''}>${esc(space.name)}</option>`).join('');
}

async function loadProfile(){
  if(!state.space)return;
  const saved=await api(`api/compensation/profile?fullWorthSpaceId=${state.space.id}`,{},true);
  if(saved?.profile)fillProfile(saved.profile);
}

async function saveProfile(){
  if(!state.space)throw new Error('Kein Finanzbereich ausgewählt.');
  const profile=readProfile();
  await api(`api/compensation/profile?fullWorthSpaceId=${state.space.id}`,json('PUT',profile));
  await calculate();
  notify('Profil gespeichert.');
}

async function calculate(){
  const profile=readProfile();
  const regularProfile=regularMonthProfile(profile);
  const [result,regular]=await Promise.all([
    api('api/compensation/calculate',json('POST',profile)),
    regularProfile?api('api/compensation/calculate',json('POST',regularProfile)):Promise.resolve(null)
  ]);
  state.result=result;state.regularMonthResult=regular;
  renderResult(result);
  return result;
}

function regularMonthProfile(profile){
  if(profile.grossInputMode!=='monthly')return null;
  const monthlyGross=number('gross-input');
  if(profile.salaryPaymentsPerYear===12&&profile.annualBonus<=0)return null;
  return{...profile,annualGross:monthlyGross*12,annualBonus:0,salaryPaymentsPerYear:12};
}

function readProfile(){
  const payments=Math.min(14,Math.max(12,Math.round(number('salary-payments')||12)));
  const mode=value('gross-period')==='monthly'?'monthly':'annual';
  const annualGross=mode==='monthly'?number('gross-input')*payments:number('gross-input');
  const taxClass=Math.round(number('tax-class'));
  return{
    name:value('profile-name')||'Aktuelles Gehalt',
    annualGross,
    annualBonus:number('annual-bonus'),
    grossInputMode:mode,
    salaryPaymentsPerYear:payments,
    taxClass,
    taxClass4Factor:taxClass===4?Math.min(1,Math.max(0.001,number('tax-class4-factor')||1)):1,
    stateCode:value('state-code'),
    churchTax:$('#church-tax').checked,
    childrenUnder25:Math.max(0,Math.round(number('children'))),
    childlessCareSurcharge:$('#childless-surcharge').checked,
    healthInsuranceAdditionalRatePercent:number('health-addon'),
    weeklyHours:number('weekly-hours'),
    vacationDays:Math.round(number('vacation-days')),
    spouseAnnualTaxableIncome:0,
    companyCar:{
      enabled:$('#car-enabled').checked,
      listPrice:number('car-list-price'),
      taxableListPriceFactor:deriveCarFactor(),
      vehicleType:value('car-vehicle-type')||'manual',
      acquisitionDate:value('car-acquisition-date')||null,
      electricRangeKm:number('car-electric-range'),
      co2GramsPerKm:number('car-co2'),
      oneWayCommuteKm:number('car-commute'),
      commuteMethod:value('car-commute-method')||'monthly',
      commuteDaysPerMonth:Math.max(0,Math.min(31,Math.round(number('car-commute-days')))),
      employeeContributionMonthly:number('car-contribution'),
      employerCostMonthly:number('car-employer-cost'),
      privateAlternativeCostMonthly:number('car-private-cost')
    },
    occupationalPension:{
      employeeContributionMonthly:number('bav-employee'),
      employerContributionMonthly:number('bav-employer'),
      projectionYears:Math.round(number('bav-years')),
      expectedAnnualReturnPercent:number('bav-return')
    },
    benefits:readBenefits()
  };
}

function fillProfile(profile){
  set('profile-name',profile.name);
  const mode=profile.grossInputMode==='monthly'?'monthly':'annual';
  const payments=Math.min(14,Math.max(12,Number(profile.salaryPaymentsPerYear)||12));
  set('gross-period',mode);set('salary-payments',payments);
  set('gross-input',mode==='monthly'?(Number(profile.annualGross)||0)/payments:profile.annualGross);
  set('annual-bonus',profile.annualBonus);
  set('tax-class',profile.taxClass||1);
  set('tax-class4-factor',profile.taxClass4Factor??1);
  set('state-code',profile.stateCode||'BW');
  $('#church-tax').checked=!!profile.churchTax;
  set('children',profile.childrenUnder25??0);
  $('#childless-surcharge').checked=!!profile.childlessCareSurcharge;
  set('health-addon',profile.healthInsuranceAdditionalRatePercent??2.9);
  set('weekly-hours',profile.weeklyHours??40);
  set('vacation-days',profile.vacationDays??30);
  const car=profile.companyCar||{};
  $('#car-enabled').checked=!!car.enabled;
  set('car-list-price',car.listPrice??50000);set('car-factor',car.taxableListPriceFactor??1);
  set('car-vehicle-type',car.vehicleType||'manual');set('car-acquisition-date',car.acquisitionDate||'2026-01-01');
  set('car-electric-range',car.electricRangeKm??80);set('car-co2',car.co2GramsPerKm??50);
  set('car-commute',car.oneWayCommuteKm??0);set('car-commute-method',car.commuteMethod||'monthly');set('car-commute-days',car.commuteDaysPerMonth??10);
  set('car-contribution',car.employeeContributionMonthly??0);set('car-employer-cost',car.employerCostMonthly??0);set('car-private-cost',car.privateAlternativeCostMonthly??0);
  const bav=profile.occupationalPension||{};
  set('bav-employee',bav.employeeContributionMonthly??0);set('bav-employer',bav.employerContributionMonthly??0);set('bav-years',bav.projectionYears??30);set('bav-return',bav.expectedAnnualReturnPercent??3);
  $('#benefits-list').innerHTML='';
  (profile.benefits||[]).forEach(addBenefit);
  syncGrossFields();syncTaxFactor();syncCarFields();syncCarRuleFields();syncCarCommuteFields();
}

function syncCarFields(){
  $('#car-fields').classList.toggle('enabled',$('#car-enabled').checked);
}
function syncGrossFields(){
  const monthly=value('gross-period')==='monthly';
  $('#gross-amount-title').textContent=monthly?'Monatsbrutto':'Jahresbrutto';
  $('#salary-payments-field').hidden=!monthly;
  const payments=Math.min(14,Math.max(12,Math.round(number('salary-payments')||12)));
  const annual=monthly?number('gross-input')*payments:number('gross-input');
  $('#gross-annual-preview').textContent=monthly?`= ${money.format(annual)} Jahresbrutto`:'Gesamtbrutto ohne zusätzlichen Bonus.';
}
function syncTaxFactor(){
  const isFour=Math.round(number('tax-class'))===4;
  $('#tax-factor-field').hidden=!isFour;
}
function hybridMinimumRange(){
  const date=value('car-acquisition-date')||'2026-01-01';
  return date>='2025-01-01'?80:(date>='2022-01-01'?60:40);
}
function deriveCarFactor(){
  const type=value('car-vehicle-type')||'manual';
  if(type==='manual')return number('car-factor')||1;
  if(type==='combustion')return 1;
  const price=number('car-list-price');
  const date=value('car-acquisition-date')||'2026-01-01';
  if(type==='electric'){
    const limit=date>='2025-07-01'?100000:(date>='2024-01-01'?70000:60000);
    return price<=limit?0.25:0.5;
  }
  if(type==='hybrid'){
    const byRange=number('car-electric-range')>=hybridMinimumRange();
    const co2=number('car-co2');
    const byCo2=co2>0&&co2<=50;
    return byRange||byCo2?0.5:1;
  }
  return 1;
}
function syncCarRuleFields(){
  const type=value('car-vehicle-type')||'manual';
  $('#car-factor-field').hidden=type!=='manual';
  const hybrid=type==='hybrid';
  $('#car-range-field').hidden=!hybrid;$('#car-co2-field').hidden=!hybrid;
  const factor=deriveCarFactor();
  if(type!=='manual')set('car-factor',factor);
  let note=`Regel: ${String(factor).replace('.',',')} % vom Bruttolistenpreis.`;
  if(type==='electric'){
    const date=value('car-acquisition-date')||'2026-01-01';
    const limit=date>='2025-07-01'?100000:(date>='2024-01-01'?70000:60000);
    note+=` E-Auto-Grenze: ${money.format(limit)}.`;
  }else if(hybrid){
    note+=` Plug-in-Hybrid: mindestens ${hybridMinimumRange()} km elektrische Reichweite oder höchstens 50 g CO₂/km.`;
  }
  $('#car-rule-summary').textContent=note;
}
function syncCarCommuteFields(){
  $('#car-commute-days-field').hidden=value('car-commute-method')!=='daily';
}

function addBenefit(benefit={}){
  const row=document.createElement('div');row.className='benefit-row';
  row.innerHTML=`
    <label>Name<input data-benefit="name" value="${attr(benefit.name||'') }" placeholder="z. B. Deutschlandticket"></label>
    <label>AG-Kosten / Monat<input data-benefit="employerCostMonthly" type="number" min="0" step="1" value="${numAttr(benefit.employerCostMonthly)}"></label>
    <label>Dein Wert / Monat<input data-benefit="personalValueMonthly" type="number" min="0" step="1" value="${numAttr(benefit.personalValueMonthly)}"></label>
    <label>Steuerpflichtig / Monat<input data-benefit="taxableBenefitMonthly" type="number" min="0" step="1" value="${numAttr(benefit.taxableBenefitMonthly)}"></label>
    <label>Eigenkosten / Monat<input data-benefit="employeeCostMonthly" type="number" min="0" step="1" value="${numAttr(benefit.employeeCostMonthly)}"></label>
    <button type="button" aria-label="Benefit entfernen">×</button>`;
  row.querySelector('button').addEventListener('click',()=>row.remove());
  $('#benefits-list').appendChild(row);
}

function readBenefits(){
  return $$('.benefit-row').map(row=>{
    const input=name=>row.querySelector(`[data-benefit="${name}"]`);
    return{
      name:input('name').value.trim()||'Benefit',
      employerCostMonthly:Number(input('employerCostMonthly').value)||0,
      personalValueMonthly:Number(input('personalValueMonthly').value)||0,
      taxableBenefitMonthly:Number(input('taxableBenefitMonthly').value)||0,
      employeeCostMonthly:Number(input('employeeCostMonthly').value)||0
    };
  });
}

function renderResult(result){
  const regular=state.regularMonthResult;
  $('#result-net-label').textContent=regular?'Netto normaler Monat':'Geschätztes Netto / Monat';
  $('#result-net-month').textContent=money2.format(regular?.estimatedCashNetMonthly??result.estimatedCashNetMonthly);
  $('#result-net-year').innerHTML=`<span class="comp-hero-sub">${esc(money.format(result.estimatedCashNetAnnual))} geschätztes Netto pro Jahr</span><span class="fw-trend positive comp-hero-badge">${esc(pct(result.estimatedNetRatioPercent))} vom Cash-Brutto</span>`;
  $('#result-employer').textContent=money.format(result.employerTotalCostAnnual);
  $('#result-fullworth').textContent=money.format(result.fullWorthCompensationValueAnnual);
  $('#result-marginal').textContent=money2.format(result.marginalNetFromNext100Gross);
  $('#result-hourly').textContent=money2.format(result.effectiveNetValuePerWorkingHour);
  const social=result.socialInsurance;
  const tax=result.taxes;
  const deductions=[
    ['Einkommensteuer',tax.estimatedIncomeTaxAnnual,'tax'],['Solidaritätszuschlag',tax.estimatedSolidaritySurchargeAnnual,'tax'],['Kirchensteuer',tax.estimatedChurchTaxAnnual,'tax'],
    ['Rentenversicherung',social.pensionAnnual,'social'],['Arbeitslosenversicherung',social.unemploymentAnnual,'social'],['Krankenversicherung',social.healthAnnual,'social'],['Pflegeversicherung',social.careAnnual,'social']
  ];
  const net=Number(result.estimatedCashNetAnnual)||0;
  const taxTotal=deductions.filter(d=>d[2]==='tax').reduce((s,d)=>s+(Number(d[1])||0),0);
  const socialTotal=deductions.filter(d=>d[2]==='social').reduce((s,d)=>s+(Number(d[1])||0),0);
  const totalDeductions=taxTotal+socialTotal;
  const cashGross=net+totalDeductions;
  const frac=v=>cashGross>0?(v/cashGross)*100:0;
  const dedRows=deductions.map(([label,amount,cls])=>compLine(label,money.format(amount),cls,cashGross>0?`${pct(frac(amount))} vom Cash-Brutto`:null)).join('');
  $('#deduction-rows').innerHTML=
    `<div class="comp-dedu-head">${donut(frac(net),frac(taxTotal),frac(socialTotal),pct(result.estimatedNetRatioPercent))}`+
    `<div class="comp-donut-legend">${legendItem('net','Netto',money.format(net))}${legendItem('tax','Steuern',money.format(taxTotal))}${legendItem('social','Sozialabgaben',money.format(socialTotal))}</div></div>`+
    `<div class="comp-dedu-rows">${dedRows}${compLine('Summe Abzüge',money.format(totalDeductions),'total',null)}</div>`;
  const car=result.companyCar,pension=result.occupationalPension;
  const summary=[];
  if(car.taxableBenefitAnnual>0||car.estimatedEffectivePersonalValueAnnual>0){summary.push(['Firmenwagen: geldwerter Vorteil',money.format(car.taxableBenefitAnnual),'']);summary.push(['Firmenwagen: geschätzter persönlicher Wert',money.format(car.estimatedEffectivePersonalValueAnnual),'pos'])}
  if(pension.totalInvestedAnnual>0){summary.push(['bAV: investiert / Jahr',money.format(pension.totalInvestedAnnual),'']);summary.push(['bAV: heutiger Nettoverzicht',money.format(pension.estimatedCurrentNetSacrificeAnnual),'']);summary.push([`bAV: Projektion ${readProfile().occupationalPension.projectionYears} Jahre`,money.format(pension.projectedValue),'pos'])}
  result.benefits.forEach((b,i)=>summary.push([b.name,money.format(b.personalValueAnnual),`cat${(i%8)+1}`]));
  if(!summary.length)summary.push(['Weitere Benefits','Keine erfasst','muted']);
  $('#benefit-summary').innerHTML=summary.map(([label,val,tone])=>benefitLine(label,val,tone)).join('');
  const a=result.assumptions;
  $('#assumptions').textContent=`${a.calculationKind}. ${a.taxSource}. ${a.socialInsuranceSource}. Stand ${a.dataAsOf}. ${a.disclaimer}`;
}

async function analyzeNegotiation(){
  const request={
    previousAnnualGross:number('neg-old-salary'),
    previousDate:value('neg-old-date'),
    currentAnnualGross:number('neg-current-salary'),
    desiredAnnualGross:number('neg-desired-salary'),
    additionalRealAdjustmentPercent:number('neg-real-adjustment'),
    comparisonDate:value('neg-date')
  };
  const result=await api('api/compensation/negotiation',json('POST',request));
  $('#neg-maintenance').textContent=money.format(result.purchasingPowerMaintenanceSalary);
  $('#neg-inflation').textContent=`Kumulierte Inflation: ${pct(result.cumulativeInflationPercent)}`;
  $('#neg-current-nominal').textContent=signedPct(result.currentNominalChangePercent);
  $('#neg-current-real').textContent=signedPct(result.currentRealChangePercent);
  $('#neg-desired-real').textContent=signedPct(result.desiredRealChangePercent);
  $('#neg-reference').textContent=money.format(result.suggestedReferenceSalary);
  const realClass=result.currentRealChangePercent>=0?'positive':'negative';
  $('#neg-explanation').innerHTML=`
    <div class="neg-line">Für dieselbe Kaufkraft wie am ${date(result.previousDate)} wären heute <strong>${money.format(result.purchasingPowerMaintenanceSalary)}</strong> nötig.</div>
    <div class="neg-line">Dein aktuelles Gehalt ist nominal <strong>${signedPct(result.currentNominalChangePercent)}</strong>, real aber <strong class="${realClass}">${signedPct(result.currentRealChangePercent)}</strong> verändert.</div>
    <div class="neg-line">Dein Wunsch liegt <strong>${money.format(result.desiredAmountAboveInflationCompensation)}</strong> über reinem Kaufkrafterhalt und entspricht real <strong>${signedPct(result.desiredRealChangePercent)}</strong>.</div>`;
  $('#inflation-source').textContent=`${result.inflationSource} · Stand ${result.dataAsOf}`;
}

async function loadInflationMetadata(){
  const data=await api('api/compensation/inflation');
  $('#inflation-source').textContent=`${data.source} · Basis ${data.base} · Stand ${data.dataAsOf}`;
}

async function saveScenario(){
  if(!state.space)throw new Error('Kein Finanzbereich ausgewählt.');
  const name=value('scenario-name').trim();
  if(!name)throw new Error('Bitte einen Szenarionamen eingeben.');
  await api(`api/compensation/scenarios?fullWorthSpaceId=${state.space.id}`,json('POST',{name,profile:readProfile()}));
  set('scenario-name','');
  await loadScenarios();
  notify('Szenario gespeichert.');
}

async function loadScenarios(){
  if(!state.space)return;
  state.scenarios=await api(`api/compensation/scenarios?fullWorthSpaceId=${state.space.id}`)||[];
  state.selected=state.selected.filter(id=>state.scenarios.some(s=>s.id===id));
  renderScenarios();
  await renderComparison();
}

function renderScenarios(){
  const root=$('#scenario-list');
  if(!state.scenarios.length){root.innerHTML='<article class="panel scenario-card"><h3>Noch keine Szenarien</h3><small>Speichere den aktuellen Rechnerstand als erstes Szenario.</small></article>';return}
  root.innerHTML=state.scenarios.map(s=>`
    <article class="panel scenario-card ${state.selected.includes(s.id)?'selected':''}" data-scenario="${s.id}">
      <h3>${esc(s.name)}</h3>
      <div class="scenario-value">${money.format(s.profile.annualGross+s.profile.annualBonus)}</div>
      <small>Brutto pro Jahr · aktualisiert ${date(s.updatedAt)}</small>
      <div class="scenario-card-actions">
        <button type="button" data-action="load">Laden</button>
        <button type="button" data-action="compare">${state.selected.includes(s.id)?'Ausgewählt':'Vergleichen'}</button>
        <button type="button" data-action="delete">Löschen</button>
      </div>
    </article>`).join('');
  root.querySelectorAll('[data-scenario]').forEach(card=>{
    const id=card.dataset.scenario;const scenario=state.scenarios.find(s=>s.id===id);
    card.querySelector('[data-action="load"]').addEventListener('click',async()=>{fillProfile(scenario.profile);showTab('calculator');await calculate()});
    card.querySelector('[data-action="compare"]').addEventListener('click',async()=>{toggleScenario(id);renderScenarios();await renderComparison()});
    card.querySelector('[data-action="delete"]').addEventListener('click',()=>deleteScenario(id).catch(handle));
  });
}

function toggleScenario(id){
  if(state.selected.includes(id)){state.selected=state.selected.filter(x=>x!==id);return}
  state.selected=[...state.selected,id].slice(-2);
}

async function renderComparison(){
  const box=$('#scenario-comparison');
  if(state.selected.length!==2){box.hidden=true;return}
  const left=state.scenarios.find(s=>s.id===state.selected[0]);
  const right=state.scenarios.find(s=>s.id===state.selected[1]);
  if(!left||!right){box.hidden=true;return}
  const result=await api('api/compensation/compare',json('POST',{left:left.profile,right:right.profile}));
  box.hidden=false;
  $('#comparison-content').innerHTML=`
    <p><strong>${esc(left.name)}</strong> → <strong>${esc(right.name)}</strong></p>
    <div class="comparison-grid">
      ${comparisonCell('Netto-Differenz / Jahr',signedMoney(result.cashNetDeltaAnnual))}
      ${comparisonCell('Gesamtwert-Differenz',signedMoney(result.fullWorthValueDeltaAnnual))}
      ${comparisonCell('Arbeitgeberkosten',signedMoney(result.employerCostDeltaAnnual))}
      ${comparisonCell('Wert / Arbeitsstunde',signedMoney(result.effectiveHourlyValueDelta))}
    </div>`;
}

async function deleteScenario(id){
  if(!confirm('Szenario wirklich löschen?'))return;
  await api(`api/compensation/scenarios/${id}?fullWorthSpaceId=${state.space.id}`,{method:'DELETE'});
  state.selected=state.selected.filter(x=>x!==id);
  await loadScenarios();
}

function comparisonCell(label,value){const positive=!String(value).startsWith('−');return `<div class="comparison-cell"><span>${esc(label)}</span><strong class="${positive?'positive':'negative'}">${esc(value)}</strong></div>`}
function signedMoney(v){const n=Number(v||0);return `${n>=0?'+':'−'}${money2.format(Math.abs(n))}`}
function signedPct(v){const n=Number(v||0);return `${n>=0?'+':'−'}${pct(Math.abs(n))}`}
function row(label,value){return `<div class="result-row"><span>${esc(label)}</span><strong>${esc(value)}</strong></div>`}
function compLine(label,value,cls,sub){
  const dot=cls&&cls!=='total'?`<i class="comp-dot comp-dot-${esc(cls)}"></i>`:'';
  const subHtml=sub?`<span class="comp-line-sub">${esc(sub)}</span>`:'';
  return `<div class="comp-line${cls==='total'?' comp-line-total':''}"><span class="comp-line-key">${dot}<span class="comp-line-label">${esc(label)}</span>${subHtml}</span><span class="amount">${esc(value)}</span></div>`;
}
function benefitLine(label,value,tone){
  const isCat=typeof tone==='string'&&tone.startsWith('cat');
  const dot=isCat?`<i class="comp-dot comp-dot-${esc(tone)}"></i>`:'';
  const amtCls=tone==='pos'?'amount positive':(tone==='muted'?'amount comp-amount-muted':'amount');
  return `<div class="comp-line"><span class="comp-line-key">${dot}<span class="comp-line-label">${esc(label)}</span></span><span class="${amtCls}">${esc(value)}</span></div>`;
}
function legendItem(cls,label,value){
  return `<div class="comp-leg"><span class="comp-sw comp-sw-${esc(cls)}"></span><span class="comp-leg-label">${esc(label)}</span><span class="amount comp-leg-amt">${esc(value)}</span></div>`;
}
function donut(netF,taxF,socialF,centerPct){
  const seg=(fr,start,cls)=>{const len=Math.max(0,(Number(fr)||0)-2);if(len<=0)return'';return `<circle class="comp-arc comp-arc-${cls}" cx="60" cy="60" r="48" pathLength="100" stroke-dasharray="${len.toFixed(2)} ${(100-len).toFixed(2)}" stroke-dashoffset="${(-(Number(start)||0)).toFixed(2)}"/>`};
  const arcs=seg(netF,0,'net')+seg(taxF,netF,'tax')+seg(socialF,(Number(netF)||0)+(Number(taxF)||0),'social');
  return `<svg class="comp-donut" viewBox="0 0 120 120" role="img" aria-label="Aufteilung des Cash-Bruttos in Netto, Steuern und Sozialabgaben">`+
    `<defs><linearGradient id="comp-net-grad" x1="0" y1="0" x2="1" y2="1"><stop class="comp-grad-a" offset="0"/><stop class="comp-grad-b" offset="1"/></linearGradient></defs>`+
    `<circle class="comp-arc-track" cx="60" cy="60" r="48"/><g transform="rotate(-90 60 60)">${arcs}</g>`+
    `<text class="comp-donut-pct" x="60" y="57">${esc(centerPct)}</text><text class="comp-donut-cap" x="60" y="71">Netto-Anteil</text></svg>`;
}
function value(id){return $(`#${id}`).value}
function number(id){return Number(value(id))||0}
function set(id,v){const el=$(`#${id}`);if(el)el.value=v??''}
function date(v){if(!v)return'—';return new Intl.DateTimeFormat('de-DE').format(new Date(`${String(v).slice(0,10)}T12:00:00`))}
function json(method,body){return{method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}}
async function api(path,options={},allow404=false){
  const response=await fetch(`/bff/backend/${path.replace(/^\//,'')}`,options);
  if(response.status===401){location.href=`/auth/login?returnUrl=${encodeURIComponent(location.pathname)}`;throw new Error('Anmeldung erforderlich.');}
  if(allow404&&response.status===404)return null;
  if(!response.ok){let message=`Fehler ${response.status}`;try{const data=await response.json();message=data.error||data.title||data.message||message}catch{}throw new Error(message)}
  if(response.status===204)return null;
  return response.json();
}
function notify(message){const toast=$('#comp-error');toast.textContent=message;toast.classList.add('show');clearTimeout(notify.timer);notify.timer=setTimeout(()=>toast.classList.remove('show'),3200)}
function handle(error){console.error(error);notify(error?.message||'Unbekannter Fehler.');}
function esc(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function attr(v){return esc(v)}
function numAttr(v){const n=Number(v);return Number.isFinite(n)?String(n):'0'}
