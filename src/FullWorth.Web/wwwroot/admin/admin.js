import { confirmMessage } from '../ui/confirm.js';
import { createDialog } from '../ui/dialog.js';
import { secureFetch } from '../security/secure-fetch.js';
const state={offset:0,limit:50,total:0,search:'',status:'',detail:null};
const $=s=>document.querySelector(s);
const esc=v=>String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
const dt=v=>v?new Intl.DateTimeFormat(undefined,{dateStyle:'medium',timeStyle:'short'}).format(new Date(v)):'—';

async function request(path,options){
  const response=await secureFetch(path,options);
  if(response.status===403){location.assign('/');throw new Error('forbidden')}
  if(!response.ok){
    let error=String(response.status);
    try{error=(await response.json()).error||error}catch{}
    throw new Error(error);
  }
  return response.status===204?null:response.json();
}

function toast(text){
  const el=$('#admin-toast');el.textContent=text;el.classList.add('show');
  clearTimeout(toast.t);toast.t=setTimeout(()=>el.classList.remove('show'),2500);
}

// Secrets travel one way. The form never receives a stored value - it only learns THAT one is
// stored - so an empty field means "leave it alone" rather than "delete it". Without that, saving a
// changed client id would silently wipe the secret every single time.
const PROVIDERS='/auth/admin/external-providers';

function describeProviders(view){
  const google=view.googleClientId&&view.googleClientSecretStored;
  const apple=view.appleServiceId&&view.appleTeamId&&view.applePrivateKeyId&&view.applePrivateKeyStored;
  const on=[google?'Google':null,apple?'Apple':null].filter(Boolean);
  return on.length?`Aktiv: ${on.join(' · ')}`:'Kein Anbieter eingerichtet';
}

async function loadProviders(){
  const form=$('#providers-form');
  if(!form)return;
  const view=await request(PROVIDERS);
  form.googleClientId.value=view.googleClientId||'';
  form.appleServiceId.value=view.appleServiceId||'';
  form.appleTeamId.value=view.appleTeamId||'';
  form.applePrivateKeyId.value=view.applePrivateKeyId||'';
  form.googleClientSecret.placeholder=view.googleClientSecretStored?'gespeichert – leer lassen':'nicht gesetzt';
  form.applePrivateKey.placeholder=view.applePrivateKeyStored?'gespeichert – leer lassen':'nicht gesetzt';
  $('#providers-state').textContent=describeProviders(view);
}

function bindProviders(){
  const form=$('#providers-form');
  if(!form)return;
  form.addEventListener('submit',async event=>{
    event.preventDefault();
    // null, not '': an untouched secret field must not clear the stored one.
    const secret=value=>value===''?null:value;
    try{
      const saved=await request(PROVIDERS,{method:'PUT',headers:{'Content-Type':'application/json'},
        body:JSON.stringify({
          googleClientId:form.googleClientId.value,
          googleClientSecret:secret(form.googleClientSecret.value),
          appleServiceId:form.appleServiceId.value,
          appleTeamId:form.appleTeamId.value,
          applePrivateKeyId:form.applePrivateKeyId.value,
          applePrivateKey:secret(form.applePrivateKey.value)
        })});
      form.googleClientSecret.value='';
      form.applePrivateKey.value='';
      $('#providers-state').textContent=describeProviders(saved);
      toast('Anmeldeanbieter gespeichert');
    }catch(err){toast(err.message||'Fehler')}
  });
}

async function loadOverview(){
  const o=await request('/auth/admin/overview');
  $('#metric-users').textContent=o.users;
  $('#metric-active').textContent=o.active;
  $('#metric-disabled').textContent=o.disabled;
  $('#metric-deleting').textContent=o.pendingDeletion;
  $('#metric-failed').textContent=o.failedDeletion;
  $('#metric-admins').textContent=o.admins;
}

// The shared .chip (app.css) with an admin tone, so the states follow the app's colours instead of a
// second badge component that has to be kept in sync by hand.
function statusChip(u){
  if(u.deletionRequestedAt)return '<span class="chip admin-chip-danger">Löschung</span>';
  if(u.isDisabled)return '<span class="chip admin-chip-warn">Gesperrt</span>';
  return '<span class="chip">Aktiv</span>';
}

async function loadUsers(){
  const params=new URLSearchParams({offset:String(state.offset),limit:String(state.limit)});
  if(state.search)params.set('search',state.search);
  if(state.status)params.set('status',state.status);
  const page=await request('/auth/admin/users?'+params);
  state.total=page.total;
  const list=$('#users');
  if(!page.items.length){
    list.innerHTML='<div class="admin-user"><div class="row-sub">Keine User gefunden.</div></div>';
  }else{
    list.innerHTML=page.items.map(u=>`
      <button class="admin-user" type="button" data-user="${u.id}">
        <div><div class="admin-user-email">${esc(u.email)}</div><div class="admin-sub">${esc(u.id)}</div></div>
        <div>${u.isAdmin?'<span class="chip admin-chip-admin">Admin</span>':'<span class="row-sub">User</span>'}</div>
        <div>${statusChip(u)}</div>
        <div><span class="chip">${u.activeSessionCount} Session${u.activeSessionCount===1?'':'s'}</span></div>
        <div aria-hidden="true">›</div>
      </button>`).join('');
    list.querySelectorAll('[data-user]').forEach(button=>button.addEventListener('click',()=>openUser(button.dataset.user)));
  }
  const from=state.total?state.offset+1:0;
  const to=Math.min(state.offset+state.limit,state.total);
  $('#page-info').textContent=`${from}–${to} von ${state.total}`;
  $('#prev').disabled=state.offset===0;
  $('#next').disabled=state.offset+state.limit>=state.total;
}

async function refresh(){
  // The provider panel is loaded alongside, and its failure must not take the user list with it: an
  // admin who cannot see their users because a settings panel threw is worse off than before.
  await Promise.all([loadOverview(),loadUsers(),loadProviders().catch(()=>{})]);
}

let detailDialog=null;

async function openUser(id){
  const detail=await request('/auth/admin/users/'+encodeURIComponent(id));
  state.detail=detail;
  const u=detail.user;

  // createDialog gives this page the app's dialog behaviour: the generated close button and the
  // full-screen phone treatment. The page used to declare a static <dialog> and style it itself,
  // which is exactly why it had neither. No mobileMode: 'sheet' is the OPT-OUT of the full-screen
  // treatment (dialogs.css keys it on :not(.fw-dialog--sheet)) and a user detail is not a sheet.
  if(detailDialog?.open)detailDialog.close();
  detailDialog=createDialog(`
    <div class="dialog-card">
      <div class="panel-head"><div><span class="admin-eyebrow">User</span><h2>${esc(u.email)}</h2></div></div>
      <div id="detail-meta" class="admin-detail-grid"></div>
      <section><h3>Sessions</h3><div id="detail-sessions" class="admin-sessions"></div></section>
      <section><h3>Aktionen</h3><div id="detail-actions" class="admin-actions"></div></section>
      <p id="detail-error" class="admin-error" hidden></p>
    </div>`,{closeLabel:'Schließen'});

  $('#detail-meta').innerHTML=`
    <div><span>Status</span><strong>${u.deletionRequestedAt?'Löschung vorgemerkt':u.isDisabled?'Gesperrt':'Aktiv'}</strong></div>
    <div><span>Admin</span><strong>${u.isAdmin?'Ja':'Nein'}</strong></div>
    <div><span>2FA</span><strong>${u.twoFactorEnabled?'Aktiv':'Aus'}</strong></div>
    <div><span>Erstellt</span><strong>${dt(u.createdAt)}</strong></div>
    <div><span>Letzte Session</span><strong>${dt(u.lastSessionSeenAt)}</strong></div>
    <div><span>Löschung geplant</span><strong>${dt(u.deletionScheduledFor)}</strong></div>`;

  $('#detail-sessions').innerHTML=detail.sessions.length
    ? detail.sessions.map(s=>`<div class="admin-session"><strong>${esc(s.deviceName)}</strong><div class="admin-sub">Zuletzt ${dt(s.lastSeenAt)} · ${s.active?'aktiv':'beendet'}</div></div>`).join('')
    : '<div class="row-sub">Keine Sessions.</div>';

  const actions=[];
  actions.push('<button type="button" data-action="revoke-sessions" class="btn btn-secondary">Sessions beenden</button>');
  if(u.deletionRequestedAt){
    actions.push('<button type="button" data-action="cancel-deletion" class="btn btn-secondary">Löschung abbrechen</button>');
  }else if(u.isDisabled){
    actions.push('<button type="button" data-action="enable" class="btn btn-primary">Entsperren</button>');
  }else{
    actions.push('<button type="button" data-action="disable" class="btn btn-secondary">Sperren</button>');
    actions.push('<button type="button" data-action="schedule-deletion" class="btn btn-danger">Löschung vormerken</button>');
  }
  if(u.isAdmin)actions.push('<button type="button" data-action="revoke-admin" class="btn btn-secondary">Adminrecht entziehen</button>');
  else actions.push('<button type="button" data-action="grant-admin" class="btn btn-secondary">Zum Admin machen</button>');
  $('#detail-actions').innerHTML=actions.join('');
  $('#detail-actions').querySelectorAll('[data-action]').forEach(b=>b.addEventListener('click',()=>runAction(b.dataset.action)));
  detailDialog.showModal();
}

async function confirmAdmin(message, confirmLabel='Bestätigen') {
  return confirmMessage({
    message,
    title: 'Bestätigen',
    confirmLabel,
    cancelLabel: 'Abbrechen',
    destructive: true
  });
}

async function runAction(action){
  const u=state.detail?.user;if(!u)return;
  if(action==='schedule-deletion'&&!await confirmAdmin(`Löschung für ${u.email} vormerken? Der User hat 7 Tage zur Reaktivierung.`,'Löschung vormerken'))return;
  if(action==='disable'&&!await confirmAdmin(`${u.email} sperren?`,'Sperren'))return;
  if(action==='revoke-admin'&&!await confirmAdmin(`Adminrecht von ${u.email} entfernen?`,'Adminrecht entfernen'))return;
  try{
    await request(`/auth/admin/users/${u.id}/${action}`,{method:'POST'});
    toast('Gespeichert');
    await refresh();
    await openUser(u.id);
  }catch(e){
    const target=$('#detail-error');
    if(!target)return;
    target.textContent=e.message==='last_admin'
      ?'Der letzte aktive Admin kann nicht gesperrt, gelöscht oder herabgestuft werden.'
      :'Aktion fehlgeschlagen: '+e.message;
    target.hidden=false;
  }
}

let searchTimer;
$('#search').addEventListener('input',e=>{
  clearTimeout(searchTimer);
  searchTimer=setTimeout(()=>{state.search=e.target.value.trim();state.offset=0;loadUsers().catch(console.error)},220);
});
$('#status').addEventListener('change',e=>{state.status=e.target.value;state.offset=0;loadUsers().catch(console.error)});
$('#refresh').addEventListener('click',()=>refresh().catch(console.error));
$('#prev').addEventListener('click',()=>{state.offset=Math.max(0,state.offset-state.limit);loadUsers().catch(console.error)});
$('#next').addEventListener('click',()=>{state.offset+=state.limit;loadUsers().catch(console.error)});

bindProviders();

refresh().catch(error=>{
  if(error.message!=='forbidden'){document.body.innerHTML='<main class="admin-shell"><h1>Admin konnte nicht geladen werden</h1></main>'}
});
