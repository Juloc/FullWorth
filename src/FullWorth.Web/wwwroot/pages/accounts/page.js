import { money, converted, maskIdentifier, incompleteMarker } from '../../components/money.js';
import { balanceMeaningLine } from '../../components/balance-meaning.js';
import { ButtonRole, buttonClass } from '../../components/buttons.js';
import { state } from '../../core/state.js';
import { emptyRow } from '../../components/empty.js';
import {
  bindAccountsPresentation,
  enhanceAccountsPresentation,
  toggleAccountGroupEditing,
  decorateManualAccountDialog,
  applyManualAccountVisual,
  editAccountVisualById,
  decorateAccountIdentity
} from './presentation.js';
// Nur fuer den Nebeneffekt: registriert den globalen [data-portfolio]-Klick-Lauscher der
// Vermoegensseite. Das reiche Depot-Dialog-Modul (Performance, TWR/XIRR, erkannte Kaeufe) lag
// bisher tot da, weil es nur ueber ein modulepreload geladen, aber nie ausgefuehrt wurde - hier
// ist der eigentliche Fehler: dieses Modul importierte niemand.
import '../../features/investment-performance.js';

let ctx = null;
let bound = false;
let openBankConnectionDialog = () => {};

function use(context) {
  if (context) ctx = context;
  if (!ctx) throw new Error('Accounts feature context is not initialized.');
  return ctx;
}

const $ = selector => document.querySelector(selector);
const api = (path, options) => ctx.api(path, options);
const bankApi = (path, options) => ctx.bankApi(path, options);
const get = key => ctx.get(key);
const esc = value => ctx.esc(value);
const date = value => ctx.date(value);
const dateTime = value => ctx.dateTime(value);
const toast = (...args) => ctx.toast(...args);
const jsonBody = (...args) => ctx.jsonBody(...args);
const dialog = (html, options = {}) => ctx.dialog(html, options);
const empty = (el, message) => ctx.empty(el, message);
const acctId = last4 => last4 ? ` · ${maskIdentifier(last4)}` : '';

// Welches Depot zu welchem Konto gehoert - gefuellt beim Laden der Liste.
let depotByAccount=new Map();

// Auffaellige Luecken in der Buchungshistorie je Konto (#131, Abschnitt 13). Der Server misst sie am
// eigenen Rhythmus des Kontos, nicht an einer festen Tagesgrenze - hier steht nur, was er gefunden
// hat, und nichts wird nachgerechnet.
let gapsByAccount=new Map();

// Mirrors the server's gate on PUT api/accounts/{id}/balance: an account without a bank connection
// keeps its balance by hand. The UI used to ask for provider === 'manual' alone, so an imported
// account - the one kind that has no connection AND no way to be synced - had no way to be given a
// balance at all, and sat in the list reading "Kontostand nicht verfügbar" and out of net worth,
// even though the server has accepted one for it since P0-4.
const canSetBalance = account =>
  !account.bankConnectionId;


function openAccountActionsDialog(account, groups) {
  const isManual = account.provider === 'manual' && !account.bankConnectionId;
  // Only a link the owner made can be taken back. A row excluded by the automatic same-IBAN rule keeps
  // offering "link", because saying it out loud is what makes the decision theirs and reversible.
  const linked = account.duplicateLinkExplicit === true;
  // Die Kontodetails stehen zuerst: das Auslassungszeichen ist seit #125 der Weg dorthin, und alles
  // Weitere ist eine Abkuerzung in dieselbe Verwaltung.
  const actions = [
    ['detail', get('accounts.details'), false],
    ['coach', get('accounts.askCoach'), false],
    ['visual', get('accounts.editVisual'), false],
    ...(groups || []).length ? [['move', get('accounts.moveToGroup'), false]] : [],
    ['rename', get('accounts.rename'), false],
    ...(canSetBalance(account) ? [['balance', get('accounts.updateBalance'), false]] : []),
    [linked ? 'unlink' : 'link', get(linked ? 'accounts.unlinkSame' : 'accounts.linkSame'), false],
    ...(isManual ? [['delete', get('accounts.delete'), true]] : [])
  ];

  const dlg = dialog(`<div class="dialog-card more-sheet account-actions-sheet">
    <div class="panel-head"><div><h2>${esc(account.displayName || account.institutionName)}</h2><div class="row-sub">${esc(account.institutionName || '')}</div></div><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div>
    <div class="more-list">${actions.map(([key, label, danger]) => `<button type="button" data-account-action="${key}" class="${danger ? 'more-list-danger' : ''}"><span>${esc(label)}</span></button>`).join('')}</div>
  </div>`, { mobileMode: 'sheet' });

  dlg.querySelector('[data-close]')?.addEventListener('click', () => dlg.close());
  dlg.querySelectorAll('[data-account-action]').forEach(button => button.addEventListener('click', () => {
    const action = button.dataset.accountAction;
    dlg.close();

    if (action === 'detail') {
      ctx.showView('account-detail', { query: 'id=' + encodeURIComponent(account.id) });
    } else if (action === 'coach') {
      window.dispatchEvent(new CustomEvent('fullworth:coach-open', { detail: {
        entityType: 'account',
        entityId: account.id,
        entityLabel: account.displayName || account.institutionName || get('accounts.title'),
        details: {
          balance: String(account.latestBalance?.amount ?? ''),
          currency: account.latestBalance?.currency || account.currency || '',
          kind: account.accountType || account.product || ''
        }
      }}));
    } else if (action === 'visual') {
      editAccountVisualById(account.id).catch(console.error);
    } else if (action === 'move') {
      openMoveToGroupDialog(account, groups);
    } else if (action === 'rename') {
      openAccountNameDialog(account);
    } else if (action === 'balance') {
      openBalanceDialog(account);
    } else if (action === 'link') {
      openAccountLinkDialog(account).catch(console.error);
    } else if (action === 'unlink') {
      unlinkAccount(account).catch(console.error);
    } else if (action === 'delete') {
      deleteAccount(account);
    }
  }));
  dlg.showModal();
}

// Eine Kontozeile: Symbol links (das setzt presentation.js), Name und Herkunft zusammen, Betrag
// rechts - und genau eine tertiaere Aktion.
//
// Vorher standen hier bis zu fuenf Rundknoepfe nebeneinander (Gruppe, Umbenennen, Kontostand,
// Loeschen, Mehr) plus ein Coach-Knopf und ein Stift, die presentation.js nachtraeglich einhaengte.
// Alle diese Wege gibt es weiter - im Menue hinter dem Auslassungszeichen und auf der Detailseite.
function accountRow(x,groups){
  const isManual=x.provider==='manual'&&!x.bankConnectionId;
  const kind=[x.product||x.accountType,isManual?get('accounts.manual'):null].filter(Boolean).join(' · ');
  const nativeAmt=x.latestBalance?money(x.latestBalance.amount,x.latestBalance.currency):'—';
  // What the headline figure IS - available (pending already deducted) or booked (not yet). One word
  // under the amount, because that is where the question is asked.
  const meaningLine=balanceMeaningLine(x.latestBalance,get,esc);
  const convertedAmt=x.baseValue!=null?`<div class="amount-converted">${converted(x.baseValue,x.baseCurrency)}</div>`:'';
  // Ein Depot steht seit #133 als eigene Zeile hier, mit dem Kurswert seiner Bestaende. Das erklaert
  // auch den Unterschied, den man sonst sucht: die Summe ueber dieser Liste enthaelt den Wert, das
  // Nettovermoegen zaehlt ihn als Depot - dasselbe Geld, einmal, nur an zwei Stellen benannt.
  // accountType==='securities' schreibt genau eine Stelle (die FinTS-Depotuebernahme).
  // Unter dem Kurswert steht, was er wert GEWORDEN ist. Ohne Einstand wird das gesagt, nicht
  // verschwiegen und schon gar nicht als 0 % behauptet.
  const depotLine=x.accountType==='securities'
    ? `<div class="amount-meaning" title="${esc(get('accounts.depotValueHint'))}">${esc(get('accounts.depotValue'))}</div>`
      +portfolioGainLine(depotByAccount.get(x.id),x.currency)
    : '';
  // A wallet-per-currency account (PayPal, Wise, Revolut) holds money in more than one currency. The
  // headline shows one of them, so the others are listed here - they used to be invisible entirely.
  const otherWallets=(x.balances||[]).slice(1);
  const walletsLine=otherWallets.length
    ? `<div class="amount-wallets">${otherWallets.map(b=>esc(money(b.amount,b.currency))).join(' · ')}</div>`
    : '';
  // The same bank account reached through a second provider - or one the owner declared the same as
  // another - stays visible with everything it holds, but it is out of the totals, and the row has to
  // say why. A link the owner made says so in their words; the automatic same-IBAN match says "doppelt".
  const duplicateNote=x.duplicateOfDisplayName
    ? ` · ${esc(get(x.duplicateLinkExplicit?'accounts.countedAs':'accounts.duplicateOf').replace('{name}',x.duplicateOfDisplayName))}`
    : '';
  // Two different dates, and only one of them is a "Datenstand". referenceDate is the date the figure
  // is valid FOR - the bank's own as-of date, or the day the owner read it off a statement - so a
  // balance anchored from last month reads as last month's. capturedAt is merely when FullWorth wrote
  // it down; calling that the data date claimed a freshness nobody had promised, so it is labelled as
  // what it is: when the figure was fetched.
  // Ein Wort in der Zeile, die Daten dazu auf der Detailseite: mehr passt hier nicht hin, und mehr
  // braucht es auch nicht, um jemanden hinsehen zu lassen.
  const gap=gapsByAccount.has(String(x.id))?` · ${esc(get('accounts.dataGap'))}`:'';
  const dataAsOf=x.latestBalance?.referenceDate
    ? ` · ${esc(get('accounts.dataAsOf'))}: ${esc(date(x.latestBalance.referenceDate))}`
    : x.latestBalance?.capturedAt
      ? ` · ${esc(get('accounts.retrievedAt'))}: ${esc(dateTime(x.latestBalance.capturedAt))}`
      : '';
  const balanceSource=x.latestBalance?.source==='manual'||x.latestBalance?.source==='import'
    ? ` · ${esc(get('accounts.source_'+x.latestBalance.source))}`
    : '';
  // An account that can carry its own balance and has none yet is not broken, it is unfinished -
  // say so instead of showing a bare em dash next to it.
  const needsBalance=!x.latestBalance&&canSetBalance(x)
    ? ` · ${esc(get('accounts.needsBalance'))}`
    : '';
  // Der Name der Bank, wenn er ein anderer ist als der, den der Benutzer vergeben hat. Auf dem Handy
  // bleibt er weg - dort steht er auf der Detailseite, und die Zeile hat den Platz nicht.
  const providerName=x.providerDisplayName&&x.providerDisplayName!==(x.displayName||'')
    ? ` · ${esc(x.providerDisplayName)}`
    : '';
  const row=document.createElement('div');row.className='row';
  const moreBtn=`<button type="button" class="${buttonClass(ButtonRole.Icon,'account-more')}" data-account-more title="${esc(get('accounts.moreActions'))}" aria-label="${esc(get('accounts.moreActions'))}">⋯</button>`;
  row.innerHTML=`<div class="row-main"><div class="row-title">${esc(x.displayName||x.institutionName)}</div><div class="row-sub">${esc(x.institutionName)}${acctId(x.ibanLast4)}<span class="row-sub-wide">${providerName}${kind?` · ${esc(kind)}`:''}${dataAsOf}${balanceSource}</span>${needsBalance}${gap}${duplicateNote}</div></div><div class="row-end"><div class="amount-stack"><div class="amount">${nativeAmt}</div>${meaningLine}${depotLine}${walletsLine}${convertedAmt}</div>${moreBtn}</div>`;
  row.querySelector('[data-account-more]')?.addEventListener('click',()=>openAccountActionsDialog(x,groups));
  // Drill-down (UX rework §3): the account row itself opens that account's bookings; management
  // controls keep their own click and are excluded here.
  //
  // Ein Depot ist die Ausnahme, und zwar keine willkuerliche: es HAT keine Buchungen. Die Bank
  // liefert dafuer eine Bestandsaufstellung (HKWPD), keine Umsaetze - Kaeufe und Verkaeufe waeren
  // ein eigener Geschaeftsvorfall. Wer auf ein Depot klickt, landete deshalb zuverlaessig in einer
  // leeren Liste, die aussah, als fehle etwas. Gemeint ist die Vermoegensansicht: dort stehen die
  // Positionen und ihr Verlauf.
  row.dataset.accountId=x.id;row.classList.add('is-drillable');row.setAttribute('role','button');row.tabIndex=0;
  const target=x.accountType==='securities'
    ? ()=>{void openDepotDialog(x);}
    : ()=>ctx.showView('transactions',{query:'accountId='+encodeURIComponent(x.id)});
  const drill=e=>{if(e.target.closest('button,a,input,select'))return;target()};
  row.addEventListener('click',drill);
  row.addEventListener('keydown',e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();drill(e)}});
  return row;
}
// Das Depot von innen: die Papiere, ihr Wert, und - sobald ein Einstand da ist - der Gewinn.
//
// Ein Klick aufs Depot landete auf der Vermoegensseite. Die nennt Depots beim NAMEN und sonst
// nichts: keine Position, kein Kurs, kein Gewinn. Wer sehen wollte, was drin liegt, hatte keinen Weg.
//
// Gerechnet wird hier nichts. PortfolioValuationService kennt Einstand, Marktwert und
// unrealisiertes Ergebnis je Position laengst - es hat nur nie jemand danach gefragt.
async function openDepotDialog(account){
  let portfolios;
  try{ portfolios=await api('api/investments/portfolios'); }
  catch(err){ toast(err.message||get('common.error')); return; }

  const portfolio=(portfolios||[]).find(item=>item.accountId===account.id);
  if(!portfolio){ toast(get('accounts.depotNoPortfolio')); return; }

  const dlg=dialog(`<div class="dialog-card"><div class="panel-head"><div><h2>${esc(account.displayName||account.institutionName)}</h2><div class="row-sub">${esc(portfolio.name)}</div></div><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><div data-depot-body></div></div>`);
  const body=dlg.querySelector('[data-depot-body]');
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();

  const show=async()=>{
    let overview;
    try{ overview=await api('api/investments/portfolios/'+encodeURIComponent(portfolio.id)+'/overview'); }
    catch(err){ toast(err.message||get('common.error')); return; }

    const currency=overview.portfolio?.currency||account.currency||'EUR';
    const positions=overview.positions||[];
    // Ein fehlender Einstand ist keine Null. Er wird benannt, nicht als 0 % ausgegeben.
    const anyCost=positions.some(item=>item.costBasis!=null&&Number(item.costBasis)>0);

    const rows=positions.map(item=>{
      const value=item.marketValue!=null?money(Number(item.marketValue),item.priceCurrency||currency):'—';
      const unit=[item.quantity!=null?nf(item.quantity)+' ×':null,
        item.price!=null?money(Number(item.price),item.priceCurrency||currency):null].filter(Boolean).join(' ');
      const gain=item.unrealizedResult!=null&&item.costBasis!=null&&Number(item.costBasis)>0
        ? gainLine(Number(item.unrealizedResult),Number(item.costBasis),item.priceCurrency||currency)
        // Kein Einstand heisst NICHT "kein Gewinn": es heisst unbekannt, und das gehoert hingeschrieben.
        : `<div class="row-sub">${esc(get('accounts.depotGainUnknown'))}</div>`;
      return `<div class="row"><div class="row-main"><div class="row-title">${esc(item.name)}</div><div class="row-sub">${esc(unit)}</div></div><div class="row-end"><div class="amount-stack"><div class="amount">${value}</div>${gain}</div></div></div>`;
    }).join('');

    // data-portfolio traegt keinen eigenen Klick-Handler: der globale Lauscher in
    // investment-performance-ui.js faengt ihn ab, schliesst diesen Dialog (er ist der naechste
    // <dialog>-Vorfahr) und oeffnet an seiner Stelle den reichen Depot-Dialog mit Performance-Tab.
    body.innerHTML=`<div class="row"><div class="row-main"><div class="row-title">${esc(get('accounts.depotTotal'))}</div><div class="row-sub">${esc(get('accounts.depotPositions'))}: ${positions.length}</div></div><div class="amount">${money(Number(overview.totalValue||0),currency)}</div></div><div class="rows">${rows||emptyRow(get('accounts.depotEmpty'))}</div>${anyCost?'':`<p class="row-sub">${esc(get('accounts.depotNoCostBasis'))}</p>`}<div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-portfolio="${esc(portfolio.id)}">${esc(get('accounts.depotHistory'))}</button><button type="button" class="${buttonClass(ButtonRole.Primary)}" data-add-trade${positions.length?'':' disabled'}>${esc(get('accounts.depotAddTrade'))}</button></div>`;

    body.querySelector('[data-add-trade]').onclick=()=>openTradeDialog(portfolio,positions,currency,show);
  };

  await show();dlg.showModal();
}

// Der Gewinn eines ganzen Depots, wie er in einer Kontozeile Platz hat.
//
// Drei Faelle, drei Antworten: kein Depot (nichts), kein Einstand (gesagt), Einstand da (Betrag und
// Prozent). Die Prozentzahl entsteht erst hier - der Server liefert Einstand und Ergebnis roh, weil
// nur die Anzeige weiss, ob sie eine zeigen will.
export function portfolioGainLine(portfolio,fallbackCurrency){
  if(!portfolio)return '';
  const currency=portfolio.currency||fallbackCurrency||'EUR';
  if(portfolio.costBasis==null||portfolio.unrealizedResult==null)
    return `<div class="amount-meaning">${esc(get('accounts.depotGainUnknown'))}</div>`;

  const result=Number(portfolio.unrealizedResult);
  const cost=Number(portfolio.costBasis);
  const sign=result>0?'+':'';
  const tone=result>0?' positive':result<0?' negative':'';
  // Unvollstaendig heisst: die Zahl stimmt fuer das, was sie kennt - und sagt, dass sie nicht alles
  // kennt. Sie zu verschweigen waere so falsch wie sie fuer vollstaendig auszugeben.
  const partial=portfolio.gainIncomplete?' '+esc(get('accounts.depotGainPartial')):'';
  const percent=cost>0?` · ${sign}${nf((result/cost)*100)} %`:'';
  return `<div class="amount-meaning${tone}">${sign}${esc(money(result,currency))}${esc(percent)}${partial}</div>`;
}

// Gewinn UND Prozent - die Prozentzahl ist die, nach der eigentlich gefragt wird.
function gainLine(result,cost,currency){
  const sign=result>0?'+':'';
  const tone=result>0?' positive':result<0?' negative':'';
  const percent=cost>0?' · '+sign+nf((result/cost)*100)+' %':'';
  return `<div class="row-sub${tone}">${esc(get('accounts.depotGain'))}: ${sign}${money(result,currency)}${esc(percent)}</div>`;
}

const nf=value=>new Intl.NumberFormat(state.lang==='de'?'de-DE':'en-US',{maximumFractionDigits:2}).format(Number(value)||0);

// Der Kauf, den die Bank nicht liefert.
//
// HKWPD ist eine Momentaufnahme: was heute im Depot liegt und was es heute wert ist. Was es
// GEKOSTET hat, steht nirgends - und ohne Einstand gibt es keinen Gewinn und keine Prozentzahl.
// Rueckwirkend liefert die Bank das auch nicht nach; der Eigentuemer kennt seine Kaeufe aber von
// seinem Girokonto.
//
// Die Auswahl kommt aus den POSITIONEN dieses Depots, nicht aus allen Wertpapieren: gekauft wurde,
// was drinliegt, und damit steht die Wertpapierkennung von vornherein richtig.
function openTradeDialog(portfolio,positions,currency,done){
  const today=new Date().toISOString().slice(0,10);
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('accounts.depotAddTrade'))}</h2><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><label><span>${esc(get('accounts.tradeSecurity'))}</span><select name="security" required>${positions.map(item=>`<option value="${esc(item.securityId)}">${esc(item.name)}</option>`).join('')}</select></label><label><span>${esc(get('accounts.tradeDate'))}</span><input type="date" name="date" value="${today}" max="${today}" required></label><label><span>${esc(get('accounts.tradeQuantity'))}</span><input type="number" name="quantity" step="0.00001" min="0.00001" required></label><label><span>${esc(get('accounts.tradePrice'))}</span><input type="number" name="price" step="0.0001" min="0" required></label><label><span>${esc(get('accounts.tradeFees'))}</span><input type="number" name="fees" step="0.01" min="0" value="0"></label><p class="row-sub" data-trade-total></p><div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(get('common.save'))}</button></div></form>`);

  const form=dlg.querySelector('form');
  const total=dlg.querySelector('[data-trade-total]');
  const amount=()=>{
    const fd=new FormData(form);
    const quantity=Number(fd.get('quantity'))||0;
    const price=Number(fd.get('price'))||0;
    const fees=Number(fd.get('fees'))||0;
    return Math.round((quantity*price+fees)*100)/100;
  };
  const refresh=()=>{ total.textContent=get('accounts.tradeTotal')+': '+money(amount(),currency); };
  form.oninput=refresh;refresh();

  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  form.onsubmit=async event=>{
    event.preventDefault();
    if(!form.reportValidity())return;
    const submit=form.querySelector('[type="submit"]');submit.disabled=true;
    const fd=new FormData(form);
    try{
      await api('api/investments/portfolios/'+encodeURIComponent(portfolio.id)+'/trades',jsonBody({
        securityId:String(fd.get('security')),
        tradeType:'buy',
        tradeDate:String(fd.get('date')),
        quantity:Number(fd.get('quantity')),
        price:Number(fd.get('price')),
        amount:amount(),
        currency,
        fees:Number(fd.get('fees'))||0
      }));
      dlg.close();
      toast(get('accounts.tradeSaved'));
      await done();
    }catch(err){ submit.disabled=false; toast(err.message||get('common.error')); }
  };
  dlg.showModal();
}
// Woran die Suche misst. Der Anbietername gehoert dazu, auch wo er nicht sichtbar ist: wer sein Konto
// umbenannt hat, sucht es trotzdem manchmal unter dem Namen, den die Bank ihm gab.
const accountSearchText=a=>[a.displayName,a.providerDisplayName,a.institutionName,a.product,a.accountType,a.ibanLast4]
  .filter(Boolean).join(' ').toLowerCase();

async function loadAccountsView(){
  // Die Depots kommen mit: ihr Gewinn gehoert in die Zeile, und die Liste liefert ihn fuer alle auf
  // einmal. Faellt der Aufruf aus, fehlt die Prozentzahl - die Kontenliste steht trotzdem.
  const [accounts,groups,portfolios,gaps]=await Promise.all([
    api('api/accounts'),
    api('api/account-groups').catch(()=>[]),
    api('api/investments/portfolios').catch(()=>[]),
    api('api/transactions/data-gaps').catch(()=>[])
  ]);
  depotByAccount=new Map((portfolios||[]).filter(item=>item.accountId).map(item=>[item.accountId,item]));
  // Nur die groesste je Konto steht in der Liste - sie kommt als erste, der Server sortiert
  // absteigend nach Laenge. Alle stehen auf der Kontodetailseite.
  gapsByAccount=new Map();
  for(const item of gaps||[])if(!gapsByAccount.has(String(item.accountId)))gapsByAccount.set(String(item.accountId),item);
  // Die Liste entsteht außerhalb des Dokuments und wird erst eingesetzt, wenn sie fertig ist -
  // samt Symbolen und Knöpfen. Vorher wurden die Zeilen gezeichnet und danach geschmückt, und jede
  // wuchs dabei von 73 auf 125 Pixel; die Seite sprang um 0,32.
  const list=$('#accounts-view-list');
  const staging=document.createElement('div');
  // Archived accounts (IsActive=false, e.g. a deleted manual account) are hidden from the list -
  // except an imported one. A Finanzguru export carries only bookings, no balance, so the import
  // creates the account archived and out of net worth until it is given one. Hiding it meant there
  // was NO path to that balance anywhere in the app: the account existed, carried its history, and
  // was invisible. It is listed now and says what it needs.
  const query=($('#accounts-search')?.value||'').trim().toLowerCase();
  const allAccounts=(accounts||[]).filter(a=>a.isActive!==false);
  const visibleAccounts=query?allAccounts.filter(a=>accountSearchText(a).includes(query)):allAccounts;
  const groupList=(groups||[]).slice().sort((a,b)=>(a.sortOrder-b.sortOrder)||a.name.localeCompare(b.name));
  const baseCur=state.space?.baseCurrency||'EUR';
  const collapsed=new Set(JSON.parse(localStorage.getItem('finance.groupsCollapsed')||'[]'));
  const byGroup=new Map();
  for(const a of visibleAccounts){const k=a.groupId||'';if(!byGroup.has(k))byGroup.set(k,[]);byGroup.get(k).push(a);}
  // Group subtotal in the base currency: the converted baseValue for foreign accounts, the native
  // amount for base-currency accounts. An account whose money could NOT be converted is left out -
  // adding a foreign figure into a base-currency total would be arithmetic across units - but leaving
  // it out silently printed a confident number that was missing real money, so the subtotal now says
  // so. Ein Konto ganz OHNE Kontostand galt hier frueher als "hat eben noch keinen Wert" und ging
  // still als 0 ein. Seit ein Import solche Konten anlegt, ist das die eine Antwort, die sicher
  // falsch ist - es fehlt ein Wert, und die Summe sagt das jetzt genauso wie beim fehlenden Kurs.
  const total=accts=>{
    const reasons=new Set();
    let sum=0;
    for(const a of accts){
      // An account that is out of net worth - a same-IBAN duplicate, or one the owner linked as the
      // same account - must be out of this subtotal too, or the group header contradicts the totals
      // right above it and the money still reads as counted twice.
      if(a.includeInNetWorth===false)continue;
      if(a.baseValue!=null){sum+=Number(a.baseValue);continue}
      if(a.latestBalance&&a.latestBalance.currency===baseCur){sum+=Number(a.latestBalance.amount);continue}
      reasons.add(a.latestBalance?'fx':'noBalance');
    }
    return{sum,reasons};
  };
  const totalMarkup=accts=>{
    const t=total(accts);
    const mark=incompleteMarker([
      t.reasons.has('fx')?esc(get('common.fxIncomplete')):'',
      t.reasons.has('noBalance')?esc(get('common.balanceMissingIncomplete')):''
    ]);
    return `${money(t.sum,baseCur)}${mark}`;
  };
  const countLabel=n=>esc(get(n===1?'accounts.countOne':'accounts.countMany').replace('{count}',n));
  // Gesamt (#125): die Summe der Konten DIESER Seite, nicht das Nettovermoegen - Immobilien und Depots
  // stehen dort und nicht hier. Ein Klick oeffnet die Buchungen aller dieser Konten, also ungefiltert.
  const totalRow=()=>{
    const head=document.createElement('div');head.className='row accounts-total is-drillable';
    head.setAttribute('role','button');head.tabIndex=0;
    head.innerHTML=`<div class="row-main"><div class="row-title">${esc(get('accounts.total'))}</div><div class="row-sub">${countLabel(visibleAccounts.length)}</div></div><div class="row-end"><span class="amount">${totalMarkup(visibleAccounts)}</span><span class="row-chevron" aria-hidden="true">›</span></div>`;
    const open=()=>ctx.showView('transactions',{query:''});
    head.addEventListener('click',open);
    head.addEventListener('keydown',e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();open()}});
    return head;
  };
  // Group header. Collapse state persists in localStorage.
  const renderBucket=(g,accts)=>{
    const gid=g.id;const isCollapsed=collapsed.has(gid);
    const head=document.createElement('div');head.className='row group-head';head.dataset.groupId=gid;
    // The chevron only expands/collapses; the name is a separate drill-down that opens all bookings of
    // the group's accounts (UX rework §3). The name keeps class `group-toggle` for accounts-ux decoration.
    const toggle=()=>{collapsed.has(gid)?collapsed.delete(gid):collapsed.add(gid);localStorage.setItem('finance.groupsCollapsed',JSON.stringify([...collapsed]));loadAccountsView();};
    head.innerHTML=`<div class="row-main"><button type="button" class="group-chevron" data-toggle aria-label="${esc(get(isCollapsed?'nav.expand':'nav.collapse'))}">${isCollapsed?'▸':'▾'}</button><button type="button" class="group-toggle is-drillable" data-group-open>${esc(g.name)}</button></div><div class="row-side"><span class="row-sub group-count">${countLabel(accts.length)}</span><span class="amount">${totalMarkup(accts)}</span></div>`;
    head.querySelector('[data-toggle]').addEventListener('click',toggle);
    head.querySelector('[data-group-open]').addEventListener('click',()=>ctx.showView('transactions',{query:'groupId='+encodeURIComponent(g.id)}));
    staging.appendChild(head);
    if(!isCollapsed)for(const a of accts)staging.appendChild(accountRow(a,groupList));
  };
  staging.appendChild(totalRow());
  // Es gibt immer mindestens eine Gruppe: der Server legt die Standardgruppe an und sortiert Konten
  // ohne eigene Gruppe dort ein. Faellt die Gruppenliste trotzdem leer aus - etwa weil sie nicht
  // geladen werden konnte - stehen die Konten weiterhin da, nur ohne Kopfzeile.
  if(!groupList.length){
    for(const x of visibleAccounts)staging.appendChild(accountRow(x,groupList));
  }else{
    for(const g of groupList)renderBucket(g,byGroup.get(g.id)||[]);
    for(const a of byGroup.get('')||[])staging.appendChild(accountRow(a,groupList));
  }
  if(!visibleAccounts.length)empty(staging,query?get('accounts.searchEmpty'):undefined);
  // Erst schmücken, dann einsetzen. Der zweite Aufruf danach ist für alles, was am Dokument hängt -
  // die Leiste und die Ungelesen-Punkte.
  await enhanceAccountsPresentation(staging);
  list.replaceChildren(...staging.childNodes);
  await enhanceAccountsPresentation();
}
function openAccountNameDialog(account){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('common.edit'))}: ${esc(get('accounts.name'))}</h2><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><label>${esc(get('accounts.name'))}<input name="name" required maxlength="120" value="${esc(account.displayName||account.institutionName||'')}"></label><div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(get('common.save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const displayName=String(new FormData(e.currentTarget).get('name')||'').trim();
    try{
      await api(`api/accounts/${account.id}`,{...jsonBody({displayName,isActive:null,includeInNetWorth:null,sortOrder:null}),method:'PATCH'});
      dlg.close();toast(get('common.saved'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}
// Delete (archive) a manual account; it then disappears from the list (archived accounts are hidden).
async function deleteAccount(account){
  const name=account.displayName||account.institutionName;
  if(!await ctx.confirm(get('accounts.deleteConfirm').replace('{name}',()=>name),{destructive:true,confirmLabel:get('accounts.delete')}))return;
  try{await api(`api/accounts/${account.id}`,{method:'DELETE'});toast(get('accounts.deleted'));await loadAccountsView()}
  catch(err){toast(err.message||get('common.error'))}
}
// Two accounts can be the same real-world account without sharing an IBAN: a PayPal, Wise or Revolut
// wallet, a cash account and a manual one have none at all, so every automatic same-IBAN check is blind
// to them. This is the owner's own decision, and it is only about counting - the picked account keeps
// counting, this one stays in the list with all of its bookings and balances and drops out of the
// totals. The picker therefore offers EVERY other account of the space, wallets included.
async function openAccountLinkDialog(account){
  let state;
  try{state=await api(`api/accounts/${account.id}/link`)}
  catch(err){toast(err.message||get('common.error'));return}
  // No chains: an account other rows are already counted as cannot itself become a duplicate. Say that
  // here instead of letting the server's conflict surface as a raw message.
  if((state?.linkedToThis||[]).length){toast(get('accounts.linkIsTarget'));return}
  // A candidate that is itself linked, or already out of the totals, cannot carry the money - the
  // server refuses both, so they are not offered.
  const candidates=(state?.candidates||[]).filter(c=>!c.isLinked&&c.includeInNetWorth);
  if(!candidates.length){toast(get('accounts.linkNoCandidates'));return}
  const label=c=>{
    const name=c.displayName||c.institutionName||'';
    const bank=c.institutionName&&c.institutionName!==name?` · ${c.institutionName}`:'';
    return `${name}${bank}${c.ibanLast4?` · ${maskIdentifier(c.ibanLast4)}`:''}`;
  };
  const opts=candidates.map(c=>`<option value="${esc(c.id)}"${state.duplicateOfAccountId===c.id?' selected':''}>${esc(label(c))}</option>`).join('');
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><div><h2>${esc(get('accounts.linkSame'))}</h2><div class="row-sub">${esc(account.displayName||account.institutionName)}</div></div><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div>
    <p class="row-sub">${esc(get('accounts.linkSameHint'))}</p>
    <label>${esc(get('accounts.linkTarget'))}<select name="target">${opts}</select></label>
    <div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(get('common.save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();
    const duplicateOfAccountId=String(new FormData(e.currentTarget).get('target')||'');
    if(!duplicateOfAccountId)return;
    try{
      await api(`api/accounts/${account.id}/link`,{...jsonBody({duplicateOfAccountId}),method:'PUT'});
      dlg.close();toast(get('accounts.linked'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}
// Undo that decision. Nothing was ever moved, so there is nothing to move back: the account only
// returns to whatever it counted as before the link.
async function unlinkAccount(account){
  const name=account.duplicateOfDisplayName||account.displayName||account.institutionName;
  if(!await ctx.confirm(get('accounts.unlinkConfirm').replace('{name}',()=>name),{confirmLabel:get('accounts.unlinkSame')}))return;
  try{
    await api(`api/accounts/${account.id}/link`,{method:'DELETE'});
    toast(get('accounts.unlinked'));await loadAccountsView();
  }catch(err){toast(err.message||get('common.error'))}
}
// Create or rename an account group (§8.1).
async function openGroupDialog(existing){
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get(existing?'accounts.renameGroup':'accounts.newGroup'))}</h2><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><label>${esc(get('accounts.groupName'))}<input name="name" required maxlength="120" value="${esc(existing?.name||'')}"></label><div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(get(existing?'common.save':'common.create'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const name=new FormData(e.currentTarget).get('name');
    try{
      if(existing)await api(`api/account-groups/${existing.id}`,{...jsonBody({name,sortOrder:existing.sortOrder}),method:'PUT'});
      else await api('api/account-groups',jsonBody({name,sortOrder:null}));
      dlg.close();toast(get('common.saved'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}
async function deleteGroup(g){
  if(!await ctx.confirm(get('accounts.deleteGroupConfirm').replace(/\{name\}/g,()=>g.name),{destructive:true,confirmLabel:get('accounts.deleteGroup')}))return;
  try{await api(`api/account-groups/${g.id}`,{method:'DELETE'});toast(get('accounts.groupDeleted'));await loadAccountsView()}
  catch(err){toast(err.message||get('common.error'))}
}
// Move an account into a group (or "Ungrouped" = clear). Owner-gated server-side.
function openMoveToGroupDialog(account,groups){
  const opts=[`<option value="">${esc(get('accounts.ungrouped'))}</option>`].concat((groups||[]).map(g=>`<option value="${g.id}"${account.groupId===g.id?' selected':''}>${esc(g.name)}</option>`)).join('');
  const dlg=dialog(`<form class="dialog-card"><div class="panel-head"><h2>${esc(get('accounts.moveToGroup'))}</h2><button type="button" data-close aria-label="${esc(get('common.close'))}">×</button></div><label>${esc(get('accounts.groups'))}<select name="group">${opts}</select></label><div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(get('common.save'))}</button></div></form>`);
  dlg.querySelector('[data-close]').onclick=()=>dlg.close();
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const groupId=new FormData(e.currentTarget).get('group')||null;
    try{await api(`api/accounts/${account.id}/group`,{...jsonBody({groupId}),method:'PUT'});dlg.close();await loadAccountsView()}
    catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}
// Disconnect a bank: permanently deletes the connection and all of its synced accounts + data.
function openAddAccountDialog(){
  const dlg=dialog(`<form method="dialog" class="dialog-card"><div class="panel-head"><h2>${esc(get('accounts.add'))}</h2><button value="cancel" data-close>×</button></div><div class="choice-grid"><button type="button" data-choice="bank"><strong>${esc(get('accounts.addBank'))}</strong><span>${esc(get('accounts.addBankHint'))}</span></button><button type="button" data-choice="manual"><strong>${esc(get('accounts.addManual'))}</strong><span>${esc(get('accounts.addManualHint'))}</span></button></div></form>`);
  dlg.querySelector('[data-choice="bank"]').addEventListener('click',async()=>{dlg.close();await openBankConnectionDialog()});
  dlg.querySelector('[data-choice="manual"]').addEventListener('click',()=>{dlg.close();openManualAccountDialog()});
  dlg.showModal();
}
function openManualAccountDialog(){
  const currency=state.space?.baseCurrency||'EUR';
  const dlg=dialog(`<form class="dialog-card"><h2>${esc(get('accounts.addManual'))}</h2><label>${esc(get('accounts.name'))}<input name="name" required maxlength="120" placeholder="${esc(get('accounts.namePlaceholder'))}"></label><label>${esc(get('accounts.institution'))}<input name="institution" maxlength="120" placeholder="${esc(get('accounts.institutionPlaceholder'))}"></label><label>${esc(get('purchases.currency'))}<input name="currency" value="${esc(currency)}" maxlength="3" required></label><label>${esc(get('accounts.startBalance'))}<input name="balance" type="number" step="0.01" inputmode="decimal" placeholder="0,00"></label><div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(get('common.create'))}</button></div></form>`);
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const fd=new FormData(e.currentTarget);
    if(!state.space){toast(get('common.error'));return}
    try{
      const created=await api('api/accounts',jsonBody({fullWorthSpaceId:state.space.id,bankConnectionId:null,displayName:fd.get('name'),currency:fd.get('currency'),includeInNetWorth:true,sortOrder:0,institutionName:fd.get('institution')||null,initialBalance:fd.get('balance')===''?null:Number(fd.get('balance'))}));
      await applyManualAccountVisual(created);
      dlg.close();toast(get('accounts.created'));await loadAccountsView();
    }catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
  decorateManualAccountDialog(dlg);
}
// A balance entered by hand is the only balance an unconnected or imported account has, so it also
// carries WHEN it is valid for and WHERE it was read off. Without an as-of date every anchor claimed
// to be today's figure, which is wrong the moment it comes off a statement.
function openBalanceDialog(account){
  const current=account.latestBalance?account.latestBalance.amount:'';
  const today=new Date();today.setMinutes(today.getMinutes()-today.getTimezoneOffset());
  const todayIso=today.toISOString().slice(0,10);
  const asOf=account.latestBalance?.referenceDate?String(account.latestBalance.referenceDate).slice(0,10):todayIso;
  const note=account.latestBalance?.note||'';
  const dlg=dialog(`<form class="dialog-card"><h2>${esc(get('accounts.updateBalance'))}</h2><div class="row-sub">${esc(account.displayName||account.institutionName)}</div><label>${esc(get('accounts.newBalance'))} (${esc(account.currency)})<input name="amount" type="number" step="0.01" inputmode="decimal" value="${current}" required></label><label>${esc(get('accounts.balanceAsOf'))}<input name="asOf" type="date" value="${esc(asOf)}" max="${esc(todayIso)}" required></label><label>${esc(get('accounts.balanceNote'))}<input name="note" type="text" maxlength="200" value="${esc(note)}" placeholder="${esc(get('accounts.balanceNoteHint'))}"></label><div class="dialog-actions"><button type="button" class="${buttonClass(ButtonRole.Secondary)}" data-cancel>${esc(get('common.cancel'))}</button><button type="submit" class="${buttonClass(ButtonRole.Primary)}">${esc(get('common.apply'))}</button></div></form>`);
  dlg.querySelector('[data-cancel]').onclick=()=>dlg.close();
  dlg.querySelector('form').onsubmit=async e=>{
    e.preventDefault();const fd=new FormData(e.currentTarget);
    const body={amount:Number(fd.get('amount')),currency:null,asOf:String(fd.get('asOf')||'')||null,note:String(fd.get('note')||'').trim()||null};
    try{await api(`api/accounts/${account.id}/balance`,{...jsonBody(body),method:'PUT'});dlg.close();toast(get('accounts.balanceUpdated'));await loadAccountsView()}catch(err){toast(err.message||get('common.error'))}
  };
  dlg.showModal();
}

// --- Kontodetails (#125) ---
//
// Die Uebersicht zeigt, was in eine Zeile passt. Alles Weitere steht hier, auf Handy wie auf
// Rechner - insbesondere der Originalname, den die Uebersicht auf schmalen Geraeten weglaesst.
//
// Die Seite liegt unter pages/accounts/detail/, gezeichnet wird sie von hier: sie ist dieselbe
// Sache wie die Liste und benutzt deren Dialoge zum Bearbeiten, statt sie ein zweites Mal zu bauen.
function detailRow(label,value){
  if(value===null||value===undefined||value==='')return '';
  return `<div class="row"><div class="row-main"><div class="row-title">${esc(label)}</div></div><div class="row-end"><span class="account-detail-value">${esc(value)}</span></div></div>`;
}

async function loadAccountDetail(){
  const id=new URLSearchParams(location.search).get('id')||'';
  const head=$('#account-detail-head'),info=$('#account-detail-info');
  if(!head||!info)return;
  const back=()=>ctx.showView('accounts');
  $('#account-detail-back').onclick=back;
  if(!id){back();return}

  let account=null;
  try{account=await api(`api/accounts/${encodeURIComponent(id)}`)}catch{account=null}
  if(!account){
    head.replaceChildren();
    info.innerHTML='';
    empty(info,get('accounts.notFound'));
    $('#account-detail-edit').hidden=true;
    return;
  }
  $('#account-detail-edit').hidden=false;

  // Gruppe und Verbindung stehen nicht am Konto, sondern daneben. Beides ist optional: faellt es aus,
  // fehlt die Zeile, statt dass dort ein erfundener Platzhalter steht.
  const [groups,connections,gaps]=await Promise.all([
    api('api/account-groups').catch(()=>[]),
    account.bankConnectionId?api('api/bank-connections').catch(()=>[]):Promise.resolve([]),
    api(`api/transactions/data-gaps?accountId=${encodeURIComponent(id)}`).catch(()=>[])
  ]);
  const group=(groups||[]).find(g=>g.id===account.groupId);
  const connection=(connections||[]).find(c=>c.id===account.bankConnectionId);

  const amount=account.latestBalance?money(account.latestBalance.amount,account.latestBalance.currency):'—';
  // Der Originalname steht hier auch dann, wenn er dem eigenen Namen entspricht - nur nicht zweimal.
  const ownName=account.displayName||account.institutionName||'';
  const providerName=account.providerDisplayName||'';
  head.innerHTML=`<div class="row-main account-detail-names"><div class="account-detail-name">${esc(ownName)}</div>${providerName&&providerName!==ownName?`<div class="account-detail-provider">${esc(providerName)}</div>`:''}</div><div class="account-detail-amount">${amount}</div>`;

  const dataAsOf=account.latestBalance?.referenceDate
    ? date(account.latestBalance.referenceDate)
    : account.latestBalance?.capturedAt
      ? dateTime(account.latestBalance.capturedAt)
      : '';
  info.innerHTML=[
    detailRow(get('accounts.ownName'),ownName),
    detailRow(get('accounts.providerName'),providerName),
    detailRow(get('accounts.bank'),account.institutionName),
    detailRow(get('accounts.identifier'),account.ibanLast4?maskIdentifier(account.ibanLast4):''),
    detailRow(get('accounts.kind'),account.product||account.accountType||''),
    detailRow(get('purchases.currency'),account.currency),
    detailRow(get('accounts.group'),group?group.name:''),
    detailRow(get('accounts.dataAsOf'),dataAsOf),
    detailRow(get('accounts.connection'),connection?connection.institutionName:get('accounts.noConnection')),
    // Ein Hinweis, keine Behauptung: was in der Luecke fehlt, weiss niemand, und FullWorth traegt
    // nichts nach. Der Satz darunter nennt die moeglichen Ursachen einmal, nicht je Luecke.
    ...(gaps||[]).map(item=>detailRow(get('accounts.dataGap'),
      get('accounts.dataGapRange').replace('{from}',date(item.from)).replace('{to}',date(item.to)))),
    (gaps||[]).length?`<div class="row"><div class="row-main"><div class="row-sub">${esc(get('accounts.dataGapHint'))}</div></div></div>`:''
  ].join('');

  // Dasselbe Symbol wie in der Liste, aus derselben Quelle.
  await decorateAccountIdentity(head,account);

  $('#account-detail-edit').onclick=()=>openAccountActionsDialog(account,groups||[]);
}

export function bindAccounts(context, openBank) {
  use(context);
  if (bound) return;
  bound = true;
  // Eine Bank zu verbinden gehoert zu den Bankverbindungen, nicht zu den Konten (#125). app.js reicht
  // den Weg dorthin herein, damit die Kontenseite die Bankdialoge nicht kennen muss.
  openBankConnectionDialog = openBank || openBankConnectionDialog;
  bindAccountsPresentation({
    openAdd: () => openAddAccountDialog()
  });
  $('#add-account')?.addEventListener('click', () => openAddAccountDialog());
  $('#add-group')?.addEventListener('click', () => toggleAccountGroupEditing().catch(console.error));
}

export async function renderAccounts(context) {
  use(context);
  return loadAccountsView();
}

export async function renderAccountDetail(context) {
  use(context);
  return loadAccountDetail();
}

export function openAddAccount(context) {
  use(context);
  return openAddAccountDialog();
}

