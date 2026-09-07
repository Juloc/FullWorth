import { state } from '../core/state.js';

const $=selector=>document.querySelector(selector);

export function createLayoutShell({ get, toast } = {}) {
  const layoutWidthMode=()=>window.innerWidth>=1024?'desktop':'tablet';
  const sidebarWidthKey=()=>`finance.sidebar.width.${layoutWidthMode()}`;
  const sidebarEffectivelyCollapsed=()=>document.body.classList.contains('nav-collapsed')||document.body.classList.contains('nav-auto-collapsed');

  function syncNavToggle(){
    const button=$('#nav-collapse');if(!button)return;
    const manual=document.body.classList.contains('nav-collapsed');
    const autoOnly=document.body.classList.contains('nav-auto-collapsed')&&!manual;
    const collapsed=manual||autoOnly;
    button.textContent=collapsed?'›':'‹';
    button.disabled=autoOnly;
    const label=autoOnly
      ? (state.lang==='de'?'Navigation wegen Platz automatisch eingeklappt':'Navigation automatically collapsed for available space')
      : get(collapsed?'nav.expand':'nav.collapse');
    button.setAttribute('aria-label',label);button.title=label;
  }

  function syncResponsiveSidebar(){
    const desktop=window.matchMedia('(min-width:768px)').matches;
    const manual=document.body.classList.contains('nav-collapsed');
    if(!desktop||manual){
      const changed=document.body.classList.contains('nav-auto-collapsed');
      document.body.classList.remove('nav-auto-collapsed');
      syncNavToggle();
      if(changed)queueMicrotask(()=>window.fwClampCoachWidth?.());
      return;
    }
    const desiredSidebar=Math.max(176,Number(localStorage.getItem(sidebarWidthKey()))||Number(localStorage.getItem('finance.sidebar.width'))||228);
    const coachKey=`finance.coach.dockWidth.${layoutWidthMode()}`;
    const coach=document.body.classList.contains('coach-dock-open')
      ? ($('#coach-dock')?.getBoundingClientRect().width||Number(localStorage.getItem(coachKey))||Number(localStorage.getItem('finance.coach.dockWidth'))||0)
      : 0;
    const minMain=window.innerWidth<1100?420:520;
    const shouldCollapse=window.innerWidth-desiredSidebar-coach<minMain;
    const changed=document.body.classList.contains('nav-auto-collapsed')!==shouldCollapse;
    document.body.classList.toggle('nav-auto-collapsed',shouldCollapse);
    syncNavToggle();
    if(changed)queueMicrotask(()=>window.fwClampCoachWidth?.());
  }

  function toggleSidebar(){
    const collapsed=!document.body.classList.contains('nav-collapsed');
    document.body.classList.toggle('nav-collapsed',collapsed);
    localStorage.setItem('finance.navCollapsed',collapsed?'1':'0');
    if(collapsed)document.body.classList.remove('nav-auto-collapsed');
    syncResponsiveSidebar();
  }

  function resetLayout(){
    ['finance.sidebar.width','finance.sidebar.width.desktop','finance.sidebar.width.tablet','finance.coach.dockWidth','finance.coach.dockWidth.desktop','finance.coach.dockWidth.tablet']
      .forEach(key=>localStorage.removeItem(key));
    localStorage.setItem('finance.navCollapsed','0');
    document.body.classList.remove('nav-collapsed','nav-auto-collapsed');
    document.documentElement.style.removeProperty('--sidebar-w');
    document.documentElement.style.removeProperty('--coach-dock-w');
    window.dispatchEvent(new CustomEvent('fullworth:layout-reset'));
    window.dispatchEvent(new Event('resize'));
    syncResponsiveSidebar();
    toast(state.lang==='de'?'Layout zurückgesetzt':'Layout reset');
  }

  function initResizableSidebar(){
    const sidebar=$('.sidebar');
    if(!sidebar)return;
    const minWidth=176;
    const defaults={desktop:228,tablet:196};
    const desktopMode=()=>window.matchMedia('(min-width:768px)').matches;
    const key=()=>sidebarWidthKey();
    const defaultWidth=()=>defaults[layoutWidthMode()];
    const savedWidth=()=>{
      const scoped=Number(localStorage.getItem(key()));
      if(scoped>0)return scoped;
      const legacy=Number(localStorage.getItem('finance.sidebar.width'));
      return legacy>0?legacy:defaultWidth();
    };
    const maxWidth=()=>{
      const coach=document.body.classList.contains('coach-dock-open')?$('#coach-dock')?.getBoundingClientRect().width||0:0;
      const minMain=window.innerWidth<1100?280:420;
      return Math.max(minWidth,Math.min(360,window.innerWidth-coach-minMain));
    };
    const handle=document.createElement('div');
    const apply=value=>{
      if(!desktopMode())return;
      const width=Math.max(minWidth,Math.min(maxWidth(),Math.round(Number(value)||savedWidth())));
      document.documentElement.style.setProperty('--sidebar-w',`${width}px`);
      handle.setAttribute('aria-valuemax',String(maxWidth()));
      handle.setAttribute('aria-valuenow',String(width));
      window.dispatchEvent(new CustomEvent('fullworth:sidebar-resize',{detail:{width}}));
      syncResponsiveSidebar();
      return width;
    };
    const save=width=>{if(width)localStorage.setItem(key(),String(width))};

    handle.className='sidebar-resizer';
    handle.setAttribute('role','separator');
    handle.setAttribute('aria-orientation','vertical');
    handle.setAttribute('aria-label','Navigation width');
    handle.setAttribute('aria-valuemin',String(minWidth));
    handle.tabIndex=0;
    sidebar.appendChild(handle);
    apply(savedWidth());

    let pointerId=null;
    handle.addEventListener('pointerdown',event=>{
      if(!desktopMode()||sidebarEffectivelyCollapsed())return;
      pointerId=event.pointerId;handle.setPointerCapture(pointerId);
      handle.classList.add('is-dragging');document.body.classList.add('sidebar-resizing');event.preventDefault();
    });
    handle.addEventListener('pointermove',event=>{if(pointerId===event.pointerId)apply(event.clientX)});
    const finish=event=>{
      if(pointerId===null||event.pointerId!==pointerId)return;
      pointerId=null;handle.classList.remove('is-dragging');document.body.classList.remove('sidebar-resizing');
      save(apply(sidebar.getBoundingClientRect().width));
    };
    handle.addEventListener('pointerup',finish);
    handle.addEventListener('pointercancel',finish);
    handle.addEventListener('dblclick',()=>save(apply(defaultWidth())));
    handle.addEventListener('keydown',event=>{
      if(!desktopMode()||sidebarEffectivelyCollapsed()||!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
      event.preventDefault();
      const current=sidebar.getBoundingClientRect().width;
      const step=event.shiftKey?40:10;
      const next=event.key==='Home'?minWidth:event.key==='End'?maxWidth():current+(event.key==='ArrowRight'?step:-step);
      save(apply(next));
    });
    window.addEventListener('resize',()=>{if(!desktopMode()){syncResponsiveSidebar();return}apply(savedWidth());syncResponsiveSidebar()});
    window.addEventListener('fullworth:coach-resize',syncResponsiveSidebar);
    window.addEventListener('fullworth:layout-reset',()=>apply(defaultWidth()));
    window.fwClampSidebarWidth=()=>apply(savedWidth());
  }

  window.fwSyncResponsiveSidebar=syncResponsiveSidebar;

  return {
    initResizableSidebar,
    resetLayout,
    syncNavToggle,
    syncResponsiveSidebar,
    toggleSidebar
  };
}
