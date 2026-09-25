import { money, percent } from '../../components/money.js';
import { createDialog } from '../../components/dialog.js';
import { apiClient } from '../../core/services.js';
import { navigate } from '../../core/navigation.js';
import { spriteHref } from '../../components/sprite.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';

const $=(s,r=document)=>r.querySelector(s), $$=(s,r=document)=>[...r.querySelectorAll(s)];
const S={space:'',look:null,bundle:null,bundleAt:0,banks:null,unread:new Set(),hasUnread:false,unreadAt:0,groupMode:false,busy:false};
let actions={openAdd:()=>{}};
let bound=false;
const DEFAULT_COLOR='#334155', DEFAULT_BACKGROUND='#eef2f7';
const ICON={wallet:'ui-wallet',bank:'ui-bank',cash:'ui-cash',card:'ui-card',chart:'ui-chart',home:'ui-home',car:'ui-car',briefcase:'ui-briefcase',folder:'ui-folder',plus:'ui-plus',grip:'ui-grip',palette:'ui-palette',transactions:'ui-transactions',link:'ui-link',save:'ui-save',close:'ui-close',edit:'ui-edit'};
const T={de:{accounts:'Konten',transactions:'Buchungen',connections:'Bankverbindungen',accountsSub:'Konten, Gruppen und Bargeld verwalten',connectionsSub:'Bankzugänge verbinden, synchronisieren und erneuern',groups:'Gruppen',addAccount:'Konto hinzufügen',addConnection:'Bankverbindung hinzufügen',addGroup:'Gruppe hinzufügen',group:'Gruppe',newGroup:'Neue Gruppe',groupName:'Gruppenname',accountIcon:'Konto-Icon',editIcon:'Icon bearbeiten',icon:'Icon',color:'Icon-Farbe',background:'Hintergrund',save:'Speichern',saving:'Speichert…',cancel:'Abbrechen',close:'Schließen',ungrouped:'Ohne Gruppe',reorder:'Konten oder ganze Gruppen am Griff verschieben.',performance:'Performance',moveAccount:'Konto verschieben',moveGroup:'Gruppe verschieben',editGroup:'Gruppe bearbeiten',restoreDefault:'Standard wiederherstellen',newTx:'Neue Buchungen'},en:{accounts:'Accounts',transactions:'Transactions',connections:'Bank connections',accountsSub:'Manage accounts, groups and cash',connectionsSub:'Connect, sync and renew bank access',groups:'Groups',addAccount:'Add account',addConnection:'Add bank connection',addGroup:'Add group',group:'Group',newGroup:'New group',groupName:'Group name',accountIcon:'Account icon',editIcon:'Edit icon',icon:'Icon',color:'Icon color',background:'Background',save:'Save',saving:'Saving…',cancel:'Cancel',close:'Close',ungrouped:'Ungrouped',reorder:'Drag accounts or whole groups by the handle.',performance:'Performance',moveAccount:'Move account',moveGroup:'Move group',editGroup:'Edit group',restoreDefault:'Restore default',newTx:'New transactions'}};
const tr=()=>T[(document.documentElement.lang||'').startsWith('en')?'en':'de'];
const text=(el,v)=>{if(el&&el.textContent!==v)el.textContent=v};
const svg=(n,c='')=>`<svg class="${c}" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><use href="${spriteHref(ICON[n]||ICON.wallet)}"></use></svg>`;
const arr=(x,k)=>Array.isArray(x)?x:Array.isArray(x?.[k])?x[k]:Array.isArray(x?.items)?x.items:[];
const sid=()=>localStorage.getItem('finance.space')||'';

async function req(path,opt={},service='backend'){return service==='banking'?apiClient.banking(path,opt):apiClient.backend(path,opt)}
function reset(){const id=sid();if(S.space===id)return;S.space=id;S.look=null;S.bundle=null;S.bundleAt=0;S.banks=null;S.unread.clear();S.hasUnread=false;S.unreadAt=0}
// Aussehen und Ungelesen-Stand kommen aus /api/account-experience - EINE Antwort fuer beides.
//
// Bis #177 lag beides in drei Blobs unter api/preferences, und der dritte hielt bis zu 500
// Buchungs-Ids: diese Seite holte bei jedem Laden 500 Buchungen, nur um zu wissen, wo ein Punkt
// hingehoert. Der Server zaehlt das je Konto selbst. Die Tabellen dahinter gab es die ganze Zeit;
// sie hatten nur keinen Aufrufer. Die drei Schluessel stehen serverseitig nicht mehr in
// PreferenceStore.AllowedKeys - dort stehen sie namentlich, hier braucht es sie nicht mehr, und
// AccountsUxBaselineTests haelt genau diese Grenze fest.
async function looks(force=false){reset();if(!S.space)return S.look={accounts:new Map(),groups:new Map(),unseen:new Map()};if(S.look&&!force)return S.look;
  const [as,gs]=await Promise.all([req('api/account-experience').catch(()=>[]),req('api/account-experience/group-appearances').catch(()=>[])]);
  const look={accounts:new Map(),groups:new Map(),unseen:new Map()};
  for(const r of arr(as)){const id=String(r.accountId||'');if(!id)continue;look.accounts.set(id,{icon:r.icon||null,color:r.iconColor||null,background:r.backgroundColor||null});look.unseen.set(id,Number(r.unseenTransactions)||0)}
  for(const r of arr(gs)){const id=String(r.groupId||'');if(!id)continue;look.groups.set(id,{icon:r.icon||null,color:r.color||null,background:r.backgroundColor||null})}
  return S.look=look}
async function saveAccountLook(id,body){await req(`api/account-experience/${id}/appearance`,{method:'PUT',body:JSON.stringify(body)});S.look=null}
async function saveGroupLook(id,body){await req(`api/account-experience/groups/${id}/appearance`,{method:'PUT',body:JSON.stringify(body)});S.look=null}
async function bundle(force=false){reset();if(!S.space)return{accounts:[],connections:[],groups:[]};if(S.bundle&&!force&&Date.now()-S.bundleAt<2500)return S.bundle;
  // Cache failures too: on a transient error (e.g. a 429 from the BFF rate limiter) still stamp bundleAt
  // and return the last-known/empty bundle instead of throwing. Without this a failed fetch never caches,
  // so repeated refreshes could refetch and turn one 429 into a request storm.
  try{const [a,c,g]=await Promise.all([req('api/accounts'),req('api/bank-connections'),req('api/account-groups')]);S.bundle={accounts:arr(a),connections:arr(c),groups:arr(g)};S.bundleAt=Date.now();return S.bundle}
  // ASSIGN the fallback to S.bundle (not just return it): the 2.5s throttle guard above requires a truthy
  // S.bundle, so on a COLD-START failure (S.bundle still null) an un-assigned fallback would leave the
  // guard bypassed and every immediate retry would refetch — the exact storm this cache is meant to prevent.
  catch(e){console.error(e);S.bundleAt=Date.now();return S.bundle=(S.bundle||{accounts:[],connections:[],groups:[]})}}
function bankKey(v){const k=(v||'').toLowerCase().normalize('NFD').replace(/\p{Diacritic}/gu,'').replace(/\b(ag|se|gmbh|bank|deutschland|germany)\b/g,'').replace(/[^a-z0-9]/g,'');return k==='dkb'?'deutschekreditbank':k}
async function banks(){if(S.banks)return S.banks;try{S.banks=arr(await req('api/banking/institutions?country=DE',{},'banking'),'aspsps')}catch{S.banks=[]}return S.banks}
function matchBank(name,bs){const k=bankKey(name);if(!k)return null;return bs.find(x=>bankKey(x.name)===k)||bs.find(x=>{const y=bankKey(x.name);return y.length>2&&(y.includes(k)||k.includes(y))})||null}
function logo(b){const raw=b?.logo||b?.logoUrl||b?.logo_url||'';try{const u=new URL(raw,location.origin);return u.origin===location.origin||(u.protocol==='https:'&&(u.hostname==='enablebanking.com'||u.hostname.endsWith('.enablebanking.com')))?u.href:''}catch{return''}}
function stored(kind,id){const v=S.look?.[kind]?.get(String(id));return v&&(v.icon||v.color||v.background)?v:null}
function hasVisualOverride(kind,id){return!!stored(kind,id)}
function vis(kind,id,fallback){const v=stored(kind,id)||{};return{icon:v.icon||fallback,color:v.color||DEFAULT_COLOR,background:v.background||DEFAULT_BACKGROUND}}
function bankForAccount(a,bs,connections=[]){const conn=a?.bankConnectionId?(connections||[]).find(x=>String(x.id)===String(a.bankConnectionId)):null;return matchBank(conn?.institutionName,bs)||matchBank(a?.institutionName,bs)}
function accountIdentity(a,bs,connections=[]){const overridden=hasVisualOverride('accounts',a.id),v=vis('accounts',a.id,'wallet'),lg=logo(bankForAccount(a,bs,connections)),bankDefault=!!a.bankConnectionId||!!lg;return{node:overridden?identity(v):identity(bankDefault?{icon:'bank',color:'#334155',background:'#eef2f7'}:v,lg,bankDefault,a.institutionName||''),sig:`${a.id}|${overridden?'custom':'default'}|${lg}|${v.icon}|${v.color}|${v.background}`}}
function identity(v,bankLogo='',bankOnly=false,alt=''){const e=document.createElement('span');e.className=`account-identity-icon${bankOnly?' bank-only':''}`;e.style.color=v.color;e.style.background=v.background;if(!bankOnly)e.insertAdjacentHTML('beforeend',svg(v.icon));if(bankLogo){const i=document.createElement('img');i.src=bankLogo;i.alt=bankOnly?alt:'';i.loading='lazy';i.referrerPolicy='no-referrer';i.className=bankOnly?'account-bank-primary':'account-bank-badge';i.onerror=()=>i.remove();e.append(i)}else if(bankOnly)e.insertAdjacentHTML('beforeend',svg('bank'));return e}
function iconButton(b,icon,label){if(!b)return;b.classList.add('ux-icon-text-button');if(b.dataset.uxIcon!==icon||!$('.ux-button-label',b)){b.innerHTML=`${svg(icon,'ux-button-icon')}<span class="ux-button-label"></span>`;b.dataset.uxIcon=icon}text($('.ux-button-label',b),label);b.title=label;b.setAttribute('aria-label',label)}
// Die Kontenseite hat seit #125 keine zweite Navigation mehr: keine lokale Leiste, keine Unterseite
// /accounts/connections. Bankverbindungen sind eine Einstellung und stehen unter
// /settings/bank-connections.
//
// Ueberschrift und Hauptaktion setzte hier bis #154 eine eigene route()-Funktion. Beides gehoert
// inzwischen woanders hin: die Ueberschrift schreibt der Server (PageHeadings), die Hauptaktion
// meldet der Einstieg der Seite an. route() fragte ausserdem nach .active - einer Klasse der alten
// Huelle, die es nicht mehr gibt -, lief also ohnehin nie mehr. Wiederbelebt haette sie dem
// Einstieg ins Wort geredet.
function toolbar(){iconButton($('#add-group'),S.groupMode?'close':'grip',S.groupMode?tr().cancel:tr().groups);iconButton($('#add-account'),'plus',tr().addAccount)}
function perf(a){for(const k of['performancePercent','gainPercent','changePercent','returnPercent'])if(a?.[k]!=null&&Number.isFinite(Number(a[k])))return Number(a[k]);return null}
const invest=a=>/(depot|portfolio|investment|securities|broker|wertpapier)/i.test(`${a?.accountType||''} ${a?.product||''} ${a?.displayName||''}`);
function placeIdentity(row,a,bs,connections=[]){const main=$('.row-main,.fw-row-main',row);if(!main)return;const resolved=accountIdentity(a,bs,connections),sig=resolved.sig;let old=$('.account-identity-icon,.tx-ident-slot',row);if(!old||old.dataset.sig!==sig){const n=resolved.node;n.dataset.sig=sig;old?old.replaceWith(n):row.insertBefore(n,main)}}
async function decorateAccounts(b,bs,stagedList=null){if(S.groupMode&&!stagedList)return;const root=stagedList||$('#accounts-view-list');if(!root)return;const byId=new Map(b.accounts.filter(a=>a.isActive!==false).map(a=>[String(a.id),a]));for(const row of $$('.row[data-account-id]:not(.group-head)',root)){const a=byId.get(String(row.dataset.accountId||''));if(!a)continue;placeIdentity(row,a,bs,b.connections);const dot=$('.account-unread-dot',row);if(S.unread.has(String(a.id))&&!dot)$('.row-title',row)?.insertAdjacentHTML('beforeend',`<span class="account-unread-dot" aria-label="${tr().newTx}"></span>`);else if(!S.unread.has(String(a.id)))dot?.remove();const p=perf(a),old=$('.account-performance',row);if(invest(a)&&p!=null){const e=old||document.createElement('span'),cl=`account-performance ${p>0?'positive':p<0?'negative':''}`;if(e.className!==cl)e.className=cl;text(e,percent(p));e.title=tr().performance;if(!old)$('.row-end,.row-side',row)?.prepend(e)}else old?.remove()}decorateGroups(b.groups);ensureAddGroup(root)}
function decorateGroups(gs){const byId=new Map(gs.map(g=>[String(g.id),g]));for(const h of $$('#accounts-view-list .group-head[data-group-id]')){const gid=String(h.dataset.groupId||'');if(!gid)continue;const g=byId.get(gid);if(!g)continue;const tg=$('.group-toggle',h),v=vis('groups',g.id,'folder'),sig=`${g.id}|${v.icon}|${v.color}|${v.background}`,old=$('.group-visual-icon',tg);if(!old||old.dataset.sig!==sig){const n=document.createElement('span');n.className='group-visual-icon';n.dataset.sig=sig;n.style.color=v.color;n.style.background=v.background;n.innerHTML=svg(v.icon);old?old.replaceWith(n):tg.prepend(n)}const r=$('[data-rename]',h);if(r&&!r.dataset.ux){const button=r.cloneNode(true);button.dataset.ux='1';button.innerHTML=svg('edit');button.title=tr().editGroup;button.setAttribute('aria-label',tr().editGroup);button.onclick=e=>{e.preventDefault();e.stopPropagation();editGroup(g)};r.replaceWith(button)}}}
function ensureAddGroup(root){let b=$('.add-account-group-row',root);if(!b){b=document.createElement('button');b.type='button';b.className='add-account-group-row';b.innerHTML=`${svg('plus')}<span></span>`;b.onclick=()=>editGroup(null);root.append(b)}text($('span',b),tr().addGroup)}
const iconNames=['wallet','bank','cash','card','chart','home','car','briefcase','folder'];
function iconPicker(sel){const w=document.createElement('div');w.className='account-icon-picker';for(const n of iconNames){const b=document.createElement('button');b.type='button';b.className='account-icon-choice';b.dataset.icon=n;b.innerHTML=svg(n);b.title=n;b.setAttribute('aria-label',n);b.classList.toggle('selected',n===sel);b.onclick=()=>{$$('.account-icon-choice',w).forEach(x=>x.classList.remove('selected'));b.classList.add('selected')};w.append(b)}return w}
function fields(v){const r=document.createElement('div');r.className='account-visual-fields';const l=document.createElement('label');l.className='account-visual-field';l.innerHTML=`<span>${tr().icon}</span>`;l.append(iconPicker(v.icon));r.append(l);const c=document.createElement('div');c.className='account-color-fields';c.innerHTML=`<label><span>${tr().color}</span><input name="iconColor" type="color" value="${v.color}"></label><label><span>${tr().background}</span><input name="iconBackground" type="color" value="${v.background}"></label>`;r.append(c);return r}
function modal(title){const d=createDialog(`<form class="dialog-card account-ux-form"><div class="panel-head account-ux-dialog-head"><h3></h3><button type="button" data-close class="account-dialog-close" aria-label="${tr().close}"></button></div><div class="account-ux-dialog-body"></div><div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary,'account-dialog-cancel')}">${tr().cancel}</button><button type="submit" class="${buttonClass(ButtonRole.Primary,'account-dialog-save')}">${tr().save}</button></div></form>`,{className:'account-ux-dialog',closeLabel:tr().close});text($('h3',d),title);$('.account-dialog-cancel',d).onclick=()=>d.close();d.showModal();return d}
async function editAccount(a){await looks();const d=modal(`${tr().accountIcon}: ${a.displayName||a.institutionName||''}`);$('.account-ux-dialog-body',d).append(fields(vis('accounts',a.id,'wallet')));if(hasVisualOverride('accounts',a.id)){const reset=document.createElement('button');reset.type='button';reset.className=buttonClass(ButtonRole.Secondary,'account-reset-default');reset.textContent=tr().restoreDefault;reset.onclick=async()=>{reset.disabled=true;try{await saveAccountLook(a.id,{icon:null,iconColor:null,backgroundColor:null});d.close();reloadAccounts()}catch(err){console.error(err);reset.disabled=false}};$('.dialog-actions',d).prepend(reset)}$('form',d).onsubmit=async e=>{e.preventDefault();const s=$('.account-dialog-save',d);s.disabled=true;try{await saveAccountLook(a.id,{icon:$('.account-icon-choice.selected',d)?.dataset.icon||'wallet',iconColor:$('[name="iconColor"]',d).value,backgroundColor:$('[name="iconBackground"]',d).value});d.close();reloadAccounts()}catch(err){console.error(err);s.disabled=false}}}
function captureOrder(){return S.groupMode?$$('#accounts-view-list .account-group-edit-block').map(b=>({groupId:b.dataset.groupId||'',accounts:$$('.account-edit-row',b).map(r=>r.dataset.accountId)})):null}
function restoreOrder(snap){if(!snap)return;const root=$('#accounts-view-list'),blocks=new Map($$('.account-group-edit-block',root).map(b=>[b.dataset.groupId||'',b])),add=$('.add-account-group-row',root),rows=new Map($$('.account-edit-row',root).map(r=>[r.dataset.accountId,r]));for(const x of snap){const b=blocks.get(x.groupId);if(!b)continue;root.insertBefore(b,add);const l=$('.account-group-edit-accounts',b);for(const id of x.accounts)if(rows.has(id))l.append(rows.get(id))}}
async function editGroup(g){await looks();const snap=captureOrder(),d=modal(g?tr().group:tr().newGroup),body=$('.account-ux-dialog-body',d),lab=document.createElement('label');lab.className='account-visual-field';lab.innerHTML=`<span>${tr().groupName}</span><input name="groupName" type="text" maxlength="120" required>`;$('[name="groupName"]',lab).value=g?.name||'';body.append(lab,fields(g?vis('groups',g.id,'folder'):{icon:'folder',color:'#334155',background:'#eef2f7'}));$('form',d).onsubmit=async e=>{e.preventDefault();const s=$('.account-dialog-save',d);s.disabled=true;try{const name=$('[name="groupName"]',d).value.trim();let id=g?.id;if(g)await req(`api/account-groups/${g.id}`,{method:'PUT',body:JSON.stringify({name,sortOrder:g.sortOrder??0})});else{const bb=await bundle(true),so=Math.max(0,...bb.groups.map(x=>Number(x.sortOrder)||0))+100;id=(await req('api/account-groups',{method:'POST',body:JSON.stringify({name,sortOrder:so})}))?.id}if(id)await saveGroupLook(id,{icon:$('.account-icon-choice.selected',d)?.dataset.icon||'folder',color:$('[name="iconColor"]',d).value,backgroundColor:$('[name="iconBackground"]',d).value});S.bundleAt=0;d.close();if(S.groupMode){await renderGroupMode();restoreOrder(snap)}else reloadAccounts()}catch(err){console.error(err);s.disabled=false}}}
function reloadAccounts(){S.bundleAt=0;if(document.body.dataset.view==='accounts')return navigate('accounts',{replace:true,path:location.pathname.startsWith('/accounts')?location.pathname:'/accounts'})}
function editAccountRow(a,bs,connections=[]){const r=document.createElement('div');r.className='account-edit-row';r.dataset.accountId=a.id;const h=document.createElement('button');h.type='button';h.className='account-drag-handle';h.dataset.dragKind='account';h.innerHTML=svg('grip');h.title=tr().moveAccount;h.setAttribute('aria-label',tr().moveAccount);const m=document.createElement('div');m.className='account-edit-main';m.innerHTML='<strong></strong><span></span>';text($('strong',m),a.displayName||a.institutionName||'');text($('span',m),a.institutionName||'');const amt=document.createElement('span');amt.className='account-edit-balance mono';text(amt,a.latestBalance?money(a.latestBalance.amount,a.latestBalance.currency||a.currency||'EUR'):'—');r.append(h,accountIdentity(a,bs,connections).node,m,amt);return r}
function groupBlock(g,as,bs,connections=[],ung=false){const b=document.createElement('section');b.className=`account-group-edit-block${ung?' is-ungrouped':''}`;b.dataset.groupId=g?.id||'';const h=document.createElement('div');h.className='account-group-edit-head';if(ung)h.innerHTML='<span class="drag-handle-spacer"></span>';else{const d=document.createElement('button');d.type='button';d.className='account-drag-handle group-handle';d.dataset.dragKind='group';d.innerHTML=svg('grip');d.title=tr().moveGroup;d.setAttribute('aria-label',tr().moveGroup);h.append(d)}h.append(identity(g?vis('groups',g.id,'folder'):{icon:'folder',color:'#64748b',background:'#eef2f7'}));const n=document.createElement('strong');text(n,g?.name||tr().ungrouped);h.append(n);if(g){const e=document.createElement('button');e.type='button';e.className=buttonClass(ButtonRole.Icon);e.innerHTML=svg('edit');e.title=tr().editGroup;e.setAttribute('aria-label',tr().editGroup);e.onclick=()=>editGroup(g);h.append(e)}const l=document.createElement('div');l.className='account-group-edit-accounts';as.sort((a,z)=>(Number(a.sortOrder)||0)-(Number(z.sortOrder)||0)||(a.displayName||'').localeCompare(z.displayName||'')).forEach(a=>l.append(editAccountRow(a,bs,connections)));b.append(h,l);return b}
async function renderGroupMode(){const root=$('#accounts-view-list');if(!root||!S.groupMode)return;const [bb,bs]=await Promise.all([bundle(true),banks()]),as=bb.accounts.filter(a=>a.isActive!==false),gs=[...bb.groups].sort((a,z)=>(Number(a.sortOrder)||0)-(Number(z.sortOrder)||0)||a.name.localeCompare(z.name));root.replaceChildren();for(const g of gs)root.append(groupBlock(g,as.filter(a=>String(a.groupId||'')===String(g.id)),bs,bb.connections));root.append(groupBlock(null,as.filter(a=>!a.groupId),bs,bb.connections,true));const add=document.createElement('button');add.type='button';add.className='add-account-group-row';add.innerHTML=`${svg('plus')}<span>${tr().addGroup}</span>`;add.onclick=()=>editGroup(null);root.append(add);let bar=$('#account-group-savebar');if(!bar){bar=document.createElement('div');bar.id='account-group-savebar';bar.className='account-group-savebar';bar.innerHTML=`<span class="account-group-save-hint">${tr().reorder}</span><div><button type="button" class="${buttonClass(ButtonRole.Secondary,'group-edit-cancel')}">${tr().cancel}</button><button type="button" class="${buttonClass(ButtonRole.Primary,'group-edit-save')}">${svg('save')}<span>${tr().save}</span></button></div>`;root.after(bar);$('.group-edit-cancel',bar).onclick=()=>leaveGroups(false);$('.group-edit-save',bar).onclick=saveGroups}drag(root)}
function drag(root){if(root.dataset.dragReady)return;root.dataset.dragReady='1';let d=null;const end=()=>{d?.item.classList.remove('is-dragging');d=null};root.addEventListener('pointerdown',e=>{const h=e.target.closest('[data-drag-kind]');if(!h)return;const kind=h.dataset.dragKind,item=kind==='group'?h.closest('.account-group-edit-block'):h.closest('.account-edit-row');if(!item)return;e.preventDefault();h.setPointerCapture?.(e.pointerId);d={kind,item,id:e.pointerId};item.classList.add('is-dragging')});root.addEventListener('pointermove',e=>{if(!d||d.id!==e.pointerId)return;if(e.clientY<72)scrollBy(0,-18);else if(e.clientY>innerHeight-92)scrollBy(0,18);const hit=document.elementFromPoint(e.clientX,e.clientY);if(!hit)return;if(d.kind==='group'){const tar=hit.closest('.account-group-edit-block:not(.is-ungrouped)');if(!tar||tar===d.item)return;const r=tar.getBoundingClientRect();root.insertBefore(d.item,e.clientY<r.top+r.height/2?tar:tar.nextSibling)}else{const block=hit.closest('.account-group-edit-block');if(!block)return;const l=$('.account-group-edit-accounts',block),tar=hit.closest('.account-edit-row');if(tar&&tar!==d.item){const r=tar.getBoundingClientRect();l.insertBefore(d.item,e.clientY<r.top+r.height/2?tar:tar.nextSibling)}else if(!tar&&l!==d.item.parentElement)l.append(d.item)}});root.addEventListener('pointerup',end);root.addEventListener('pointercancel',end)}
// Eine Anfrage fuer die ganze Neuordnung.
//
// Vorher war es eine je verschobener Gruppe, eine je umgehaengtem Konto und noch eine je neuer
// Position - bei zwanzig Konten also gut dreissig Aufrufe nebeneinander, von denen jeder einzeln
// scheitern konnte. Wer dabei die Verbindung verlor, hatte anschliessend eine halbe Reihenfolge:
// zwei Konten in der neuen Gruppe, der Rest in der alten. /api/account-experience/reorder macht
// alles in einer Transaktion - entweder die neue Ordnung steht, oder die alte steht noch.
async function saveGroups(){const btn=$('.group-edit-save');if(!btn||btn.disabled)return;btn.disabled=true;text($('span',btn),tr().saving);
  try{const groups=[],accounts=[];let gi=0;
    for(const block of $$('#accounts-view-list .account-group-edit-block')){const gid=block.dataset.groupId||null;
      if(gid)groups.push({groupId:gid,sortOrder:(++gi)*100});
      $$('.account-edit-row',block).forEach((r,i)=>accounts.push({accountId:r.dataset.accountId,groupId:gid,sortOrder:(i+1)*100}))}
    await req('api/account-experience/reorder',{method:'POST',body:JSON.stringify({groups,accounts})});
    S.bundleAt=0;await leaveGroups(true)}
  catch(e){console.error(e);btn.disabled=false;text($('span',btn),tr().save)}}
async function enterGroups(){if(S.groupMode)return;if(location.pathname.replace(/\/+$/,'')==='/accounts/connections')await navigate('accounts',{replace:true,path:'/accounts'});S.groupMode=true;document.body.classList.add('account-group-editing');toolbar();await looks();await renderGroupMode()}
async function leaveGroups(saved){S.groupMode=false;document.body.classList.remove('account-group-editing');$('#account-group-savebar')?.remove();toolbar();if(saved)S.bundleAt=0;reloadAccounts()}
async function unread(force=false){reset();if(!S.space)return;if(!force&&Date.now()-S.unreadAt<5000)return applyUnread();S.unreadAt=Date.now();
  try{const look=await looks(force);S.unread=new Set([...look.unseen].filter(([,n])=>n>0).map(([id])=>id));S.hasUnread=S.unread.size>0;applyUnread()}catch(e){console.error(e)}}
// Gesehen wird je Konto vermerkt, nicht global. Vorher stand im Blob eine Liste der zuletzt
// bekannten Buchungs-Ids; wer ein Konto oeffnete, quittierte damit auch jedes andere.
async function markSeen(){try{const look=await looks(true),ids=[...look.unseen].filter(([,n])=>n>0).map(([id])=>id);
  if(ids.length)await Promise.all(ids.map(id=>req(`api/account-experience/${id}/seen`,{method:'POST',body:'{}'})));
  S.look=null;S.unread.clear();S.hasUnread=false;S.unreadAt=Date.now();applyUnread()}catch(e){console.error(e)}}
function applyUnread(){for(const b of [...$$('#nav [data-view="transactions"]'),...$$('#bottom-nav [data-view="transactions"]')]){const d=$('.nav-unread-dot',b);if(S.hasUnread&&!d)b.insertAdjacentHTML('beforeend',`<span class="nav-unread-dot" aria-label="${tr().newTx}"></span>`);else if(!S.hasUnread)d?.remove()}}
function manualDialog(target=null){const dialogs=target?[target]:$$('dialog:not([data-ux-manual])');for(const d of dialogs){const f=$('form',d),n=f?.querySelector('[name="name"]'),i=f?.querySelector('[name="institution"]'),bal=f?.querySelector('[name="balance"]');if(!f||!n||!i||!bal)continue;d.dataset.uxManual='1';const fs=fields({icon:'wallet',color:'#334155',background:'#eef2f7'});$('.dialog-actions',f)?.before(fs);f.addEventListener('submit',()=>{sessionStorage.setItem('finance.pending-manual-visual',JSON.stringify({icon:$('.account-icon-choice.selected',fs)?.dataset.icon||'wallet',color:$('[name="iconColor"]',fs).value,background:$('[name="iconBackground"]',fs).value,at:Date.now()}))},{capture:true,once:true})}}
async function applyManualCreated(a){if(!a?.id)return;const raw=sessionStorage.getItem('finance.pending-manual-visual');if(!raw)return;let p;try{p=JSON.parse(raw)}catch{return}if(!p||Date.now()-Number(p.at||0)>15000){sessionStorage.removeItem('finance.pending-manual-visual');return}await saveAccountLook(a.id,{icon:p.icon,iconColor:p.color,backgroundColor:p.background});sessionStorage.removeItem('finance.pending-manual-visual');S.bundleAt=0;}
// stagedList: die Kontenliste, solange sie noch nicht im Dokument hängt. Sie dort zu schmücken
// heißt, dass der Benutzer die undekorierte Fassung nie sieht - und genau die war der Sprung: eine
// Zeile wuchs von 73 auf 125 Pixel, sobald ihr Symbol und ihre Knöpfe dazukamen.
async function enhance(stagedList=null){if(S.busy)return;S.busy=true;try{reset();toolbar();if(S.space){await looks();const [bb,bs]=await Promise.all([bundle(),banks()]);if(stagedList||(document.body.dataset.view==='accounts'&&!S.groupMode)){await decorateAccounts(bb,bs,stagedList)}}await unread()}finally{S.busy=false}}

export function bindAccountsPresentation(nextActions = {}) {
  actions = { ...actions, ...nextActions };
  if (bound) return;
  bound = true;

  // Bis #154 kam hier ein Ereignis, wenn die Huelle die Ansicht wechselte. Ein Wechsel ist jetzt eine
  // echte Navigation: dieses Modul laeuft dabei ohnehin neu, und was zu tun ist, haengt nur noch
  // daran, welche Seite gerade offen ist. Der Zuhoerer war seit der letzten migrierten Seite tot -
  // es gab niemanden mehr, der das Ereignis abschickt.
  if (document.body.dataset.view === 'transactions') markSeen().catch(console.error);
  else unread().catch(console.error);
}

// Das Kontosymbol auch ausserhalb der Liste - auf der Kontodetailseite steht dieselbe Sache und
// soll nicht zweimal gebaut werden. Das Element braucht nur ein .row-main, davor kommt das Symbol.
export async function decorateAccountIdentity(element, account) {
  if (!element || !account) return;
  reset();
  if (!S.space) return;
  await looks();
  const [b, bs] = await Promise.all([bundle(), banks()]);
  placeIdentity(element, account, bs, b.connections);
}

export async function enhanceAccountsPresentation(stagedList = null) {
  return enhance(stagedList);
}

export async function toggleAccountGroupEditing() {
  if (S.groupMode) return leaveGroups(false);
  return enterGroups();
}

export function decorateManualAccountDialog(dialog) {
  manualDialog(dialog || null);
}

export async function applyManualAccountVisual(account) {
  return applyManualCreated(account);
}

export async function editAccountVisualById(accountId) {
  const id = String(accountId || '');
  if (!id) return;
  const bb = await bundle(true);
  const account = bb.accounts.find(item => String(item.id) === id);
  if (account) await editAccount(account);
}
