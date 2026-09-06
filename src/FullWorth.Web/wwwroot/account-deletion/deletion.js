const deadline = document.querySelector('#deadline');
const reactivate = document.querySelector('#reactivate');
const logout = document.querySelector('#logout');
const error = document.querySelector('#error');
const title = document.querySelector('#title');
const message = document.querySelector('#message');

async function status(){
  const response = await fetch('/auth/account-deletion/status',{credentials:'same-origin'});
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
  const response = await fetch('/auth/account-deletion/cancel',{method:'POST',credentials:'same-origin'});
  if(response.ok){ location.assign('/'); return; }
  error.textContent='Die Reaktivierung konnte nicht abgeschlossen werden.';
  error.hidden=false;
  reactivate.disabled=false;
});

logout.addEventListener('click', async ()=>{
  await fetch('/auth/logout',{method:'POST',credentials:'same-origin'});
  location.assign('/auth/login');
});

status().catch(()=>{
  error.textContent='Der Löschstatus konnte nicht geladen werden.';
  error.hidden=false;
});
