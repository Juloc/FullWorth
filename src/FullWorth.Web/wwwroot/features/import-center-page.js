import { api as sharedApi } from '../core/services.js';
import { snapshotUploadFile } from '../security/secure-fetch.js';
import { confirmMessage } from '../ui/confirm.js';
const lang=(localStorage.getItem('finance.language')||'de').startsWith('en')?'en':'de';
const t={
  de:{subtitle:'Buchungen und Depots aus anderen Apps übernehmen.',back:'Zurück',space:'FullWorth Space',txHint:'Für Bank- und App-Exporte. CSV und XLSX werden zuerst analysiert; du bestätigst Spalten und Zielkonten vor dem Import.',invHint:'Für Parqet, Finanzfluss und andere Depot-Exporte. Käufe, Verkäufe, Dividenden, Zinsen, Gebühren und Steuern werden geprüft.',analyse:'Datei analysieren',review:'Import prüfen',import:'Importieren',importDepot:'Depot importieren',mapping:'Spalten zuordnen',targets:'Konten zuordnen',targetsHint:'Jedes in der Datei gefundene Konto bekommt ein Zielkonto in FullWorth.',securities:'Wertpapiere prüfen',working:'Wird verarbeitet …',needFile:'Bitte zuerst eine CSV- oder XLSX-Datei wählen.',needMapping:'Datum und Betrag müssen zugeordnet sein.',needInvestmentMapping:'Handelsdatum und Transaktionsart müssen zugeordnet sein.',needAccount:'Bitte alle Quellkonten einem Zielkonto zuordnen.',needPortfolio:'Bitte ein Zieldepot wählen.',done:'Import abgeschlossen.',error:'Import fehlgeschlagen.',rows:'Zeilen',ready:'Bereit',errors:'Fehler',imported:'Importiert',duplicates:'Duplikate',newPortfolio:'Neues Import-Depot anlegen',none:'Nicht zuordnen',autoNew:'Automatisch / neu anlegen',matched:'Automatisch erkannt',willCreate:'Wird neu angelegt',noAccounts:'Keine beschreibbaren Konten gefunden.',noPortfolios:'Noch kein Depot vorhanden.',historyEmpty:'Noch keine Depotimporte.',rollback:'Import rückgängig machen',rollbackConfirm:'Diesen Depotimport wirklich rückgängig machen? Spätere abhängige Buchungen schützen den Rollback automatisch.',rolledBack:'Import wurde rückgängig gemacht.',healthy:'Plausibel',checkWarnings:'Hinweise',cash:'Cash',positions:'Positionen',rowsTitle:'Buchungen prüfen',rowsHint:'Wähle ab, was nicht importiert werden soll. Zeilen mit Fehlern sind nicht auswählbar.',selectAll:'Alle auswählen',selectedOf:'{n} von {total} ausgewählt',errorsTitle:'Zeile nicht importierbar',notImportable:'nicht importierbar',noRows:'Keine Zeilen gefunden.',showing:'Angezeigt werden die ersten {n} von {total} Zeilen. Die Auswahl gilt für alle.',noneSelected:'Bitte mindestens eine Buchung auswählen.',duplicate:'Schon vorhanden',dupInFile:'kommt in dieser Datei doppelt vor',dupExternalKey:'Buchungs-ID wurde schon importiert',dupExisting:'gleiches Datum, gleicher Betrag, gleicher Empfänger schon gebucht',dupHint:'{n} Buchungen sind schon vorhanden und deshalb abgewählt.',dupChecking:'Duplikate werden geprüft …',txHistory:'Letzte Buchungsimporte',txHistoryEmpty:'Noch keine Buchungsimporte.',txRollback:'Import rückgängig machen',txRollbackConfirm:'Diesen Import wirklich rückgängig machen? Buchungen, die du danach geteilt, verschlagwortet, geprüft oder mit einem Vertrag verknüpft hast, bleiben erhalten.',txRolledBack:'{removed} entfernt, {kept} behalten.',statusCompleted:'Abgeschlossen',statusCancelled:'Abgebrochen',statusFailed:'Fehlgeschlagen',statusRolledBack:'Rückgängig gemacht'},
  en:{subtitle:'Import transactions and portfolios from other apps.',back:'Back',space:'FullWorth Space',txHint:'For bank and finance-app exports. CSV and XLSX are analysed first; you confirm columns and target accounts before committing.',invHint:'For Parqet, Finanzfluss and other portfolio exports. Buys, sells, dividends, interest, fees and taxes are validated.',analyse:'Analyse file',review:'Review import',import:'Import',importDepot:'Import portfolio',mapping:'Map columns',targets:'Map accounts',targetsHint:'Every source account found in the file is mapped to a FullWorth account.',securities:'Review securities',working:'Processing …',needFile:'Choose a CSV or XLSX file first.',needMapping:'Date and amount must be mapped.',needInvestmentMapping:'Trade date and transaction type must be mapped.',needAccount:'Map every source account to a target account.',needPortfolio:'Choose a target portfolio.',done:'Import completed.',error:'Import failed.',rows:'Rows',ready:'Ready',errors:'Errors',imported:'Imported',duplicates:'Duplicates',newPortfolio:'Create new import portfolio',none:'Do not map',autoNew:'Automatic / create new',matched:'Auto matched',willCreate:'Will be created',noAccounts:'No writable accounts found.',noPortfolios:'No portfolio exists yet.',historyEmpty:'No portfolio imports yet.',rollback:'Roll back import',rollbackConfirm:'Really roll back this portfolio import? Later dependent trades automatically block an unsafe rollback.',rolledBack:'Import rolled back.',healthy:'Plausible',checkWarnings:'Warnings',cash:'Cash',positions:'Positions',rowsTitle:'Review bookings',rowsHint:'Uncheck anything you do not want to import. Rows with errors cannot be selected.',selectAll:'Select all',selectedOf:'{n} of {total} selected',errorsTitle:'Row cannot be imported',notImportable:'not importable',noRows:'No rows found.',showing:'Showing the first {n} of {total} rows. The selection applies to all of them.',noneSelected:'Select at least one booking.',duplicate:'Already there',dupInFile:'appears twice in this file',dupExternalKey:'booking id was already imported',dupExisting:'same date, amount and counterparty already booked',dupHint:'{n} bookings already exist and were unchecked.',dupChecking:'Checking for duplicates …',txHistory:'Recent transaction imports',txHistoryEmpty:'No transaction imports yet.',txRollback:'Roll back import',txRollbackConfirm:'Really roll back this import? Bookings you have since split, tagged, reviewed or linked to a contract are kept.',txRolledBack:'{removed} removed, {kept} kept.',statusCompleted:'Completed',statusCancelled:'Cancelled',statusFailed:'Failed',statusRolledBack:'Rolled back'}
}[lang];
document.documentElement.lang=lang;

const $=id=>document.getElementById(id);
const state={space:null,accounts:[],portfolios:[],securities:[],tx:{file:null,detect:null,jobId:null,summary:null,candidates:[],selected:new Set(),duplicates:new Map(),history:[]},inv:{file:null,detect:null,jobId:null,summary:null,history:[]}};

function setText(id,value){const el=$(id);if(el)el.textContent=value}
setText('page-subtitle',t.subtitle);setText('back-link',t.back);setText('space-label',t.space);setText('tx-hint',t.txHint);setText('inv-hint',t.invHint);
setText('tx-detect',t.analyse);setText('inv-detect',t.analyse);setText('tx-stage',t.review);setText('inv-stage',t.review);setText('tx-commit',t.import);setText('inv-commit',t.importDepot);
setText('tx-mapping-title',t.mapping);setText('inv-mapping-title',t.mapping);setText('tx-target-title',t.targets);setText('tx-target-hint',t.targetsHint);setText('inv-security-title',t.securities);setText('inv-type-title',lang==='de'?'Gefundene Buchungstypen':'Detected transaction types');setText('inv-reconciliation-title',lang==='de'?'Depot-Prüfung':'Portfolio check');setText('inv-history-title',lang==='de'?'Letzte Depotimporte':'Recent portfolio imports');setText('tx-history-title',t.txHistory);
$('main')?.classList.add('ic-polish');

async function api(path,options={}){return sharedApi(path,options)}
function status(kind,message){setText(`${kind}-status`,message);const el=$(`${kind}-status`);if(el){el.classList.toggle('is-busy',message===t.working);el.classList.toggle('is-done',message===t.done);el.classList.remove('is-error')}}
function error(kind,err){console.error(err);status(kind,`${t.error} ${err?.message||''}`.trim());$(`${kind}-status`)?.classList.add('is-error')}
function currentFile(kind){const input=$(kind==='tx'?'tx-file':'inv-file');return input.files?.[0]||null}
async function formWithFile(file){const uploadFile=await snapshotUploadFile(file);const body=new FormData();body.append('file',uploadFile,file.name);return body}
function normHeader(value){return String(value||'').normalize('NFD').replace(/[\u0300-\u036f]/g,'').toLowerCase().replace(/[^a-z0-9]/g,'')}
function findHeader(headers,...aliases){const all=headers||[];for(const alias of aliases){const wanted=normHeader(alias);const match=all.find(header=>normHeader(header)===wanted);if(match)return match}return null}
function headerValues(data,header){return (data?.preview||[]).map(row=>row?.[header]).filter(value=>String(value??'').trim().length>0)}
function isTradeRepublicExport(data){const required=['account_type','category','asset_class','type','symbol','shares','transaction_id'];return required.every(name=>findHeader(data?.headers,name))}
function importProviderName(){const preset=$('inv-preset')?.value;return preset==='traderepublic'?'Trade Republic':preset==='parqet'?'Parqet':preset==='finanzfluss'?'Finanzfluss':null}
function detectedCurrency(data){const header=findHeader(data?.headers,'currency','Währung','Waehrung');const value=header?headerValues(data,header).find(Boolean):null;return String(value||'EUR').trim().toUpperCase()}
function presetSuggestedMapping(kind,data){
  const mapping={...(data?.suggestedMapping||{})};
  const preset=$(kind==='tx'?'tx-preset':'inv-preset')?.value||'generic';
  const set=(key,...aliases)=>{const header=findHeader(data?.headers, ...aliases);if(header)mapping[key]=header};
  if(kind==='tx'&&preset==='outbank'){
    set('date','Buchungsdatum','Buchungstag','Datum');set('amount','Betrag','Umsatz');set('currency','Währung','Waehrung');
    set('counterparty','Auftraggeber/Empfänger','Auftraggeber / Empfänger','Auftraggeber/Empfaenger','Empfänger/Auftraggeber','Empfaenger/Auftraggeber','Empfänger','Empfaenger','Auftraggeber','Name');
    set('description','Verwendungszweck','Buchungstext','Buchung','Text');set('account','Kontoname','Konto','Account');set('category','Kategorie');set('externalKey','Umsatz-ID','Umsatz ID','Transaktions-ID','Transaktions ID','ID');
  }
  if(kind==='tx'&&preset==='finanzfluss'){
    set('date','Transaktionsdatum','Buchungsdatum','Buchungstag','Datum','Date');set('amount','Betrag','Gesamtbetrag','Amount');set('currency','Währung','Waehrung','Currency');
    set('counterparty','Empfänger/Auftraggeber','Empfaenger/Auftraggeber','Gegenpartei','Empfänger','Empfaenger','Händler','Haendler','Name');set('description','Verwendungszweck','Beschreibung','Text','Description');
    set('account','Portfolio','Kontoname','Konto','Account');set('category','Kategorie','Category');set('externalKey','Buchungs-ID','Buchungs ID','Transaktions-ID','Transaction ID','ID');
  }
  if(kind==='inv'&&preset==='traderepublic'){
    set('tradeDate','date','datetime');set('tradeType','type');set('securityName','name');set('assetClass','asset_class','asset class');set('quantity','shares');set('price','price');set('amount','amount');set('currency','currency');set('fees','fee');set('taxes','tax');set('externalKey','transaction_id','transaction id');
    const symbol=findHeader(data?.headers,'symbol');const values=symbol?headerValues(data,symbol):[];
    if(symbol&&values.length&&values.every(value=>/^[A-Za-z]{2}[A-Za-z0-9]{10}$/.test(String(value).trim()))){mapping.isin=symbol;if(mapping.ticker===symbol)mapping.ticker=null}else if(symbol)mapping.ticker=symbol;
  }
  if(kind==='inv'&&preset==='parqet'){
    set('tradeDate','date','Datum');set('tradeType','type','Typ','Art');set('quantity','shares','Stück','Stueck','Anzahl');set('price','price','Kurs');set('amount','amount','Betrag');set('currency','currency','Währung','Waehrung');set('fees','fee','Gebühr','Gebuehr','Gebühren','Gebuehren');set('taxes','tax','Steuer','Steuern');set('wkn','wkn');
    const identifier=findHeader(data?.headers,'identifier');
    if(identifier){const values=headerValues(data,identifier);if(values.length&&values.every(value=>/^[A-Za-z]{2}[A-Za-z0-9]{10}$/.test(String(value).trim())))mapping.isin=identifier;else if(!mapping.ticker)mapping.ticker=identifier}
  }
  if(kind==='inv'&&preset==='finanzfluss'){
    set('tradeDate','Transaktionsdatum','Handelsdatum','Datum','Date');set('tradeType','Transaktionstyp','Transaktionsart','Typ','Art','Type');set('settlementDate','Valuta','Wertstellung','Settlement Date');
    set('securityName','Vermögenswert','Vermoegenswert','Wertpapier','Wertpapierbezeichnung','Security','Name');set('isin','ISIN');set('wkn','WKN');set('ticker','Symbol','Ticker');set('quantity','Anteile','Stückzahl','Stueckzahl','Stück','Stueck','Quantity','Shares');
    set('price','Preis','Kurs','Price');set('grossAmount','Bruttobetrag','Brutto','Gross Amount');set('amount','Betrag','Gesamtbetrag','Amount','Netto');set('currency','Währung','Waehrung','Currency');set('fees','Gebühren','Gebuehren','Fees','Fee');set('taxes','Steuern','Taxes','Tax');set('withholdingTax','Quellensteuer','Withholding Tax');set('externalKey','Transaktions-ID','Transaction ID','Order ID','ID');
  }
  return mapping;
}
function selectOptions(headers,value,required=false){
  const select=document.createElement('select');
  if(!required){const empty=document.createElement('option');empty.value='';empty.textContent=t.none;select.appendChild(empty)}
  for(const header of headers){const option=document.createElement('option');option.value=header;option.textContent=header;if(header===value)option.selected=true;select.appendChild(option)}
  if(required&&!value&&headers.length)select.value='';
  return select;
}
function renderMapping(kind,definition){
  const data=state[kind].detect;const root=$(kind==='tx'?'tx-mapping':'inv-mapping');root.innerHTML='';const suggestedMapping=presetSuggestedMapping(kind,data);
  for(const field of definition){const label=document.createElement('label');label.className='field';const span=document.createElement('span');span.textContent=field.label+(field.required?' *':'');const suggested=suggestedMapping?.[field.key]||'';const select=selectOptions(data.headers,suggested,field.required);select.dataset.mapping=field.key;label.append(span,select);root.appendChild(label)}
  renderPreview(kind,data.preview||[]);$(kind==='tx'?'tx-mapping-section':'inv-mapping-section').hidden=false;
}
function collectMapping(kind,definition){const root=$(kind==='tx'?'tx-mapping':'inv-mapping');const mapping={};for(const field of definition){const select=root.querySelector(`[data-mapping="${field.key}"]`);mapping[field.key]=select?.value||null;if(field.required&&!mapping[field.key])throw new Error(kind==='tx'?t.needMapping:t.needInvestmentMapping)}return mapping}
function renderPreview(kind,rows){const head=$(kind==='tx'?'tx-preview-head':'inv-preview-head'),body=$(kind==='tx'?'tx-preview-body':'inv-preview-body');head.innerHTML='';body.innerHTML='';if(!rows.length)return;const headers=Object.keys(rows[0]).slice(0,10);const tr=document.createElement('tr');for(const h of headers){const th=document.createElement('th');th.textContent=h;tr.appendChild(th)}head.appendChild(tr);for(const row of rows.slice(0,10)){const tr=document.createElement('tr');for(const h of headers){const td=document.createElement('td');td.textContent=row[h]??'';td.title=String(row[h]??'');tr.appendChild(td)}body.appendChild(tr)}}
function metric(label,value,tone){const article=document.createElement('article');article.className='metric';if(tone==='pos'&&Number(value)>0)article.classList.add('ic-metric-pos');if(tone==='neg'&&Number(value)>0)article.classList.add('ic-metric-neg');const span=document.createElement('span');span.textContent=label;const strong=document.createElement('strong');strong.textContent=String(value??0);article.append(span,strong);return article}
function renderMetrics(id,values){const root=$(id);root.innerHTML='';for(const [label,value,tone] of values)root.appendChild(metric(label,value,tone))}
function accountLabel(a){const suffix=a.ibanLast4?` · ${a.ibanLast4}`:'';return `${a.displayName||a.institutionName||'Konto'}${suffix}`}
function guessAccount(source){if(!source)return state.accounts.find(a=>a.isActive)||state.accounts[0]||null;const norm=source.toLowerCase();const exact=state.accounts.filter(a=>a.ibanLast4&&norm.includes(String(a.ibanLast4).toLowerCase()));if(exact.length===1)return exact[0];const byName=state.accounts.filter(a=>norm.includes(String(a.displayName||'').toLowerCase())||String(a.displayName||'').toLowerCase().includes(norm));return byName.length===1?byName[0]:(state.accounts.find(a=>a.isActive)||state.accounts[0]||null)}
function accountSelect(source){const select=document.createElement('select');select.className='account-map-select';const empty=document.createElement('option');empty.value='';empty.textContent=t.none;select.appendChild(empty);const guess=guessAccount(source);for(const account of state.accounts){const option=document.createElement('option');option.value=account.id;option.textContent=accountLabel(account);if(guess?.id===account.id)option.selected=true;select.appendChild(option)}return select}

const txFields=[{key:'date',label:lang==='de'?'Datum':'Date',required:true},{key:'amount',label:lang==='de'?'Betrag':'Amount',required:true},{key:'currency',label:lang==='de'?'Währung':'Currency'},{key:'counterparty',label:lang==='de'?'Empfänger / Händler':'Counterparty'},{key:'description',label:lang==='de'?'Verwendungszweck':'Description'},{key:'account',label:lang==='de'?'Quellkonto':'Source account'},{key:'category',label:lang==='de'?'Kategorie':'Category'},{key:'externalKey',label:'ID'}];
const invFields=[{key:'tradeDate',label:lang==='de'?'Handelsdatum':'Trade date',required:true},{key:'tradeType',label:lang==='de'?'Transaktionsart':'Transaction type',required:true},{key:'settlementDate',label:lang==='de'?'Valuta':'Settlement date'},{key:'securityName',label:lang==='de'?'Wertpapier':'Security'},{key:'isin',label:'ISIN'},{key:'wkn',label:'WKN'},{key:'ticker',label:'Ticker'},{key:'quantity',label:lang==='de'?'Stückzahl':'Quantity'},{key:'price',label:lang==='de'?'Kurs':'Price'},{key:'grossAmount',label:lang==='de'?'Brutto':'Gross amount'},{key:'amount',label:lang==='de'?'Betrag':'Amount'},{key:'currency',label:lang==='de'?'Währung':'Currency'},{key:'fees',label:lang==='de'?'Gebühren':'Fees'},{key:'taxes',label:lang==='de'?'Steuern':'Taxes'},{key:'withholdingTax',label:lang==='de'?'Quellensteuer':'Withholding tax'},{key:'assetClass',label:lang==='de'?'Anlageklasse':'Asset class'},{key:'externalKey',label:'ID'}];

async function detectTransactions(){const file=currentFile('tx');if(!file){status('tx',t.needFile);return}state.tx.file=file;status('tx',t.working);$('tx-review-section').hidden=true;$('tx-result').hidden=true;try{state.tx.detect=await api(`api/import-mapping/detect?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`,{method:'POST',body:await formWithFile(file)});renderMapping('tx',txFields);status('tx',`${state.tx.detect.rowCount} ${t.rows}.`)}catch(err){error('tx',err)}}
async function stageTransactions(){try{const mapping=collectMapping('tx',txFields);status('tx',t.working);const body=await formWithFile(state.tx.file);body.append('mapping',JSON.stringify(mapping));const staged=await api(`api/import-mapping/upload?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`,{method:'POST',body});state.tx.jobId=staged.jobId;const [summary,candidates]=await Promise.all([api(`api/import-mapping/jobs/${staged.jobId}/summary?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`),api(`api/import-jobs/${staged.jobId}/candidates?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`)]);state.tx.summary=summary;state.tx.candidates=candidates;state.tx.duplicates=new Map();renderTransactionTargets(summary,staged);renderTransactionCandidates();$('tx-review-section').hidden=false;status('tx','');await refreshDuplicatePreview()}catch(err){error('tx',err)}}
function renderTransactionTargets(summary,staged){const root=$('tx-account-mapping');root.innerHTML='';const sources=summary.sourceAccounts?.length?summary.sourceAccounts:[{source:'',count:staged.ready||0}];for(const item of sources){const row=document.createElement('div');row.className='row ic-map-row';row.dataset.source=item.source??'';const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=item.source||(lang==='de'?'Ohne Kontoangabe':'No source account');const sub=document.createElement('div');sub.className='row-sub';sub.textContent=`${item.count} ${t.rows}`;main.append(title,sub);row.append(main,accountSelect(item.source||''));root.appendChild(row)}if(!state.accounts.length){const note=document.createElement('p');note.className='row-sub ic-empty';note.textContent=t.noAccounts;root.appendChild(note)}renderMetrics('tx-review-summary',[[t.rows,staged.sourceRows],[t.ready,staged.ready,'pos'],[t.errors,staged.errors,'neg']])}

// --- Review step: the actual bookings ---------------------------------------------------------
// The candidate rows were already being fetched and then discarded, so the confirmation showed three
// numbers and no bookings, and committed everything with candidateIds:null. The backend honours
// CandidateIds, so this is where you choose. Rendering is capped and says so - a 5.000-row file must
// not build 5.000 DOM rows - while the selection itself always covers every row.
const MAX_RENDERED_CANDIDATES = 200;
const candidateReady = c => c.validationStatus === 'ready';
const fill = (template, values) => Object.entries(values).reduce((text, [key, value]) => text.replaceAll(`{${key}}`, value), template);

function candidateAmount(candidate) {
  const value = Number(candidate.amount ?? 0);
  try { return new Intl.NumberFormat(lang === 'de' ? 'de-DE' : 'en-US', { style: 'currency', currency: candidate.currency || 'EUR' }).format(value); }
  catch { return `${value.toFixed(2)} ${candidate.currency || ''}`.trim(); }
}

const duplicateReasonText = reason => reason === 'in_file' ? t.dupInFile
  : reason === 'external_key' ? t.dupExternalKey
  : reason === 'existing' ? t.dupExisting : t.duplicate;

function candidateRow(candidate) {
  const ready = candidateReady(candidate);
  const duplicateReason = state.tx.duplicates.get(candidate.id);
  const row = document.createElement('div');
  row.className = ready ? 'row ic-candidate' : 'row ic-candidate ic-candidate-error';
  if (ready && duplicateReason) row.classList.add('ic-candidate-duplicate');

  const label = document.createElement('label');
  label.className = 'check ic-candidate-label';
  const box = document.createElement('input');
  box.type = 'checkbox';
  box.dataset.candidate = candidate.id;
  box.checked = ready && !duplicateReason;
  box.disabled = !ready;
  box.addEventListener('change', () => {
    if (box.checked) state.tx.selected.add(candidate.id); else state.tx.selected.delete(candidate.id);
    updateSelectedCount();
  });

  const main = document.createElement('span');
  main.className = 'row-main';
  const title = document.createElement('span');
  title.className = 'row-title';
  title.textContent = candidate.counterparty || candidate.description || '—';
  const sub = document.createElement('span');
  sub.className = 'row-sub';
  sub.textContent = [candidate.bookingDate, candidate.categoryText,
    ready ? null : (candidate.validationError || t.notImportable),
    duplicateReason ? `${t.duplicate} — ${duplicateReasonText(duplicateReason)}` : null].filter(Boolean).join(' · ');
  main.append(title, sub);
  label.append(box, main);

  const amount = document.createElement('span');
  amount.className = 'amount';
  amount.textContent = candidateAmount(candidate);

  row.append(label, amount);
  return row;
}

function updateSelectedCount() {
  const ready = (state.tx.candidates || []).filter(candidateReady).length;
  setText('tx-selected-count', fill(t.selectedOf, { n: state.tx.selected.size, total: ready }));
  const master = $('tx-select-all');
  if (!master) return;
  master.checked = ready > 0 && state.tx.selected.size === ready;
  master.indeterminate = state.tx.selected.size > 0 && state.tx.selected.size < ready;
}

function renderTransactionCandidates() {
  const list = $('tx-candidates');
  if (!list) return;
  const all = state.tx.candidates || [];
  // Duplicates stay visible but unchecked: importing them a second time is almost never what you
  // want, and hiding them would make the row count disagree with the file you picked.
  state.tx.selected = new Set(all.filter(c => candidateReady(c) && !state.tx.duplicates.has(c.id)).map(c => c.id));
  const duplicateCount = all.filter(c => candidateReady(c) && state.tx.duplicates.has(c.id)).length;
  const duplicateHint = $('tx-duplicate-hint');
  if (duplicateHint) {
    duplicateHint.hidden = duplicateCount === 0;
    duplicateHint.textContent = fill(t.dupHint, { n: duplicateCount });
  }

  setText('tx-rows-title', t.rowsTitle);
  setText('tx-rows-hint', t.rowsHint);
  setText('tx-select-all-label', t.selectAll);

  // Distinct reasons, the way the portfolio review already does it - a count alone tells you nothing.
  const errors = $('tx-errors');
  const reasons = [...new Set(all.filter(c => !candidateReady(c)).map(c => c.validationError).filter(Boolean))].slice(0, 5);
  errors.innerHTML = '';
  errors.hidden = reasons.length === 0;
  for (const message of reasons) {
    const row = document.createElement('div');
    row.className = 'row';
    const main = document.createElement('div');
    main.className = 'row-main';
    const title = document.createElement('div');
    title.className = 'row-title';
    title.textContent = t.errorsTitle;
    const sub = document.createElement('div');
    sub.className = 'row-sub';
    sub.textContent = message;
    main.append(title, sub);
    row.appendChild(main);
    errors.appendChild(row);
  }

  list.innerHTML = '';
  if (!all.length) {
    const empty = document.createElement('p');
    empty.className = 'row-sub ic-empty';
    empty.textContent = t.noRows;
    list.appendChild(empty);
    updateSelectedCount();
    return;
  }
  for (const candidate of all.slice(0, MAX_RENDERED_CANDIDATES)) list.appendChild(candidateRow(candidate));
  if (all.length > MAX_RENDERED_CANDIDATES) {
    const note = document.createElement('p');
    note.className = 'row-sub ic-empty';
    note.textContent = fill(t.showing, { n: MAX_RENDERED_CANDIDATES, total: all.length });
    list.appendChild(note);
  }
  updateSelectedCount();
}
function collectAccountMappings({ required = true } = {}) {
  const mappings = {};
  for (const row of $('tx-account-mapping').querySelectorAll('.row')) {
    const value = row.querySelector('select')?.value;
    if (!value) {
      if (required) throw new Error(t.needAccount);
      return null;
    }
    mappings[row.dataset.source || ''] = value;
  }
  return mappings;
}

// The duplicate check compares against the transactions of the TARGET account, so it cannot run at
// upload time - only once the accounts are mapped, and again whenever that mapping changes.
async function refreshDuplicatePreview() {
  if (!state.tx.jobId) return;
  const mappings = collectAccountMappings({ required: false });
  if (!mappings) {
    state.tx.duplicates = new Map();
    renderTransactionCandidates();
    return;
  }
  try {
    status('tx', t.dupChecking);
    const preview = await api(`api/import-mapping/jobs/${state.tx.jobId}/duplicate-preview?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`,
      { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceAccountMappings: mappings, defaultAccountId: null }) });
    state.tx.duplicates = new Map((preview.candidates || []).filter(item => item.status === 'duplicate').map(item => [item.id, item.reason]));
    status('tx', '');
  } catch (err) {
    // A failed preview must not block the import - the commit runs the same check authoritatively.
    console.warn('duplicate preview failed', err);
    state.tx.duplicates = new Map();
    status('tx', '');
  }
  renderTransactionCandidates();
}

async function commitTransactions(){try{if(!state.tx.selected.size)throw new Error(t.noneSelected);const mappings=collectAccountMappings();status('tx',t.working);const result=await api(`api/import-mapping/jobs/${state.tx.jobId}/commit?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sourceAccountMappings:mappings,defaultAccountId:null,categoryMappings:{},createMissingCategories:$('tx-create-categories').checked,runFullWorthCategorization:$('tx-run-rules').checked,candidateIds:[...state.tx.selected]})});renderResult('tx',result);await loadTransactionHistory();status('tx',t.done)}catch(err){error('tx',err)}}

async function detectInvestments(){const file=currentFile('inv');if(!file){status('inv',t.needFile);return}state.inv.file=file;status('inv',t.working);$('inv-review-section').hidden=true;$('inv-result').hidden=true;try{state.inv.detect=await api(`api/investment-import/detect?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`,{method:'POST',body:await formWithFile(file)});if(isTradeRepublicExport(state.inv.detect)){$('inv-preset').value='traderepublic';$('inv-new-portfolio-name').value='Trade Republic';$('inv-new-portfolio-currency').value=detectedCurrency(state.inv.detect);const matches=state.portfolios.filter(p=>!p.isArchived&&String(p.name||'').toLowerCase().includes('trade republic'));$('inv-portfolio').value=matches.length===1?matches[0].id:'__new__';syncPortfolioTarget()}renderMapping('inv',invFields);status('inv',`${state.inv.detect.rowCount} ${t.rows}.`)}catch(err){error('inv',err)}}
async function stageInvestments(){try{const mapping=collectMapping('inv',invFields);if($('inv-preset')?.value==='traderepublic')mapping.sourceProvider='trade_republic';status('inv',t.working);const body=await formWithFile(state.inv.file);body.append('mapping',JSON.stringify(mapping));const staged=await api(`api/investment-import/upload?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`,{method:'POST',body});state.inv.jobId=staged.jobId;state.inv.summary=await api(`api/investment-import/jobs/${staged.jobId}/summary?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`);renderInvestmentReview(state.inv.summary,staged);$('inv-review-section').hidden=false;status('inv','')}catch(err){error('inv',err)}}
function renderInvestmentReview(summary,staged){const root=$('inv-security-summary');root.innerHTML='';const validationErrors=[...new Set((summary.preview||[]).filter(item=>item.validationStatus==='error').map(item=>item.validationError).filter(Boolean))].slice(0,5);for(const message of validationErrors){const row=document.createElement('div');row.className='row';const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=lang==='de'?'Importfehler':'Import error';const sub=document.createElement('div');sub.className='row-sub';sub.textContent=message;main.append(title,sub);row.appendChild(main);root.appendChild(row)}for(const item of summary.securities||[]){const row=document.createElement('div');row.className='row ic-map-row';row.dataset.key=item.key;const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=item.name||item.isin||item.wkn||item.ticker||item.key;const sub=document.createElement('div');sub.className='row-sub';sub.textContent=[item.isin,item.wkn,item.ticker,item.assetType,`${item.count} ${t.rows}`].filter(Boolean).join(' · ');main.append(title,sub);const select=document.createElement('select');select.className='account-map-select';const auto=document.createElement('option');auto.value='';auto.textContent=item.autoMatchId?`${t.matched}: ${item.autoMatchName}`:t.autoNew;select.appendChild(auto);for(const sec of state.securities){const option=document.createElement('option');option.value=sec.id;option.textContent=[sec.name,sec.isin,sec.ticker].filter(Boolean).join(' · ');if(item.autoMatchId===sec.id)option.selected=true;select.appendChild(option)}row.append(main,select);root.appendChild(row)}renderInvestmentTypeSummary(summary);renderMetrics('inv-review-summary',[[t.rows,staged.sourceRows],[t.ready,summary.ready,'pos'],[t.errors,summary.errors,'neg']])}
function investmentTypeLabel(type){const labels={buy:lang==='de'?'Käufe':'Buys',sell:lang==='de'?'Verkäufe':'Sells',cancellation:lang==='de'?'Stornos':'Cancellations',dividend:lang==='de'?'Dividenden':'Dividends',interest:lang==='de'?'Zinsen':'Interest',fee:lang==='de'?'Gebühren':'Fees',tax:lang==='de'?'Steuern':'Taxes',deposit:lang==='de'?'Einzahlungen':'Deposits',withdrawal:lang==='de'?'Auszahlungen':'Withdrawals',security_transfer_in:lang==='de'?'Depotübertrag rein':'Security transfers in',security_transfer_out:lang==='de'?'Depotübertrag raus':'Security transfers out',split:'Splits',other:lang==='de'?'Sonstiges':'Other'};return labels[type]||type}
function renderInvestmentTypeSummary(summary){const root=$('inv-type-summary');if(!root)return;root.innerHTML='';for(const item of summary?.transactionTypes||[]){const row=document.createElement('div');row.className='row';const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=investmentTypeLabel(item.type);const sub=document.createElement('div');sub.className='row-sub';sub.textContent=`${item.count} ${t.rows}`;main.append(title,sub);const amount=document.createElement('strong');amount.className='amount';amount.textContent=String(item.count);row.append(main,amount);root.appendChild(row)}}
function renderInvestmentReconciliation(data){const section=$('inv-reconciliation');if(!section)return;if(!data){section.hidden=true;return}section.hidden=false;const cash=(data.cashBalances||[]).map(x=>`${Number(x.amount).toLocaleString(lang==='de'?'de-DE':'en-US',{maximumFractionDigits:2})} ${x.currency}`).join(' · ')||'—';renderMetrics('inv-reconciliation-summary',[[t.positions,(data.positions||[]).length,data.healthy?'pos':''],[t.cash,cash,''],[t.checkWarnings,(data.warnings||[]).length,(data.warnings||[]).some(x=>x.severity==='error')?'neg':'warn']]);const root=$('inv-reconciliation-warnings');root.innerHTML='';if(!(data.warnings||[]).length){const row=document.createElement('div');row.className='row';const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=t.healthy;main.appendChild(title);row.appendChild(main);root.appendChild(row);return}for(const warning of data.warnings){const row=document.createElement('div');row.className='row';const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=warning.message||warning.code;const sub=document.createElement('div');sub.className='row-sub';sub.textContent=warning.severity||'';main.append(title,sub);row.appendChild(main);root.appendChild(row)}}
async function loadInvestmentResources(){if(!state.space)return;const q=`fullWorthSpaceId=${encodeURIComponent(state.space.id)}`;const [portfolios,securities]=await Promise.all([api(`api/investments/portfolios?${q}`).catch(()=>[]),api(`api/investments/securities?${q}`).catch(()=>[])]);state.portfolios=portfolios||[];state.securities=securities||[];fillPortfolioSelect()}
const jobStatusLabel=status=>({completed:t.statusCompleted,cancelled:t.statusCancelled,failed:t.statusFailed,rolled_back:t.statusRolledBack})[status]||status;

// Mirrors the portfolio history. A job is only offered for rollback when the backend says it left a
// provenance trail - an import from before that existed cannot be undone, and the button must not
// pretend otherwise.
function renderTransactionHistory(){
  const root=$('tx-history');
  if(!root)return;
  root.innerHTML='';
  const items=(state.tx.history||[]).filter(item=>item.importedCount>0||item.status==='rolled_back');
  if(!items.length){
    const empty=document.createElement('div');
    empty.className='row-sub';
    empty.textContent=t.txHistoryEmpty;
    root.appendChild(empty);
    return;
  }
  for(const item of items){
    const row=document.createElement('div');
    row.className='row';
    const main=document.createElement('div');
    main.className='row-main';
    const title=document.createElement('div');
    title.className='row-title';
    title.textContent=item.fileName||'Import';
    const sub=document.createElement('div');
    sub.className='row-sub';
    const when=item.createdAt?new Date(item.createdAt).toLocaleString(lang==='de'?'de-DE':'en-US'):'';
    sub.textContent=[when,`${item.importedCount||0} ${t.imported.toLowerCase()}`,jobStatusLabel(item.status)].filter(Boolean).join(' · ');
    main.append(title,sub);
    row.appendChild(main);
    if(item.rollbackAvailable){
      const button=document.createElement('button');
      button.type='button';
      button.className='ghost';
      button.textContent=t.txRollback;
      button.onclick=()=>rollbackTransactionImport(item.id);
      row.appendChild(button);
    }
    root.appendChild(row);
  }
}

async function loadTransactionHistory(){
  if(!state.space)return;
  state.tx.history=await api(`api/import-jobs?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`).catch(()=>[]);
  renderTransactionHistory();
}

async function rollbackTransactionImport(jobId){
  if(!await confirmMessage({message:t.txRollbackConfirm,title:t.txRollback,confirmLabel:t.txRollback,cancelLabel:t.back,destructive:true}))return;
  try{
    status('tx',t.working);
    const result=await api(`api/import-jobs/${encodeURIComponent(jobId)}/rollback?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`,{method:'POST'});
    await loadTransactionHistory();
    status('tx',fill(t.txRolledBack,{removed:result.removed??0,kept:result.kept??0}));
  }catch(err){error('tx',err)}
}

async function loadInvestmentHistory(){if(!state.space)return;const q=`fullWorthSpaceId=${encodeURIComponent(state.space.id)}`;state.inv.history=await api(`api/investment-import/history?${q}&limit=25`).catch(()=>[]);renderInvestmentHistory()}
function renderInvestmentHistory(){const root=$('inv-history');if(!root)return;root.innerHTML='';const items=state.inv.history||[];if(!items.length){const empty=document.createElement('div');empty.className='row-sub';empty.textContent=t.historyEmpty;root.appendChild(empty);return}for(const item of items){const row=document.createElement('div');row.className='row';const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=item.portfolioName||item.fileName||'Import';const sub=document.createElement('div');sub.className='row-sub';const when=item.createdAt?new Date(item.createdAt).toLocaleString(lang==='de'?'de-DE':'en-US'):'';sub.textContent=[item.fileName,when,`${item.imported||0} ${t.imported.toLowerCase()}`,item.status].filter(Boolean).join(' · ');main.append(title,sub);row.appendChild(main);if(item.rollbackAvailable){const button=document.createElement('button');button.type='button';button.className='ghost';button.textContent=t.rollback;button.onclick=()=>rollbackInvestmentImport(item.id);row.appendChild(button)}root.appendChild(row)}}
async function rollbackInvestmentImport(jobId){if(!await confirmMessage({message:t.rollbackConfirm,title:t.rollback,confirmLabel:t.rollback,cancelLabel:t.back,destructive:true}))return;try{status('inv',t.working);const q=`fullWorthSpaceId=${encodeURIComponent(state.space.id)}`;await api(`api/investment-import/jobs/${encodeURIComponent(jobId)}/rollback?${q}`,{method:'POST'});await Promise.all([loadInvestmentResources(),loadInvestmentHistory()]);status('inv',t.rolledBack)}catch(err){error('inv',err)}}

function investmentTarget(){const value=$('inv-portfolio').value;if(value&&value!=='__new__')return{portfolioId:value,createPortfolio:null};if(value!=='__new__')throw new Error(t.needPortfolio);const name=$('inv-new-portfolio-name').value.trim();const currency=$('inv-new-portfolio-currency').value.trim().toUpperCase();if(!name||!/^[A-Za-z]{3}$/.test(currency))throw new Error(lang==='de'?'Bitte Depotname und gültige 3-stellige Währung angeben.':'Enter a portfolio name and a valid 3-letter currency.');return{portfolioId:null,createPortfolio:{name,currency,providerName:importProviderName()}}}
async function commitInvestments(){try{status('inv',t.working);const target=investmentTarget();const mappings={};for(const row of $('inv-security-summary').querySelectorAll('.row')){const value=row.querySelector('select')?.value;if(value)mappings[row.dataset.key]=value}const result=await api(`api/investment-import/jobs/${state.inv.jobId}/commit?fullWorthSpaceId=${encodeURIComponent(state.space.id)}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({...target,securityMappings:mappings,createMissingSecurities:$('inv-create-securities').checked,candidateIds:[...state.tx.selected]})});renderResult('inv',result);renderInvestmentReconciliation(result.reconciliation);await Promise.all([loadInvestmentResources(),loadInvestmentHistory()]);status('inv',t.done)}catch(err){error('inv',err)}}
function renderResult(kind,result){const root=$(kind==='tx'?'tx-result':'inv-result');root.innerHTML='';for(const [label,value,tone] of [[t.imported,result.imported,'pos'],[t.duplicates,result.duplicates,'warn'],[t.rows,result.total,'']]){const row=document.createElement('div');row.className='row ic-result-row';const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=label;const amount=document.createElement('strong');amount.className='amount';if(tone==='pos'&&Number(value)>0)amount.classList.add('positive');if(tone==='warn'&&Number(value)>0)amount.classList.add('ic-amt-warn');amount.textContent=String(value??0);main.appendChild(title);row.append(main,amount);root.appendChild(row)}root.hidden=false}
function syncPortfolioTarget(){const creating=$('inv-portfolio').value==='__new__';$('inv-new-portfolio-fields').hidden=!creating;$('inv-new-portfolio-name').required=creating;$('inv-new-portfolio-currency').required=creating}
function fillPortfolioSelect(){const select=$('inv-portfolio');select.innerHTML='';for(const p of state.portfolios.filter(p=>!p.isArchived)){const option=document.createElement('option');option.value=p.id;option.textContent=`${p.name} · ${p.currency||'EUR'}`;select.appendChild(option)}const add=document.createElement('option');add.value='__new__';add.textContent=t.newPortfolio;select.appendChild(add);if(!state.portfolios.some(p=>!p.isArchived))select.value='__new__';select.onchange=syncPortfolioTarget;syncPortfolioTarget()}
async function boot(){try{const spaces=await api('api/fullworth-spaces');const saved=localStorage.getItem('finance.space');state.space=spaces.find(s=>s.id===saved)||spaces[0]||null;if(!state.space)throw new Error('No FullWorth Space');setText('space-name',state.space.name||'—');const q=`fullWorthSpaceId=${encodeURIComponent(state.space.id)}`;const [accounts,portfolios,securities,history,transactionHistory]=await Promise.all([api(`api/accounts?${q}`),api(`api/investments/portfolios?${q}`).catch(()=>[]),api(`api/investments/securities?${q}`).catch(()=>[]),api(`api/investment-import/history?${q}&limit=25`).catch(()=>[]),api(`api/import-jobs?${q}`).catch(()=>[])]);state.accounts=accounts||[];state.portfolios=portfolios||[];state.securities=securities||[];state.inv.history=history||[];state.tx.history=transactionHistory||[];fillPortfolioSelect();renderInvestmentHistory();renderTransactionHistory()}catch(err){console.error(err);status('tx',t.error);status('inv',t.error)}}

document.querySelectorAll('[data-import-mode]').forEach(button=>button.addEventListener('click',()=>{const mode=button.dataset.importMode;document.querySelectorAll('[data-import-mode]').forEach(x=>x.classList.toggle('active',x===button));$('transaction-import').hidden=mode!=='transactions';$('investment-import').hidden=mode!=='investments';(mode==='transactions'?$('transaction-import'):$('investment-import')).scrollIntoView({behavior:'smooth',block:'start'})}));
$('tx-preset').addEventListener('change',()=>{if(state.tx.detect)renderMapping('tx',txFields)});$('inv-preset').addEventListener('change',()=>{const preset=$('inv-preset').value;if($('inv-portfolio').value==='__new__'){$('inv-new-portfolio-name').value=preset==='traderepublic'?'Trade Republic':preset==='parqet'?'Parqet Import':preset==='finanzfluss'?'Finanzfluss Import':(lang==='de'?'Importiertes Depot':'Imported portfolio')}if(state.inv.detect)renderMapping('inv',invFields)});
$('tx-detect').addEventListener('click',detectTransactions);$('tx-stage').addEventListener('click',stageTransactions);$('tx-commit').addEventListener('click',commitTransactions);$('inv-detect').addEventListener('click',detectInvestments);$('inv-stage').addEventListener('click',stageInvestments);$('inv-commit').addEventListener('click',commitInvestments);
// Select-all covers every candidate, not just the rendered slice, so a capped list still commits
// exactly what the count promises.
$('tx-select-all')?.addEventListener('change',()=>{
  const on=$('tx-select-all').checked;
  const all=state.tx.candidates||[];
  state.tx.selected=on?new Set(all.filter(candidateReady).map(c=>c.id)):new Set();
  for(const box of $('tx-candidates').querySelectorAll('input[data-candidate]')) if(!box.disabled) box.checked=on;
  updateSelectedCount();
});
// Delegated: the account selects are created per source account, so they do not exist yet at boot.
$('tx-account-mapping')?.addEventListener('change',event=>{if(event.target?.matches('select'))refreshDuplicatePreview()});
await boot();
