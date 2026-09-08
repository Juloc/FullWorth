import { confirmMessage } from '../ui/confirm.js';
import {
  $ as H$, $$ as H$$, euro as heuro, euro2 as heuro2, esc, attr, val as hval, num as hnum, setVal as hset,
  spaceId, fmtDate, localIsoDate, api as hapi, json as hjson, notify as hmessage,
  signedEuro0 as signedMoney, readProfile as readHistoryProfile, fillProfile as fillHistoryProfile,
  loadOtherIncomeTypes, otherIncomeTypeOptions, otherIncomeName, otherIncomeActiveOn
} from './compensation-shared.js';
// The history view intentionally shows one decimal on percentages (the calculator allows two).
const hpct=v=>`${Number(v||0).toLocaleString('de-DE',{minimumFractionDigits:1,maximumFractionDigits:1})} %`;

// Time windows in months, mirroring the Vermögen view. 0 = all available history.
const HISTORY_WINDOWS=[{m:6,label:'6 M'},{m:12,label:'1 J'},{m:24,label:'2 J'},{m:60,label:'5 J'},{m:120,label:'10 J'},{m:0,label:'Max'}];

// All series are ANNUAL values, so they share one scale. The last three come from the separate
// other-income track (sonstige regelmäßige Einkünfte) and are NOT employer figures.
const HISTORY_SERIES=[
  ['contractualGrossAnnual','gross','Brutto'],
  ['estimatedCashNetAnnual','net','Netto'],
  ['purchasingPowerMaintenanceGrossAnnual','inflation','Kaufkrafterhalt'],
  ['fullWorthCompensationValueAnnual','total','Gesamtwert'],
  ['companyCarNetCashImpactAnnual','car','Firmenwagen'],
  ['otherRegularIncomeAnnual','other','Sonstige Einkünfte'],
  ['otherRegularIncomeCountedAnnual','other-counted','Sonstige angerechnet'],
  ['personallyAvailableTotalIncomeAnnual','personal','Persönlich verfügbar']
];

const hstate={
  entries:[],timeline:null,editing:null,
  windowMonths:0,
  scope:'single',
  // Firmenwagen is off by default: it is a small, often negative line that would otherwise flatten the scale.
  // The three other-income curves start off too and are switched on once (see loadHistory) as soon as the
  // timeline actually reports such records — without any they would just duplicate the Netto line.
  series:{gross:true,net:true,inflation:true,total:true,car:false,other:false,'other-counted':false,personal:false},
  otherSeriesPrimed:false
};

const enabledSeries=()=>HISTORY_SERIES.filter(([,cls])=>hstate.series[cls]);

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
  H$('#history-edit-save').addEventListener('click',()=>saveEditedEvent().catch(herror));
  H$('#history-edit-cancel').addEventListener('click',cancelEdit);
  H$('#space-select').addEventListener('change',()=>{if(H$('#tab-history')?.classList.contains('active'))loadHistory().catch(herror)});
  H$('#history-date').value=localIsoDate();

  // One delegated handler for the time window, the single/joint scope and the curve toggles.
  H$('#tab-history')?.addEventListener('click',event=>{
    const win=event.target.closest('[data-history-window]');
    if(win){hstate.windowMonths=Number(win.dataset.historyWindow)||0;syncHistoryControls();loadHistory().catch(herror);return}
    const scope=event.target.closest('[data-history-scope]');
    if(scope){hstate.scope=scope.dataset.historyScope==='joint'?'joint':'single';syncHistoryControls();loadHistory().catch(herror);return}
    const serie=event.target.closest('[data-history-series]');
    if(serie){
      const key=serie.dataset.historySeries;
      hstate.series[key]=!hstate.series[key];
      syncHistoryControls();
      renderHistoryChart(hstate.timeline);
    }
  });
  syncHistoryControls();
}

function syncHistoryControls(){
  H$$('[data-history-window]').forEach(b=>{
    const on=Number(b.dataset.historyWindow)===hstate.windowMonths;
    b.classList.toggle('active',on);b.setAttribute('aria-selected',on?'true':'false');
  });
  H$$('[data-history-scope]').forEach(b=>{
    const on=b.dataset.historyScope===hstate.scope;
    b.classList.toggle('active',on);b.setAttribute('aria-selected',on?'true':'false');
  });
  H$$('[data-history-series]').forEach(b=>{
    const on=!!hstate.series[b.dataset.historySeries];
    b.classList.toggle('on',on);b.setAttribute('aria-pressed',on?'true':'false');
  });
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
    <button id="history-save" class="btn btn-primary" type="button">Aktuellen Rechnerstand ab Datum speichern</button>
  </article>
  <div id="history-summary" class="metric-grid history-summary"></div>
  <article class="panel history-chart-card">
    <div class="history-chart-head">
      <div><h2>Gehalt, Netto und Kaufkraft</h2><small>Alle Werte jährlich. Die Inflationslinie zeigt, welches Brutto für die Kaufkraft des Startpunkts nötig wäre.</small></div>
      <div class="fw-cycle history-scope" role="tablist" aria-label="Umfang">
        <button type="button" role="tab" data-history-scope="single">Einzeln</button>
        <button type="button" role="tab" data-history-scope="joint">Gemeinsam</button>
      </div>
    </div>
    <div class="fw-cycle history-windows" role="tablist" aria-label="Zeitraum">${HISTORY_WINDOWS.map(x=>`<button type="button" role="tab" data-history-window="${x.m}">${x.label}</button>`).join('')}</div>
    <div id="history-chart"></div>
    <div class="history-legend" aria-label="Kurven ein- und ausblenden">${HISTORY_SERIES.map(([,cls,label])=>`<button type="button" class="history-series-toggle" data-history-series="${cls}"><i class="history-key history-key-${cls}"></i>${label}</button>`).join('')}</div>
    <div id="history-other-income" class="history-track" hidden></div>
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
    <button id="history-edit-save" class="btn btn-primary" type="button">Änderung aktualisieren</button>
    <button id="history-edit-cancel" class="btn btn-secondary" type="button">Abbrechen</button>
  </div>
</div>`}

async function openHistory(){
  H$$('.comp-tabs button').forEach(b=>b.classList.toggle('active',b.dataset.historyTab==='history'));
  H$$('.comp-tab').forEach(tab=>tab.classList.toggle('active',tab.id==='tab-history'));
  await loadHistory().catch(herror);
}

async function loadHistory(){
  const space=spaceId();if(!space)return;
  const query=new URLSearchParams({fullWorthSpaceId:space});
  if(hstate.windowMonths>0)query.set('from',subtractMonths(localIsoDate(),hstate.windowMonths));
  if(hstate.scope==='joint')query.set('scope','joint');
  const [entries,timeline]=await Promise.all([
    hapi(`api/compensation/history?fullWorthSpaceId=${encodeURIComponent(space)}`),
    hapi(`api/compensation/timeline?${query}`)
  ]);
  hstate.entries=entries||[];hstate.timeline=timeline;
  // Only once: the first timeline that carries other income turns its curves on, afterwards the user's
  // own legend choice is respected (including switching them off again).
  if(!hstate.otherSeriesPrimed&&hasOtherIncome(timeline)){
    hstate.otherSeriesPrimed=true;
    hstate.series.other=true;hstate.series.personal=true;
    syncHistoryControls();
  }
  if(hasOtherIncome(timeline))await loadOtherIncomeTypes();
  renderHistorySummary(timeline?.summary,timeline);
  renderHistoryChart(timeline);
  renderOtherIncomeTrack(timeline);
  renderHistoryYears(timeline);
  renderHistoryList();
}

const hasOtherIncome=timeline=>
  (timeline?.otherIncome||[]).length>0||Number(timeline?.summary?.currentOtherRegularIncomeAnnual||0)>0;

// A static caption under the legend: which records the „Sonstige Einkünfte“ curve is actually made of.
// Static on purpose — the hover tip must stay the only thing that changes while the mouse moves.
function renderOtherIncomeTrack(timeline){
  const root=H$('#history-other-income');if(!root)return;
  const records=timeline?.otherIncome||[];
  if(!records.length){root.hidden=true;root.innerHTML='';return}
  const options=otherIncomeTypeOptions();
  root.hidden=false;
  root.innerHTML=`<span class="history-track-label">Sonstige Einkünfte (nicht Teil des Arbeitgeber-Gesamtpakets):</span>`+
    records.map(record=>`<span class="history-track-item${record.countsTowardPersonalIncome?' counted':''}">`+
      `${esc(otherIncomeName(record,options))} · ${esc(heuro2.format(Number(record.monthlyAmount)||0))} / Monat`+
      `${record.countsTowardPersonalIncome?' · angerechnet':' · nicht angerechnet'}`+
      `${otherIncomeActiveOn(record)?'':' · aktuell inaktiv'}</span>`).join('');
}

async function createHistoryEvent(){
  const space=spaceId();if(!space)throw new Error('Kein Finanzbereich ausgewählt.');
  const title=H$('#history-title').value.trim();if(!title)throw new Error('Bezeichnung fehlt.');
  const payload={effectiveDate:H$('#history-date').value,eventType:H$('#history-type').value,title,note:H$('#history-note').value.trim()||null,profile:readHistoryProfile()};
  await hapi(`api/compensation/history?fullWorthSpaceId=${encodeURIComponent(space)}`,hjson('POST',payload));
  H$('#history-title').value='';H$('#history-note').value='';
  await loadHistory();hmessage('Änderung gespeichert.');
}

function renderHistorySummary(summary,timeline){
  const root=H$('#history-summary');
  if(!summary){root.innerHTML='<article class="panel history-empty">Noch keine Historie. Stelle den Rechner auf einen Stand und speichere ihn mit einem Datum.</article>';return}
  // The two other-income metrics only appear when such records exist, so a pure salary history keeps
  // its four cards.
  const otherIncome=hasOtherIncome(timeline)?`
    <article class="metric"><span>Sonstige Einkünfte</span><strong>${heuro.format(summary.currentOtherRegularIncomeAnnual)}</strong><small>${heuro2.format((Number(summary.currentOtherRegularIncomeAnnual)||0)/12)} / Monat · kein Arbeitgeber-Bestandteil</small></article>
    <article class="metric"><span>Persönlich verfügbar</span><strong>${heuro.format(summary.currentPersonallyAvailableTotalIncomeAnnual)}</strong><small>Netto ${heuro.format(summary.currentNetAnnual)} + angerechnete sonstige Einkünfte</small></article>`:'';
  root.innerHTML=`
    <article class="metric"><span>Brutto aktuell</span><strong>${heuro.format(summary.currentGrossAnnual)}</strong><small>seit Start ${signedPct(summary.nominalChangePercent)} nominal</small></article>
    <article class="metric"><span>Kaufkrafterhalt</span><strong>${heuro.format(summary.purchasingPowerMaintenanceGrossAnnual)}</strong><small>Inflation seit Start ${signedPct(summary.inflationPercent)}</small></article>
    <article class="metric"><span>Reale Gehaltsänderung</span><strong class="${summary.realChangePercent>=0?'positive':'negative'}">${signedPct(summary.realChangePercent)}</strong><small>Brutto nach Inflation</small></article>
    <article class="metric"><span>Gesamtwert aktuell</span><strong>${heuro.format(summary.currentFullWorthValueAnnual)}</strong><small>Netto ${heuro.format(summary.currentNetAnnual)} / Jahr</small></article>${otherIncome}`;
}

function renderHistoryChart(timeline){
  const root=H$('#history-chart'),points=timeline?.points||[];
  if(points.length<1){root.innerHTML='<div class="history-empty">Noch keine Daten für den Verlauf.</div>';return}
  const w=960,h=330,left=62,right=18,top=18,bottom=42;
  const shown=enabledSeries();
  if(!shown.length){root.innerHTML='<div class="history-empty">Keine Kurve ausgewählt. Wähle oben mindestens einen Wert.</div>';return}
  const values=points.flatMap(p=>shown.map(([key])=>Number(p[key])||0));
  // The Firmenwagen line can be negative, so the scale must be able to go below zero.
  const max=Math.max(...values,1)*1.08,min=Math.min(0,...values);
  const dates=points.map(p=>new Date(`${p.date}T12:00:00`).getTime()),d0=Math.min(...dates),d1=Math.max(...dates);
  const x=t=>left+(d1===d0?0.5:(t-d0)/(d1-d0))*(w-left-right);
  const y=v=>top+(max-v)/(max-min)*(h-top-bottom);
  const series=(key,cls)=>`<polyline class="history-line ${cls}" points="${points.map((p,i)=>`${x(dates[i]).toFixed(1)},${y(Number(p[key])||0).toFixed(1)}`).join(' ')}"/>`;
  const dots=(key,cls)=>points.map((p,i)=>`<circle class="history-dot ${cls}" cx="${x(dates[i]).toFixed(1)}" cy="${y(Number(p[key])||0).toFixed(1)}" r="2.4"/>`).join('');
  const yTicks=[0,.25,.5,.75,1].map(f=>{const v=max-(max-min)*f,yy=top+f*(h-top-bottom);return `<line class="history-grid" x1="${left}" x2="${w-right}" y1="${yy}" y2="${yy}"/><text class="history-axis" x="${left-8}" y="${yy+3}" text-anchor="end">${shortMoney(v)}</text>`}).join('');
  const markerDates=[...new Set((timeline.events||[]).map(e=>e.effectiveDate))].map(d=>new Date(`${d}T12:00:00`).getTime()).filter(t=>t>=d0&&t<=d1);
  const markers=markerDates.map(t=>`<line class="history-event-line" x1="${x(t)}" x2="${x(t)}" y1="${top}" y2="${h-bottom}"/>`).join('');
  const first=points[0],last=points[points.length-1];
  root.innerHTML=`<div class="history-chart-wrap">`+
    `<svg class="history-chart" viewBox="0 0 ${w} ${h}" role="img" aria-label="Gehaltsverlauf mit Inflation, Werte per Mauszeiger">`+
      `${yTicks}${markers}`+
      `<line class="history-crosshair is-hidden" x1="0" x2="0" y1="${top}" y2="${h-bottom}"/>`+
      shown.map(([key,cls])=>series(key,`history-line-${cls}`)).join('')+
      shown.map(([key,cls])=>dots(key,`history-dot-${cls}`)).join('')+
      `<g class="history-hover-dots"></g>`+
      `<rect class="history-hit" x="${left}" y="${top}" width="${(w-left-right).toFixed(1)}" height="${(h-top-bottom).toFixed(1)}" fill="transparent"/>`+
      `<text class="history-axis" x="${left}" y="${h-12}">${fmtDate(first.date)}</text><text class="history-axis" x="${w-right}" y="${h-12}" text-anchor="end">${fmtDate(last.date)}</text>`+
    `</svg>`+
    `<div class="history-chart-tip" hidden></div>`+
  `</div>`;
  const eventByDate=new Map();(timeline.events||[]).forEach(e=>{if(!eventByDate.has(e.effectiveDate))eventByDate.set(e.effectiveDate,e.title)});
  wireChartHover(root,points,dates,{x,y,w},eventByDate,shown);
}

// The hover readout is an ABSOLUTELY positioned box inside .history-chart-wrap: showing, hiding, moving
// or regrowing it can never reflow the card or the page — and the card itself must not react to :hover at
// all (see the transform:none guard in compensation-history.css). With all eight curves on, the tip can be
// taller than the plot area, so it is clamped against the whole card instead of just the plot rectangle.
function wireChartHover(root,points,dates,geo,eventByDate,shown){
  const wrap=root.querySelector('.history-chart-wrap'),svg=root.querySelector('svg.history-chart');
  const card=root.closest('.history-chart-card');
  const tip=root.querySelector('.history-chart-tip'),cross=svg?.querySelector('.history-crosshair'),hoverG=svg?.querySelector('.history-hover-dots');
  if(!wrap||!svg||!tip||!cross||!hoverG)return;
  function move(evt){
    const rect=svg.getBoundingClientRect();if(!rect.width)return;
    const vbx=(evt.clientX-rect.left)/rect.width*geo.w;
    let idx=0,best=Infinity;
    for(let i=0;i<points.length;i++){const d=Math.abs(geo.x(dates[i])-vbx);if(d<best){best=d;idx=i}}
    const p=points[idx],px=geo.x(dates[idx]);
    cross.setAttribute('x1',px.toFixed(1));cross.setAttribute('x2',px.toFixed(1));cross.classList.remove('is-hidden');
    hoverG.innerHTML=shown.map(([key,cls])=>`<circle class="history-dot-active history-dot-${cls}" cx="${px.toFixed(1)}" cy="${geo.y(Number(p[key])||0).toFixed(1)}" r="4.2"/>`).join('');
    const ev=eventByDate.get(p.date);
    tip.innerHTML=`<div class="tip-date">${fmtDate(p.date)}${ev?` · <span class="tip-event">${esc(ev)}</span>`:''}</div>`+
      shown.map(([key,cls,label])=>`<div class="tip-row"><span class="tip-key"><i class="history-key history-key-${cls}"></i>${label}</span><span class="tip-val">${heuro.format(Number(p[key])||0)}</span></div>`).join('')+
      (hstate.series.personal?`<div class="tip-row tip-sub"><span class="tip-key">Persönlich verfügbar / Monat</span><span class="tip-val">${heuro2.format((Number(p.personallyAvailableTotalIncomeAnnual)||0)/12)}</span></div>`:'')+
      `<div class="tip-row tip-sub"><span class="tip-key">Steuern</span><span class="tip-val">${heuro.format(p.taxesAnnual)}</span></div>`+
      `<div class="tip-row tip-sub"><span class="tip-key">Sozialabgaben</span><span class="tip-val">${heuro.format(p.socialInsuranceAnnual)}</span></div>`+
      `<div class="tip-row tip-sub"><span class="tip-key">AG-Kosten</span><span class="tip-val">${heuro.format(p.employerTotalCostAnnual)}</span></div>`+
      `<div class="tip-row tip-sub"><span class="tip-key">Real seit Start</span><span class="tip-val ${p.realChangeFromBaselinePercent>=0?'positive':'negative'}">${signedPct(p.realChangeFromBaselinePercent)}</span></div>`;
    tip.hidden=false;
    const wr=wrap.getBoundingClientRect(),tw=tip.offsetWidth||200,th=tip.offsetHeight||150;
    let lx=evt.clientX-wr.left+16;if(lx+tw>wr.width)lx=evt.clientX-wr.left-tw-16;
    // Vertical bounds are the card's, so a tall tip stays fully readable instead of being cut off at the
    // plot edge — and still never leaves the card.
    const box=card?card.getBoundingClientRect():wr;
    const minY=Math.min(4,box.top-wr.top+8),maxY=box.bottom-wr.top-th-8;
    let ly=evt.clientY-wr.top-th/2;
    tip.style.left=`${Math.max(4,lx)}px`;tip.style.top=`${Math.min(Math.max(minY,ly),Math.max(minY,maxY))}px`;
  }
  svg.addEventListener('mousemove',move);
  svg.addEventListener('mouseleave',()=>{tip.hidden=true;cross.classList.add('is-hidden');hoverG.innerHTML=''});
}

function renderHistoryYears(timeline){
  const root=H$('#history-years'),points=timeline?.points||[];
  if(!points.length){root.innerHTML='<div class="history-empty">Noch keine Jahreswerte.</div>';return}
  const byYear=new Map();
  for(const point of points)byYear.set(Number(String(point.date).slice(0,4)),point);
  const rows=[...byYear.entries()].sort((a,b)=>b[0]-a[0]);
  // The two income columns are added only when the space actually has other-income records.
  const withOther=hasOtherIncome(timeline);
  const extraHead=withOther?'<th>Sonstige Einkünfte</th><th>Persönlich verfügbar</th>':'';
  const extraCells=p=>withOther?`<td>${heuro.format(p.otherRegularIncomeAnnual)}</td><td>${heuro.format(p.personallyAvailableTotalIncomeAnnual)}</td>`:'';
  root.innerHTML=`<table class="history-years"><thead><tr><th>Jahr</th><th>Brutto</th><th>Netto</th><th>Steuern</th><th>Sozialabgaben</th><th>AG-Kosten</th><th>Gesamtwert</th>${extraHead}<th>Real seit Start</th></tr></thead><tbody>${rows.map(([year,p])=>`<tr><td>${year}</td><td>${heuro.format(p.contractualGrossAnnual)}</td><td>${heuro.format(p.estimatedCashNetAnnual)}</td><td>${heuro.format(p.taxesAnnual)}</td><td>${heuro.format(p.socialInsuranceAnnual)}</td><td>${heuro.format(p.employerTotalCostAnnual)}</td><td>${heuro.format(p.fullWorthCompensationValueAnnual)}</td>${extraCells(p)}<td class="${p.realChangeFromBaselinePercent>=0?'positive':'negative'}">${signedPct(p.realChangeFromBaselinePercent)}</td></tr>`).join('')}</tbody></table>`;
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
      <div class="history-entry-actions"><button type="button" class="btn btn-secondary" data-history-edit>Bearbeiten</button><button type="button" class="btn btn-danger" data-history-delete>Löschen</button></div>
    </article>`).join('');
  root.querySelectorAll('[data-history-id]').forEach(card=>{
    const entry=hstate.entries.find(x=>x.id===card.dataset.historyId);
    card.querySelector('[data-history-edit]').addEventListener('click',()=>beginEdit(entry));
    card.querySelector('[data-history-delete]').addEventListener('click',()=>deleteEvent(entry).catch(herror));
  });
}

function beginEdit(entry){
  hstate.editing=entry;fillHistoryProfile(entry.resolvedProfile);H$('#calculate')?.click();
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
  if(!await confirmMessage({message:`Änderung „${entry.title}“ vom ${fmtDate(entry.effectiveDate)} löschen?`,title:'Änderung löschen',confirmLabel:'Löschen',cancelLabel:'Abbrechen',destructive:true}))return;
  await hapi(`api/compensation/history/${entry.id}?fullWorthSpaceId=${encodeURIComponent(spaceId())}`,{method:'DELETE'});
  await loadHistory();hmessage('Änderung gelöscht.');
}

function fieldLabel(path){const map={annualGross:'Brutto',annualBonus:'Bonus',taxClass:'Steuerklasse',taxClass4Factor:'Faktor',annualTaxAllowance:'Freibetrag',childAllowanceUnits:'Kinderfreibetrag',childrenUnder25:'Kinder',age:'Alter',birthDate:'Geburtsdatum',taxYear:'Steuerjahr',oneOffPayments:'Sonderzahlungen',churchTax:'Kirchensteuer',weeklyHours:'Wochenstunden',vacationDays:'Urlaub',healthInsuranceAdditionalRatePercent:'GKV-Zusatzbeitrag','companyCar.enabled':'Firmenwagen','companyCar.listPrice':'Listenpreis','companyCar.oneWayCommuteKm':'Arbeitsweg','occupationalPension.employeeContributionMonthly':'bAV eigener Beitrag','occupationalPension.employerContributionMonthly':'bAV Arbeitgeber',benefits:'Benefits'};return map[path]||path.replaceAll('.',' › ')}
function eventLabel(t){return ({salary:'Gehalt',tax:'Steuer',marriage:'Heirat',child:'Kind',family:'Familie',worktime:'Arbeitszeit',benefit:'Benefit','company-car':'Firmenwagen',pension:'bAV',insurance:'Versicherung',job:'Jobwechsel',combined:'Mehrere Änderungen',other:'Sonstiges'})[t]||t}
function signedPct(v){const n=Number(v||0);return `${n>=0?'+':'−'}${hpct(Math.abs(n))}`}
function shortMoney(v){const n=Number(v||0);return n>=1000?`${(n/1000).toLocaleString('de-DE',{maximumFractionDigits:0})}k €`:heuro.format(n)}
function subtractMonths(date,months){const d=new Date(`${date}T12:00:00`);d.setMonth(d.getMonth()-months);return localIsoDate(d)}
function herror(e){console.error(e);hmessage(e?.message||'Unbekannter Fehler.')}
