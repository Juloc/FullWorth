import { api as sharedApi, jsonBody } from '../core/services.js';
import { confirmMessage } from '../ui/confirm.js';
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
    linkHeading:'Importkonten verbinden',
    linkHint:'Ordne importierte Historienkonten dem echten Bank- oder FullWorth-Konto zu. Hat das Ziel keinen Kontostand, trage den aktuellen Stand ein. Fehlende Buchungen kannst du danach unter „Buchungen“ ergänzen.',
    manageAccounts:'Bank/Konto verbinden oder anlegen',loadingLinks:'Konten werden geladen …',
    noPending:'Keine unbestätigte Importhistorie vorhanden.',noTargets:'Kein Zielkonto vorhanden.',
    target:'Zielkonto',currentBalance:'Aktueller Kontostand',optional:'optional',requiredBalance:'Für dieses Konto ist ein aktueller Kontostand erforderlich.',
    hasBalance:'Aktueller Kontostand ist bereits vorhanden.',link:'Verbinden',confirmHistory:'Historie bestätigen',
    linking:'Wird verbunden …',confirming:'Historie wird bestätigt …',linked:'Importhistorie verbunden.',
    confirmed:'Importhistorie bestätigt.',transactions:'Buchungen',period:'Zeitraum',iban:'IBAN-Endung',
    moved:'verschoben',merged:'zusammengeführt',trusted:'für Vermögenshistorie freigegeben',
    addMissing:'Fehlende Buchung ergänzen',inactive:'inaktiv',currencyMismatch:'Währung passt nicht zum Importkonto.'
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
    linkHeading:'Link imported accounts',
    linkHint:'Map imported history accounts to the real bank or FullWorth account. If the target has no balance, enter the current balance. Missing bookings can then be added under Transactions.',
    manageAccounts:'Connect or create bank/account',loadingLinks:'Loading accounts …',
    noPending:'No unconfirmed imported history.',noTargets:'No target account available.',
    target:'Target account',currentBalance:'Current balance',optional:'optional',requiredBalance:'A current balance is required for this account.',
    hasBalance:'A current balance already exists.',link:'Link',confirmHistory:'Confirm history',
    linking:'Linking …',confirming:'Confirming history …',linked:'Imported history linked.',
    confirmed:'Imported history confirmed.',transactions:'Transactions',period:'Period',iban:'IBAN suffix',
    moved:'moved',merged:'merged',trusted:'approved for wealth history',
    addMissing:'Add missing booking',inactive:'inactive',currencyMismatch:'Currency does not match the imported account.'
  }
}[lang];

document.documentElement.lang=lang;
for(const [id,key] of Object.entries({
  'import-subtitle':'subtitle','import-back':'back','import-heading':'heading','import-hint':'hint','import-space-label':'space',
  'import-safety-title':'safetyTitle','import-safety':'safety','import-file-label':'file','finanzguru-submit':'submit',
  'import-link-heading':'linkHeading','import-link-hint':'linkHint','import-link-accounts':'manageAccounts'
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
let space=null;
let linkOptions={importAccounts:[],targetAccounts:[],attachedHistory:[]};

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
function targetLabel(target){
  const parts=[target.institutionName,target.displayName].filter(Boolean);
  let label=[...new Set(parts)].join(' · ')||target.id;
  label+=` · ${target.currency}`;
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
  const add=node('a','ghost import-add-missing',text.addMissing);
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
    option.disabled=target.currency!==item.currency;
    if(target.id===(item.linkedTargetAccountId||item.suggestedTargetAccountId))option.selected=true;
    select.append(option);
  }
  if(!select.value){
    const compatible=linkOptions.targetAccounts.find(target=>target.currency===item.currency);
    if(compatible)select.value=compatible.id;
  }
  targetField.append(select);

  const balanceField=node('label','field import-balance-field');
  balanceField.append(node('span','',`${text.currentBalance} (${text.optional})`));
  const balance=node('input','');
  balance.type='number';balance.step='0.01';balance.inputMode='decimal';
  const balanceHint=node('span','row-sub import-balance-hint','');
  balanceField.append(balance,balanceHint);

  const button=node('button','primary-action',text.link);
  button.type='button';
  const sync=()=>balanceControl(currentTarget(select),balanceField,balance,balanceHint);
  select.addEventListener('change',sync);sync();

  button.addEventListener('click',async()=>{
    const target=currentTarget(select);
    if(!target)return;
    if(target.currency!==item.currency){linkStatus.textContent=text.currencyMismatch;return;}
    const raw=balance.value.trim();
    if(!target.hasCurrentBalance&&raw===''){linkStatus.textContent=text.requiredBalance;balance.focus();return;}
    button.disabled=true;select.disabled=true;balance.disabled=true;linkStatus.textContent=text.linking;
    try{
      const data=await sharedApi(
        `api/import/finanzguru/accounts/${encodeURIComponent(item.id)}/link?fullWorthSpaceId=${encodeURIComponent(space.id)}`,
        jsonBody({
          targetAccountId:target.id,
          currentBalance:raw===''?null:Number(raw),
          currentBalanceCurrency:target.currency
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
  card.append(controls);
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

  const button=node('button','primary-action',text.confirmHistory);
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

try{
  const spaces=await sharedApi('api/fullworth-spaces');
  const saved=localStorage.getItem('finance.space');
  space=spaces.find(item=>item.id===saved)||spaces[0]||null;
  document.getElementById('import-space').textContent=space?.name||'—';
  if(space)await renderLinkOptions();
}catch(error){
  console.error(error);
  status.textContent=text.error;
  submit.disabled=true;
}

form.addEventListener('submit',async event=>{
  event.preventDefault();
  const file=fileInput.files?.[0];
  if(!file||!space)return;
  if(!await confirmMessage({message:text.confirm,title:text.heading,confirmLabel:text.submit,cancelLabel:text.cancel}))return;
  submit.disabled=true;fileInput.disabled=true;status.textContent=text.working;result.hidden=true;result.innerHTML='';
  try{
    const uploadFile=window.financeFileUpload?.snapshot?await window.financeFileUpload.snapshot(file):file;
    const body=new FormData();body.append('file',uploadFile,uploadFile.name);
    const data=await sharedApi(`api/import/finanzguru?fullWorthSpaceId=${encodeURIComponent(space.id)}`,{method:'POST',body});
    const rows=[
      [text.rows,data.sourceRows],[text.imported,data.transactionsImported],[text.existing,data.alreadyImported],
      [text.matched,data.matchedExistingTransactions],[text.accounts,data.accountsMatched],[text.createdAccounts,data.accountsCreated],[text.splits,data.splitTransactions]
    ];
    result.innerHTML='';
    for(const [label,value] of rows){
      const row=node('div','row');
      const main=node('div','row-main');
      main.append(node('div','row-title',label));
      row.append(main,node('div','amount',String(value??0)));
      result.appendChild(row);
    }
    result.hidden=false;status.textContent=text.done;
    await renderLinkOptions();
  }catch(error){
    console.error(error);status.textContent=`${text.error} ${error.message||''}`.trim();
  }finally{
    submit.disabled=false;fileInput.disabled=false;
  }
});
