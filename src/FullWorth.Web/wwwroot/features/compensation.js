import { confirmMessage } from '../ui/confirm.js';
import {
  $, $$, euro as money, euro2 as money2, pct, signedEuro as signedMoney, signedPct,
  esc, attr, val as value, num as number, setVal as set, fmtDate as date, localIsoDate,
  api, json, notify, readProfile, fillProfile, deriveCarFactor, hybridMinimumRange,
  addBenefitRow as addBenefit, readBenefits,
  addOneOffFromPreset as addOneOff, fillOneOffPresets, syncAgeFields
} from './compensation-shared.js';
const state={spaces:[],space:null,result:null,scenarios:[],selected:[]};

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
  fillOneOffPresets();
  $('#add-oneoff').addEventListener('click',()=>addOneOff());
  // The derived age depends on both the birth date and the selected tax year.
  ['birth-date','tax-year'].forEach(id=>$(`#${id}`)?.addEventListener('change',syncAgeFields));
  $('#analyze-negotiation').addEventListener('click',()=>analyzeNegotiation().catch(handle));
  $('#save-scenario').addEventListener('click',()=>saveScenario().catch(handle));
  $('#clear-comparison').addEventListener('click',()=>{state.selected=[];renderScenarios();renderComparison()});
  $('#children').addEventListener('change',()=>{if(number('children')>0)$('#childless-surcharge').checked=false});
  syncGrossFields();syncTaxFactor();syncCarRuleFields();syncCarCommuteFields();syncAgeFields();
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
  const [saved,history]=await Promise.all([
    api(`api/compensation/profile?fullWorthSpaceId=${state.space.id}`,{},true),
    api(`api/compensation/history?fullWorthSpaceId=${state.space.id}`,{},true)
  ]);
  const today=localIsoDate(new Date());
  const current=(history||[]).filter(entry=>entry.effectiveDate<=today).at(-1);
  if(current?.resolvedProfile)fillProfile(current.resolvedProfile);
  else if(saved?.profile)fillProfile(saved.profile);
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
  const result=await api('api/compensation/calculate',json('POST',profile));
  state.result=result;
  renderResult(result);
  return result;
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

function renderResult(result){
  const avg=Number(result.estimatedAverageCashNetMonthly)||0;
  const regularMonth=Number(result.estimatedCashNetMonthly)||0;
  // Only surface the yearly average separately when a bonus / extra salary makes it differ from a normal month.
  const showAverage=Math.abs(avg-regularMonth)>=0.01;
  $('#result-net-label').textContent='Netto normaler Monat';
  $('#result-net-month').textContent=money2.format(regularMonth);
  const avgNote=showAverage?` · Ø ${esc(money2.format(avg))}/Monat`:'';
  $('#result-net-year').innerHTML=`<span class="comp-hero-sub">${esc(money.format(result.estimatedCashNetAnnual))} Jahresnetto${avgNote}</span><span class="fw-trend positive comp-hero-badge">${esc(pct(result.estimatedNetRatioPercent))} vom Cash-Brutto</span>`;
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
  const profile=readProfile();
  const summary=[];
  const oneOffTotal=(profile.oneOffPayments||[]).reduce((sum,payment)=>sum+(Number(payment.amount)||0),0);
  if(oneOffTotal>0)summary.push(['Sonderzahlungen (nur im Jahresnetto)',money.format(oneOffTotal),'pos']);
  if(car.taxableBenefitAnnual>0||car.estimatedEffectivePersonalValueAnnual>0){summary.push(['Firmenwagen: geldwerter Vorteil',money.format(car.taxableBenefitAnnual),'']);summary.push(['Firmenwagen: geschätzter persönlicher Wert',money.format(car.estimatedEffectivePersonalValueAnnual),'pos'])}
  if(pension.totalInvestedAnnual>0){summary.push(['bAV: investiert / Jahr',money.format(pension.totalInvestedAnnual),'']);summary.push(['bAV: heutiger Nettoverzicht',money.format(pension.estimatedCurrentNetSacrificeAnnual),'']);summary.push([`bAV: Projektion ${profile.occupationalPension.projectionYears} Jahre`,money.format(pension.projectedValue),'pos'])}
  result.benefits.forEach((b,i)=>summary.push([b.name,money.format(b.personalValueAnnual),`cat${(i%8)+1}`]));
  if(!summary.length)summary.push(['Weitere Benefits','Keine erfasst','muted']);
  $('#benefit-summary').innerHTML=summary.map(([label,val,tone])=>benefitLine(label,val,tone)).join('');
  const a=result.assumptions;
  // The tax year comes from the calculation, so the badge always matches the law that was actually applied.
  const note=$('#comp-hero-note');
  if(note)note.textContent=`Planungsrechnung für Deutschland · Steuerjahr ${a.taxYear}`;
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
        <button type="button" class="btn btn-secondary" data-action="load">Laden</button>
        <button type="button" class="btn btn-secondary" data-action="compare">${state.selected.includes(s.id)?'Ausgewählt':'Vergleichen'}</button>
        <button type="button" class="btn btn-danger" data-action="delete">Löschen</button>
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
  if(!await confirmMessage({message:'Szenario wirklich löschen?',title:'Szenario löschen',confirmLabel:'Löschen',cancelLabel:'Abbrechen',destructive:true}))return;
  await api(`api/compensation/scenarios/${id}?fullWorthSpaceId=${state.space.id}`,{method:'DELETE'});
  state.selected=state.selected.filter(x=>x!==id);
  await loadScenarios();
}

function comparisonCell(label,value){const positive=!String(value).startsWith('−');return `<div class="comparison-cell"><span>${esc(label)}</span><strong class="${positive?'positive':'negative'}">${esc(value)}</strong></div>`}
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
function handle(error){console.error(error);notify(error?.message||'Unbekannter Fehler.');}
