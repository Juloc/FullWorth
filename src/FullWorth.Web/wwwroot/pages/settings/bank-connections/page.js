// Bankverbindungen: verbinden, erneuern, synchronisieren, trennen - und der Enable-Banking-Assistent.
//
// Das lag bis #125 in pages/accounts/page.js, weil die Kontenseite die Liste der Verbindungen mit
// anzeigte. Sie tut es nicht mehr: eine Bankverbindung ist eine Einstellung, kein Konto, und die
// Kontenuebersicht soll Konten zeigen. Die Dialoge sind mitgezogen, damit nicht die Haelfte einer
// Sache an einem Ort liegt und die andere Haelfte am anderen - die Kontenseite ruft nur noch
// openBankConnection() auf, so wie app.js es ihr uebergibt.

import { state } from '../../../core/state.js';
import { emptyRow } from '../../../components/empty.js';

let ctx = null;

function use(context) {
  if (context) ctx = context;
  if (!ctx) throw new Error('Bank connections context is not initialized.');
  return ctx;
}

const $ = selector => document.querySelector(selector);
const api = (path, options) => ctx.api(path, options);
const bankApi = (path, options) => ctx.bankApi(path, options);
const get = key => ctx.get(key);
const esc = value => ctx.esc(value);
const dateTime = value => ctx.dateTime(value);
const toast = (...args) => ctx.toast(...args);
const jsonBody = (...args) => ctx.jsonBody(...args);
const dialog = (html, options = {}) => ctx.dialog(html, options);
const empty = (el, message) => ctx.empty(el, message);

// Die Zeile einer Verbindung. Sie stand frueher in loadAccountsView und wurde dort zwischen den
// Konten gezeichnet.
function connectionRow(x){
  const health=x.healthStatus||'authorized';
  const label=get(`accounts.health_${health}`);
  // Rot bleiben beide Sorten - es sind echte Problemzustaende. Unterschiedlich ist nur, was hilft.
  const warn=['reauthorization_required','expired','revoked','closed','error','partial_history'].includes(health);
  // A parked TAN is not a broken connection: Reconnect would start a fresh authorization and throw
  // the pending challenge away. The only action that helps is answering the TAN.
  const needsTan=health==='tan_required';
  // Und aus demselben Grund: eine Verbindung, die auf die Kontenauswahl wartet, ist ebenfalls nicht
  // kaputt. "Neu verbinden" wuerde die Sitzung wegwerfen und die PIN erneut erfragen.
  const needsSelection=health==='selection_pending';
  // Und zum dritten Mal derselbe Gedanke: ein Abgleich, der schiefging, ist keine kaputte
  // Anmeldung. Die Sitzung steht, die Freigabe gilt - es hat nur ein Abruf nicht geklappt. Hier
  // wurde bisher "Neu verbinden" angeboten, also die PIN erneut verlangt und die gute Sitzung
  // weggeworfen, fuer einen Fehler, den ein zweiter Versuch behebt. Genau das ist passiert, als die
  // ING ein Depot ablehnte: vier Konten standen da, das Depot fehlte, und der einzige Weg zurueck
  // war die komplette Neuanmeldung.
  const retryable=health==='error'||health==='partial_history';
  const expiry=Number.isFinite(x.daysUntilExpiry)&&x.daysUntilExpiry>=0&&health!=='expired'?` · ${get('accounts.expiresIn').replace('{days}',x.daysUntilExpiry)}`:'';
  const nextSync=x.nextSyncAllowedAt?` · ${get('accounts.nextSyncAllowed')}: ${dateTime(x.nextSyncAllowedAt)}`:'';
  // Der Code gehoert sichtbar in die Zeile. "Fehler beim Abgleich" allein ist nichts, was man
  // weitergeben kann - und weitergeben will man ihn genau in diesem Moment.
  const errorCode=x.lastError?` · ${x.lastError}`:'';
  const row=document.createElement('div');row.className='row';row.dataset.connectionId=x.id;
  row.innerHTML=`<div class="row-main"><div class="row-title">${esc(x.institutionName)}</div><div class="row-sub">${esc(get('accounts.validUntil'))}: ${dateTime(x.validUntil)} · ${esc(get('accounts.lastSync'))}: ${dateTime(x.lastSyncedAt)}${esc(expiry)}${esc(nextSync)}${esc(errorCode)}</div></div><div class="row-side"><div class="amount${warn?' negative':''}">${esc(label)}</div><button type="button" class="ghost" data-sync-history>${esc(get('accounts.syncHistory'))}</button>${needsTan?`<button type="button" class="ghost" data-enter-tan>${esc(get('accounts.enterTan'))}</button>`:needsSelection?`<button type="button" class="ghost" data-finish-selection>${esc(get('bankingSetup.ingFinishSelection'))}</button>`:retryable?`<button type="button" class="ghost" data-retry-sync>${esc(get('accounts.retrySync'))}</button>`:warn?`<button type="button" class="ghost" data-reconnect>${esc(get('accounts.reconnect'))}</button>`:`<button type="button" class="icon-button" data-sync title="${esc(get('accounts.syncNow'))}" aria-label="${esc(get('accounts.syncNow'))}">⟳</button>`}<button type="button" class="ghost danger" data-disconnect>${esc(get('accounts.disconnect'))}</button></div>`;
  row.querySelector('[data-sync-history]')?.addEventListener('click',()=>openSyncHistory(x));
  row.querySelector('[data-sync]')?.addEventListener('click',ev=>syncConnection(x.id,ev.currentTarget));
  row.querySelector('[data-retry-sync]')?.addEventListener('click',ev=>syncConnection(x.id,ev.currentTarget));
  row.querySelector('[data-reconnect]')?.addEventListener('click',ev=>reconnectConnection(x,ev.currentTarget));
  row.querySelector('[data-enter-tan]')?.addEventListener('click',ev=>openPendingTanDialog(x,ev.currentTarget));
  row.querySelector('[data-finish-selection]')?.addEventListener('click',ev=>resumeSelection(x,ev.currentTarget));
  row.querySelector('[data-disconnect]').addEventListener('click',ev=>disconnectConnection(x,ev.currentTarget));
  return row;
}

async function loadConnections(){
  const list=$('#connections-list');
  if(!list)return;
  const connections=await api('api/bank-connections');
  const staging=document.createElement('div');
  for(const x of connections||[])staging.appendChild(connectionRow(x));
  if(!(connections||[]).length)empty(staging);
  list.replaceChildren(...staging.childNodes);
}

async function openSyncHistory(connection){
  let history;
  try{
    history=await api(`api/bank-connections/${connection.id}/sync-history?limit=10`);
  }catch(err){toast(err.message||get('common.error'));return}
  const locale=state.lang==='de'?'de-DE':'en-US';
  const rows=(history||[]).map(item=>{
    const resultKey={success:'accounts.syncResultSuccess',partial:'accounts.syncResultPartial',error:'accounts.syncResultError'}[item.result]||'accounts.syncResultError';
    const seconds=Math.max(0,Number(item.durationMs)||0)/1000;
    const duration=new Intl.NumberFormat(locale,{maximumFractionDigits:2}).format(seconds)+' s';
    const error=item.errorCode?` · ${esc(item.errorCode)}`:'';
    return `<div class="row"><div class="row-main"><div class="row-title">${esc(get(resultKey))}${error}</div><div class="row-sub">${esc(get('accounts.syncStartedAt'))}: ${esc(dateTime(item.startedAt))} · ${esc(get('accounts.syncFinishedAt'))}: ${esc(dateTime(item.completedAt))}</div></div><div class="row-side"><span class="row-sub">${esc(get('accounts.syncDuration'))}: ${esc(duration)}</span></div></div>`;
  }).join('');
  const dlg=dialog(`<div class="dialog-card"><div class="panel-head"><div><h2>${esc(get('accounts.syncHistory'))}</h2><div class="row-sub">${esc(connection.institutionName||'')}</div></div><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><div class="rows">${rows||emptyRow(get('accounts.syncHistoryEmpty'))}</div>
    <textarea class="report-text" data-report-text rows="8" readonly hidden></textarea>
    <div class="dialog-actions"><button type="button" class="ghost" data-copy-report>${esc(get('accounts.copyReport'))}</button></div></div>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-copy-report]').onclick=ev=>copyReport(connection,history,ev.currentTarget);
  dlg.showModal();
}

// Was in ein Issue gehoert, in einem Stueck - und ohne alles, was dort nichts zu suchen hat.
//
// Bewusst NICHT dabei: IBAN, Kontonummern, Kontonamen, Sitzungskennungen. Ein Fehlerbericht ueber
// eine Bankverbindung landet in einem oeffentlichen Repository; er darf die Verbindung beschreiben,
// nicht ihren Inhaber. Die Verbindungs-Id ist eine zufaellige Guid dieser Installation und hilft
// beim Wiederfinden in den Protokollen.
function connectionReport(connection,history){
  const line=(label,value)=>value?label+': '+value:null;
  const runs=(history||[]).slice(0,5)
    .filter(item=>item&&(item.startedAt||item.result||item.errorCode))
    .map(item=>'  '+[item.startedAt,item.result,item.errorCode].filter(Boolean).join('  '));
  return [
    line('FullWorth',document.getElementById('app-version')?.textContent?.trim()),
    line('Institut',connection.institutionName),
    line('Anbieter',connection.provider),
    line('Status',connection.status),
    line('Zustand',connection.healthStatus),
    line('Fehlercode',connection.lastError),
    line('Letzter Sync',connection.lastSyncedAt),
    line('Freigabe bis',connection.validUntil),
    line('Naechster Sync',connection.nextSyncAllowedAt),
    line('Verbindung',connection.id),
    runs.length?'Letzte Laeufe:':null,
    ...runs
  ].filter(Boolean).join('\n');
}

async function copyReport(connection,history,button){
  const text=connectionReport(connection,history);
  try{
    await navigator.clipboard.writeText(text);
    toast(get('accounts.reportCopied'));
  }catch{
    // Ohne Zwischenablage-Recht ist der Bericht nicht verloren: markiert im Feld steht er da, und
    // Strg+C tut den Rest. Eine Fehlermeldung waere hier die schlechtere Antwort.
    const box=button.closest('.dialog-card').querySelector('[data-report-text]');
    box.hidden=false;box.value=text;box.select();
  }
}

async function syncConnection(id,button){
  if(button)button.disabled=true;
  try{
    const r=await bankApi(`api/banking/connections/${id}/sync?force=true`,{method:'POST'});
    const status=(r&&r.status)||'started';
    const messages={started:'accounts.syncStarted',completed:'accounts.syncCompleted',partial_history:'accounts.syncPartial',error:'accounts.syncError',already_running:'accounts.syncRunning',cooldown:'accounts.syncCooldown',reauthorization_required:'accounts.syncReauth',tan_required:'accounts.syncTanRequired'};
    const detail=status==='cooldown'&&r?.nextSyncAllowedAt?` · ${get('accounts.nextSyncAllowed')}: ${dateTime(r.nextSyncAllowedAt)}`:'';
    toast(`${get(messages[status]||'accounts.syncStarted')}${detail}`);
    // A TAN is waiting: open it straight away instead of leaving the user with a toast and no way in.
    if(status==='tan_required'){await openPendingTanDialog({id},null);return}
    await ctx.reload();
  }catch(err){toast(err.message||get('common.error'));if(button)button.disabled=false}
}

async function disconnectConnection(connection,button){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('accounts.disconnect'))}: ${esc(connection.institutionName)}</h2><button type="button" data-close>×</button></div>
    <p class="row-sub">${esc(get('accounts.disconnectProviderHint'))}</p>
    <label class="check"><input type="radio" name="policy" value="keep" checked> <span>${esc(get('accounts.disconnectKeep'))}</span></label>
    <label class="check"><input type="radio" name="policy" value="delete"> <span>${esc(get('accounts.disconnectDelete'))}</span></label>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="danger">${esc(get('accounts.disconnect'))}</button></div></form>`);
  const form=dlg.querySelector('form');
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  form.onsubmit=async e=>{
    e.preventDefault();
    const deleteLocalData=new FormData(form).get('policy')==='delete';
    if(deleteLocalData&&!await ctx.confirm(get('accounts.disconnectConfirm').replace('{name}',()=>connection.institutionName),{destructive:true,confirmLabel:get('accounts.disconnect')}))return;
    if(button)button.disabled=true;form.querySelector('[type="submit"]').disabled=true;
    try{
      await bankApi(`api/banking/connections/${connection.id}?deleteLocalData=${deleteLocalData}`,{method:'DELETE'});
      dlg.close();toast(get(deleteLocalData?'accounts.disconnected':'accounts.disconnectedKept'));await ctx.reload();
    }catch(err){toast(err.message||get('common.error'));if(button)button.disabled=false;form.querySelector('[type="submit"]').disabled=false}
  };
  dlg.showModal();
}
// §17: re-authorizes an expired/errored connection IN PLACE (reconnectConnectionId) instead of
// creating a duplicate connection for the same institution.
async function reconnectConnection(connection,button){
  // FinTS connections are re-authorized with their own login, not through Enable Banking. Without
  // this check the Enable Banking flow ran with the FinTS connection id, which rewrote the
  // connection provider - or, with no Enable Banking profile, opened its setup wizard for a bank
  // that never needed one.
  if(String(connection?.provider||'').toLowerCase()==='fints'){
    if(button)button.disabled=false;
    return openIngConnectionOptions(connection);
  }
  if(button)button.disabled=true;
  try{
    const status=await bankApi('api/banking/status');
    if(!bankingReady(status)){if(button)button.disabled=false;return openEnableBankingWizard(status)}
    const country=(connection.country||'DE').toUpperCase();
    const data=await bankApi(`api/banking/institutions?country=${encodeURIComponent(country)}&psuType=${encodeURIComponent(connection.psuType||'personal')}`);
    const bank=(data.aspsps||[]).find(x=>(x.name||'').toLowerCase()===(connection.institutionName||'').toLowerCase());
    if(button)button.disabled=false;
    if(bank)return openBankConnectionOptions(bank,connection.id,status.profile?.id||null,connection.psuType||'personal');
    // ASPSP names can change. Fall back to the fresh provider list and let the user select the
    // renamed successor while keeping the existing FullWorth connection id.
    toast(get('bankingSetup.bankRenamedHint'));
    return openBankDialog(connection,country);
  }catch(err){toast(err.message||get('common.error'));if(button)button.disabled=false}
}

// action. Called with no argument for the "+ new budget" flow.
const ENABLE_BANKING_SIGN_IN='https://enablebanking.com/sign-in/';
const ENABLE_BANKING_APPS='https://enablebanking.com/cp/applications';
const ENABLE_BANKING_LINKED='https://enablebanking.com/docs/api/linked-accounts';
const ENABLE_BANKING_STATUS='https://enablebanking.com/cp/aspsps';

function bankingReady(status){
  if(status?.profile)return status.profile.environment==='SANDBOX'||status.profile.active===true;
  return false;
}

async function renderEnableBankingSettings(){
  const row=$('#enable-banking-settings'),sub=$('#enable-banking-status');
  if(!row||!sub)return;
  sub.textContent=get('bankingSetup.loading');
  try{
    const status=await bankApi('api/banking/status');
    if(status.profile){
      sub.textContent=status.profile.active||status.profile.environment==='SANDBOX'
        ?get('bankingSetup.ready').replace('{name}',status.profile.applicationName||status.profile.applicationId)
        :get('bankingSetup.activationRequired');
    }else if(status.legacyConfigured)sub.textContent=get('bankingSetup.legacy');
    else sub.textContent=get('bankingSetup.notConfigured');
    row.onclick=()=>openEnableBankingWizard(status);
  }catch(err){
    sub.textContent=err.message||get('common.error');
    row.onclick=()=>openEnableBankingWizard(null);
  }
}

function openEnableBankingWizard(initialStatus,options={}){
  let status=initialStatus;
  let autoPoll=null;
  let autoRegistrationId=null;
  const dlg=dialog('<div class="dialog-card banking-setup"><div class="panel-head"><h2></h2><button type="button" data-close aria-label="Close">×</button></div><div data-step></div></div>');
  const step=dlg.querySelector('[data-step]');
  dlg.querySelector('h2').textContent=get('bankingSetup.title');
  const stopAutoPoll=()=>{if(autoPoll){clearTimeout(autoPoll);autoPoll=null}};
  const cancelAutoRegistration=()=>{
    stopAutoPoll();
    const id=autoRegistrationId;
    autoRegistrationId=null;
    if(id)bankApi(`api/banking/profile/register/${encodeURIComponent(id)}`,{method:'DELETE'}).catch(()=>{});
  };
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.addEventListener('close',()=>{cancelAutoRegistration();options.onClose?.()},{once:true});

  const showIntro=()=>{
    stopAutoPoll();
    step.innerHTML=`<p>${esc(get('bankingSetup.privateIntro'))}</p>
      <p class="row-sub">${esc(get('bankingSetup.privateBoundary'))}</p>
      <label class="check"><input type="checkbox" data-ack> ${esc(get('bankingSetup.ack'))}</label>
      <div class="dialog-actions"><button type="button" data-next disabled>${esc(get('auth.continue'))}</button></div>`;
    const ack=step.querySelector('[data-ack]'),next=step.querySelector('[data-next]');
    ack.onchange=()=>next.disabled=!ack.checked;
    next.onclick=showSetupChoice;
  };

  const showSetupChoice=()=>{
    stopAutoPoll();
    step.innerHTML=`<p class="row-sub">${esc(get('bankingSetup.setupChoiceHint'))}</p>
      <div class="setup-choice-grid">
        <button type="button" class="setup-choice" data-auto><strong>${esc(get('bankingSetup.automaticTitle'))}</strong><span>${esc(get('bankingSetup.automaticHint'))}</span></button>
        <button type="button" class="setup-choice" data-manual><strong>${esc(get('bankingSetup.manualTitle'))}</strong><span>${esc(get('bankingSetup.manualHint'))}</span></button>
      </div>
      <div class="dialog-actions"><button type="button" data-back>${esc(get('onboarding.back'))}</button></div>`;
    step.querySelector('[data-auto]').onclick=showAutomatic;
    step.querySelector('[data-manual]').onclick=showCredentials;
    step.querySelector('[data-back]').onclick=showIntro;
  };

  const showAutomatic=()=>{
    stopAutoPoll();
    const callback=status?.callbackUrl||'';
    step.innerHTML=`<form data-auto-form>
      <p class="row-sub">${esc(get('bankingSetup.automaticExplain'))}</p>
      <label>${esc(get('bankingSetup.email'))}<input name="email" type="email" autocomplete="email" required maxlength="254"></label>
      <label>${esc(get('bankingSetup.environment'))}<select name="environment"><option value="PRODUCTION">${esc(get('bankingSetup.environmentProduction'))}</option><option value="SANDBOX">${esc(get('bankingSetup.environmentSandbox'))}</option></select></label>
      <label>${esc(get('bankingSetup.callback'))}<input readonly value="${esc(callback)}"></label>
      <p class="row-sub">${esc(get('bankingSetup.automaticSecurity'))}</p>
      <div class="dialog-actions"><button type="button" data-back>${esc(get('onboarding.back'))}</button><button type="submit">${esc(get('bankingSetup.sendLogin'))}</button></div>
    </form>`;
    const form=step.querySelector('form');
    step.querySelector('[data-back]').onclick=showSetupChoice;
    form.onsubmit=async e=>{
      e.preventDefault();
      const button=form.querySelector('[type="submit"]');
      const fd=new FormData(form);
      button.disabled=true;
      try{
        const started=await bankApi('api/banking/profile/register/start',jsonBody({
          email:String(fd.get('email')||'').trim(),
          environment:String(fd.get('environment')||'PRODUCTION')
        }));
        autoRegistrationId=started.id;
        showAutomaticWaiting(started);
      }catch(err){
        toast(err.message||get('common.error'));
        button.disabled=false;
      }
    };
  };

  const automaticStatusText=value=>{
    if(value==='waiting_for_email')return get('bankingSetup.waitingForEmail');
    if(value==='registering')return get('bankingSetup.registeringApp');
    if(value==='verifying')return get('bankingSetup.verifyingApp');
    return get('bankingSetup.loading');
  };

  const showAutomaticFailure=registration=>{
    stopAutoPoll();
    const retry=registration?.canRetryVerification
      ? `<button type="button" data-retry>${esc(get('bankingSetup.retryVerification'))}</button>`
      : `<button type="button" data-again>${esc(get('bankingSetup.tryAgain'))}</button>`;
    step.innerHTML=`<p>${esc(registration?.status==='expired'?get('bankingSetup.autoExpired'):get('bankingSetup.autoFailed'))}</p>
      <div class="dialog-actions"><button type="button" class="ghost" data-manual>${esc(get('bankingSetup.useManual'))}</button>${retry}</div>`;
    step.querySelector('[data-manual]').onclick=()=>{cancelAutoRegistration();showCredentials()};
    step.querySelector('[data-again]')?.addEventListener('click',()=>{cancelAutoRegistration();showAutomatic()});
    step.querySelector('[data-retry]')?.addEventListener('click',async e=>{
      e.currentTarget.disabled=true;
      try{
        const next=await bankApi(`api/banking/profile/register/${encodeURIComponent(autoRegistrationId)}/retry`,jsonBody({}));
        if(next.status==='completed'){
          status=await bankApi('api/banking/status');
          await renderEnableBankingSettings();
          showProfile();
          toast(get('bankingSetup.autoComplete'));
          return;
        }
        showAutomaticFailure(next);
      }catch(err){toast(err.message||get('common.error'));e.currentTarget.disabled=false}
    });
  };

  const pollAutomatic=async id=>{
    if(id!==autoRegistrationId)return;
    try{
      const next=await bankApi(`api/banking/profile/register/${encodeURIComponent(id)}`);
      if(id!==autoRegistrationId)return;
      if(next.status==='completed'){
        stopAutoPoll();
        status=await bankApi('api/banking/status');
        await renderEnableBankingSettings();
        showProfile();
        toast(get('bankingSetup.autoComplete'));
        return;
      }
      if(next.status==='failed'||next.status==='expired'){
        showAutomaticFailure(next);
        return;
      }
      const statusEl=step.querySelector('[data-auto-status]');
      if(statusEl)statusEl.textContent=automaticStatusText(next.status);
    }catch(err){
      const statusEl=step.querySelector('[data-auto-status]');
      if(statusEl)statusEl.textContent=err.message||get('common.error');
    }
    autoPoll=setTimeout(()=>pollAutomatic(id),1500);
  };

  const showAutomaticWaiting=started=>{
    stopAutoPoll();
    step.innerHTML=`<p>${esc(get('bankingSetup.emailSent'))}</p>
      <p class="row-sub" data-auto-status>${esc(get('bankingSetup.waitingForEmail'))}</p>
      <div class="setup-meta">
        <div><span>${esc(get('bankingSetup.privacyUrl'))}</span><a href="${esc(started.privacyUrl||'https://fullworth.de/privacy/')}" target="_blank" rel="noopener">${esc(started.privacyUrl||'https://fullworth.de/privacy/')} ↗</a></div>
        <div><span>${esc(get('bankingSetup.termsUrl'))}</span><a href="${esc(started.termsUrl||'https://fullworth.de/terms/')}" target="_blank" rel="noopener">${esc(started.termsUrl||'https://fullworth.de/terms/')} ↗</a></div>
      </div>
      <div class="dialog-actions"><button type="button" class="ghost" data-manual>${esc(get('bankingSetup.useManual'))}</button></div>`;
    step.querySelector('[data-manual]').onclick=()=>{cancelAutoRegistration();showCredentials()};
    autoPoll=setTimeout(()=>pollAutomatic(started.id),800);
  };

  const showCredentials=()=>{
    stopAutoPoll();
    const callback=status?.callbackUrl||'';
    step.innerHTML=`<p class="row-sub">${esc(get('bankingSetup.createApp'))}</p>
      <p><a href="${ENABLE_BANKING_APPS}" target="_blank" rel="noopener">${esc(get('bankingSetup.apiApplications'))} ↗</a></p>
      <label>${esc(get('bankingSetup.callback'))}<input data-callback readonly value="${esc(callback)}"></label>
      <label>${esc(get('bankingSetup.applicationId'))}<input data-app-id required autocomplete="off"></label>
      <label>${esc(get('bankingSetup.privateKey'))}<input data-key type="file" accept=".pem,text/plain" required></label>
      <p class="row-sub">${esc(get('bankingSetup.keyHint'))}</p>
      <div class="dialog-actions"><button type="button" data-back>${esc(get('onboarding.back'))}</button><button type="button" data-verify>${esc(get('bankingSetup.verify'))}</button></div>`;
    step.querySelector('[data-back]').onclick=showSetupChoice;
    step.querySelector('[data-verify]').onclick=async e=>{
      const button=e.currentTarget,appId=step.querySelector('[data-app-id]').value.trim(),file=step.querySelector('[data-key]').files?.[0];
      if(!appId||!file){toast(get('bankingSetup.missingCredentials'));return}
      button.disabled=true;
      try{
        const privateKeyPem=await file.text();
        await bankApi('api/banking/profile/verify',jsonBody({applicationId:appId,privateKeyPem}));
        status=await bankApi('api/banking/status');
        showProfile();
        await renderEnableBankingSettings();
      }catch(err){toast(err.message||get('common.error'));button.disabled=false}
    };
  };

  const showProfile=()=>{
    stopAutoPoll();
    autoRegistrationId=null;
    const p=status?.profile;
    if(!p){showIntro();return}
    const ready=p.environment==='SANDBOX'||p.active;
    step.innerHTML=`<div class="row"><div class="row-main"><div class="row-title">${esc(p.applicationName||p.applicationId)}</div>
      <div class="row-sub">${esc(p.environment)} · ${ready?esc(get('bankingSetup.active')):esc(get('bankingSetup.inactive'))}</div></div></div>
      <p class="row-sub">${esc(ready?get('bankingSetup.complete'):get('bankingSetup.activateRestricted'))}</p>
      ${!ready&&p.environment==='PRODUCTION'?`<p><a href="${ENABLE_BANKING_APPS}" target="_blank" rel="noopener">${esc(get('bankingSetup.activateAccounts'))} ↗</a> · <a href="${ENABLE_BANKING_LINKED}" target="_blank" rel="noopener">${esc(get('bankingSetup.instructions'))} ↗</a></p>`:''}
      <div class="dialog-actions"><button type="button" class="ghost danger" data-remove>${esc(get('bankingSetup.remove'))}</button><button type="button" data-recheck>${esc(get('bankingSetup.recheck'))}</button><button type="button" data-done>${esc(get('common.close'))}</button></div>`;
    step.querySelector('[data-done]').onclick=()=>dlg.close();
    step.querySelector('[data-recheck]').onclick=async e=>{
      e.currentTarget.disabled=true;
      try{await bankApi('api/banking/profile/recheck',jsonBody({}));status=await bankApi('api/banking/status');showProfile();await renderEnableBankingSettings()}
      catch(err){toast(err.message||get('common.error'));e.currentTarget.disabled=false}
    };
    step.querySelector('[data-remove]').onclick=async e=>{
      if(!await confirmDialog(ctx,get('bankingSetup.removeConfirm'),{destructive:true,confirmLabel:get('bankingSetup.remove')}))return;
      e.currentTarget.disabled=true;
      try{await bankApi('api/banking/profile',{method:'DELETE'});status=await bankApi('api/banking/status');showIntro();await renderEnableBankingSettings()}
      catch(err){toast(err.message||get('common.error'));e.currentTarget.disabled=false}
    };
  };

  if(status?.profile)showProfile();else showIntro();
  dlg.showModal();
}

function authMethodsFor(bank,psuType){
  return (bank.auth_methods||[])
    .filter(m=>!(m&&typeof m==='object'&&m.hidden_method))
    .map(m=>{
      if(typeof m==='string')return{name:m,label:m,psuType:null,approach:null,credentials:[]};
      return{
        name:m.name||m.id||'',
        label:m.title||m.name||m.id||'',
        psuType:m.psu_type||null,
        approach:m.approach||null,
        credentials:Array.isArray(m.credentials)?m.credentials:[]
      };
    })
    .filter(m=>m.name&&(!m.psuType||!psuType||String(m.psuType).toLowerCase()===String(psuType).toLowerCase()));
}

function bankNameKey(value){
  return String(value||'').normalize('NFKD').toLowerCase().replace(/[^a-z0-9]+/g,' ').trim();
}

function mergeBankOptions(rows){
  const merged=new Map();
  for(const row of Array.isArray(rows)?rows:[]){
    const name=bankNameKey(row?.name),country=String(row?.country||'').toUpperCase();
    const key=`${country}|${name}`;
    if(!name||!country)continue;
    const existing=merged.get(key);
    if(!existing){
      merged.set(key,{...row,psu_types:[...(row.psu_types||[])],auth_methods:[...(row.auth_methods||[])]});
      continue;
    }
    existing.psu_types=[...new Set([...(existing.psu_types||[]),...(row.psu_types||[])])];
    const seen=new Set((existing.auth_methods||[]).map(x=>JSON.stringify(x)));
    for(const method of row.auth_methods||[]){
      const signature=JSON.stringify(method);
      if(!seen.has(signature)){existing.auth_methods.push(method);seen.add(signature)}
    }
    existing.beta=Boolean(existing.beta||row.beta);
    if(!existing.logo&&row.logo)existing.logo=row.logo;
    if(!existing.group&&row.group)existing.group=row.group;
  }
  return [...merged.values()];
}

function providerStatusSeverity(value){
  const status=String(value||'').toLowerCase().replace(/[_-]+/g,' ');
  if(!status)return null;
  if(status.includes('major')||status.includes('disruption')||status.includes('critical')||status==='down')return'major';
  if(status.includes('possible')||status.includes('problem')||status.includes('warning')||status.includes('degraded'))return'possible';
  if(status.includes('no problems')||status.includes('healthy')||status==='ok'||status.includes('available'))return'ok';
  return'unknown';
}

function providerStatusLabel(severity){
  if(severity==='major')return get('bankingSetup.statusMajor');
  if(severity==='possible')return get('bankingSetup.statusPossible');
  if(severity==='ok')return get('bankingSetup.statusOk');
  return get('bankingSetup.statusUnknown');
}

function bankStatusMatchKeys(value){
  const full=bankNameKey(value),keys=new Set(full?[full]:[]);
  const first=full.split(' ')[0]||'';
  if(first.length>=3&&first.length<=5)keys.add(first);
  return keys;
}

function applyProviderStatuses(banks,statusView){
  if(!statusView?.available||!Array.isArray(statusView.statuses))return banks;
  const rank={major:3,possible:2,unknown:1,ok:0};
  for(const bank of banks){
    const names=bankStatusMatchKeys(bank.name);
    const groupName=typeof bank.group==='string'?bank.group:(bank.group?.name||bank.group?.title||'');
    for(const key of bankStatusMatchKeys(groupName))names.add(key);
    const matches=statusView.statuses.filter(s=>{
      if(String(s.country||'').toUpperCase()!==String(bank.country||'').toUpperCase())return false;
      const brandKeys=bankStatusMatchKeys(s.brand);
      return [...brandKeys].some(key=>names.has(key));
    });
    if(!matches.length)continue;
    const best=matches.map(s=>({raw:s.status,severity:providerStatusSeverity(s.status)}))
      .sort((a,b)=>(rank[b.severity]??-1)-(rank[a.severity]??-1))[0];
    bank.providerStatus=best.raw;
    bank.providerStatusSeverity=best.severity;
  }
  return banks;
}

function openProviderStatusConnection(country,onConnected){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('bankingSetup.statusConnect'))}</h2><button type="button" data-close>×</button></div>
    <p class="row-sub">${esc(get('bankingSetup.statusConnectHint'))}</p>
    <label>${esc(get('bankingSetup.email'))}<input name="email" type="email" autocomplete="email" required maxlength="254"></label>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('bankingSetup.sendLogin'))}</button></div></form>`);
  const form=dlg.querySelector('form');let pollTimer=null,closed=false;
  const stop=()=>{closed=true;if(pollTimer)clearTimeout(pollTimer)};
  dlg.addEventListener('close',stop);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();

  const connected=async()=>{
    toast(get('bankingSetup.statusConnected'),5000);
    dlg.close();
    if(onConnected)await onConnected();
  };
  const poll=async()=>{
    if(closed)return;
    try{
      const current=await bankApi(`api/banking/provider-status?country=${encodeURIComponent(country)}`);
      if(current?.available){await connected();return}
    }catch{}
    pollTimer=setTimeout(poll,2000);
  };

  form.onsubmit=async e=>{
    e.preventDefault();
    const submit=form.querySelector('[type="submit"]'),email=String(new FormData(form).get('email')||'').trim();
    submit.disabled=true;
    try{
      const started=await bankApi('api/banking/provider-status/connect/start',jsonBody({email}));
      const manual=started?.manualCompletionRequired===true;
      form.innerHTML=`<div class="panel-head"><h2>${esc(get('bankingSetup.statusConnect'))}</h2><button type="button" data-close>×</button></div>
        <p>${esc(get(manual?'bankingSetup.statusEmailSentManual':'bankingSetup.statusEmailSent'))}</p>
        ${manual?`<label>${esc(get('bankingSetup.statusLoginLink'))}<input name="loginLink" type="text" autocomplete="off" required placeholder="http://localhost:8888/?oobCode=…"></label>
          <p class="row-sub">${esc(get('bankingSetup.statusManualHint'))}</p>`:`<p class="row-sub">${esc(get('bankingSetup.waitingForEmail'))}</p>`}
        <div class="dialog-actions"><button type="button" data-close-bottom>${esc(get('common.close'))}</button>${manual?`<button type="button" data-complete>${esc(get('bankingSetup.statusCompleteLogin'))}</button>`:''}</div>`;
      form.querySelector('[data-close]').onclick=()=>dlg.close();
      form.querySelector('[data-close-bottom]').onclick=()=>dlg.close();
      if(manual){
        const complete=form.querySelector('[data-complete]'),input=form.querySelector('[name="loginLink"]');
        complete.onclick=async()=>{
          complete.disabled=true;
          try{
            const result=await bankApi('api/banking/provider-status/connect/complete',jsonBody({
              id:started.id,
              loginLinkOrCode:String(input.value||'').trim()
            }));
            if(!result?.success){
              toast(get(result?.errorCode==='missing_oob_code'?'bankingSetup.statusManualInvalid':'common.error'));
              complete.disabled=false;
              return;
            }
            await connected();
          }catch(err){toast(err.message||get('common.error'));complete.disabled=false}
        };
        input.focus();
      }else pollTimer=setTimeout(poll,1000);
    }catch(err){toast(err.message||get('common.error'));submit.disabled=false}
  };
  dlg.showModal();
}

function isIngEnableBank(bank){
  const key=bankNameKey(bank?.name);
  return key==='ing'||key==='ing diba'||key.startsWith('ing ')||key.includes('ing diba');
}

function ingFinTsBankOption(){
  return{name:'ING',country:'DE',group:'FinTS',fullworthProvider:'fints',psu_types:['personal'],auth_methods:[]};
}

async function openIngConnectionOptions(reconnectConnection=null){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('bankingSetup.ingFinTsTitle'))}</h2><button type="button" data-close>&times;</button></div>
    <label>${esc(get('bankingSetup.ingFinTsMode'))}<select name="mode"><option value="fints" selected>${esc(get('bankingSetup.ingFinTsFull'))}</option><option value="enable">${esc(get('bankingSetup.ingEnableBankingOnly'))}</option></select></label>
    <p class="row-sub" data-mode-hint></p>
    <div data-fints-fields><label>${esc(get('bankingSetup.ingUserId'))}<input name="userId" autocomplete="username" required></label><label>${esc(get('bankingSetup.ingPin'))}<input name="pin" type="password" autocomplete="current-password" required></label></div>
    <p class="row-sub" data-connect-status aria-live="polite"></p><div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('bankingSetup.connect'))}</button></div></form>`);
  const form=dlg.querySelector('form'),mode=form.elements.mode,fields=dlg.querySelector('[data-fints-fields]'),hint=dlg.querySelector('[data-mode-hint]');
  const draw=()=>{
    const useFinTs=mode.value==='fints';
    fields.hidden=!useFinTs;
    form.elements.userId.required=useFinTs;
    form.elements.pin.required=useFinTs;
    hint.textContent=get(useFinTs?'bankingSetup.ingFinTsFullHint':'bankingSetup.ingEnableBankingHint');
  };
  draw();mode.onchange=draw;
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  form.onsubmit=async e=>{
    e.preventDefault();
    const submit=form.querySelector('[type="submit"]');submit.disabled=true;
    const status=form.querySelector('[data-connect-status]');
    try{
      if(mode.value==='enable'){
        const status=await bankApi('api/banking/status');
        if(!bankingReady(status)){
          dlg.close();
          openEnableBankingWizard(status,{onClose:()=>openBankDialog(reconnectConnection,'DE')});
          return;
        }
        const data=await bankApi('api/banking/institutions?country=DE');
        const bank=mergeBankOptions(data.aspsps||[]).find(isIngEnableBank);
        if(!bank)throw new Error(get('common.error'));
        dlg.close();
        openBankConnectionOptions(bank,reconnectConnection?.id||null,status.profile?.id||null);
        return;
      }
      const fd=new FormData(form);
      // Die Anmeldung ist kurz (zwei bis vier Sekunden). Trotzdem sagt die Zeile, was laeuft -
      // vorher war ein ausgegrauter Knopf die gesamte Rueckmeldung, und eine falsche PIN sah aus
      // wie ein haengender Aufruf.
      status.textContent=get('bankingSetup.ingConnecting');
      const result=await bankApi('api/banking/fints/ing/connect',jsonBody({
        userId:String(fd.get('userId')||'').trim(),
        pin:String(fd.get('pin')||''),
        reconnectConnectionId:reconnectConnection?.id||null
      }));
      dlg.close();
      // Nichts ist uebernommen, bis der Eigentuemer die Konten gewaehlt hat. Der lange Teil kommt
      // danach - und dann weiss er, worauf er wartet.
      if(result.status==='TAN_REQUIRED')openIngTanDialog(result);
      else openIngSelection(result);
    }catch(err){status.textContent='';toast(err.message||get('common.error'));submit.disabled=false}
  };
  dlg.showModal();
}

// Reopens the challenge a connection is already waiting on. The sync that hit the TAN parked it on the
// connection; before this there was no way to answer it outside the dialog that first triggered it, so
// the connection stayed stuck forever.
async function openPendingTanDialog(connection,button){
  if(button)button.disabled=true;
  try{
    const pending=await bankApi(`api/banking/fints/connections/${encodeURIComponent(connection.id)}/challenge`);
    if(!pending||!pending.challenge){
      toast(get('accounts.tanUnavailable'));
      await ctx.reload();
      return;
    }
    openIngTanDialog(pending);
  }catch(err){toast(err.message||get('common.error'))}
  finally{if(button)button.disabled=false}
}

// Eine abgebrochene Auswahl ist keine Sackgasse: die Kontenliste liegt seit dem Verbinden
// verschluesselt an der Verbindung und wird ohne Bankkontakt wieder gelesen - der Dialog geht
// genau dort weiter, wo er geschlossen wurde.
async function resumeSelection(connection,button){
  if(button)button.disabled=true;
  try{
    openIngSelection(await bankApi('api/banking/fints/connections/'+encodeURIComponent(connection.id)+'/accounts'));
  }catch(err){toast(err.message||get('common.error'));}
  finally{if(button)button.disabled=false;}
}

// Die Kontenauswahl: was die Bank gemeldet hat, mit Haekchen, und danach erst der lange Teil.
//
// Frueher machte das Verbinden alles in einem Aufruf - Anmeldung, neunzig Tage Umsaetze, bis zu
// fuenfzig Depotseiten - und die einzige Rueckmeldung war ein ausgegrauter Knopf. Eine falsche PIN
// und ein langer Abruf sahen gleich aus. Jetzt ist die Anmeldung kurz, die Auswahl kommt sofort, und
// gewartet wird auf etwas, das der Benutzer selbst ausgeloest hat.
//
// Abgewaehlt heisst NICHT "nicht geholt": das Konto entsteht und wird weiter synchronisiert, es wird
// nur nicht angezeigt und nicht mitgezaehlt. Spaeter einschalten zeigt sofort die volle Historie.
function openIngSelection(initial){
  let current=initial;
  // Der Kopf gehoert in die Karte, nicht in den Schritt: createDialog stellt das Schliessen-Kreuz,
  // und ein Schritt, der seinen eigenen Kopf mitbraechte, bekaeme ein zweites daneben.
  const dlg=dialog('<div class="dialog-card"><div class="panel-head"><h2 data-title></h2></div><div data-step></div></div>');
  const step=dlg.querySelector('[data-step]');
  const title=dlg.querySelector('[data-title]');
  const hidden=new Set((current.discovered||[]).filter(a=>a.visible===false).map(a=>a.key));

  const count=()=>{
    const all=current.discovered||[];
    const chosen=all.filter(a=>!hidden.has(a.key)).length;
    const master=step.querySelector('[data-select-all]');
    if(master){master.checked=chosen===all.length&&all.length>0;master.indeterminate=chosen>0&&chosen<all.length;}
    const label=step.querySelector('[data-selected-count]');
    if(label)label.textContent=get('bankingSetup.ingSelectedCount').replace('{n}',String(chosen)).replace('{total}',String(all.length));
  };

  const rowHtml=a=>'<div class="row"><label class="check ing-select-row">'
    +'<input type="checkbox" data-account="'+esc(a.key)+'"'+(hidden.has(a.key)?'':' checked')+'>'
    +'<div class="row-main"><div class="row-title">'+esc(a.name)+'</div>'
    +'<div class="row-sub">'+esc(get(a.kind==='depot'?'bankingSetup.ingKindDepot':'bankingSetup.ingKindCash'))
    +(a.ibanLast4?' · •••• '+esc(a.ibanLast4):'')
    +(a.currency?' · '+esc(a.currency):'')+'</div></div></label></div>';

  const showSelection=()=>{
    const all=current.discovered||[];
    title.textContent=get('bankingSetup.ingSelectTitle');
    step.innerHTML='<p class="row-sub">'+esc(get('bankingSetup.ingSelectHint'))+'</p>'
      +'<div class="row"><label class="check"><input type="checkbox" data-select-all>'
      +'<span>'+esc(get('bankingSetup.ingSelectAll'))+'</span></label>'
      +'<span class="row-sub" data-selected-count></span></div>'
      +'<div class="rows ing-select-list">'+all.map(rowHtml).join('')+'</div>'
      +(all.length?'':'<p class="row-sub">'+esc(get('bankingSetup.ingSelectEmpty'))+'</p>')
      +'<div class="dialog-actions"><button type="button" class="ghost" data-cancel>'+esc(get('common.cancel'))+'</button>'
      +'<button type="button" data-import>'+esc(get('bankingSetup.ingImport'))+'</button></div>';
    step.querySelector('[data-cancel]').onclick=()=>dlg.close();
    for(const box of step.querySelectorAll('[data-account]'))
      box.onchange=()=>{if(box.checked)hidden.delete(box.dataset.account);else hidden.add(box.dataset.account);count();};
    const master=step.querySelector('[data-select-all]');
    if(master)master.onchange=()=>{
      for(const box of step.querySelectorAll('[data-account]')){
        box.checked=master.checked;
        if(master.checked)hidden.delete(box.dataset.account);else hidden.add(box.dataset.account);
      }
      count();
    };
    step.querySelector('[data-import]').onclick=()=>{void runImport();};
    count();
  };

  const showImporting=()=>{
    const chosen=(current.discovered||[]).filter(a=>!hidden.has(a.key)).length;
    title.textContent=get('bankingSetup.ingImporting');
    step.innerHTML='<p class="row-sub">'+esc(get('bankingSetup.ingImportingHint'))+'</p><div class="rows" data-busy></div>';
    ctx.skeleton(step.querySelector('[data-busy]'),Math.max(1,chosen));
  };

  // "1 Depots" stand hier, bis die Zahlen ihr eigenes Wort bekamen: die App zaehlt mit einem
  // Schluesselpaar (accounts.countOne/countMany), und was null ist, wird gar nicht erst genannt.
  const countLabel=(one,many,n)=>get(n===1?one:many).replace('{count}',String(n));

  const showDone=result=>{
    const imported=result.imported||{accounts:0,depots:0,hidden:0,missing:0,error:null};
    const parts=[];
    if(imported.accounts)parts.push(countLabel('accounts.countOne','accounts.countMany',imported.accounts));
    if(imported.depots)parts.push(countLabel('bankingSetup.ingDepotOne','bankingSetup.ingDepotMany',imported.depots));
    const text=parts.length
      ? get(imported.hidden?'bankingSetup.ingImportedHidden':'bankingSetup.ingImported')
          .replace('{what}',parts.join(' '+get('common.and')+' '))
          .replace('{hidden}',String(imported.hidden))
      : get('bankingSetup.ingImportedNone');
    // Der Abruf kann mittendrin abbrechen. Dann sind die bis dahin angelegten Konten echt - und die
    // Zahl allein ist trotzdem die falsche Auskunft, weil die Bank mehr gemeldet hatte.
    //
    // Benannt statt gezaehlt: "1 fehlt" beantwortet die Frage nicht, die man dann stellt, naemlich
    // WELCHES. Die Antwort traegt zu jedem gemeldeten Konto, ob es eine Id hat - genau daran haengt
    // es. Der Knopf heisst hier "Erneut versuchen", nicht "Schliessen".
    const absent=(current.discovered||[]).filter(a=>!a.accountId).map(a=>a.name);
    const incomplete=absent.length>0||!!imported.error;
    const trouble=incomplete
      ?'<p class="row-sub">'
        +(absent.length?esc(get('bankingSetup.ingImportMissing').replace('{names}',absent.join(', ')))+' ':'')
        +(imported.error?esc(get('bankingSetup.ingImportError').replace('{code}',imported.error)):'')+'</p>'
      :'';
    title.textContent=get(incomplete?'bankingSetup.ingDonePartly':'bankingSetup.ingDone');
    step.innerHTML='<p>'+esc(text)+'</p>'+trouble
      +'<div class="dialog-actions">'
      +(incomplete?'<button type="button" class="ghost" data-retry>'+esc(get('accounts.retrySync'))+'</button>':'')
      +'<button type="button" data-finish>'+esc(get('common.close'))+'</button></div>';
    step.querySelector('[data-retry]')?.addEventListener('click',()=>{void runImport();});
    step.querySelector('[data-finish]').onclick=async()=>{dlg.close();await ctx.reload();};
  };

  const runImport=async()=>{
    showImporting();
    try{
      const result=await bankApi('api/banking/fints/connections/'+encodeURIComponent(current.connectionId)+'/import',
        jsonBody({hidden:[...hidden]}));
      current=result;
      // Mitten im Abruf kann die Bank doch noch eine TAN verlangen.
      if(result.status==='TAN_REQUIRED'){dlg.close();openIngTanDialog(result);return;}
      showDone(result);
    }catch(err){toast(err.message||get('common.error'));showSelection();}
  };

  showSelection();dlg.showModal();
}

function openIngTanDialog(initial){
  let current=initial;
  const dlg=dialog('<div class="dialog-card"><div data-tan-content></div></div>');
  const root=dlg.querySelector('[data-tan-content]');
  const complete=async result=>{
    current=result;
    if(result.status!=='TAN_REQUIRED'){
      // Die TAN hat den Dialog geoeffnet, nicht die Frage beantwortet, welche Konten gewuenscht sind.
      dlg.close();openIngSelection(result);return;
    }
    render();
  };
  const render=()=>{
    const challenge=current.challenge||{};
    const decoupled=challenge.isDecoupled===true;
    root.innerHTML=`<div class="panel-head"><h2>${esc(get('bankingSetup.ingTan'))}</h2><button type="button" data-close>&times;</button></div>
      ${challenge.challenge?`<p>${esc(challenge.challenge)}</p>`:''}<p class="row-sub">${esc(get(decoupled?'bankingSetup.ingDecoupledHint':'bankingSetup.ingTanHint'))}</p>
      ${decoupled?'':`<label>${esc(get('bankingSetup.ingTan'))}<input name="tan" inputmode="numeric" autocomplete="one-time-code" required></label>`}
      <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="button" data-submit>${esc(get(decoupled?'bankingSetup.ingPoll':'bankingSetup.connect'))}</button></div>`;
    root.querySelector('[data-close]').onclick=()=>dlg.close();root.querySelector('[data-cancel]').onclick=()=>dlg.close();
    root.querySelector('[data-submit]').onclick=async e=>{
      const button=e.currentTarget;button.disabled=true;
      try{
        const path=decoupled?`api/banking/fints/connections/${encodeURIComponent(current.connectionId)}/poll`:`api/banking/fints/connections/${encodeURIComponent(current.connectionId)}/tan`;
        const body=decoupled?jsonBody({}):jsonBody({tan:String(root.querySelector('[name="tan"]')?.value||'').trim()});
        await complete(await bankApi(path,body));
      }catch(err){toast(err.message||get('common.error'));button.disabled=false}
    };
  };
  render();dlg.showModal();
}

function openBankConnectionOptions(bank,reconnectConnectionId=null,profileId=null,preferredPsuType=null){
  const rawPsuTypes=Array.isArray(bank.psu_types)?bank.psu_types.filter(Boolean):[];
  const psuTypes=[...new Set((rawPsuTypes.length?rawPsuTypes:['personal','business']).map(x=>String(x).toLowerCase()))]
    .sort((a,b)=>(a==='personal'?0:a==='business'?1:2)-(b==='personal'?0:b==='business'?1:2));
  const requestedDefault=String(preferredPsuType||'personal').toLowerCase();
  const defaultPsuType=psuTypes.includes(requestedDefault)
    ? requestedDefault
    : (psuTypes.includes('personal')?'personal':psuTypes[0]||'personal');
  const health=bank.providerStatusSeverity;
  const healthWarning=health&&health!=='ok'
    ? `<div class="bank-status-warning ${esc(health)}"><strong>${esc(providerStatusLabel(health))}</strong><span>${esc(get('bankingSetup.statusWarning'))}</span><a href="${ENABLE_BANKING_STATUS}" target="_blank" rel="noopener">${esc(get('bankingSetup.statusPage'))} ↗</a></div>`
    : '';
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(bank.name)}</h2><button type="button" data-close>×</button></div>
    ${healthWarning}
    ${psuTypes.length>1?`<label>${esc(get('bankingSetup.accountType'))}<select name="psuType">${psuTypes.map(x=>`<option value="${esc(x)}"${x===defaultPsuType?' selected':''}>${esc(get('bankingSetup.psu_'+x)||x)}</option>`).join('')}</select></label>`:`<input type="hidden" name="psuType" value="${esc(defaultPsuType)}">`}
    <p class="row-sub" data-business-notice hidden>${esc(get('bankingSetup.businessNotice'))}</p>
    <label class="check"><input type="checkbox" data-limit-accounts> <span>${esc(get('bankingSetup.limitAccounts'))}</span></label>
    <label data-account-access hidden>${esc(get('bankingSetup.accountIdentifiers'))}<textarea name="accountAccess" rows="3" autocomplete="off" placeholder="DE89370400440532013000&#10;BBAN|123456|Optional issuer"></textarea><span class="row-sub">${esc(get('bankingSetup.accountIdentifiersHint'))}</span></label>
    <div data-auth-method></div>
    <div data-credentials></div>
    <div class="dialog-actions"><button type="button" data-cancel>${esc(get('common.cancel'))}</button><button type="submit">${esc(get('bankingSetup.connect'))}</button></div></form>`);
  const form=dlg.querySelector('form'),methodRoot=dlg.querySelector('[data-auth-method]'),credentialRoot=dlg.querySelector('[data-credentials]'),businessNotice=dlg.querySelector('[data-business-notice]');
  const limitAccounts=dlg.querySelector('[data-limit-accounts]'),accountAccess=dlg.querySelector('[data-account-access]');
  limitAccounts.onchange=()=>{accountAccess.hidden=!limitAccounts.checked;if(limitAccounts.checked)form.elements.accountAccess.focus()};
  let methods=[];

  const drawCredentials=()=>{
    credentialRoot.innerHTML='';
    const methodSelect=form.elements.authMethod;
    const method=methods.find(m=>m.name===methodSelect?.value);
    for(const field of method?.credentials||[]){
      const name=field.name||field.id;if(!name)continue;
      const label=document.createElement('label');
      label.append(document.createTextNode(field.title||name));
      const input=document.createElement('input');input.name='credential:'+name;input.autocomplete='off';
      input.type=/password|pin|secret/i.test(name+' '+(field.title||''))?'password':'text';
      if(field.template)try{new RegExp(field.template);input.pattern=field.template}catch{}
      input.required=field.required===true;
      label.append(input);
      if(field.description){
        const hint=document.createElement('span');hint.className='row-sub';hint.textContent=field.description;label.append(hint);
      }
      credentialRoot.append(label);
    }
  };

  const drawMethods=()=>{
    const psuType=form.elements.psuType?.value||defaultPsuType;
    if(businessNotice)businessNotice.hidden=String(psuType).toLowerCase()!=='business';
    methods=authMethodsFor(bank,psuType);
    methodRoot.innerHTML='';
    credentialRoot.innerHTML='';
    if(!methods.length)return;
    const label=document.createElement('label');label.append(document.createTextNode(get('bankingSetup.authMethod')));
    const select=document.createElement('select');select.name='authMethod';
    const defaultOption=document.createElement('option');defaultOption.value='';defaultOption.textContent=get('bankingSetup.bankDefault');select.append(defaultOption);
    for(const method of methods){
      const option=document.createElement('option');option.value=method.name;
      option.textContent=method.label+(method.approach?' · '+method.approach:'');
      select.append(option);
    }
    select.onchange=drawCredentials;label.append(select);methodRoot.append(label);drawCredentials();
  };

  form.elements.psuType?.addEventListener('change',drawMethods);drawMethods();
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  form.onsubmit=async e=>{
    e.preventDefault();
    const fd=new FormData(form),authMethod=fd.get('authMethod')||null,credentials={};
    const selectedPsuType=fd.get('psuType')||'personal';
    const selectedMethod=authMethod?authMethodsFor(bank,selectedPsuType).find(m=>m.name===authMethod):null;
    if(authMethod&&!selectedMethod){toast(get('common.error'));return}
    for(const[k,v]of fd.entries())if(k.startsWith('credential:')&&String(v).length)credentials[k.slice(11)]=String(v);
    let accounts=null;
    if(limitAccounts.checked){
      const lines=String(fd.get('accountAccess')||'').split(/\r?\n/).map(x=>x.trim()).filter(Boolean);
      if(!lines.length){toast(get('bankingSetup.accountIdentifiersMissing'));return}
      accounts=[];
      for(const line of lines){
        if(!line.includes('|')){
          const iban=line.replace(/\s+/g,'').toUpperCase();
          if(!/^[A-Z]{2}[A-Z0-9]{13,32}$/.test(iban)){toast(get('bankingSetup.accountIdentifiersMissing'));return}
          accounts.push({iban});continue;
        }
        const parts=line.split('|').map(x=>x.trim()),schemeName=(parts[0]||'').toUpperCase(),identification=parts[1]||'',issuer=parts.slice(2).join('|').trim();
        if(!schemeName||!identification){toast(get('bankingSetup.accountIdentifiersMissing'));return}
        accounts.push({other:{identification,schemeName,issuer:issuer||null}});
      }
    }
    const body={
      institutionName:bank.name,country:bank.country||'DE',validDays:365,
      authMethod,credentials:Object.keys(credentials).length?credentials:null,
      reconnectConnectionId,enableBankingProfileId:profileId,
      psuType:selectedPsuType,language:state.lang,
      credentialsAutosubmit:Object.keys(credentials).length?true:null,
      accounts
    };
    const submit=form.querySelector('[type="submit"]');submit.disabled=true;
    try{const result=await bankApi('api/banking/connect',jsonBody(body));location.href=result.authorizationUrl}
    catch(err){toast(err.message||get('common.error'));submit.disabled=false}
  };
  dlg.showModal();
}

async function openBankDialog(reconnectConnection=null,initialCountry='DE'){
  let status=null;
  try{status=await bankApi('api/banking/status')}catch{}

  const dlg=dialog(`<form method="dialog" class="dialog-card"><div class="panel-head"><h2>${esc(reconnectConnection?get('accounts.reconnect'):get('accounts.addBank'))}</h2><button value="cancel">×</button></div>
    <label>${esc(get('bankingSetup.country'))}<input id="bank-country" value="${esc(String(initialCountry||'DE').toUpperCase())}" maxlength="2" minlength="2" pattern="[A-Za-z]{2}" autocapitalize="characters"></label>
    <input id="bank-search" type="search" placeholder="Bank">
    <div id="bank-status-tools" class="bank-status-tools"><a href="${ENABLE_BANKING_STATUS}" target="_blank" rel="noopener">${esc(get('bankingSetup.statusPage'))} ↗</a><span data-status-state></span><button type="button" data-status-connect hidden>${esc(get('bankingSetup.statusConnect'))}</button></div>
    <div id="bank-options" class="bank-options"></div></form>`);
  const box=dlg.querySelector('#bank-options'),search=dlg.querySelector('#bank-search'),countryInput=dlg.querySelector('#bank-country');
  const statusTools=dlg.querySelector('#bank-status-tools'),statusState=statusTools.querySelector('[data-status-state]'),statusConnect=statusTools.querySelector('[data-status-connect]');
  let banks=[],providerStatusState=null;

  // Enable Banking's /aspsps only returns the banks the API application is ENABLED for, so with a
  // private application a bank that exists at Enable Banking is simply absent here until it has been
  // added there. An empty list used to read "Keine Einträge", which looks like the bank is not
  // supported at all - the one thing it does not mean.
  const emptyHint=()=>`<div class="row-sub">${esc(get('bankingSetup.bankMissingHint'))}<br><a href="${ENABLE_BANKING_APPS}" target="_blank" rel="noopener">${esc(get('bankingSetup.apiApplications'))} ↗</a></div>`;
  const draw=filter=>{
    box.innerHTML='';
    let shown=0;
    for(const bank of banks.filter(x=>!filter||(x.name||'').toLowerCase().includes(filter.toLowerCase())).slice(0,100)){
      shown++;
      const b=document.createElement('button');b.type='button';b.className='bank-option';
      if(bank.logo){
        const logo=document.createElement('img');logo.className='bank-option-logo';logo.src=bank.logo;logo.alt='';
        logo.loading='lazy';logo.referrerPolicy='no-referrer';logo.onerror=()=>logo.remove();b.appendChild(logo);
      }
      const text=document.createElement('span');text.className='bank-option-text';
      const title=document.createElement('strong');title.textContent=bank.name||'';text.appendChild(title);
      const groupName=typeof bank.group==='string'?bank.group:(bank.group?.name||bank.group?.title||'');
      const types=(bank.psu_types||[]).map(type=>get('bankingSetup.psu_'+type)||type).join(' / ');
      const health=bank.providerStatusSeverity;
      const issue=health&&health!=='ok'?providerStatusLabel(health):null;
      const meta=[bank.country||countryInput.value.toUpperCase(),groupName,types,bank.beta?get('bankingSetup.beta'):null,issue].filter(Boolean);
      const sub=document.createElement('span');sub.className='row-sub';sub.textContent=meta.join(' · ');text.appendChild(sub);
      if(health&&health!=='ok')b.classList.add('bank-option-status-'+health);
      b.appendChild(text);
      if(health&&health!=='ok'){
        const chip=document.createElement('span');
        chip.className='bank-status-chip '+health;
        const dot=document.createElement('span');dot.className='bank-status-dot';dot.setAttribute('aria-hidden','true');
        chip.append(dot,document.createTextNode(providerStatusLabel(health)));
        b.appendChild(chip);
      }
      b.onclick=()=>{dlg.close();if(bank.fullworthProvider==='fints')openIngConnectionOptions(reconnectConnection);else openBankConnectionOptions(bank,reconnectConnection?.id||null,status?.profile?.id||null)};
      box.appendChild(b);
    }
    if(!shown)box.innerHTML=emptyHint();
  };

  const loadCountry=async()=>{
    const country=countryInput.value.trim().toUpperCase();
    if(!/^[A-Z]{2}$/.test(country))return;
    countryInput.value=country;box.innerHTML=`<div class="row-sub">${esc(get('bankingSetup.loading'))}</div>`;
    const ing=country==='DE'?[ingFinTsBankOption()]:[];
    if(!bankingReady(status)){
      providerStatusState=null;
      statusConnect.hidden=true;
      statusState.textContent=get('bankingSetup.notConfigured');
      statusTools.classList.add('status-attention');
      statusTools.classList.remove('status-active');
      banks=ing;draw(search.value);return;
    }
    try{
      const [data,providerStatus]=await Promise.all([
        bankApi(`api/banking/institutions?country=${encodeURIComponent(country)}`),
        bankApi(`api/banking/provider-status?country=${encodeURIComponent(country)}`).catch(()=>({available:false,reason:'provider_status_request_failed',statuses:[]}))
      ]);
      providerStatusState=providerStatus;
      const canConnectStatus=providerStatus&&
        (providerStatus.reason==='control_panel_access_unavailable'||providerStatus.reason==='control_panel_login_expired');
      statusConnect.hidden=!canConnectStatus;
      statusState.textContent=providerStatus?.available
        ?get('bankingSetup.statusActive')
        :(canConnectStatus?get('bankingSetup.statusConnectShort'):get('bankingSetup.statusUnavailable'));
      statusTools.classList.toggle('status-attention',providerStatus?.available!==true);
      statusTools.classList.toggle('status-active',providerStatus?.available===true);
      const enableBanks=mergeBankOptions(data.aspsps||[]).filter(bank=>country!=='DE'||!isIngEnableBank(bank)).sort((a,b)=>(a.name||'').localeCompare(b.name||''));
      banks=[...ing,...applyProviderStatuses(enableBanks,providerStatusState)];
      draw(search.value);
    }catch(err){banks=ing;if(ing.length)draw(search.value);else box.innerHTML=`<div class="row-sub">${esc(err.message||get('common.error'))}</div>`}
  };
  statusConnect.onclick=()=>openProviderStatusConnection(
    countryInput.value.trim().toUpperCase()||'DE',
    loadCountry);
  search.oninput=e=>draw(e.target.value);
  countryInput.addEventListener('change',loadCountry);
  countryInput.addEventListener('input',()=>{if(countryInput.value.trim().length===2)loadCountry()});
  dlg.showModal();
  await loadCountry();
}


export async function renderBankConnections(context) {
  use(context);
  // Eigenschaftszuweisung statt addEventListener: renderBankConnections laeuft bei jedem ctx.reload()
  // erneut, ein addEventListener wuerde Zuhoerer stapeln. {once:true} loeste das Stapeln und schuf
  // einen toten Knopf - nach einem abgebrochenen Dialog passierte beim naechsten Klick nichts mehr,
  // bis man die Seite verliess. Eine Zuweisung ist idempotent und die Form, die diese Datei sonst
  // ueberall benutzt.
  const addButton = $('#add-bank-connection');
  if (addButton) addButton.onclick = () => { void openBankDialog(); };
  return loadConnections();
}

export function openBankConnection(context, reconnectConnection = null, country = 'DE') {
  use(context);
  return openBankDialog(reconnectConnection, country);
}

export function openBankingSetup(context, status, options = {}) {
  use(context);
  return openEnableBankingWizard(status, options);
}

export async function renderBankingSettings(context) {
  use(context);
  return renderEnableBankingSettings();
}
