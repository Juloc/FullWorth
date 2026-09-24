import { api as sharedApi, jsonBody } from '../../../../../core/services.js';
import { snapshotUploadFile } from '../../../../../security/secure-fetch.js';
import { confirmMessage } from '../../../../../components/confirm.js';
import { ButtonRole, buttonClass } from '../../../../../components/buttons.js';
import { createDialog } from '../../../../../components/dialog.js';
import { selectionListHtml, createSelectionList } from '../../../../../components/selection-list.js';
import { esc as escapeHtml } from '../../../../../core/html.js';
const lang=(localStorage.getItem('finance.language')||'de').startsWith('en')?'en':'de';
const text={
  de:{
    subtitle:'Historische Buchungen aus Finanzguru übernehmen.',back:'Zurück',heading:'Alle Buchungen importieren',
    hint:'Wähle den Finanzguru-Export „Alle Buchungen“ im .xlsx-Format.',space:'FullWorth Space',
    safetyTitle:'Sicherer Import',
    safety:'Importierte Buchungen bleiben zunächst reine Historie. Erst nach einer eindeutigen Kontozuordnung werden sie für die Vermögenshistorie verwendet.',
    file:'Finanzguru .xlsx Export',submit:'Importieren',cancel:'Abbrechen',working:'Import läuft …',confirm:'Diese Datei jetzt importieren?',
    done:'Import abgeschlossen.',error:'Import fehlgeschlagen.',rows:'Quellzeilen',imported:'Neue Buchungen',
    existing:'Bereits importiert',matched:'Mit bestehenden Buchungen abgeglichen',accounts:'Konten zugeordnet',
    createdAccounts:'Historienkonten erstellt',splits:'Split-Buchungen',
    previewTitle:'Das wird passieren',previewNew:'Neu',previewExisting:'Schon vorhanden',
    previewMatched:'Deckt sich mit der Bank',previewPeriod:'Zeitraum',previewAccountNew:'wird angelegt',
    previewAccountLinked:'verknüpftes Konto',previewAccountImport:'Importkonto',
    chooseRows:'Zeilen auswählen …',chooseRowsTitle:'Welche Zeilen sollen übernommen werden?',
    apply:'Übernehmen',previewNothing:'Nichts Neues in dieser Datei.',checking:'Datei wird gelesen …',
    selectAll:'Alle',selectedCount:'{n} von {total} ausgewählt',
    linkHeading:'Importkonten verbinden',
    linkHint:'Ordne importierte Historienkonten dem echten Bank- oder FullWorth-Konto zu. Hat das Ziel keinen Kontostand, trage den aktuellen Stand ein. Fehlende Buchungen kannst du danach unter „Buchungen“ ergänzen.',
    manageAccounts:'Bank/Konto verbinden oder anlegen',loadingLinks:'Konten werden geladen …',
    noPending:'Keine unbestätigte Importhistorie vorhanden.',noTargets:'Kein Zielkonto vorhanden.',
    target:'Zielkonto',currentBalance:'Aktueller Kontostand',optional:'optional',requiredBalance:'Für dieses Konto ist ein aktueller Kontostand erforderlich.',
    hasBalance:'Aktueller Kontostand ist bereits vorhanden.',link:'Verbinden',confirmHistory:'Historie bestätigen',
    linking:'Wird verbunden …',confirming:'Historie wird bestätigt …',linked:'Importhistorie verbunden.',
    confirmed:'Importhistorie bestätigt.',transactions:'Buchungen',period:'Zeitraum',iban:'IBAN-Endung',
    moved:'verschoben',merged:'zusammengeführt',trusted:'für Vermögenshistorie freigegeben',
    addMissing:'Fehlende Buchung ergänzen',inactive:'inaktiv',currencyMismatch:'Währung passt nicht zum Importkonto.',
    matchesTitle:'Doppelte Buchungen',matchesNone:'Keine doppelten Buchungen gefunden.',
    matchesHint:'{n} Buchungen kommen auf beiden Seiten vor. Sie werden zu je einer zusammengeführt - wähle ab, was getrennt bleiben soll.',
    movedHint:'{n} weitere Buchungen ziehen unverändert mit um.',
    winner:'Welche Fassung behalten?',winnerTarget:'Die des Zielkontos',winnerImport:'Die importierte',
    winnerHint:'Betrifft Kategorie, Aufteilung und Notiz. Die Buchung des Zielkontos bleibt in jedem Fall bestehen - sie trägt den Schlüssel, an dem die Bank sie wiedererkennt.',
    loadingMatches:'Treffer werden geprüft …',
    historyHeading:'Frühere Importe',historyEmpty:'Noch keine Finanzguru-Importe.',
    rollback:'Import rückgängig machen',
    rollbackConfirm:'Diesen Finanzguru-Import wirklich rückgängig machen? Buchungen, die du seither verlinkt, geteilt, verschlagwortet, geprüft oder mit einem Vertrag verknüpft hast, bleiben erhalten.',
    rolledBack:'{removed} entfernt, {kept} behalten.',statusCompleted:'Abgeschlossen',statusRolledBack:'Rückgängig gemacht'
  },
  en:{
    subtitle:'Import historical transactions from Finanzguru.',back:'Back',heading:'Import all transactions',
    hint:'Select the Finanzguru “Alle Buchungen” export in .xlsx format.',space:'FullWorth Space',
    safetyTitle:'Safe import',
    safety:'Imported transactions initially remain history only. They are used for wealth history only after a clear account mapping is confirmed.',
    file:'Finanzguru .xlsx export',submit:'Import',cancel:'Cancel',working:'Importing …',confirm:'Import this file now?',
    done:'Import completed.',error:'Import failed.',rows:'Source rows',imported:'New transactions',
    existing:'Already imported',matched:'Matched existing transactions',accounts:'Accounts matched',
    createdAccounts:'History accounts created',splits:'Split transactions',
    previewTitle:'What will happen',previewNew:'New',previewExisting:'Already there',
    previewMatched:'Matches the bank',previewPeriod:'Period',previewAccountNew:'will be created',
    previewAccountLinked:'linked account',previewAccountImport:'import account',
    chooseRows:'Choose rows …',chooseRowsTitle:'Which rows should be imported?',
    apply:'Import',previewNothing:'Nothing new in this file.',checking:'Reading the file …',
    selectAll:'All',selectedCount:'{n} of {total} selected',
    linkHeading:'Link imported accounts',
    linkHint:'Map imported history accounts to the real bank or FullWorth account. If the target has no balance, enter the current balance. Missing bookings can then be added under Transactions.',
    manageAccounts:'Connect or create bank/account',loadingLinks:'Loading accounts …',
    noPending:'No unconfirmed imported history.',noTargets:'No target account available.',
    target:'Target account',currentBalance:'Current balance',optional:'optional',requiredBalance:'A current balance is required for this account.',
    hasBalance:'A current balance already exists.',link:'Link',confirmHistory:'Confirm history',
    linking:'Linking …',confirming:'Confirming history …',linked:'Imported history linked.',
    confirmed:'Imported history confirmed.',transactions:'Transactions',period:'Period',iban:'IBAN suffix',
    moved:'moved',merged:'merged',trusted:'approved for wealth history',
    addMissing:'Add missing booking',inactive:'inactive',currencyMismatch:'Currency does not match the imported account.',
    matchesTitle:'Duplicate bookings',matchesNone:'No duplicate bookings found.',
    matchesHint:'{n} bookings appear on both sides. Each pair is merged into one — uncheck what should stay separate.',
    movedHint:'{n} further bookings move across unchanged.',
    winner:'Which version to keep?',winnerTarget:'The target account’s',winnerImport:'The imported one',
    winnerHint:'Affects category, split and note. The target account’s booking always survives — it carries the key the bank recognises it by.',
    loadingMatches:'Checking matches …',
    historyHeading:'Previous imports',historyEmpty:'No Finanzguru imports yet.',
    rollback:'Roll back import',
    rollbackConfirm:'Really roll back this Finanzguru import? Bookings you have since linked, split, tagged, reviewed or linked to a contract are kept.',
    rolledBack:'{removed} removed, {kept} kept.',statusCompleted:'Completed',statusRolledBack:'Rolled back'
  }
}[lang];

document.documentElement.lang=lang;
for(const [id,key] of Object.entries({
  'import-subtitle':'subtitle','import-back':'back','import-heading':'heading','import-hint':'hint','import-space-label':'space',
  'import-safety-title':'safetyTitle','import-safety':'safety','import-file-label':'file','finanzguru-submit':'submit',
  'import-link-heading':'linkHeading','import-link-hint':'linkHint','import-link-accounts':'manageAccounts',
  'import-history-heading':'historyHeading'
})){
  const node=document.getElementById(id);
  if(node)node.textContent=text[key];
}

const form=document.getElementById('finanzguru-form');
const fileInput=document.getElementById('finanzguru-file');
const submit=document.getElementById('finanzguru-submit');
const status=document.getElementById('import-status');
const result=document.getElementById('import-result');
const linkStatus=document.getElementById('import-link-status');
const linkList=document.getElementById('import-link-list');
const historyList=document.getElementById('import-history');
let space=null;
let linkOptions={importAccounts:[],targetAccounts:[],attachedHistory:[]};
let history=[];

const fill=(template,values)=>Object.entries(values).reduce((s,[k,v])=>s.replaceAll(`{${k}}`,v),template);
const jobStatusLabel=jobStatus=>({completed:text.statusCompleted,rolled_back:text.statusRolledBack})[jobStatus]||jobStatus;

function node(tag,className,textValue){
  const el=document.createElement(tag);
  if(className)el.className=className;
  if(textValue!==undefined&&textValue!==null)el.textContent=String(textValue);
  return el;
}
function formatDate(value){
  if(!value)return '—';
  const d=new Date(String(value).slice(0,10)+'T00:00:00');
  return Number.isNaN(d.getTime())?String(value):new Intl.DateTimeFormat(lang==='de'?'de-DE':'en-US').format(d);
}
function formatPeriod(first,last){
  if(!first&&!last)return '—';
  if(first===last)return formatDate(first);
  return `${formatDate(first)} – ${formatDate(last)}`;
}
// "XXX" ist der ISO-4217-Code fuer "keine Waehrung" - PayPal-Wallets melden ihn, weil sie mehrere
// zugleich halten. Er sagt „hier steht keine", nicht „hier steht eine andere" (#112).
function declaredCurrency(value){
  const code=(value||'').trim().toUpperCase();
  return code.length===3&&code!=='XXX'?code:null;
}
// Widersprechen sich zwei Angaben? Nur wenn BEIDE eine Waehrung nennen und die sich unterscheidet.
// Ein Gleichheitsvergleich machte aus der fehlenden Angabe einen Widerspruch und sperrte das Konto.
function currenciesConflict(left,right){
  const a=declaredCurrency(left),b=declaredCurrency(right);
  return !!a&&!!b&&a!==b;
}

function targetLabel(target){
  const parts=[target.institutionName,target.displayName].filter(Boolean);
  let label=[...new Set(parts)].join(' · ')||target.id;
  // Ohne erklaerte Waehrung steht keine da. „PayPal · XXX" las sich wie eine Waehrung namens XXX.
  if(declaredCurrency(target.currency))label+=` · ${target.currency}`;
  if(target.ibanLast4)label+=` · •••• ${target.ibanLast4}`;
  if(target.isActive===false)label+=` · ${text.inactive}`;
  return label;
}
function currentTarget(select){
  return linkOptions.targetAccounts.find(item=>item.id===select.value)||null;
}
function balanceControl(target,wrapper,input,hint){
  if(!target){input.required=false;input.disabled=true;hint.textContent='';return;}
  input.disabled=false;
  input.required=!target.hasCurrentBalance;
  input.placeholder=target.hasCurrentBalance?text.optional:'0,00';
  hint.textContent=target.hasCurrentBalance?text.hasBalance:text.requiredBalance;
  wrapper.classList.toggle('required-balance',!target.hasCurrentBalance);
}
function actionSummary(data,targetId,successText){
  linkStatus.replaceChildren();
  const line=node('div','import-link-success',successText);
  const details=node('div','row-sub',
    `${data.transactionsMoved??0} ${text.moved} · ${data.transactionsMerged??0} ${text.merged} · ${data.transactionsTrustedForHistory??0} ${text.trusted}`);
  const add=node('a',buttonClass(ButtonRole.Secondary,'import-add-missing'),text.addMissing);
  add.href=`/transactions?accountId=${encodeURIComponent(targetId)}`;
  linkStatus.append(line,details,add);
}

async function renderLinkOptions(){
  if(!space)return;
  linkStatus.textContent=text.loadingLinks;
  linkList.replaceChildren();
  try{
    linkOptions=await sharedApi(`api/import/finanzguru/accounts?fullWorthSpaceId=${encodeURIComponent(space.id)}`)||{importAccounts:[],targetAccounts:[],attachedHistory:[]};
    linkStatus.textContent='';
    const imports=linkOptions.importAccounts||[];
    const attached=linkOptions.attachedHistory||[];
    if(!imports.length&&!attached.length){
      linkList.append(node('div','row-sub import-link-empty',text.noPending));
      return;
    }

    for(const item of imports)linkList.append(buildImportLinkCard(item));
    for(const item of attached)linkList.append(buildAttachedHistoryCard(item));
  }catch(error){
    console.error(error);
    linkStatus.textContent=`${text.error} ${error.message||''}`.trim();
  }
}

function metadata(item){
  const meta=node('div','import-link-meta');
  meta.append(
    node('span','',`${text.transactions}: ${item.transactionCount??0}`),
    node('span','',`${text.period}: ${formatPeriod(item.firstBookingDate,item.lastBookingDate)}`)
  );
  if(item.ibanLast4)meta.append(node('span','',`${text.iban}: •••• ${item.ibanLast4}`));
  return meta;
}

function buildImportLinkCard(item){
  const card=node('div','import-link-item');
  const title=node('div','import-link-title',item.displayName||'Finanzguru');
  title.append(node('span','import-link-currency',item.currency));
  card.append(title,metadata(item));

  if(!linkOptions.targetAccounts?.length){
    card.append(node('div','row-sub',text.noTargets));
    return card;
  }

  const controls=node('div','import-link-controls');
  const targetField=node('label','field');
  targetField.append(node('span','',text.target));
  const select=node('select','import-target');
  for(const target of linkOptions.targetAccounts){
    const option=document.createElement('option');
    option.value=target.id;
    option.textContent=targetLabel(target);
    option.disabled=currenciesConflict(target.currency,item.currency);
    // Ein ausgegrauter Eintrag ohne Begruendung ist eine Sackgasse: „PayPal · XXX" stand da und sagte
    // nicht, warum es nicht geht. Der Grund steht jetzt im Eintrag selbst (#112).
    if(option.disabled){option.textContent+=` — ${text.currencyMismatch}`;option.title=text.currencyMismatch;}
    if(target.id===(item.linkedTargetAccountId||item.suggestedTargetAccountId))option.selected=true;
    select.append(option);
  }
  if(!select.value){
    const compatible=linkOptions.targetAccounts.find(target=>!currenciesConflict(target.currency,item.currency));
    if(compatible)select.value=compatible.id;
  }
  targetField.append(select);

  const balanceField=node('label','field import-balance-field');
  balanceField.append(node('span','',`${text.currentBalance} (${text.optional})`));
  const balance=node('input','');
  balance.type='number';balance.step='0.01';balance.inputMode='decimal';
  const balanceHint=node('span','row-sub import-balance-hint','');
  balanceField.append(balance,balanceHint);

  const button=node('button',buttonClass(ButtonRole.Primary),text.link);
  button.type='button';

  // Was das Zuordnen tun wird, BEVOR es etwas tut. Vorher war "Verbinden" ein Knopf, nach dem
  // Buchungen verschwunden waren, ohne dass jemand vorher sagen konnte, welche.
  const matches=node('div','import-link-matches');
  const excluded=new Set();
  let preferImport=false;
  const loadMatches=async()=>{
    const target=currentTarget(select);
    matches.innerHTML='';excluded.clear();
    if(!target)return;
    matches.append(node('div','row-sub',text.loadingMatches));
    let preview;
    try{
      preview=await sharedApi(
        `api/import/finanzguru/accounts/${encodeURIComponent(item.id)}/link-preview`
        +`?fullWorthSpaceId=${encodeURIComponent(space.id)}&targetAccountId=${encodeURIComponent(target.id)}`);
    }catch(error){console.error(error);matches.innerHTML='';return;}
    matches.innerHTML='';
    const rows=preview.matches||[];
    if(!rows.length){
      matches.append(node('div','row-sub',text.matchesNone));
      if(preview.movedWithoutMatch)matches.append(
        node('div','row-sub',text.movedHint.replace('{n}',preview.movedWithoutMatch)));
      return;
    }
    matches.append(node('div','import-link-title',text.matchesTitle));
    matches.append(node('div','row-sub',text.matchesHint.replace('{n}',rows.length)));

    const choice=node('div','import-link-winner');
    for(const [value,label] of [['target',text.winnerTarget],['import',text.winnerImport]]){
      const option=node('label','check');
      const radio=document.createElement('input');
      radio.type='radio';radio.name=`winner-${item.id}`;radio.value=value;
      radio.checked=value==='target';
      radio.addEventListener('change',()=>{if(radio.checked)preferImport=value==='import';});
      option.append(radio,node('span','',label));
      choice.append(option);
    }
    matches.append(node('div','row-sub',text.winner),choice,node('div','row-sub',text.winnerHint));

    for(const row of rows){
      const line=node('label','check import-link-match');
      const box=document.createElement('input');
      box.type='checkbox';box.checked=true;
      box.addEventListener('change',()=>{
        if(box.checked)excluded.delete(row.importTransactionId);
        else excluded.add(row.importTransactionId);
      });
      const label=[row.date,row.counterparty||row.importDescription||'',`${row.amount} ${row.currency}`]
        .filter(Boolean).join(' · ');
      line.append(box,node('span','',label));
      matches.append(line);
    }
    if(preview.movedWithoutMatch)matches.append(
      node('div','row-sub',text.movedHint.replace('{n}',preview.movedWithoutMatch)));
  };

  const sync=()=>balanceControl(currentTarget(select),balanceField,balance,balanceHint);
  select.addEventListener('change',()=>{sync();loadMatches().catch(console.error);});
  sync();loadMatches().catch(console.error);

  button.addEventListener('click',async()=>{
    const target=currentTarget(select);
    if(!target)return;
    if(target.currency!==item.currency){linkStatus.textContent=text.currencyMismatch;return;}
    // Der Kontostand ist optional, und das war er auch vorher schon - nur sperrte diese Zeile das
    // Zuordnen, solange keiner dastand. Ohne Stand zaehlt das Konto als unvollstaendig; das sagt die
    // Vermoegensseite, und es ist jederzeit nachtragbar.
    const raw=balance.value.trim();
    button.disabled=true;select.disabled=true;balance.disabled=true;linkStatus.textContent=text.linking;
    try{
      const data=await sharedApi(
        `api/import/finanzguru/accounts/${encodeURIComponent(item.id)}/link?fullWorthSpaceId=${encodeURIComponent(space.id)}`,
        jsonBody({
          targetAccountId:target.id,
          currentBalance:raw===''?null:Number(raw),
          currentBalanceCurrency:target.currency,
          preferImport,
          excludedImportTransactionIds:[...excluded]
        })
      );
      actionSummary(data,target.id,text.linked);
      await renderLinkOptions();
      actionSummary(data,target.id,text.linked);
    }catch(error){
      console.error(error);linkStatus.textContent=error.message||text.error;
    }finally{button.disabled=false;select.disabled=false;balance.disabled=false;}
  });

  controls.append(targetField,balanceField,button);
  card.append(controls,matches);
  return card;
}

function buildAttachedHistoryCard(item){
  const card=node('div','import-link-item attached-history');
  const title=node('div','import-link-title',item.displayName||item.institutionName||'Konto');
  title.append(node('span','import-link-currency',item.currency));
  card.append(title,metadata(item));

  const controls=node('div','import-link-controls');
  const balanceField=node('label','field import-balance-field');
  balanceField.append(node('span','',`${text.currentBalance} (${text.optional})`));
  const balance=node('input','');
  balance.type='number';balance.step='0.01';balance.inputMode='decimal';
  const hint=node('span','row-sub import-balance-hint',item.hasCurrentBalance?text.hasBalance:text.requiredBalance);
  balance.required=!item.hasCurrentBalance;
  balance.placeholder=item.hasCurrentBalance?text.optional:'0,00';
  balanceField.classList.toggle('required-balance',!item.hasCurrentBalance);
  balanceField.append(balance,hint);

  const button=node('button',buttonClass(ButtonRole.Primary),text.confirmHistory);
  button.type='button';
  button.addEventListener('click',async()=>{
    const raw=balance.value.trim();
    if(!item.hasCurrentBalance&&raw===''){linkStatus.textContent=text.requiredBalance;balance.focus();return;}
    button.disabled=true;balance.disabled=true;linkStatus.textContent=text.confirming;
    try{
      const data=await sharedApi(
        `api/import/finanzguru/accounts/${encodeURIComponent(item.targetAccountId)}/confirm-history?fullWorthSpaceId=${encodeURIComponent(space.id)}`,
        jsonBody({
          currentBalance:raw===''?null:Number(raw),
          currentBalanceCurrency:item.currency
        })
      );
      actionSummary(data,item.targetAccountId,text.confirmed);
      await renderLinkOptions();
      actionSummary(data,item.targetAccountId,text.confirmed);
    }catch(error){
      console.error(error);linkStatus.textContent=error.message||text.error;
    }finally{button.disabled=false;balance.disabled=false;}
  });
  controls.append(balanceField,button);
  card.append(controls);
  return card;
}

// Dieselbe Liste wie auf der allgemeinen Import-Seite (api/import-jobs), nur auf Finanzguru
// eingegrenzt: sonst zoege jeder CSV- und Kontoauszugsimport des Bereichs hier mit hinein.
function renderHistory(){
  if(!historyList)return;
  historyList.replaceChildren();
  if(!history.length){
    historyList.append(node('div','row-sub',text.historyEmpty));
    return;
  }
  for(const item of history){
    const row=node('div','row');
    const main=node('div','row-main');
    main.append(node('div','row-title',item.fileName||'Finanzguru'));
    const when=item.createdAt?new Date(item.createdAt).toLocaleString(lang==='de'?'de-DE':'en-US'):'';
    main.append(node('div','row-sub',[when,`${item.importedCount||0} ${text.imported.toLowerCase()}`,jobStatusLabel(item.status)].filter(Boolean).join(' · ')));
    row.append(main);
    if(item.rollbackAvailable){
      const button=node('button',buttonClass(ButtonRole.Secondary),text.rollback);
      button.type='button';
      button.addEventListener('click',()=>rollbackImport(item.id));
      row.append(button);
    }
    historyList.append(row);
  }
}

async function loadHistory(){
  if(!space)return;
  history=await sharedApi(`api/import-jobs?fullWorthSpaceId=${encodeURIComponent(space.id)}&adapterKey=finanzguru_xlsx`).catch(()=>[]);
  renderHistory();
}

async function rollbackImport(jobId){
  if(!await confirmMessage({message:text.rollbackConfirm,title:text.rollback,confirmLabel:text.rollback,cancelLabel:text.cancel,destructive:true}))return;
  try{
    status.textContent=text.working;
    const result=await sharedApi(`api/import-jobs/${encodeURIComponent(jobId)}/rollback?fullWorthSpaceId=${encodeURIComponent(space.id)}`,{method:'POST'});
    await Promise.all([loadHistory(),renderLinkOptions()]);
    status.textContent=fill(text.rolledBack,{removed:result.removed??0,kept:result.kept??0});
  }catch(error){
    console.error(error);
    status.textContent=`${text.error} ${error.message||''}`.trim();
  }
}

try{
  const spaces=await sharedApi('api/fullworth-spaces');
  const saved=localStorage.getItem('finance.space');
  space=spaces.find(item=>item.id===saved)||spaces[0]||null;
  document.getElementById('import-space').textContent=space?.name||'—';
  if(space)await Promise.all([renderLinkOptions(),loadHistory()]);
}catch(error){
  console.error(error);
  status.textContent=text.error;
  submit.disabled=true;
}

// Der Zwischenschritt (#131, Schritt 4). Der Finanzguru-Import war der letzte, bei dem Hochladen
// gleich Festschreiben hiess - wer drei Jahre Historie hochlud, sah erst danach, was daraus geworden
// war. Jetzt: lesen, zeigen was passieren WUERDE, und erst auf Zuruf schreiben.
//
// Die Zeilen holt die Auswahl aus /api/import-jobs/{id}/candidates - derselben Liste, aus der jeder
// andere Import seine Vorschau nimmt. Der Auftrag liegt ja in denselben Tabellen.
let staged=null;

function previewRow(label,value){
  const row=node('div','row');
  const main=node('div','row-main');
  main.append(node('div','row-title',label));
  row.append(main,node('div','amount',String(value??0)));
  return row;
}

function renderPreview(preview){
  result.innerHTML='';
  result.append(previewRow(text.rows,preview.sourceRows));
  result.append(previewRow(text.previewNew,preview.newRows));
  result.append(previewRow(text.previewExisting,preview.alreadyImported));
  result.append(previewRow(text.previewMatched,preview.matchedExisting));
  if(preview.from&&preview.to){
    const period=node('div','row');
    const main=node('div','row-main');
    main.append(node('div','row-title',text.previewPeriod));
    period.append(main,node('div','row-sub',`${preview.from} – ${preview.to}`));
    result.append(period);
  }
  // Welches Konto welche Zeilen trifft - die Frage, die vorher erst hinterher zu beantworten war.
  for(const account of preview.accounts||[]){
    const row=node('div','row');
    const main=node('div','row-main');
    main.append(node('div','row-title',account.displayName));
    const what=account.status==='new'?text.previewAccountNew
      :account.status==='linked'?text.previewAccountLinked:text.previewAccountImport;
    main.append(node('div','row-sub',`${what}${account.accountName?` · ${account.accountName}`:''}`));
    row.append(main,node('div','amount',String(account.rows)));
    result.append(row);
  }

  const actions=node('div','import-preview-actions');
  if(preview.newRows>0){
    const choose=document.createElement('button');
    choose.type='button';
    choose.className=buttonClass(ButtonRole.Secondary);
    choose.textContent=text.chooseRows;
    choose.addEventListener('click',()=>void chooseRows());
    const apply=document.createElement('button');
    apply.type='button';
    apply.className=buttonClass(ButtonRole.Primary);
    apply.textContent=text.apply;
    apply.addEventListener('click',()=>void commit(null));
    actions.append(choose,apply);
  }else{
    actions.append(node('div','row-sub',text.previewNothing));
  }
  result.append(actions);
  result.hidden=false;
}

async function chooseRows(){
  const rows=await sharedApi(
    `api/import-jobs/${encodeURIComponent(staged.jobId)}/candidates?fullWorthSpaceId=${encodeURIComponent(space.id)}`);
  // Nur die neuen sind waehlbar: eine Zeile abzuwaehlen, die ohnehin nicht geschrieben wird, waere
  // eine Entscheidung ueber nichts.
  const items=(rows||[]).filter(row=>row.duplicateStatus==='new').map(row=>({
    id:escapeHtml(row.id),
    selected:true,
    html:`<div class="row-main"><div class="row-title">${escapeHtml(row.counterparty||row.description||'')}</div>`
      +`<div class="row-sub">${escapeHtml(row.bookingDate||'')} · ${escapeHtml(row.amount)} ${escapeHtml(row.currency)}</div></div>`
  }));
  const dialog=createDialog(`<div class="dialog-card"><div class="panel-head"><h2>${escapeHtml(text.chooseRowsTitle)}</h2></div>
    ${selectionListHtml(items,{rowClass:'row check-row',selectAllLabel:escapeHtml(text.selectAll)})}
    <div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${escapeHtml(text.cancel)}</button><button type="button" class="${buttonClass(ButtonRole.Primary)}" data-apply>${escapeHtml(text.apply)}</button></div>
  </div>`,{closeLabel:text.cancel});
  const list=createSelectionList();
  list.mount(dialog,{counterFormat:(n,total)=>text.selectedCount.replace('{n}',n).replace('{total}',total)});
  dialog.querySelector('[data-cancel]').addEventListener('click',()=>dialog.close());
  dialog.querySelector('[data-apply]').addEventListener('click',()=>{
    const chosen=list.getSelectedIds();
    dialog.close();
    void commit(chosen);
  });
  dialog.showModal();
}

async function commit(candidateIds){
  if(!await confirmMessage({message:text.confirm,title:text.heading,confirmLabel:text.submit,cancelLabel:text.cancel}))return;
  submit.disabled=true;fileInput.disabled=true;status.textContent=text.working;
  try{
    const data=await sharedApi(
      `api/import/finanzguru/jobs/${encodeURIComponent(staged.jobId)}/commit?fullWorthSpaceId=${encodeURIComponent(space.id)}`,
      jsonBody({candidateIds}));
    renderResult(data);
    staged=null;
    await Promise.all([renderLinkOptions(),loadHistory()]);
  }catch(error){
    console.error(error);status.textContent=`${text.error} ${error.message||''}`.trim();
  }finally{
    submit.disabled=false;fileInput.disabled=false;
  }
}

function renderResult(data){
  const rows=[
    [text.rows,data.sourceRows],[text.imported,data.transactionsImported],[text.existing,data.alreadyImported],
    [text.matched,data.matchedExistingTransactions],[text.accounts,data.accountsMatched],[text.createdAccounts,data.accountsCreated],[text.splits,data.splitTransactions]
  ];
  result.innerHTML='';
  for(const [label,value] of rows) result.appendChild(previewRow(label,value));
  result.hidden=false;status.textContent=text.done;
}

form.addEventListener('submit',async event=>{
  event.preventDefault();
  const file=fileInput.files?.[0];
  if(!file||!space)return;
  submit.disabled=true;fileInput.disabled=true;status.textContent=text.checking;result.hidden=true;result.innerHTML='';
  try{
    const uploadFile=await snapshotUploadFile(file);
    const body=new FormData();body.append('file',uploadFile,uploadFile.name);
    staged=await sharedApi(`api/import/finanzguru/stage?fullWorthSpaceId=${encodeURIComponent(space.id)}`,{method:'POST',body});
    renderPreview(staged);
    status.textContent='';
  }catch(error){
    console.error(error);status.textContent=`${text.error} ${error.message||''}`.trim();
  }finally{
    submit.disabled=false;fileInput.disabled=false;
  }
});
