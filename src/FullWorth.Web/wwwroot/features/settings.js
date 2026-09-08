import { secureFetch } from '../security/secure-fetch.js';
import { state } from '../core/state.js';
import { privacyDefault } from '../ui/privacy.js';
import { openPinDialog } from '../ui/lock.js';
import { renderSharing } from './sharing.js';

let ctx=null;
let accessSetup=null;
let renderEnableBankingSettings=null;
const use=context=>{if(context)ctx=context;if(!ctx)throw new Error('Settings context not initialized.');return ctx};
const $=selector=>document.querySelector(selector);
const get=path=>ctx.get(path);
const esc=value=>ctx.esc(value);
const toast=(message,duration)=>ctx.toast(message,duration);
const dialog=(html,options)=>ctx.dialog(html,options);

export function bindSettings(context,deps={}){
  use(context);
  accessSetup=deps.accessSetup||accessSetup;
  renderEnableBankingSettings=deps.renderEnableBankingSettings||renderEnableBankingSettings;
  $('#delete-account')?.addEventListener('click',openDeleteAccountDialog);
  $('#admin-nav')?.addEventListener('click',()=>location.assign('/admin'));
  $('#admin-settings-link')?.addEventListener('click',()=>location.assign('/admin'));
  $('#two-factor-settings')?.addEventListener('click',openTwoFactorDialog);
  $('#lock-settings')?.addEventListener('click',()=>openPinDialog(ctx));
}

async function openDeleteAccountDialog(){
  const dlg=dialog(`
    <form class="dialog-card" id="delete-account-form">
      <div class="panel-head"><h2>${get('settings.deleteAccount')}</h2></div>
      <p class="row-sub">${get('settings.deleteAccountExplain')}</p>
      <div class="form-grid">
        <label><span>${get('auth.password')}</span><input id="delete-account-password" type="password" autocomplete="current-password" required></label>
        <label class="check"><input id="delete-account-confirm" type="checkbox" required><span>${get('settings.deleteAccountConfirm')}</span></label>
      </div>
      <p id="delete-account-error" class="row-sub" hidden></p>
      <div class="dialog-actions">
        <button type="button" class="btn btn-secondary" data-close>${get('common.cancel')}</button>
        <button type="submit" class="btn btn-danger">${get('settings.deleteAccountAction')}</button>
      </div>
    </form>`,{closeLabel:get('common.close')});
  const form=dlg.querySelector('#delete-account-form');
  dlg.querySelectorAll('[data-close]').forEach(button=>button.addEventListener('click',()=>{if(dlg.open)dlg.close('cancel')}));
  form.addEventListener('submit',async e=>{
    e.preventDefault();
    const password=dlg.querySelector('#delete-account-password').value;
    const confirmed=dlg.querySelector('#delete-account-confirm').checked;
    const error=dlg.querySelector('#delete-account-error');
    if(!password||!confirmed)return;
    const submit=form.querySelector('button[type="submit"]');
    submit.disabled=true;error.hidden=true;
    try{
      const response=await secureFetch('/auth/account-deletion/request',{
        method:'POST',
        headers:{'Content-Type':'application/json'},
        body:JSON.stringify({currentPassword:password})
      });
      if(response.ok){location.assign('/account/deletion');return}
      const payload=await response.json().catch(()=>({}));
      error.textContent=payload.error==='invalid_password'?get('settings.deleteAccountPasswordInvalid'):get('settings.deleteAccountFailed');
      error.hidden=false;
    }catch{
      error.textContent=get('settings.deleteAccountFailed');error.hidden=false;
    }finally{submit.disabled=false}
  });
  dlg.showModal();
}

async function openTwoFactorDialog(){
  let status;
  try{
    status=await secureFetch('/auth/two-factor/status',{cache:'no-store'}).then(r=>r.ok?r.json():Promise.reject());
  }catch{toast(get('common.error'));return}

  if(status.enabled){
    const dlg=dialog(`
      <form class="dialog-card" id="two-factor-disable-form">
        <div class="panel-head"><h2>${get('twoFactor.title')}</h2></div>
        <p class="row-sub">${get('twoFactor.enabled')}</p>
        <label><span>${get('twoFactor.code')}</span><input id="two-factor-disable-code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" required></label>
        <div class="dialog-actions"><button type="button" class="btn btn-secondary" data-close>${get('common.cancel')}</button><button type="submit" class="btn btn-danger">${get('twoFactor.disable')}</button></div>
      </form>`);
    dlg.querySelector('[data-close]')?.addEventListener('click',()=>dlg.close());
    dlg.querySelector('form').addEventListener('submit',async e=>{
      e.preventDefault();
      const code=dlg.querySelector('#two-factor-disable-code').value;
      const response=await secureFetch('/auth/two-factor/disable',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code})});
      if(response.ok){state.capabilities.twoFactorEnabled=false;dlg.close();toast(get('twoFactor.disabled'));return}
      toast(get('twoFactor.invalidCode'));
    });
    dlg.showModal();return;
  }

  let setup;
  try{
    const response=await secureFetch('/auth/two-factor/setup',{method:'POST'});
    if(!response.ok)throw new Error();
    setup=await response.json();
  }catch{toast(get('common.error'));return}

  const dlg=dialog(`
    <form class="dialog-card" id="two-factor-enable-form">
      <div class="panel-head"><h2>${get('twoFactor.title')}</h2></div>
      <p class="row-sub">${get('twoFactor.setupHelp')}</p>
      <div class="row"><div class="row-main"><div class="row-title">${get('twoFactor.sharedKey')}</div><div class="row-sub"><code class="two-factor-key">${esc(setup.sharedKey)}</code></div></div></div>
      <label><span>${get('twoFactor.code')}</span><input id="two-factor-enable-code" inputmode="numeric" autocomplete="one-time-code" maxlength="8" required></label>
      <div class="dialog-actions"><button type="button" class="btn btn-secondary" data-close>${get('common.cancel')}</button><button type="submit">${get('twoFactor.enable')}</button></div>
    </form>`);
  dlg.querySelector('[data-close]')?.addEventListener('click',()=>dlg.close());
  dlg.querySelector('form').addEventListener('submit',async e=>{
    e.preventDefault();
    const code=dlg.querySelector('#two-factor-enable-code').value;
    const response=await secureFetch('/auth/two-factor/enable',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({code})});
    if(response.ok){state.capabilities.twoFactorEnabled=true;dlg.close();toast(get('twoFactor.enabledToast'));return}
    toast(get('twoFactor.invalidCode'));
  });
  dlg.showModal();
}


export async function renderSettings(context=ctx){use(context);$('#language').value=state.lang;$('#theme').value=state.theme;$('#privacy-default').checked=privacyDefault();await Promise.all([renderSharing(ctx),renderEnableBankingSettings?.(ctx),accessSetup?.renderAiAccessSettings(),accessSetup?.renderCloudSettings()])}
