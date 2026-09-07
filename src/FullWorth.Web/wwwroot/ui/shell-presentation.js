import { bindCompensationNavigation, compensationMoreButton } from './compensation-navigation.js';

const $=selector=>document.querySelector(selector);
const $$=selector=>[...document.querySelectorAll(selector)];

export function createShellPresentation({
  state,
  i18n,
  get,
  primaryAction,
  dialog,
  navigate
}) {
  const media=matchMedia('(prefers-color-scheme: dark)');

  function renderTranslations(){
    i18n.apply(document);
    const reset=$('#layout-reset');
    if(reset){
      reset.querySelector('span').textContent=state.lang==='de'?'Layout zurücksetzen':'Reset layout';
      reset.querySelector('small').textContent=state.lang==='de'
        ?'Seitenleisten, Breiten und Panel-Zustand'
        :'Sidebars, widths and panel state';
    }
    $$('.sidebar button[data-view], #bottom-nav button[data-view]').forEach(button=>{
      const label=button.querySelector('span')?.textContent||'';
      if(label){button.title=label;button.setAttribute('aria-label',label)}
    });
  }

  function renderPageHeader(){
    const page=state.messages.pages?.[state.view];
    if(page){
      $('#page-title').textContent=page.title;
      $('#page-subtitle').textContent=page.subtitle;
    }
    const action=primaryAction[state.view];
    const button=$('#primary-action');
    if(action){
      button.hidden=false;
      button.textContent=get(action[0]);
      button.onclick=action[1];
    }else{
      button.hidden=true;
      button.onclick=null;
    }
  }

  function applyTheme(){
    const actual=state.theme==='system'?(media.matches?'dark':'light'):state.theme;
    document.documentElement.dataset.theme=actual;
    const meta=document.querySelector('meta[name="theme-color"]');
    if(meta)meta.setAttribute('content',actual==='dark'?'#121416':'#f5f6f7');
    const button=$('#theme-toggle');
    if(button)button.dataset.themePref=state.theme;
  }

  function renderUserBlock(){
    const space=state.space;
    $('#user-space-name').textContent=space?.name||'';
    $('#user-space-sub').textContent=space?.baseCurrency||'';
    $('#user-avatar').textContent=(space?.name||'F').trim().charAt(0).toUpperCase()||'F';
  }

  function openMoreSheet(moreViews,escape){
    const items=moreViews.map(view=>{
      const source=$(`.sidebar button[data-view="${view}"]`);
      const icon=source?source.querySelector('svg').outerHTML:'';
      const nav=get(`nav.${view}`);
      const label=view==='transactions'
        ? get('transactions.allTx')
        : (nav===`nav.${view}`?(state.messages.pages?.[view]?.title||view):nav);
      return `<button type="button" data-go="${view}" class="${state.view===view?'active':''}">${icon}<span>${escape(label)}</span></button>`;
    }).join('')+compensationMoreButton(escape(get('nav.compensation')));

    const dlg=dialog(
      `<form method="dialog" class="dialog-card more-sheet"><div class="panel-head"><h2>${escape(get('nav.more'))}</h2><button value="cancel" data-close>×</button></div><div class="more-list">${items}</div></form>`,
      {mobileMode:'sheet'});
    dlg.classList.add('more-sheet-dialog');
    dlg.querySelectorAll('[data-go]').forEach(button=>button.addEventListener('click',()=>{
      dlg.close();
      navigate(button.dataset.go,{query:''});
    }));
    bindCompensationNavigation(dlg);
    dlg.showModal();
  }

  return {
    applyTheme,
    media,
    openMoreSheet,
    renderPageHeader,
    renderTranslations,
    renderUserBlock
  };
}
