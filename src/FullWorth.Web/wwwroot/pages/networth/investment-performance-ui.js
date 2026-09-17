import { money, percent, setMoneyLocale } from '../../components/money.js';
import { onPrivacyChange } from '../../components/privacy.js';
import { bindChartScrubber } from '../../components/chart-scrubber.js';
import { api as sharedApi, jsonBody } from '../../core/services.js';
import { createDialog } from '../../components/dialog.js';
import { showToast } from '../../components/toast.js';
import { confirmMessage } from '../../components/confirm.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { selectionListHtml, createSelectionList } from '../../components/selection-list.js';

const $ = (s, root = document) => root.querySelector(s);
const $$ = (s, root = document) => [...root.querySelectorAll(s)];
const esc = (v) => String(v ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c]));
const lang = () => document.documentElement.lang?.startsWith('en') ? 'en' : 'de';
const text = (de, en) => lang() === 'en' ? en : de;
const dateText = (value) => value ? new Intl.DateTimeFormat(lang() === 'en' ? 'en-US' : 'de-DE').format(new Date(`${String(value).slice(0,10)}T12:00:00`)) : '—';


const api=(path,options)=>sharedApi(path,options);
const json=(method,body)=>jsonBody(body,method);
const toast=message=>showToast(message);
function modal(title,portfolioId){
  const dialog=createDialog(
    `<div class="dialog-card fp-dialog-card ip-card"><div class="panel-head fp-dialog-head ip-head"><div><h2>${esc(title)}</h2><div data-ip-subtitle class="fp-muted"></div></div></div><div data-ip-root></div></div>`,
    {className:'fp-dialog ip-dialog',closeLabel:text('Schließen','Close')});
  // Der Dialog sagt selbst, zu welchem Depot er gehört. Vorher merkte sich das Nachbarmodul die
  // Kennung in einem eigenen Klick-Lauscher, der vor diesem hier registriert sein musste - denn der
  // hier ruft stopImmediatePropagation. Diese Abhängigkeit an der Ladereihenfolge war der einzige
  // Grund, warum das Nachbarmodul zur Laufzeit nachladen musste.
  if(portfolioId)dialog.dataset.portfolio=portfolioId;
  dialog.addEventListener('close',()=>{if(active?.dialog===dialog)active=null},{once:true});
  dialog.showModal();
  document.dispatchEvent(new CustomEvent('fullworth:investment-dialog-opened', { detail: { dialog, portfolioId } }));
  return dialog;
}

let active=null;
onPrivacyChange(()=>active?.render?.());

function setLocale(){setMoneyLocale(lang())}
function pct(value){return value==null?'—':percent(Number(value)*100)}
function amount(value,currency){setLocale();return value==null?'—':money(value,currency)}

async function openPortfolio(portfolioId){
  try{
    const [portfolios,access]=await Promise.all([
      api('api/investments/portfolios'),
      api('api/access/effective').catch(()=>({capabilities:{}}))
    ]);
    const portfolio=portfolios.find(x=>x.id===portfolioId);
    if(!portfolio)throw new Error(text('Depot nicht verfügbar.','Portfolio is not available.'));
    const dialog=modal(portfolio.name,portfolioId);
    active={dialog,portfolio,tab:'overview',period:'1y',access,render:null};
    active.render=()=>renderShell(active);
    await active.render();
  }catch(error){toast(error.message)}
}

async function renderShell(state){
  if(!state.dialog?.open)return;
  const root=$('[data-ip-root]',state.dialog);
  const subtitle=$('[data-ip-subtitle]',state.dialog);
  const canManage=!!state.access?.capabilities?.['investments.manage'];
  subtitle.textContent=`${state.portfolio.currency}${state.portfolio.isArchived?` · ${text('Archiviert','Archived')}`:''}`;
  root.innerHTML=`<div class="ip-tabs" role="tablist">
    ${tabButton('overview',text('Übersicht','Overview'),state)}
    ${tabButton('positions',text('Positionen','Positions'),state)}
    ${tabButton('transactions',text('Transaktionen','Transactions'),state)}
    ${tabButton('performance',text('Performance','Performance'),state)}
    ${tabButton('income',text('Erträge','Income'),state)}
    ${canManage?tabButton('bookings',text('Erkannte Käufe','Detected purchases'),state):''}
  </div><div data-ip-content class="ip-content"><div class="ip-loading">${esc(text('Laden…','Loading…'))}</div></div>`;
  $$('[data-ip-tab]',root).forEach(button=>button.onclick=()=>{state.tab=button.dataset.ipTab;renderShell(state)});
  await renderTab(state,$('[data-ip-content]',root));
}
function tabButton(key,label,state){return `<button type="button" role="tab" data-ip-tab="${key}" aria-selected="${state.tab===key}" class="${state.tab===key?'active':''}">${esc(label)}</button>`}

async function renderTab(state,container){
  try{
    if(state.tab==='overview')return await renderOverview(state,container);
    if(state.tab==='positions')return await renderPositions(state,container);
    if(state.tab==='transactions')return await renderTransactions(state,container);
    if(state.tab==='performance')return await renderPerformance(state,container);
    if(state.tab==='income')return await renderIncome(state,container);
    if(state.tab==='bookings')return await renderBookings(state,container);
  }catch(error){container.innerHTML=`<div class="ip-error">${esc(error.message)}</div>`}
}

async function loadOverview(state){return api(`api/investments/portfolios/${state.portfolio.id}/overview`)}

async function renderOverview(state,container){
  const overview=await loadOverview(state);
  const canManage=!!state.access?.capabilities?.['investments.manage'];
  container.innerHTML=`<div class="ip-metrics">
    ${metric(text('Gesamtwert','Total value'),amount(overview.totalValue,state.portfolio.currency))}
    ${metric(text('Wertpapiere','Securities'),amount(overview.marketValue,state.portfolio.currency))}
    ${metric(text('Cash','Cash'),amount(overview.cash,state.portfolio.currency))}
    ${metric(text('Realisiert','Realized'),amount(overview.realizedResult,state.portfolio.currency))}
    ${metric(text('Dividenden','Dividends'),amount(overview.dividends,state.portfolio.currency))}
    ${metric(text('Datenqualität','Data quality'),overview.incomplete?text('Unvollständig','Incomplete'):text('Vollständig','Complete'))}
  </div>
  ${overview.incomplete?`<div class="ip-warning">${esc(text('Mindestens ein Kurs oder Wechselkurs fehlt bzw. ist veraltet. Werte werden nicht still 1:1 geschätzt.','At least one price or FX rate is missing or stale. Values are never silently assumed 1:1.'))}</div>`:''}
  <section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Größte Positionen','Top positions'))}</h3></div>${positionRows((overview.positions||[]).slice(0,8),state.portfolio.currency)}</section>
  ${canManage?`<div class="ip-actions"><button type="button" class="${buttonClass(ButtonRole.Primary)}" data-ip-add>${esc(text('Transaktion hinzufügen','Add transaction'))}</button><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-ip-price>${esc(text('Kurs erfassen','Add price'))}</button><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-ip-settings>${esc(text('Depot-Einstellungen','Portfolio settings'))}</button></div>`:''}`;
  if(canManage){
    $('[data-ip-add]',container).onclick=()=>openTradeDialog(state);
    $('[data-ip-price]',container).onclick=()=>openPriceDialog(state);
    $('[data-ip-settings]',container).onclick=()=>openSettingsDialog(state,overview.portfolio);
  }
}
function metric(label,value){return `<div class="ip-metric"><span>${esc(label)}</span><strong>${value}</strong></div>`}
function positionRows(rows,currency){
  if(!rows.length)return `<div class="fp-muted">${esc(text('Keine Positionen.','No positions.'))}</div>`;
  return `<div class="ip-list">${rows.map(row=>`<div class="ip-row"><div><strong>${esc(row.name)}</strong><div class="fp-muted">${esc(String(row.quantity))} · ${esc(row.priceState||'missing')}${row.priceDate?` · ${esc(dateText(row.priceDate))}`:''}</div></div><div class="ip-row-value"><strong>${amount(row.marketValue,currency)}</strong><span>${row.unrealizedResult==null?'—':amount(row.unrealizedResult,currency)}</span></div></div>`).join('')}</div>`;
}

async function renderPositions(state,container){
  const overview=await loadOverview(state);
  container.innerHTML=`<section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Positionen','Positions'))}</h3><span>${overview.positions?.length||0}</span></div>${positionRows(overview.positions||[],state.portfolio.currency)}</section>`;
}

async function renderTransactions(state,container){
  const rows=await api(`api/investments/portfolios/${state.portfolio.id}/trades`);
  const canManage=!!state.access?.capabilities?.['investments.manage'];
  container.innerHTML=`${canManage?`<div class="ip-actions"><button type="button" class="${buttonClass(ButtonRole.Primary)}" data-ip-add>${esc(text('Transaktion hinzufügen','Add transaction'))}</button></div>`:''}<section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Transaktionen','Transactions'))}</h3><span>${rows.length}</span></div><div class="ip-list">${rows.map(row=>`<div class="ip-row"><div><strong>${esc(tradeLabel(row.tradeType))}</strong><div class="fp-muted">${esc(dateText(row.tradeDate))}${row.quantity!=null?` · ${esc(String(row.quantity))}`:''}</div></div><div class="ip-row-value"><strong>${amount(row.amount,row.currency)}</strong>${canManage?`<button type="button" class="${buttonClass(ButtonRole.Icon,'ip-icon')}" data-ip-delete="${row.id}" aria-label="${esc(text('Löschen','Delete'))}">×</button>`:''}</div></div>`).join('')||`<div class="fp-muted">${esc(text('Noch keine Investment-Transaktionen.','No investment transactions yet.'))}</div>`}</div></section>`;
  if(canManage){
    $('[data-ip-add]',container)?.addEventListener('click',()=>openTradeDialog(state));
    $$('[data-ip-delete]',container).forEach(button=>button.onclick=async()=>{
      if(!await confirmMessage({message:text('Investment-Transaktion wirklich löschen?','Delete this investment transaction?'),title:text('Transaktion löschen','Delete transaction'),confirmLabel:text('Löschen','Delete'),cancelLabel:text('Abbrechen','Cancel'),destructive:true}))return;
      button.disabled=true;
      try{await api(`api/investments/portfolios/${state.portfolio.id}/trades/${button.dataset.ipDelete}`,{method:'DELETE'});toast(text('Transaktion gelöscht.','Transaction deleted.'));await renderShell(state)}catch(error){toast(error.message);button.disabled=false}
    });
  }
}
function tradeLabel(type){return ({buy:text('Kauf','Buy'),sell:text('Verkauf','Sell'),dividend:text('Dividende','Dividend'),deposit:text('Einzahlung','Deposit'),withdrawal:text('Auszahlung','Withdrawal'),fee:text('Gebühr','Fee'),tax:text('Steuer','Tax'),interest:text('Zins','Interest'),split:text('Split','Split'),security_transfer_in:text('Übertrag Eingang','Transfer in'),security_transfer_out:text('Übertrag Ausgang','Transfer out')})[type]||type}

function periodRange(period,trades){
  const end=new Date();
  const endIso=end.toISOString().slice(0,10);
  const start=new Date(end);
  if(period==='1m')start.setMonth(start.getMonth()-1);
  else if(period==='3m')start.setMonth(start.getMonth()-3);
  else if(period==='ytd')start.setMonth(0,1);
  else if(period==='3y')start.setFullYear(start.getFullYear()-3);
  else if(period==='all'){
    const first=trades.map(x=>String(x.tradeDate||'')).filter(Boolean).sort()[0];
    return {from:first||`${end.getFullYear()}-01-01`,to:endIso};
  } else start.setFullYear(start.getFullYear()-1);
  return {from:start.toISOString().slice(0,10),to:endIso};
}
async function renderPerformance(state,container){
  const trades=await api(`api/investments/portfolios/${state.portfolio.id}/trades`);
  const range=periodRange(state.period,trades);
  const perf=await api(`api/investments/portfolios/${state.portfolio.id}/performance?from=${range.from}&to=${range.to}`);
  const canManage=!!state.access?.capabilities?.['investments.manage'];
  const points=perf.points||[];
  // Weniger als zwei bewertete Punkte ergeben keine Kurve - das ist meistens kein fehlender Kurs,
  // sondern ein fehlender Kauf: ohne Transaktion kennt PortfolioValuationService keinen Bestand, den
  // es bewerten koennte. Der Hinweis fuehrt deshalb direkt zu den erkannten Kaeufen statt nur zu sagen,
  // dass Daten fehlen.
  const chartHasData=points.filter(p=>p.portfolioReturn!=null).length>=2;
  container.innerHTML=`<div class="ip-periods">${['1m','3m','ytd','1y','3y','all'].map(key=>`<button type="button" data-ip-period="${key}" class="${state.period===key?'active':''}">${key==='ytd'?'YTD':key.toUpperCase()}</button>`).join('')}</div>
  <div class="ip-metrics">
    ${metric('TWR',pct(perf.twr))}
    ${metric(text('Persönliche Rendite (XIRR)','Personal return (XIRR)'),pct(perf.xirr))}
    ${metric(text('Benchmark','Benchmark'),pct(perf.benchmarkReturn))}
    ${metric(text('Endwert','Ending value'),amount(perf.marketValue,perf.currency))}
  </div>
  ${perf.incomplete?`<div class="ip-warning"><strong>${esc(text('Unvollständige Bewertungsdaten','Incomplete valuation data'))}</strong><div>${esc((perf.reasons||[]).join(', '))}</div></div>`:''}
  <section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Performance-Verlauf','Performance history'))}</h3><span>${esc(dateText(perf.effectiveFrom))} – ${esc(dateText(perf.to))}</span></div>${performanceChart(points)}
  ${!chartHasData&&canManage?`<div class="ip-empty-cta"><p>${esc(text('Noch keine Historie? FullWorth findet Käufe oft direkt in den Kontobuchungen.','No history yet? FullWorth can often find purchases directly in your account bookings.'))}</p><button type="button" class="${buttonClass(ButtonRole.Primary)}" data-ip-goto-bookings>${esc(text('Käufe aus Buchungen suchen','Search purchases from bookings'))}</button></div>`:''}
  </section>`;
  bindPerformanceScrubber(container,points);
  // War $() (querySelector, EIN Element) statt $$(): .forEach existiert darauf nicht, der Wurf lief
  // ins try/catch von renderTab, und dessen catch ERSETZT den ganzen Tab-Inhalt durch die
  // Fehlermeldung - Metriken, Warnbox und Chart waren also bei jedem Oeffnen der Performance-Tab weg,
  // nicht nur die Zeitraum-Knoepfe.
  $$('[data-ip-period]',container).forEach(button=>button.onclick=()=>{state.period=button.dataset.ipPeriod;renderPerformance(state,container)});
  $('[data-ip-goto-bookings]',container)?.addEventListener('click',()=>{state.tab='bookings';renderShell(state)});
}
function performanceChart(points){
  const valid=points.filter(p=>p.portfolioReturn!=null);
  if(valid.length<2)return `<div class="fp-muted">${esc(text('Noch nicht genug Daten für einen Performance-Chart.','Not enough data for a performance chart yet.'))}</div>`;
  const values=valid.flatMap(p=>[Number(p.portfolioReturn)*100,p.benchmarkReturn==null?null:Number(p.benchmarkReturn)*100]).filter(Number.isFinite);
  let min=Math.min(...values,0),max=Math.max(...values,0);if(max-min<1){max+=.5;min-=.5}
  const width=720,height=240,pad=24;
  const xy=(value,index)=>{const x=pad+(index/(valid.length-1))*(width-pad*2);const y=height-pad-((value-min)/(max-min))*(height-pad*2);return`${x.toFixed(1)},${y.toFixed(1)}`};
  const portfolio=valid.map((p,i)=>xy(Number(p.portfolioReturn)*100,i)).join(' ');
  const benchmark=valid.every(p=>p.benchmarkReturn!=null)?valid.map((p,i)=>xy(Number(p.benchmarkReturn)*100,i)).join(' '):'';
  const zeroY=height-pad-((0-min)/(max-min))*(height-pad*2);
  const last=valid[valid.length-1];
  const latestBenchmark=last.benchmarkReturn==null?'':` · ${esc(text('Benchmark','Benchmark'))} ${pct(last.benchmarkReturn)}`;
  return `<div class="ip-chart-wrap"><div class="ip-chart-readout">${esc(dateText(last.date))} · FullWorth ${pct(last.portfolioReturn)}${latestBenchmark}</div><svg class="ip-chart" viewBox="0 0 ${width} ${height}" role="img" aria-label="${esc(text('Performance und Benchmark','Performance and benchmark'))}"><line x1="${pad}" y1="${zeroY}" x2="${width-pad}" y2="${zeroY}" class="ip-zero"/><polyline points="${portfolio}" class="ip-line ip-line-main"/>${benchmark?`<polyline points="${benchmark}" class="ip-line ip-line-benchmark"/>`:''}</svg><div class="ip-chart-legend"><span><i class="main"></i>FullWorth</span>${benchmark?`<span><i class="benchmark"></i>${esc(text('Benchmark','Benchmark'))}</span>`:''}</div></div>`;
}

function bindPerformanceScrubber(container,points){
  const valid=points.filter(point=>point.portfolioReturn!=null);
  const svg=$('.ip-chart',container),readout=$('.ip-chart-readout',container);
  if(!svg||!readout||valid.length<2)return;
  const values=valid.flatMap(point=>[Number(point.portfolioReturn)*100,point.benchmarkReturn==null?null:Number(point.benchmarkReturn)*100]).filter(Number.isFinite);
  let min=Math.min(...values,0),max=Math.max(...values,0);if(max-min<1){max+=.5;min-=.5}
  const width=720,height=240,pad=24;
  const pointsForScrub=valid.map((point,index)=>{
    const x=pad+(index/(valid.length-1))*(width-pad*2);
    const main=Number(point.portfolioReturn)*100;
    const markers=[{y:height-pad-((main-min)/(max-min))*(height-pad*2)}];
    if(point.benchmarkReturn!=null){
      const benchmark=Number(point.benchmarkReturn)*100;
      markers.push({y:height-pad-((benchmark-min)/(max-min))*(height-pad*2),className:'benchmark'});
    }
    return {x,label:dateText(point.date),markers,data:point};
  });
  const original=readout.innerHTML;
  bindChartScrubber(svg,pointsForScrub,{
    initialIndex:pointsForScrub.length-1,
    onChange:point=>{
      const benchmark=point.data.benchmarkReturn==null?'':` · ${text('Benchmark','Benchmark')} ${pct(point.data.benchmarkReturn)}`;
      readout.textContent=`${point.label} · FullWorth ${pct(point.data.portfolioReturn)}${benchmark}`;
    },
    onReset:()=>{readout.innerHTML=original},
    formatAria:point=>{
      const benchmark=point.data.benchmarkReturn==null?'':`, ${text('Benchmark','Benchmark')} ${pct(point.data.benchmarkReturn)}`;
      return `${point.label}: FullWorth ${pct(point.data.portfolioReturn)}${benchmark}`;
    }
  });
}

async function renderIncome(state,container){
  const year=new Date().getFullYear();
  const response=await api(`api/investments/portfolios/${state.portfolio.id}/dividends?year=${year}`);
  const items=response.items||[];
  const months=Array.from({length:12},(_,i)=>({month:i,total:0}));
  items.forEach(item=>{const month=new Date(`${String(item.date).slice(0,10)}T12:00:00`).getMonth();if(months[month])months[month].total+=Number(item.amount||0)});
  const max=Math.max(...months.map(x=>x.total),0);
  container.innerHTML=`<div class="ip-metrics">${metric(`${text('Dividenden','Dividends')} ${year}`,amount(response.total||0,state.portfolio.currency))}${metric(text('Zahlungen','Payments'),String(items.length))}</div>
  <section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Monatlich','Monthly'))}</h3></div><div class="ip-bars">${months.map(row=>`<div class="ip-bar-row"><span>${new Intl.DateTimeFormat(lang()==='en'?'en-US':'de-DE',{month:'short'}).format(new Date(year,row.month,1))}</span><div><i style="width:${max?Math.max(2,(row.total/max)*100):0}%"></i></div><strong>${amount(row.total,state.portfolio.currency)}</strong></div>`).join('')}</div></section>
  <section class="ip-section"><div class="ip-section-head"><h3>${esc(text('Zahlungen','Payments'))}</h3></div><div class="ip-list">${items.map(item=>`<div class="ip-row"><div><strong>${esc(item.security||text('Dividende','Dividend'))}</strong><div class="fp-muted">${esc(dateText(item.date))}</div></div><div class="ip-row-value"><strong>${amount(item.amount,item.currency)}</strong>${Number(item.taxes||0)?`<span>${esc(text('Steuern','Tax'))}: ${amount(item.taxes,item.currency)}</span>`:''}</div></div>`).join('')||`<div class="fp-muted">${esc(text('Keine Dividenden im gewählten Jahr.','No dividends in the selected year.'))}</div>`}</div></section>`;
}

// Erkannte Kaeufe (Teil B des Buchungs-Abgleichs). ComputeSuggestionsAsync gleicht bei jedem Aufruf
// frisch ab - hier wird deshalb genauso wenig zwischengespeichert wie im Endpoint selbst. "Sicher"
// heisst hier vorausgewaehlt und leicht mit einem Klick zu uebernehmen, NIE automatisch geschrieben:
// das entscheidet immer ein Klick auf "Übernehmen", auch wenn confident=true ist.
async function loadBookingSuggestions(state){return api(`api/reconciliation/securities-bookings?portfolioId=${encodeURIComponent(state.portfolio.id)}`)}

// Eine Karte je Wertpapier, jede mit ihrer eigenen Auswahl - "Uebernehmen" auf Karte A darf niemals
// lesen, was auf Karte B angehakt ist. Ueber das Karten-ELEMENT selbst indiziert (nicht die
// Wertpapier-Id als String), damit ein esc() auf dem Weg durchs Markup nie zu einem Schluessel fuehren
// kann, der nicht mehr zum data-Attribut passt. Neu gefuellt bei jedem renderBookings() (voller
// Neuaufbau des Containers, siehe container.innerHTML unten), nie nur ergaenzt.
const bookingSelections=new WeakMap();

async function renderBookings(state,container){
  const data=await loadBookingSuggestions(state);
  const summaries=data.summaries||[];
  const matches=data.matches||[];
  if(!summaries.length){
    container.innerHTML=`<div class="ip-empty-cta"><p>${esc(text('Keine erkannten Käufe. FullWorth vergleicht Kontobuchungen mit den Positionen dieses Depots und schlägt nur vor, was zusammenpasst - übernommen wird nichts von selbst.','No detected purchases. FullWorth compares account bookings against this portfolio\'s positions and only suggests a match - nothing is applied on its own.'))}</p></div>`;
    return;
  }
  const bySecurity=new Map(summaries.map(summary=>[summary.securityId,matches.filter(match=>match.securityId===summary.securityId)]));
  container.innerHTML=`<div class="ip-empty-cta"><p>${esc(text('Vorschläge aus Kontobuchungen. Sichere Treffer sind vorausgewählt - übernommen wird erst mit einem Klick auf "Übernehmen".','Suggestions from account bookings. Confident matches are pre-selected - nothing is applied until you click "Apply".'))}</p></div>
  ${summaries.map(summary=>bookingSummaryCard(summary,bySecurity.get(summary.securityId)||[],state.portfolio.currency)).join('')}`;
  for(const summary of summaries){
    const card=container.querySelector(`[data-ip-booking-security="${CSS.escape(String(summary.securityId))}"]`);
    if(!card)continue;
    bookingSelections.set(card,createSelectionList().mount(card));
  }
  bindBookingActions(state,container);
}

function bookingSummaryCard(summary,matches,portfolioCurrency){
  const currency=matches[0]?.currency||portfolioCurrency;
  return `<section class="ip-section ip-booking-card${summary.confident?' ip-confident':''}" data-ip-booking-security="${esc(summary.securityId)}">
    <div class="ip-section-head">
      <h3>${esc(summary.name||text('Unbekanntes Wertpapier','Unknown security'))}</h3>
      <span>${summary.confident?esc(text('Sicher','Confident')):esc(text('Bitte prüfen','Please review'))}</span>
    </div>
    <div class="ip-metrics">
      ${metric(text('Aktueller Bestand','Current holding'),summary.q==null?'—':String(summary.q))}
      ${metric(text('Gefunden: Stück','Found: quantity'),summary.sumQuantity==null?'—':String(summary.sumQuantity))}
      ${metric(text('Gefunden: Betrag','Found: amount'),amount(summary.sumGross,currency))}
      ${metric(text('Buchungen','Bookings'),String(summary.matches||0))}
    </div>
    ${selectionListHtml(matches.map(match=>bookingMatchItem(match)),{rowClass:'ip-row ip-booking-row',rowsClass:'ip-list'})}
    <div class="ip-actions">
      <button type="button" class="${buttonClass(ButtonRole.Primary)}" data-ip-booking-apply>${esc(text('Übernehmen','Apply'))}</button>
      <button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-ip-booking-dismiss>${esc(text('Ablehnen','Dismiss'))}</button>
    </div>
  </section>`;
}

function bookingMatchItem(match){
  // Eine Buchung ohne Stueckzahl entstand aus einem Betrag ohne erkennbaren Kurs - der Bestand stimmt
  // trotzdem nur, wenn jemand die Stueckzahl von Hand ergaenzt. Das steht hier, statt eine Zahl zu
  // erfinden.
  const quantityText=match.quantity==null
    ?text('Stückzahl fehlt – manuell nachtragen','Quantity missing – add manually')
    :`${String(match.quantity)}${match.quantityEstimated?` (${esc(text('geschätzt','estimated'))})`:''}`;
  return {
    id:esc(match.transactionId),
    selected:!!match.confident,
    html:`<div class="row-main"><strong>${esc(dateText(match.date))}</strong><div class="fp-muted">${esc(quantityText)}</div></div>
    <div class="ip-row-value"><strong>${amount(match.gross,match.currency)}</strong>${match.confident?`<span class="ip-badge">${esc(text('sicher','confident'))}</span>`:''}</div>`
  };
}

function bindBookingActions(state,container){
  const run=(button,path,confirmMessageText)=>async()=>{
    const card=button.closest('[data-ip-booking-security]');
    const list=card?bookingSelections.get(card):null;
    const ids=list?list.getSelectedIds():[];
    if(!ids.length){toast(text('Bitte mindestens eine Buchung auswählen.','Select at least one booking.'));return}
    button.disabled=true;
    try{
      await api(`api/reconciliation/securities-bookings/${path}`,json('POST',{portfolioId:state.portfolio.id,transactionIds:ids}));
      toast(confirmMessageText);
      await renderShell(state);
    }catch(error){toast(error.message);button.disabled=false}
  };
  $$('[data-ip-booking-apply]',container).forEach(button=>button.onclick=run(button,'apply',text('Käufe übernommen.','Purchases applied.')));
  $$('[data-ip-booking-dismiss]',container).forEach(button=>button.onclick=run(button,'dismiss',text('Buchungen ausgeblendet.','Bookings dismissed.')));
}

async function openTradeDialog(state){
  const securities=await api('api/investments/securities');
  const types=['buy','sell','dividend','interest','fee','tax','deposit','withdrawal','security_transfer_in','security_transfer_out','split','other'];
  const dialog=createDialog(`<form class="dialog-card fp-dialog-card ip-form"><div class="panel-head fp-dialog-head"><h2>${esc(text('Investment-Transaktion','Investment transaction'))}</h2></div><div class="ip-form-grid"><label>${esc(text('Typ','Type'))}<select name="type">${types.map(type=>`<option value="${type}">${esc(tradeLabel(type))}</option>`).join('')}</select></label><label>${esc(text('Wertpapier','Security'))}<select name="security"><option value="">—</option>${securities.map(x=>`<option value="${x.id}">${esc(x.name)}</option>`).join('')}</select></label><label>${esc(text('Handelstag','Trade date'))}<input name="date" type="date" value="${new Date().toISOString().slice(0,10)}" required></label><label>${esc(text('Valuta','Settlement'))}<input name="settlement" type="date"></label><label>${esc(text('Stück / Split-Faktor','Quantity / split ratio'))}<input name="qty" type="number" step="0.0000000001"></label><label>${esc(text('Kurs','Price'))}<input name="price" type="number" step="0.0000000001"></label><label>${esc(text('Brutto','Gross'))}<input name="gross" type="number" step="0.01"></label><label>${esc(text('Betrag','Amount'))}<input name="amount" type="number" step="0.01" value="0" required></label><label>${esc(text('Gebühren','Fees'))}<input name="fees" type="number" step="0.01" value="0"></label><label>${esc(text('Steuern','Taxes'))}<input name="taxes" type="number" step="0.01" value="0"></label><label>${esc(text('Quellensteuer','Withholding tax'))}<input name="withholding" type="number" step="0.01" value="0"></label></div><label>${esc(text('Notiz','Note'))}<textarea name="notes"></textarea></label><div class="ip-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-close-2>${esc(text('Abbrechen','Cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(text('Speichern','Save'))}</button></div></form>`,{className:'fp-dialog ip-small-dialog',closeLabel:text('Schließen','Close')});
  const close=()=>dialog.close();$('[data-close-2]',dialog).onclick=close;
  $('form',dialog).onsubmit=async event=>{event.preventDefault();const form=event.currentTarget;const submit=$('button[type="submit"]',form);submit.disabled=true;const data=new FormData(form);try{await api(`api/investments/portfolios/${state.portfolio.id}/trades`,json('POST',{securityId:data.get('security')||null,tradeType:data.get('type'),tradeDate:data.get('date'),settlementDate:data.get('settlement')||null,quantity:data.get('qty')?Number(data.get('qty')):null,price:data.get('price')?Number(data.get('price')):null,grossAmount:data.get('gross')?Number(data.get('gross')):null,amount:Number(data.get('amount')||0),currency:state.portfolio.currency,fees:Number(data.get('fees')||0),taxes:Number(data.get('taxes')||0),withholdingTax:Number(data.get('withholding')||0),source:'manual',externalKey:null,notes:data.get('notes')||null}));toast(text('Transaktion gespeichert.','Transaction saved.'));dialog.close();await renderShell(state)}catch(error){toast(error.message);submit.disabled=false}};
  dialog.showModal();
}

async function openPriceDialog(state){
  const securities=await api('api/investments/securities');
  const dialog=createDialog(`<form class="dialog-card fp-dialog-card ip-form"><div class="panel-head fp-dialog-head"><h2>${esc(text('Kurs erfassen','Add price'))}</h2></div><label>${esc(text('Wertpapier','Security'))}<select name="security">${securities.map(x=>`<option value="${x.id}">${esc(x.name)}</option>`).join('')}</select></label><label>${esc(text('Datum','Date'))}<input name="date" type="date" value="${new Date().toISOString().slice(0,10)}"></label><label>${esc(text('Kurs','Price'))}<input name="price" type="number" step="0.0000000001" required></label><label>${esc(text('Währung','Currency'))}<input name="currency" maxlength="3" value="${esc(state.portfolio.currency)}"></label><div class="ip-actions"><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(text('Speichern','Save'))}</button></div></form>`,{className:'fp-dialog ip-small-dialog',closeLabel:text('Schließen','Close')});
  $('form',dialog).onsubmit=async event=>{event.preventDefault();const submit=$('button[type="submit"]',event.currentTarget);submit.disabled=true;const data=new FormData(event.currentTarget);try{await api('api/investments/prices',json('PUT',{securityId:data.get('security'),priceDate:data.get('date'),price:Number(data.get('price')),currency:data.get('currency'),source:'manual'}));toast(text('Kurs gespeichert.','Price saved.'));dialog.close();await renderShell(state)}catch(error){toast(error.message);submit.disabled=false}};dialog.showModal();
}

async function openSettingsDialog(state,current){
  const [accounts,benchmarks]=await Promise.all([api('api/accounts'),api('api/investments/benchmarks').catch(()=>[])]);
  const dialog=createDialog(`<form class="dialog-card fp-dialog-card ip-form"><div class="panel-head fp-dialog-head"><h2>${esc(text('Depot-Einstellungen','Portfolio settings'))}</h2></div><label>${esc(text('Name','Name'))}<input name="name" value="${esc(current.name||state.portfolio.name)}" required></label><div class="ip-form-grid"><label>${esc(text('Währung','Currency'))}<input name="currency" value="${esc(current.currency||state.portfolio.currency)}" maxlength="3"></label><label>${esc(text('Konto','Account'))}<select name="account"><option value="">—</option>${accounts.map(a=>`<option value="${a.id}" ${a.id===current.accountId?'selected':''}>${esc(a.displayName||a.institutionName||a.id)}</option>`).join('')}</select></label><label>${esc(text('Benchmark','Benchmark'))}<select name="benchmark"><option value="">—</option>${benchmarks.filter(x=>x.securityId).map(x=>`<option value="${x.securityId}" ${x.securityId===current.benchmarkSecurityId?'selected':''}>${esc(x.name)}</option>`).join('')}</select></label><label>${esc(text('Anbieter','Provider'))}<input name="provider" value="${esc(current.providerName||'')}"></label></div><label class="fp-check"><input name="networth" type="checkbox" ${current.includeInNetWorth!==false?'checked':''}> ${esc(text('In Nettovermögen einbeziehen','Include in net worth'))}</label><label class="fp-check"><input name="archived" type="checkbox" ${current.isArchived?'checked':''}> ${esc(text('Archiviert','Archived'))}</label><div class="ip-actions"><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(text('Speichern','Save'))}</button></div></form>`,{className:'fp-dialog ip-small-dialog',closeLabel:text('Schließen','Close')});
  $('form',dialog).onsubmit=async event=>{event.preventDefault();const submit=$('button[type="submit"]',event.currentTarget);submit.disabled=true;const data=new FormData(event.currentTarget);try{await api(`api/investments/portfolios/${state.portfolio.id}/settings`,json('PUT',{name:data.get('name'),currency:data.get('currency'),accountId:data.get('account')||null,benchmarkSecurityId:data.get('benchmark')||null,providerName:data.get('provider')||null,isManual:true,includeInNetWorth:event.currentTarget.networth.checked,isArchived:event.currentTarget.archived.checked}));state.portfolio={...state.portfolio,name:data.get('name'),currency:String(data.get('currency')).toUpperCase(),isArchived:event.currentTarget.archived.checked};toast(text('Depot gespeichert.','Portfolio saved.'));dialog.close();await renderShell(state)}catch(error){toast(error.message);submit.disabled=false}};dialog.showModal();
}

document.addEventListener('click',event=>{
  const button=event.target.closest('[data-portfolio]');
  if(!button||button.closest('.ip-dialog'))return;
  event.preventDefault();event.stopImmediatePropagation();
  const id=button.dataset.portfolio;
  const parent=button.closest('dialog');if(parent?.open)parent.close();
  openPortfolio(id);
},true);
