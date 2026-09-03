const lang=(localStorage.getItem('finance.language')||'de').startsWith('en')?'en':'de';
const text={
  de:{subtitle:'Historische Buchungen aus Finanzguru übernehmen.',back:'Zurück',heading:'Alle Buchungen importieren',hint:'Wähle den Finanzguru-Export „Alle Buchungen“ im .xlsx-Format.',space:'FullWorth Space',safetyTitle:'Sicherer Import',safety:'Wiederholte Imports werden über die Buchungs-ID erkannt. Eindeutig passende Live-Konten werden verwendet; sonst entsteht ein Historienkonto ohne Einfluss auf den aktuellen Nettovermögenswert.',file:'Finanzguru .xlsx Export',submit:'Importieren',working:'Import läuft …',confirm:'Diese Datei jetzt importieren?',done:'Import abgeschlossen.',error:'Import fehlgeschlagen.',rows:'Quellzeilen',imported:'Neue Buchungen',existing:'Bereits importiert',matched:'Mit bestehenden Buchungen abgeglichen',accounts:'Konten zugeordnet',createdAccounts:'Historienkonten erstellt',splits:'Split-Buchungen'},
  en:{subtitle:'Import historical transactions from Finanzguru.',back:'Back',heading:'Import all transactions',hint:'Select the Finanzguru “Alle Buchungen” export in .xlsx format.',space:'FullWorth Space',safetyTitle:'Safe import',safety:'Repeated imports are detected by booking ID. Unambiguous live accounts are reused; otherwise a history-only account is created without affecting current net worth.',file:'Finanzguru .xlsx export',submit:'Import',working:'Importing …',confirm:'Import this file now?',done:'Import completed.',error:'Import failed.',rows:'Source rows',imported:'New transactions',existing:'Already imported',matched:'Matched existing transactions',accounts:'Accounts matched',createdAccounts:'History accounts created',splits:'Split transactions'}
}[lang];

document.documentElement.lang=lang;
for(const [id,key] of Object.entries({
  'import-subtitle':'subtitle','import-back':'back','import-heading':'heading','import-hint':'hint','import-space-label':'space',
  'import-safety-title':'safetyTitle','import-safety':'safety','import-file-label':'file','finanzguru-submit':'submit'
})) document.getElementById(id).textContent=text[key];

const form=document.getElementById('finanzguru-form');
const fileInput=document.getElementById('finanzguru-file');
const submit=document.getElementById('finanzguru-submit');
const status=document.getElementById('import-status');
const result=document.getElementById('import-result');
let space=null;

try{
  const response=await fetch('/bff/backend/api/fullworth-spaces');
  if(!response.ok)throw new Error(String(response.status));
  const spaces=await response.json();
  const saved=localStorage.getItem('finance.space');
  space=spaces.find(item=>item.id===saved)||spaces[0]||null;
  document.getElementById('import-space').textContent=space?.name||'—';
}catch(error){
  console.error(error);
  status.textContent=text.error;
  submit.disabled=true;
}

form.addEventListener('submit',async event=>{
  event.preventDefault();
  const file=fileInput.files?.[0];
  if(!file||!space)return;
  if(!window.confirm(text.confirm))return;
  submit.disabled=true;fileInput.disabled=true;status.textContent=text.working;result.hidden=true;result.innerHTML='';
  try{
    const body=new FormData();body.append('file',file,file.name);
    const response=await fetch(`/bff/backend/api/import/finanzguru?fullWorthSpaceId=${encodeURIComponent(space.id)}`,{method:'POST',body});
    if(!response.ok){let message=`${response.status}`;try{const data=await response.json();message=data.error||data.title||message}catch{}throw new Error(message)}
    const data=await response.json();
    const rows=[
      [text.rows,data.sourceRows],[text.imported,data.transactionsImported],[text.existing,data.alreadyImported],
      [text.matched,data.matchedExistingTransactions],[text.accounts,data.accountsMatched],[text.createdAccounts,data.accountsCreated],[text.splits,data.splitTransactions]
    ];
    result.innerHTML='';
    for(const [label,value] of rows){const row=document.createElement('div');row.className='row';const main=document.createElement('div');main.className='row-main';const title=document.createElement('div');title.className='row-title';title.textContent=label;const amount=document.createElement('div');amount.className='amount';amount.textContent=String(value??0);main.appendChild(title);row.append(main,amount);result.appendChild(row)}
    result.hidden=false;status.textContent=text.done;
  }catch(error){console.error(error);status.textContent=`${text.error} ${error.message||''}`.trim()}
  finally{submit.disabled=false;fileInput.disabled=false}
});
