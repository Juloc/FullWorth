const $=(s,r=document)=>r.querySelector(s);
const $$=(s,r=document)=>[...r.querySelectorAll(s)];
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const sid=()=>localStorage.getItem('finance.space')||'';
const en=()=>document.documentElement.lang?.startsWith('en');
const t=(de,enText)=>en()?enText:de;
function withSpace(path){const [base,q='']=path.split('?');const p=new URLSearchParams(q);if(sid()&&!p.has('fullWorthSpaceId'))p.set('fullWorthSpaceId',sid());return `${base}?${p}`}
async function api(path,opt){const r=await fetch(`/bff/backend/${withSpace(path.replace(/^\//,''))}`,opt);if(!r.ok){let m=`${r.status}`;try{const b=await r.json();m=b.error||b.title||m}catch{}const e=new Error(m);e.status=r.status;throw e}if(r.status===204)return null;return r.json()}
const json=(method,body)=>({method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
function toast(message){const el=$('#toast');if(!el)return;el.textContent=message;el.classList.add('show');clearTimeout(toast.timer);toast.timer=setTimeout(()=>el.classList.remove('show'),3500)}
function ensureCss(){if(document.querySelector('link[data-atb-css]'))return;const l=document.createElement('link');l.rel='stylesheet';l.href='/advanced-transaction-bulk.css';l.dataset.atbCss='1';document.head.appendChild(l)}ensureCss();

function currentFilter(){
  const flag=$('#tx-flags')?.value||'';
  return {
    query:$('#tx-query')?.value.trim()||null,
    direction:$('#tx-direction')?.value||null,
    status:flag==='pending'?'PDNG':null,
    includeIgnored:flag==='ignored',
    isIgnored:flag==='ignored'?true:null,
    transfersOnly:flag==='transfers',
    reviewState:flag==='needs_review'?'needs_review':flag==='reviewed'?'reviewed':null,
    tagId:$('#ci-tag-filter')?.value||null
  };
}

function selectedIds(){return $$('.ci-select-tx:checked').map(box=>box.closest('tr[data-tx-id]')?.dataset.txId).filter(Boolean)}

async function openBulk(mode){
  const ids=mode==='selected'?selectedIds():null;
  if(mode==='selected'&&!ids.length){toast(t('Keine Buchungen ausgewählt.','No transactions selected.'));return}
  const base={filter:mode==='all'?currentFilter():null,transactionIds:mode==='selected'?ids:null,expectedCount:0,confirmSelection:false};
  let preview;
  try{preview=await api('api/transaction-bulk/advanced-preview',json('POST',base))}catch(e){toast(e.message);return}
  if(!preview.count){toast(t('Keine schreibbaren Buchungen entsprechen der Auswahl.','No writable transactions match the selection.'));return}

  let refs;
  try{refs=await Promise.all([api('api/categories'),api('api/category-intelligence/tags'),api('api/contracts'),api('api/access/effective').catch(()=>({capabilities:{}}))])}catch(e){toast(e.message);return}
  const [categories,tags,contracts,access]=refs;
  const canContracts=!!access.capabilities?.['contracts.manage'];

  const d=document.createElement('dialog');d.className='atb-dialog';d.innerHTML=`<form class="atb-card"><div class="atb-head"><div><h2>${esc(mode==='all'?t('Alle Treffer bearbeiten','Edit all matches'):t('Auswahl erweitert bearbeiten','Advanced edit selection'))}</h2><p>${preview.count} ${esc(t('Buchungen werden serverseitig neu geprüft.','transactions will be revalidated on the server.'))}</p></div><button type="button" data-close aria-label="${esc(t('Schließen','Close'))}">×</button></div><div class="atb-preview"><strong>${preview.count} ${esc(t('betroffen','affected'))}</strong><div class="atb-sample">${(preview.sample||[]).map(x=>`<span>${esc(x.date||'')} · ${esc(x.counterparty||x.description||'—')} · ${Number(x.amount).toLocaleString(en()?'en-US':'de-DE',{minimumFractionDigits:2,maximumFractionDigits:2})} ${esc(x.currency)}</span>`).join('')}</div></div><label>${esc(t('Aktion','Action'))}<select name="action"><option value="category">${esc(t('Kategorie setzen / entfernen','Set / clear category'))}</option><option value="reviewed">${esc(t('Als geprüft markieren','Mark reviewed'))}</option><option value="unreviewed">${esc(t('Als ungeprüft markieren','Mark unreviewed'))}</option><option value="exclude">${esc(t('Aus Statistiken ausschließen','Exclude from statistics'))}</option><option value="include">${esc(t('In Statistiken einbeziehen','Include in statistics'))}</option><option value="tag-add">${esc(t('Tag hinzufügen','Add tag'))}</option><option value="tag-remove">${esc(t('Tag entfernen','Remove tag'))}</option>${canContracts?`<option value="contract-link">${esc(t('Mit Vertrag verknüpfen','Link to contract'))}</option><option value="contract-unlink">${esc(t('Vertragsverknüpfung entfernen','Remove contract link'))}</option>`:''}<option value="note">${esc(t('Notizen ersetzen','Replace notes'))}</option>${preview.canPairTransfer?`<option value="transfer">${esc(t('Als Transferpaar verknüpfen','Pair as transfer'))}</option>`:''}</select></label><div data-action-fields></div><label class="atb-confirm"><input name="confirm" type="checkbox" required> <span>${esc(t(`Ich bestätige die Änderung an ${preview.count} Buchungen.`,`I confirm the change to ${preview.count} transactions.`))}</span></label><div class="atb-actions"><button type="button" class="ghost" data-cancel>${esc(t('Abbrechen','Cancel'))}</button><button type="submit">${esc(t('Änderung anwenden','Apply change'))}</button></div></form>`;document.body.appendChild(d);
  const form=$('form',d),action=$('[name="action"]',d),fields=$('[data-action-fields]',d);
  const renderFields=()=>{
    const a=action.value;
    if(a==='category')fields.innerHTML=`<label>${esc(t('Kategorie','Category'))}<select name="category"><option value="">${esc(t('Nicht kategorisiert','Uncategorized'))}</option>${(categories||[]).filter(c=>!c.isArchived).map(c=>`<option value="${c.id}">${esc(c.name)}</option>`).join('')}</select></label>`;
    else if(a==='tag-add'||a==='tag-remove')fields.innerHTML=`<label>${esc(t('Tag','Tag'))}<select name="tag" required><option value="">${esc(t('Auswählen…','Choose…'))}</option>${(tags||[]).map(tag=>`<option value="${tag.id}">${esc(tag.name)}</option>`).join('')}</select></label>`;
    else if(a==='contract-link'||a==='contract-unlink')fields.innerHTML=`<label>${esc(t('Vertrag','Contract'))}<select name="contract" required><option value="">${esc(t('Auswählen…','Choose…'))}</option>${(contracts||[]).filter(c=>c.isActive).map(c=>`<option value="${c.id}">${esc(c.name)}</option>`).join('')}</select></label>${a==='contract-link'?`<p class="atb-note">${esc(t('Nur Ausgaben ohne bestehende Vertragszuordnung. Der volle Buchungsbetrag wird als Zahlung verknüpft.','Only expenses without an existing contract allocation. The full transaction amount is linked as the payment.'))}</p>`:''}`;
    else if(a==='note')fields.innerHTML=`<label>${esc(t('Neue Notiz','New note'))}<textarea name="note" maxlength="2000" rows="4" placeholder="${esc(t('Leer lassen, um Notizen zu entfernen','Leave empty to clear notes'))}"></textarea></label><label class="atb-danger"><input name="confirmNotes" type="checkbox" required> <span>${esc(t('Bestehende Notizen aller ausgewählten Buchungen werden ersetzt.','Existing notes on all selected transactions will be replaced.'))}</span></label>`;
    else if(a==='transfer')fields.innerHTML=`<p class="atb-note">${esc(t('FullWorth hat genau zwei sichere Gegenbuchungen erkannt: unterschiedliche Konten, gleicher Betrag/Währung, entgegengesetztes Vorzeichen und maximal 3 Tage Abstand.','FullWorth detected exactly two safe opposite legs: different accounts, equal amount/currency, opposite signs and no more than 3 days apart.'))}</p>`;
    else fields.innerHTML='';
  };
  renderFields();action.onchange=renderFields;
  $('[data-close]',d).onclick=$('[data-cancel]',d).onclick=()=>d.close();d.addEventListener('close',()=>d.remove());
  form.onsubmit=async e=>{
    e.preventDefault();const a=action.value;const payload={...base,expectedCount:preview.count,confirmSelection:true};
    if(a==='category'){payload.updateCategory=true;payload.categoryId=$('[name="category"]',fields)?.value||null;payload.isReviewed=true}
    else if(a==='reviewed')payload.isReviewed=true;
    else if(a==='unreviewed')payload.isReviewed=false;
    else if(a==='exclude')payload.isIgnored=true;
    else if(a==='include')payload.isIgnored=false;
    else if(a==='tag-add')payload.addTagIds=[$('[name="tag"]',fields).value];
    else if(a==='tag-remove')payload.removeTagIds=[$('[name="tag"]',fields).value];
    else if(a==='contract-link'){payload.contractAction='link';payload.contractId=$('[name="contract"]',fields).value}
    else if(a==='contract-unlink'){payload.contractAction='unlink';payload.contractId=$('[name="contract"]',fields).value}
    else if(a==='note'){payload.replaceNotes=true;payload.note=$('[name="note"]',fields).value||null;payload.confirmReplaceNotes=$('[name="confirmNotes"]',fields).checked}
    else if(a==='transfer')payload.pairAsTransfer=true;
    const submit=$('button[type="submit"]',form);submit.disabled=true;
    try{const result=await api('api/transaction-bulk/apply',json('POST',payload));toast(t(`${result.changed} Buchungen geändert.`,`${result.changed} transactions changed.`));d.close();$('#tx-apply')?.click()}catch(err){toast(err.message);submit.disabled=false}
  };
  d.showModal();
}

function install(){
  const toolbar=$('#view-transactions .toolbar');
  if(toolbar&&!toolbar.querySelector('[data-atb-all]')){const b=document.createElement('button');b.type='button';b.className='ghost';b.dataset.atbAll='1';b.textContent=t('Alle Treffer bearbeiten','Edit all matches');b.onclick=()=>openBulk('all');toolbar.appendChild(b)}
  const bulk=$('#ci-bulkbar');
  if(bulk&&!bulk.querySelector('[data-atb-selected]')){const b=document.createElement('button');b.type='button';b.className='ghost';b.dataset.atbSelected='1';b.textContent=t('Erweitert','Advanced');b.onclick=()=>openBulk('selected');const clear=bulk.querySelector('[data-ci-clear]');bulk.insertBefore(b,clear||null)}
}
function boot(){install();new MutationObserver(()=>{clearTimeout(boot.timer);boot.timer=setTimeout(install,100)}).observe(document.body,{subtree:true,childList:true})}boot();
