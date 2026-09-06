const H$=s=>document.querySelector(s);
const H$$=s=>[...document.querySelectorAll(s)];
const heuro=new Intl.NumberFormat('de-DE',{style:'currency',currency:'EUR',maximumFractionDigits:0});
const hpct=v=>`${Number(v||0).toLocaleString('de-DE',{minimumFractionDigits:1,maximumFractionDigits:1})} %`;
const hstate={entries:[],timeline:null,editing:null};

initHistory();

function initHistory(){
  const tabs=H$('.comp-tabs'),toast=H$('#comp-error');
  if(!tabs||!toast)return;
  tabs.insertAdjacentHTML('beforeend','<button data-history-tab="history" type="button">Verlauf</button>');
  toast.insertAdjacentHTML('beforebegin',historyMarkup());
  const stack=H$('#tab-calculator .comp-form-stack');
  if(stack)stack.insertAdjacentHTML('afterbegin',historyEditMarkup());
  H$$('[data-history-tab]').forEach(b=>b.addEventListener('click',()=>openHistory()));
  H$('#history-save').addEventListener('click',()=>createHistoryEvent().catch(herror));
  H$('#history-range').addEventListener('change',()=>loadHistory().catch(herror));
  H$('#history-edit-save').addEventListener('click',()=>saveEditedEvent().catch(herror));
  H$('#history-edit-cancel').addEventListener('click',cancelEdit);
  H$('#space-select').addEventListener('change',()=>{if(H$('#tab-history')?.classList.contains('active'))loadHistory().catch(herror)});
  H$('#history-date').value=localDate();
}

function historyMarkup(){return `
<section id="tab-history" class="comp-tab">
  <article class="panel history-toolbar">
    <label>Datum<input id="history-date" type="date"></label>
    <label>Art<select id="history-type">
      <option value="salary">Gehalt</option><option value="tax">Steuer</option>
      <option value="marriage">Heirat</option><option value="child">Kind</option>
      <option value="family">Familie</option><option value="worktime">Arbeitszeit</option>
      <option value="benefit">Benefit</option><option value="company-car">Firmenwagen</option>
      <option value="pension">bAV</option><option value="insurance">Versicherung</option>
      <option value="job">Jobwechsel</option><option value="combined">Mehrere Änderungen</option>
      <option value="other">Sonstiges</option>
    </select></label>
    <label class="history-title">Bezeichnung<input id="history-title" placeholder="z. B. Gehaltserhöhung auf 62.000 €"></label>
    <label class="history-title">Notiz<input id="history-note" maxlength="1000" placeholder="optional"></label>
    <button id="history-save" class="primary-action" type="button">Aktuellen Rechnerstand ab Datum speichern</button>
  </article>
  <div id="history-summary" class="metric-grid history-summary"></div>
  <article class="panel history-chart-card">
    <div class="history-chart-head"><div><h2>Gehalt, Netto und Kaufkraft</h2><small>Alle Werte jährlich. Die Inflationslinie zeigt, welches Brutto für die Kaufkraft des Startpunkts nötig wäre.</small></div>
      <select id="history-range"><option value="all">Gesamt</option><option value="1">1 Jahr</option><option value="3">3 Jahre</option><option value="5">5 Jahre</option></select>
    </div>
    <div id="history-chart"></div>
    <div class="history-legend"><span><i class="history-key history-key-gross"></i>Brutto</span><span><i class="history-key history-key-net"></i>Netto</span><span><i class="history-key history-key-inflation"></i>Kaufkrafterhalt</span><span><i class="history-key history-key-total"></i>Gesamtwert</span></div>
  </article>
  <article class="panel history-years-card"><h2>Jahresvergleich</h2><div id="history-years"></div></article>
  <div id="history-list" class="history-list"></div>
</section>`}

function historyEditMarkup(){return `
<div id="history-edit-bar" class="history-edit-bar" hidden>
  <div class="history-edit-bar-head"><strong>Historische Änderung bearbeiten</strong><span id="history-edit-changes"></span></div>
  <div class="history-edit-grid">
    <label>Datum<input id="history-edit-date" type="date"></label>
    <label>Art<select id="history-edit-type">
      <option value="salary">Gehalt</option><option value="tax">Steuer</option><option value="marriage">Heirat</option><option value="child">Kind</option><option value="family">Familie</option><option value="worktime">Arbeitszeit</option><option value="benefit">Benefit</option><option value="company-car">Firmenwagen</option><option value="pension">bAV</option><option value="insurance">Versicherung</option><option value="job">Jobwechsel</option><option value="combined">Mehrere Änderungen</option><option value="other">Sonstiges</option>
    </select></label>
    <label>Titel<input id="history-edit-title"></label>
    <label>Notiz<input id="history-edit-note" maxlength="1000"></label>
    <button id="history-edit-save" class="primary-action" type="button">Änderung aktualisieren</button>
    <button id="history-edit-cancel" type="button">Abbrechen</button>
  </div>
</div>`}

async function openHistory(){
  H$$('.comp-tabs button').forEach(b=>b.classList.toggle('active',b.dataset.historyTab==='history'));
  H$$('.comp-tab').forEach(tab=>tab.classList.toggle('active',tab.id==='tab-history'));
  await loadHistory().catch(herror);
}

async function loadHistory(){
  const space=spaceId();if(!space)return;
  const range=H$('#history-range')?.value||'all';
  const query=new URLSearchParams({fullWorthSpaceId:space});
  if(range!=='all')query.set('from',subtractYears(localDate(),Number(range)));
  const [entries,timeline]=await Promise.all([
    hapi(`api/compensation/history?fullWorthSpaceId=${encodeURIComponent(space)}`),
    hapi(`api/compensation/timeline?${query}`)
  ]);
  hstate.entries=entries||[];hstate.timeline=timeline;
  renderHistorySummary(timeline?.summary);
  renderHistoryChart(timeline);
  renderHistoryYears(timeline);
  renderHistoryList();
}

async function createHistoryEvent(){
  const space=spaceId();if(!space)throw new Error('Kein Finanzbereich ausgewählt.');
  const title=H$('#history-title').value.trim();if(!title)throw new Error('Bezeichnung fehlt.');
  const payload={effectiveDate:H$('#history-date').value,eventType:H$('#history-type').value,title,note:H$('#history-note').value.trim()||null,profile:readHistoryProfile()};
  await hapi(`api/compensation/history?fullWorthSpaceId=${encodeURIComponent(space)}`,hjson('POST',payload));
  H$('#history-title').value='';H$('#history-note').value='';
  await loadHistory();hmessage('Änderung gespeichert.');
}

function renderHistorySummary(summary){
  const root=H$('#history-summary');
  if(!summary){root.innerHTML='<article class="panel history-empty">Noch keine Historie. Stelle den Rechner auf einen Stand und speichere ihn mit einem Datum.</article>';return}
  root.innerHTML=`
    <article class="metric"><span>Brutto aktuell</span><strong>${heuro.format(summary.currentGrossAnnual)}</strong><small>seit Start ${signedPct(summary.nominalChangePercent)} nominal</small></article>
    <article class="metric"><span>Kaufkrafterhalt</span><strong>${heuro.format(summary.purchasingPowerMaintenanceGrossAnnual)}</strong><small>Inflation seit Start ${signedPct(summary.inflationPercent)}</small></article>
    <article class="metric"><span>Reale Gehaltsänderung</span><strong class="${summary.realChangePercent>=0?'positive':'negative'}">${signedPct(summary.realChangePercent)}</strong><small>Brutto nach Inflation</small></article>
    <article class="metric"><span>Gesamtwert aktuell</span><strong>${heuro.format(summary.currentFullWorthValueAnnual)}</strong><small>Netto ${heuro.format(summary.currentNetAnnual)} / Jahr</small></article>`;
}

function renderHistoryChart(timeline){
  const root=H$('#history-chart'),points=timeline?.points||[];
  if(points.length<1){root.innerHTML='<div class="history-empty">Noch keine Daten für den Verlauf.</div>';return}
  const w=960,h=330,left=62,right=18,top=18,bottom=42;
  const values=points.flatMap(p=>[p.contractualGrossAnnual,p.estimatedCashNetAnnual,p.fullWorthCompensationValueAnnual,p.purchasingPowerMaintenanceGrossAnnual]).map(Number);
  const max=Math.max(...values,1)*1.08,min=0;
  const dates=points.map(p=>new Date(`${p.date}T12:00:00`).getTime()),d0=Math.min(...dates),d1=Math.max(...dates);
  const x=t=>left+(d1===d0?0.5:(t-d0)/(d1-d0))*(w-left-right);
  const y=v=>top+(max-v)/(max-min)*(h-top-bottom);
  const series=(key,cls)=>`<polyline class="history-line ${cls}" points="${points.map((p,i)=>`${x(dates[i]).toFixed(1)},${y(Number(p[key])||0).toFixed(1)}`).join(' ')}"/>`;
  const yTicks=[0,.25,.5,.75,1].map(f=>{const v=max*(1-f),yy=top+f*(h-top-bottom);return `<line class="history-grid" x1="${left}" x2="${w-right}" y1="${yy}" y2="${yy}"/><text class="history-axis" x="${left-8}" y="${yy+3}" text-anchor="end">${shortMoney(v)}</text>`}).join('');
  const markerDates=[...new Set((timeline.events||[]).map(e=>e.effectiveDate))].map(d=>new Date(`${d}T12:00:00`).getTime()).filter(t=>t>=d0&&t<=d1);
  const markers=markerDates.map(t=>`<line class="history-event-line" x1="${x(t)}" x2="${x(t)}" y1="${top}" y2="${h-bottom}"/>`).join('');
  const first=points[0],last=points[points.length-1];
  root.innerHTML=`<svg class="history-chart" viewBox="0 0 ${w} ${h}" role="img" aria-label="Gehaltsverlauf mit Inflation">${yTicks}${markers}${series('contractualGrossAnnual','history-line-gross')}${series('estimatedCashNetAnnual','history-line-net')}${series('purchasingPowerMaintenanceGrossAnnual','history-line-inflation')}${series('fullWorthCompensationValueAnnual','history-line-total')}<text class="history-axis" x="${left}" y="${h-12}">${fmtDate(first.date)}</text><text class="history-axis" x="${w-right}" y="${h-12}" text-anchor="end">${fmtDate(last.date)}</text></svg>`;
}

function renderHistoryYears(timeline){
  const root=H$('#history-years'),points=timeline?.points||[];
  if(!points.length){root.innerHTML='<div class="history-empty">Noch keine Jahreswerte.</div>';return}
  const byYear=new Map();
  for(const point of points)byYear.set(Number(String(point.date).slice(0,4)),point);
  const rows=[...byYear.entries()].sort((a,b)=>b[0]-a[0]);
  root.innerHTML=`<table class="history-years"><thead><tr><th>Jahr</th><th>Brutto</th><th>Netto</th><th>Steuern</th><th>Sozialabgaben</th><th>AG-Kosten</th><th>Gesamtwert</th><th>Real seit Start</th></tr></thead><tbody>${rows.map(([year,p])=>`<tr><td>${year}</td><td>${heuro.format(p.contractualGrossAnnual)}</td><td>${heuro.format(p.estimatedCashNetAnnual)}</td><td>${heuro.format(p.taxesAnnual)}</td><td>${heuro.format(p.socialInsuranceAnnual)}</td><td>${heuro.format(p.employerTotalCostAnnual)}</td><td>${heuro.format(p.fullWorthCompensationValueAnnual)}</td><td class="${p.realChangeFromBaselinePercent>=0?'positive':'negative'}">${signedPct(p.realChangeFromBaselinePercent)}</td></tr>`).join('')}</tbody></table>`;
}

function historyDeltaHtml(delta){
  if(!delta)return '';
  const items=[
    ['Brutto',delta.grossAnnual],['Netto',delta.cashNetAnnual],['Steuern',delta.taxesAnnual],
    ['Sozial',delta.socialInsuranceAnnual],['AG-Kosten',delta.employerCostAnnual],['Gesamtwert',delta.fullWorthValueAnnual]
  ].filter(([,v])=>Math.abs(Number(v||0))>=0.01);
  if(!items.length)return '<div class="history-delta"><span>Keine finanzielle Änderung</span></div>';
  return `<div class="history-delta">${items.map(([label,v])=>`<span class="${Number(v)>=0?'positive':'negative'}">${label} ${signedMoney(v)}</span>`).join('')}</div>`;
}

function renderHistoryList(){
  const root=H$('#history-list'),entries=[...hstate.entries].sort((a,b)=>b.effectiveDate.localeCompare(a.effectiveDate)||b.sequence-a.sequence);
  if(!entries.length){root.innerHTML='<article class="panel history-empty">Noch keine Änderungen gespeichert.</article>';return}
  root.innerHTML=entries.map(e=>`
    <article class="panel history-entry" data-history-id="${e.id}">
      <div class="history-entry-date">${fmtDate(e.effectiveDate)}</div>
      <div class="history-entry-main"><div><span class="history-event-badge">${eventLabel(e.eventType)}</span></div><h3>${esc(e.title)}</h3>${e.note?`<p>${esc(e.note)}</p>`:''}<div class="history-changes">${(e.changedFields||[]).slice(0,8).map(f=>`<span class="history-change">${esc(fieldLabel(f))}</span>`).join('')}${(e.changedFields||[]).length>8?`<span class="history-change">+${e.changedFields.length-8}</span>`:''}</div>${historyDeltaHtml(e.deltaFromPrevious)}</div>
      <div class="history-entry-actions"><button type="button" data-history-edit>Bearbeiten</button><button type="button" data-history-delete>Löschen</button></div>
    </article>`).join('');
  root.querySelectorAll('[data-history-id]').forEach(card=>{
    const entry=hstate.entries.find(x=>x.id===card.dataset.historyId);
    card.querySelector('[data-history-edit]').addEventListener('click',()=>beginEdit(entry));
    card.querySelector('[data-history-delete]').addEventListener('click',()=>deleteEvent(entry).catch(herror));
  });
}

function beginEdit(entry){
  hstate.editing=entry;fillHistoryProfile(entry.resolvedProfile);
  H$('#history-edit-date').value=entry.effectiveDate;H$('#history-edit-type').value=entry.eventType;
  H$('#history-edit-title').value=entry.title;H$('#history-edit-note').value=entry.note||'';
  H$('#history-edit-changes').textContent=`${(entry.changedFields||[]).length} geänderte Felder`;
  H$('#history-edit-bar').hidden=false;
  H$$('.comp-tabs button').forEach(b=>b.classList.toggle('active',b.dataset.tab==='calculator'));
  H$$('.comp-tab').forEach(tab=>tab.classList.toggle('active',tab.id==='tab-calculator'));
  H$('#history-edit-bar').scrollIntoView({behavior:'smooth',block:'start'});
}

async function saveEditedEvent(){
  if(!hstate.editing)return;
  const payload={effectiveDate:H$('#history-edit-date').value,eventType:H$('#history-edit-type').value,title:H$('#history-edit-title').value.trim(),note:H$('#history-edit-note').value.trim()||null,profile:readHistoryProfile()};
  if(!payload.title)throw new Error('Bezeichnung fehlt.');
  await hapi(`api/compensation/history/${hstate.editing.id}?fullWorthSpaceId=${encodeURIComponent(spaceId())}`,hjson('PUT',payload));
  cancelEdit();await loadHistory();hmessage('Historische Änderung aktualisiert.');
}

function cancelEdit(){hstate.editing=null;if(H$('#history-edit-bar'))H$('#history-edit-bar').hidden=true}

async function deleteEvent(entry){
  if(!confirm(`Änderung „${entry.title}“ vom ${fmtDate(entry.effectiveDate)} löschen?`))return;
  await hapi(`api/compensation/history/${entry.id}?fullWorthSpaceId=${encodeURIComponent(spaceId())}`,{method:'DELETE'});
  await loadHistory();hmessage('Änderung gelöscht.');
}

function readHistoryProfile(){
  const payments=Math.min(14,Math.max(12,Math.round(hnum('salary-payments')||12))),mode=hval('gross-period')==='monthly'?'monthly':'annual',taxClass=Math.round(hnum('tax-class'));
  return{name:hval('profile-name')||'Aktuelles Gehalt',annualGross:mode==='monthly'?hnum('gross-input')*payments:hnum('gross-input'),annualBonus:hnum('annual-bonus'),grossInputMode:mode,salaryPaymentsPerYear:payments,taxClass,taxClass4Factor:taxClass===4?Math.min(1,Math.max(.001,hnum('tax-class4-factor')||1)):1,annualTaxAllowance:Math.max(0,hnum('annual-tax-allowance')),childAllowanceUnits:hval('child-allowance-units')===''?null:Math.max(0,hnum('child-allowance-units')),stateCode:hval('state-code'),churchTax:H$('#church-tax').checked,childrenUnder25:Math.max(0,Math.round(hnum('children'))),age:hval('employee-age')===''?null:Math.max(0,Math.round(hnum('employee-age'))),childlessCareSurcharge:H$('#childless-surcharge').checked,pensionInsuranceEnabled:H$('#pension-insurance').checked,unemploymentInsuranceEnabled:H$('#unemployment-insurance').checked,healthInsuranceAdditionalRatePercent:hnum('health-addon'),weeklyHours:hnum('weekly-hours'),vacationDays:Math.round(hnum('vacation-days')),spouseAnnualTaxableIncome:0,companyCar:{enabled:H$('#car-enabled').checked,listPrice:hnum('car-list-price'),taxableListPriceFactor:historyCarFactor(),vehicleType:hval('car-vehicle-type')||'manual',acquisitionDate:hval('car-acquisition-date')||null,electricRangeKm:hnum('car-electric-range'),co2GramsPerKm:hnum('car-co2'),oneWayCommuteKm:hnum('car-commute'),commuteMethod:hval('car-commute-method')||'monthly',commuteDaysPerMonth:Math.max(0,Math.min(31,Math.round(hnum('car-commute-days')))),employeeContributionMonthly:hnum('car-contribution'),employerCostMonthly:hnum('car-employer-cost'),privateAlternativeCostMonthly:hnum('car-private-cost')},occupationalPension:{employeeContributionMonthly:hnum('bav-employee'),employerContributionMonthly:hnum('bav-employer'),projectionYears:Math.round(hnum('bav-years')),expectedAnnualReturnPercent:hnum('bav-return')},benefits:H$$('.benefit-row').map(row=>{const g=x=>row.querySelector(`[data-benefit="${x}"]`);return{name:g('name').value.trim()||'Benefit',employerCostMonthly:Number(g('employerCostMonthly').value)||0,personalValueMonthly:Number(g('personalValueMonthly').value)||0,taxableBenefitMonthly:Number(g('taxableBenefitMonthly').value)||0,employeeCostMonthly:Number(g('employeeCostMonthly').value)||0}})};
}

function fillHistoryProfile(p){
  hset('profile-name',p.name);const mode=p.grossInputMode==='monthly'?'monthly':'annual',payments=Math.min(14,Math.max(12,Number(p.salaryPaymentsPerYear)||12));
  hset('gross-period',mode);hset('salary-payments',payments);hset('gross-input',mode==='monthly'?(Number(p.annualGross)||0)/payments:p.annualGross);hset('annual-bonus',p.annualBonus);
  hset('tax-class',p.taxClass||1);hset('tax-class4-factor',p.taxClass4Factor||1);hset('annual-tax-allowance',p.annualTaxAllowance??0);hset('child-allowance-units',p.childAllowanceUnits??'');hset('state-code',p.stateCode||'BW');
  H$('#church-tax').checked=!!p.churchTax;hset('children',p.childrenUnder25??0);hset('employee-age',p.age??'');H$('#childless-surcharge').checked=p.childlessCareSurcharge!==false;H$('#pension-insurance').checked=p.pensionInsuranceEnabled!==false;H$('#unemployment-insurance').checked=p.unemploymentInsuranceEnabled!==false;
  hset('health-addon',p.healthInsuranceAdditionalRatePercent??2.9);hset('weekly-hours',p.weeklyHours??40);hset('vacation-days',p.vacationDays??30);
  const car=p.companyCar||{};H$('#car-enabled').checked=!!car.enabled;hset('car-list-price',car.listPrice??50000);hset('car-factor',car.taxableListPriceFactor??1);hset('car-vehicle-type',car.vehicleType||'manual');hset('car-acquisition-date',car.acquisitionDate||'2026-01-01');hset('car-electric-range',car.electricRangeKm??80);hset('car-co2',car.co2GramsPerKm??50);hset('car-commute',car.oneWayCommuteKm??0);hset('car-commute-method',car.commuteMethod||'monthly');hset('car-commute-days',car.commuteDaysPerMonth??10);hset('car-contribution',car.employeeContributionMonthly??0);hset('car-employer-cost',car.employerCostMonthly??0);hset('car-private-cost',car.privateAlternativeCostMonthly??0);
  const bav=p.occupationalPension||{};hset('bav-employee',bav.employeeContributionMonthly??0);hset('bav-employer',bav.employerContributionMonthly??0);hset('bav-years',bav.projectionYears??30);hset('bav-return',bav.expectedAnnualReturnPercent??3);
  const list=H$('#benefits-list');list.innerHTML='';(p.benefits||[]).forEach(addHistoryBenefit);
  ['gross-period','tax-class','car-enabled','car-vehicle-type','car-commute-method'].forEach(id=>H$('#'+id)?.dispatchEvent(new Event('change')));
  H$('#calculate')?.click();
}

function addHistoryBenefit(b={}){
  const row=document.createElement('div');row.className='benefit-row';
  row.innerHTML=`<label>Name<input data-benefit="name" value="${attr(b.name||'')}"></label><label>AG-Kosten / Monat<input data-benefit="employerCostMonthly" type="number" min="0" value="${hn(b.employerCostMonthly)}"></label><label>Dein Wert / Monat<input data-benefit="personalValueMonthly" type="number" min="0" value="${hn(b.personalValueMonthly)}"></label><label>Steuerpflichtig / Monat<input data-benefit="taxableBenefitMonthly" type="number" min="0" value="${hn(b.taxableBenefitMonthly)}"></label><label>Eigenkosten / Monat<input data-benefit="employeeCostMonthly" type="number" min="0" value="${hn(b.employeeCostMonthly)}"></label><button type="button">×</button>`;
  row.querySelector('button').addEventListener('click',()=>row.remove());H$('#benefits-list').appendChild(row);
}

function historyCarFactor(){
  const type=hval('car-vehicle-type')||'manual';if(type==='manual')return hnum('car-factor')||1;if(type==='combustion')return 1;
  const price=hnum('car-list-price'),date=hval('car-acquisition-date')||'2026-01-01';
  if(type==='electric'){const limit=date>='2025-07-01'?100000:(date>='2024-01-01'?70000:60000);return price<=limit?.25:.5}
  if(type==='hybrid'){const min=date>='2025-01-01'?80:(date>='2022-01-01'?60:40),co2=hnum('car-co2');return hnum('car-electric-range')>=min||(co2>0&&co2<=50)?.5:1}
  return 1;
}

function fieldLabel(path){const map={annualGross:'Brutto',annualBonus:'Bonus',taxClass:'Steuerklasse',taxClass4Factor:'Faktor',annualTaxAllowance:'Freibetrag',childAllowanceUnits:'Kinderfreibetrag',childrenUnder25:'Kinder',age:'Alter',churchTax:'Kirchensteuer',weeklyHours:'Wochenstunden',vacationDays:'Urlaub',healthInsuranceAdditionalRatePercent:'GKV-Zusatzbeitrag','companyCar.enabled':'Firmenwagen','companyCar.listPrice':'Listenpreis','companyCar.oneWayCommuteKm':'Arbeitsweg','occupationalPension.employeeContributionMonthly':'bAV eigener Beitrag','occupationalPension.employerContributionMonthly':'bAV Arbeitgeber',benefits:'Benefits'};return map[path]||path.replaceAll('.',' › ')}
function eventLabel(t){return ({salary:'Gehalt',tax:'Steuer',marriage:'Heirat',child:'Kind',family:'Familie',worktime:'Arbeitszeit',benefit:'Benefit','company-car':'Firmenwagen',pension:'bAV',insurance:'Versicherung',job:'Jobwechsel',combined:'Mehrere Änderungen',other:'Sonstiges'})[t]||t}
function signedPct(v){const n=Number(v||0);return `${n>=0?'+':'−'}${hpct(Math.abs(n))}`}
function signedMoney(v){const n=Number(v||0);return `${n>=0?'+':'−'}${heuro.format(Math.abs(n))}`}
function shortMoney(v){const n=Number(v||0);return n>=1000?`${(n/1000).toLocaleString('de-DE',{maximumFractionDigits:0})}k €`:heuro.format(n)}
function localDate(){const d=new Date(),p=n=>String(n).padStart(2,'0');return `${d.getFullYear()}-${p(d.getMonth()+1)}-${p(d.getDate())}`}
function subtractYears(date,years){const d=new Date(`${date}T12:00:00`);d.setFullYear(d.getFullYear()-years);return localIso(d)}
function localIso(d){const p=n=>String(n).padStart(2,'0');return `${d.getFullYear()}-${p(d.getMonth()+1)}-${p(d.getDate())}`}
function fmtDate(v){if(!v)return'—';return new Intl.DateTimeFormat('de-DE').format(new Date(`${String(v).slice(0,10)}T12:00:00`))}
function spaceId(){return H$('#space-select')?.value||''}
function hval(id){return H$('#'+id)?.value??''}function hnum(id){return Number(hval(id))||0}function hset(id,v){const e=H$('#'+id);if(e)e.value=v??''}
function hn(v){const n=Number(v);return Number.isFinite(n)?n:0}
function hjson(method,body){return{method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}}
async function hapi(path,options={}){const r=await fetch(`/bff/backend/${String(path).replace(/^\//,'')}`,options);if(!r.ok){let m=`Fehler ${r.status}`;try{const b=await r.json();m=b.error||b.title||b.message||m}catch{}throw new Error(m)}if(r.status===204)return null;return r.json()}
function hmessage(t){const e=H$('#comp-error');e.textContent=t;e.classList.add('show');clearTimeout(hmessage.timer);hmessage.timer=setTimeout(()=>e.classList.remove('show'),3200)}
function herror(e){console.error(e);hmessage(e?.message||'Unbekannter Fehler.')}
function esc(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function attr(v){return esc(v)}
