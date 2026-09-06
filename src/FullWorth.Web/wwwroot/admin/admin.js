const state={offset:0,limit:50,total:0,search:'',status:'',detail:null};
const $=s=>document.querySelector(s);
const esc=v=>String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
const dt=v=>v?new Intl.DateTimeFormat(undefined,{dateStyle:'medium',timeStyle:'short'}).format(new Date(v)):'—';

async function request(path,options){
  const response=await fetch(path,options);
  if(response.status===403){location.assign('/');throw new Error('forbidden')}
  if(!response.ok){
    let error=String(response.status);
    try{error=(await response.json()).error||error}catch{}
    throw new Error(error);
  }
  return response.status===204?null:response.json();
}

function toast(text){
  const el=$('#toast');el.textContent=text;el.classList.add('show');
  clearTimeout(toast.t);toast.t=setTimeout(()=>el.classList.remove('show'),2500);
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

function statusBadge(u){
  if(u.deletionRequestedAt)return '<span class="badge danger">Löschung</span>';
  if(u.isDisabled)return '<span class="badge warn">Gesperrt</span>';
  return '<span class="badge">Aktiv</span>';
}

async function loadUsers(){
  const params=new URLSearchParams({offset:String(state.offset),limit:String(state.limit)});
  if(state.search)params.set('search',state.search);
  if(state.status)params.set('status',state.status);
  const page=await request('/auth/admin/users?'+params);
  state.total=page.total;
  const list=$('#users');
  if(!page.items.length){
    list.innerHTML='<div class="user-row"><div>Keine User gefunden.</div></div>';
  }else{
    list.innerHTML=page.items.map(u=>`
      <button class="user-row" type="button" data-user="${u.id}">
        <div><div class="user-email">${esc(u.email)}</div><div class="sub">${esc(u.id)}</div></div>
        <div>${u.isAdmin?'<span class="badge admin">Admin</span>':'User'}</div>
        <div>${statusBadge(u)}</div>
        <div><span class="badge">${u.activeSessionCount} Session${u.activeSessionCount===1?'':'s'}</span></div>
        <div>›</div>
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
  await Promise.all([loadOverview(),loadUsers()]);
}

async function openUser(id){
  const detail=await request('/auth/admin/users/'+encodeURIComponent(id));
  state.detail=detail;
  const u=detail.user;
  $('#detail-email').textContent=u.email;
  $('#detail-meta').innerHTML=`
    <div><span>Status</span><strong>${u.deletionRequestedAt?'Löschung vorgemerkt':u.isDisabled?'Gesperrt':'Aktiv'}</strong></div>
    <div><span>Admin</span><strong>${u.isAdmin?'Ja':'Nein'}</strong></div>
    <div><span>2FA</span><strong>${u.twoFactorEnabled?'Aktiv':'Aus'}</strong></div>
    <div><span>Erstellt</span><strong>${dt(u.createdAt)}</strong></div>
    <div><span>Letzte Session</span><strong>${dt(u.lastSessionSeenAt)}</strong></div>
    <div><span>Löschung geplant</span><strong>${dt(u.deletionScheduledFor)}</strong></div>`;

  $('#detail-sessions').innerHTML=detail.sessions.length
    ? detail.sessions.map(s=>`<div class="session"><strong>${esc(s.deviceName)}</strong><div class="sub">Zuletzt ${dt(s.lastSeenAt)} · ${s.active?'aktiv':'beendet'}</div></div>`).join('')
    : '<div class="sub">Keine Sessions.</div>';

  const actions=[];
  actions.push('<button type="button" data-action="revoke-sessions" class="secondary">Sessions beenden</button>');
  if(u.deletionRequestedAt){
    actions.push('<button type="button" data-action="cancel-deletion" class="secondary">Löschung abbrechen</button>');
  }else if(u.isDisabled){
    actions.push('<button type="button" data-action="enable">Entsperren</button>');
  }else{
    actions.push('<button type="button" data-action="disable" class="secondary">Sperren</button>');
    actions.push('<button type="button" data-action="schedule-deletion" class="danger">Löschung vormerken</button>');
  }
  if(u.isAdmin)actions.push('<button type="button" data-action="revoke-admin" class="secondary">Adminrecht entziehen</button>');
  else actions.push('<button type="button" data-action="grant-admin" class="secondary">Zum Admin machen</button>');
  $('#detail-actions').innerHTML=actions.join('');
  $('#detail-actions').querySelectorAll('[data-action]').forEach(b=>b.addEventListener('click',()=>runAction(b.dataset.action)));
  $('#detail-error').hidden=true;
  $('#user-dialog').showModal();
}

async function runAction(action){
  const u=state.detail?.user;if(!u)return;
  if(action==='schedule-deletion'&&!confirm(`Löschung für ${u.email} vormerken? Der User hat 7 Tage zur Reaktivierung.`))return;
  if(action==='disable'&&!confirm(`${u.email} sperren?`))return;
  if(action==='revoke-admin'&&!confirm(`Adminrecht von ${u.email} entfernen?`))return;
  try{
    await request(`/auth/admin/users/${u.id}/${action}`,{method:'POST'});
    toast('Gespeichert');
    await refresh();
    await openUser(u.id);
  }catch(e){
    const target=$('#detail-error');
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
$('#close-detail').addEventListener('click',()=>$('#user-dialog').close());
$('#user-dialog').addEventListener('click',e=>{if(e.target===$('#user-dialog'))$('#user-dialog').close()});

refresh().catch(error=>{
  if(error.message!=='forbidden'){document.body.innerHTML='<main class="admin-shell"><h1>Admin konnte nicht geladen werden</h1></main>'}
});
