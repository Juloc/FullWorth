const deadline = document.querySelector('#deadline');
const reactivate = document.querySelector('#reactivate');
const logout = document.querySelector('#logout');
const error = document.querySelector('#error');
const title = document.querySelector('#title');
const message = document.querySelector('#message');

let csrfToken = null;

async function csrf(){
  if(csrfToken) return csrfToken;
  const response = await fetch('/auth/antiforgery',{credentials:'same-origin',cache:'no-store'});
  if(!response.ok) throw new Error('csrf');
  csrfToken = (await response.json()).token;
  return csrfToken;
}

async function post(path){
  return fetch(path,{
    method:'POST',
    credentials:'same-origin',
    headers:{'X-CSRF-TOKEN':await csrf()}
  });
}

async function status(){
  const response = await fetch('/auth/account-deletion/status',{credentials:'same-origin',cache:'no-store'});
  if(!response.ok){ location.assign('/auth/login'); return; }
  const data = await response.json();
  if(!data.pending){
    location.assign('/');
    return;
  }
  deadline.textContent = data.scheduledFor
    ? new Intl.DateTimeFormat(undefined,{dateStyle:'full',timeStyle:'short'}).format(new Date(data.scheduledFor))
    : '–';
  reactivate.disabled = !data.canReactivate;
  if(!data.canReactivate){
    title.textContent = 'Löschung wird verarbeitet';
    message.textContent = 'Die Wiederherstellungsfrist ist abgelaufen. Die irreversible Bereinigung wurde gestartet oder steht unmittelbar bevor.';
  }
}

reactivate.addEventListener('click', async ()=>{
  reactivate.disabled=true;
  error.hidden=true;
  try{
    const response = await post('/auth/account-deletion/cancel');
    if(response.ok){ location.assign('/'); return; }
  }catch{}
  error.textContent='Die Reaktivierung konnte nicht abgeschlossen werden.';
  error.hidden=false;
  reactivate.disabled=false;
});

logout.addEventListener('click', async ()=>{
  try{ await post('/auth/logout'); }catch{}
  location.assign('/auth/login');
});

status().catch(()=>{
  error.textContent='Der Löschstatus konnte nicht geladen werden.';
  error.hidden=false;
});
