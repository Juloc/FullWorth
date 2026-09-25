// Die Möbel, die auf jeder Seite stehen (#154).
//
// Überschrift, Theme-Schalter, Privatmodus, die beiden Überlaufmenüs, die globale Suche, die
// Sitzungssperre, die Höhe der Topbar. Das ist alles, was wirklich überall gilt — was eine Seite
// betrifft, lädt die Seite selbst.
//
// Es gibt diese Datei EINMAL und sie wird von beiden benutzt: von der alten Hülle (app.js) und von
// jeder Razor-Seite. Sonst gäbe es während der Migration zwei Fassungen derselben Topbar, und genau
// das ist in diesem Haus schon mehrfach schiefgegangen. Der Unterschied steckt in zwei Funktionen,
// die der Aufrufer mitbringt: wie man zu einer anderen Ansicht kommt und wie man die aktuelle neu
// zeichnet. In der Hülle ist das ein Ansichtswechsel, auf einer echten Seite eine Adresse.
//
// Theme und Darstellung stehen bewusst NICHT hier: sie müssen vor dem ersten Zeichnen gelten und
// laufen als klassische Skripte im Kopf (app/theme.js, app/boot.js). Ein Modul käme zu spät, und das
// Ergebnis wäre ein Aufblitzen des falschen Themes.
//
// Was noch in app.js steht und dorthin gehört, solange es die Hülle gibt: die Seitenleiste, die sich
// bei wenig Platz selbst einklappt, und das Coach-Dock. Beide hängen aneinander und an der Breite
// des Docks — sie ziehen zusammen um, nicht einzeln.

import { i18n } from '../core/services.js';
import { state } from '../core/state.js';
import { isPrivate, togglePrivacy, onPrivacyChange, privacyDefault } from '../components/privacy.js';
import { installTopbarMetrics } from '../components/topbar-metrics.js';
import { spriteHref } from '../components/sprite.js';
import { installNavigation } from '../core/navigation.js';
import { emitAppEvent, onAppEvent } from '../core/event-bus.js';
import { setPrimaryAction } from '../features/ux-kit.js';
import { openGlobalSearch } from './global-search.js';
import { initLock } from './lock.js';
import { MENU } from './menu.js';
import { pathForView } from './routes.js';
import { createPageContext, loadSession } from './page-context.js';

const root = document.documentElement;
const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const get = path => i18n.get(path);

const esc = value => String(value ?? '').replace(/[&<>'"]/g, character =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[character]));

/**
 * @param {object} options
 * @param {(view: string, opts?: object) => void} [options.navigate] Wie man zu einer Ansicht kommt.
 * @param {() => void} [options.reload] Wie man die aktuelle Oberfläche neu zeichnet.
 * @param {object} [options.context] Der Seitenkontext für Suche und Sperre.
 * @param {() => string} [options.currentView] Welche Ansicht gerade offen ist.
 * @param {(view: string) => [string, Function, string?]|null} [options.primaryAction]
 *        Die Hauptaktion der Ansicht — in der Hülle aus einer Tabelle, auf einer Seite von ihr selbst.
 */
export function createShell({
  navigate = view => { location.assign(pathForView(view)); },
  reload = () => location.reload(),
  context = null,
  currentView = () => document.body.dataset.view || state.view,
  primaryAction = () => null
} = {}) {
  const ctx = context ?? createPageContext({ reload });

  function syncPrivacyToggle() {
    const button = $('#privacy-toggle');
    if (!button) return;
    button.setAttribute('aria-pressed', String(isPrivate()));
    button.classList.toggle('active', isPrivate());
    // Nur in der Leiste, solange er AN ist: ein ausgeschalteter Schalter sagt nichts, und genau
    // dafür gibt es das Überlaufmenü. Das Attribut sitzt am <html>, weil app/boot.js es vor dem
    // ersten Zeichnen setzt — ein hidden, das JavaScript später nachträgt, schiebt die Leiste.
    root.dataset.privacy = isPrivate() ? 'on' : 'off';
    const preference = $('#privacy-default');
    if (preference) preference.checked = privacyDefault();
  }

  // Der Fuss der Seitenleiste: Raumname, Waehrung und ein Buchstabe als Zeichen (§3.1).
  function renderUserBlock() {
    const space = state.space;
    const name = $('#user-space-name');
    const sub = $('#user-space-sub');
    const avatar = $('#user-avatar');
    if (name) name.textContent = space?.name || '';
    if (sub) sub.textContent = space?.baseCurrency || '';
    if (avatar) avatar.textContent = (space?.name || 'F').trim().charAt(0).toUpperCase() || 'F';
  }

  function syncThemeToggle() {
    const button = $('#theme-toggle');
    if (button) button.dataset.themePref = state.theme;
  }

  function applyTheme() {
    const persisted = window.FullWorthTheme.readThemeState();
    const applied = window.FullWorthTheme.applyTheme(
      { mode: state.theme, seed: persisted.seed, logoMode: persisted.logoMode });
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.setAttribute('content', applied.mode === 'dark' ? '#121416' : '#f5f6f7');
    syncThemeToggle();
  }

  /**
   * Überschrift, Unterzeile und Hauptaktion.
   *
   * Auf einer Razor-Seite steht der Text schon im Dokument (PageHeadings) — hier wird er nur für eine
   * andere Sprache ersetzt. Eine Seite ohne Untertitel hat keinen Schlüssel dafür; ein leerer Wert
   * wäre eine vergessene Übersetzung und keine Absicht.
   */
  function renderPageHeader() {
    const view = currentView();
    const page = state.messages.pages?.[view];
    const title = $('#page-title');
    const subtitle = $('#page-subtitle');
    if (title && subtitle) {
      const fallback = $(`.sidebar .nav-item[data-entry="${view}"] span`)?.textContent || '';
      title.textContent = page?.title ?? fallback;
      subtitle.textContent = page?.subtitle ?? '';
    }

    const action = primaryAction(view);
    const button = $('#primary-action');
    if (!button) return;
    if (action) {
      button.hidden = false;
      setPrimaryAction(button, get(action[0]), action[2] || 'add');
      button.onclick = action[1];
    } else {
      button.hidden = true;
      button.onclick = null;
    }
  }

  // Das Überlaufmenü der Topbar. Dieselbe Darstellung wie „Mehr" unten, damit es im Haus ein
  // Menümuster gibt und nicht ein zweites.
  function openTopbarMenu() {
    const entries = [
      ['privacy', get('privacy.toggle'),
        `<svg viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref('ui-eye')}"></use></svg>`,
        () => togglePrivacy()],
      ['refresh', get('common.refresh'),
        `<svg viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref('ui-refresh')}"></use></svg>`,
        () => reload()]
    ];
    const items = entries
      .map(([key, label, icon]) => `<button type="button" data-menu="${key}">${icon}<span>${esc(label)}</span></button>`)
      .join('');
    const dialog = ctx.dialog(
      `<form method="dialog" class="dialog-card more-sheet"><div class="panel-head"><h2>${esc(get('nav.more'))}</h2><button value="cancel" data-close>×</button></div><div class="more-list">${items}</div></form>`,
      { mobileMode: 'sheet' });
    dialog.classList.add('more-sheet-dialog');
    for (const [key, , , run] of entries)
      dialog.querySelector(`[data-menu="${key}"]`)?.addEventListener('click', () => { dialog.close(); run(); });
    dialog.showModal();
  }

  function openMoreSheet() {
    const view = currentView();
    const visible = entry => !entry.admin || state.capabilities?.admin;
    const groups = MENU.map(group => {
      const items = group.items.filter(visible).map(entry => {
        const target = entry.href ? `data-open="${entry.href}"` : `data-go="${entry.view}"`;
        const active = view === entry.view ? ' class="active"' : '';
        return `<button type="button" ${target}${active}><svg viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref(entry.icon)}"></use></svg><span>${esc(get(entry.label))}</span></button>`;
      }).join('');
      return `<h3 class="more-group">${esc(get(group.label))}</h3><div class="more-list">${items}</div>`;
    }).join('');
    const dialog = ctx.dialog(
      `<form method="dialog" class="dialog-card more-sheet"><div class="panel-head"><h2>${esc(get('nav.more'))}</h2><button value="cancel" data-close>&times;</button></div>${groups}</form>`,
      { mobileMode: 'sheet' });
    dialog.classList.add('more-sheet-dialog');
    dialog.querySelectorAll('[data-go]').forEach(button =>
      button.addEventListener('click', () => { dialog.close(); navigate(button.dataset.go); }));
    dialog.querySelectorAll('[data-open]').forEach(button =>
      button.addEventListener('click', () => { dialog.close(); location.assign(button.dataset.open); }));
    dialog.showModal();
  }

  // Der Zustand steht im aria-expanded der Überschrift — eine zweite Klasse dafür wäre dieselbe
  // Aussage doppelt. app/nav-state.js liest ihn beim Parsen wieder ein, also vor dem ersten Bild.
  function toggleGroup(head) {
    head.setAttribute('aria-expanded', head.getAttribute('aria-expanded') === 'false' ? 'true' : 'false');
    const closed = $('.nav-group-head[aria-expanded="false"]').map(item => item.dataset.group);
    // Schluessel und Form muessen exakt zu app/nav-state.js passen - das liest sie beim Parsen
    // wieder ein, vor dem ersten Bild. Eine andere Schreibweise hiesse: die Gruppen klappen beim
    // naechsten Laden wieder auf, und niemand sieht warum.
    localStorage.setItem('finance.navClosedGroups', closed.join(' '));
  }

  function bind() {
    $('#theme-toggle')?.addEventListener('click', () => {
      const order = ['system', 'light', 'dark'];
      state.theme = order[(order.indexOf(state.theme) + 1) % order.length] || 'system';
      window.FullWorthTheme.writeThemeState({ mode: state.theme });
      applyTheme();
      const select = $('#theme');
      if (select) select.value = state.theme;
    });
    // Stellt das System auf Dunkel um, waehrend die Seite offen ist, soll sie folgen - aber nur,
    // wenn der Benutzer "System" gewaehlt hat. Dieser Zuhoerer stand bis zum Ende von #154 in
    // app.js; ohne ihn bliebe eine offene Seite im alten Farbschema stehen, bis man sie neu laedt.
    matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
      if (state.theme === 'system') applyTheme();
    });

    // Der Einklapp-Knopf stand im Markup jeder Seite, verdrahtet wurde er aber nur in app.js -
    // auf einer Razor-Seite war er da und tat nichts. Hier gilt er fuer beide.
    $('#nav-collapse')?.addEventListener('click', toggleSidebar);
    // "Layout zuruecksetzen" steht auf der Einstellungsseite, setzt aber die Moebel zurueck.
    $('#layout-reset')?.addEventListener('click', resetLayout);
    initResizableSidebar();
    syncResponsiveSidebar();
    // "Zeichne neu" kam bis #154 bei der alten Huelle an. Ohne diesen Zuhoerer bliebe die Seite nach
    // einer Aenderung stehen, ohne dass irgendwo ein Fehler auftaucht.
    onAppEvent('surface:reload', () => reload());
    $('#privacy-toggle')?.addEventListener('click', () => togglePrivacy());
    $('#global-search')?.addEventListener('click', () => openGlobalSearch(ctx));
    $('#topbar-more')?.addEventListener('click', openTopbarMenu);
    $('#bottom-more')?.addEventListener('click', openMoreSheet);
    $$('.nav-group-head').forEach(head => head.addEventListener('click', () => toggleGroup(head)));
    // Desktop-Tastenkürzel: „/" öffnet die Suche, solange nicht in einem Feld getippt wird (§19).
    document.addEventListener('keydown', event => {
      if (event.key !== '/' || /^(INPUT|TEXTAREA|SELECT)$/.test(event.target.tagName) || event.target.isContentEditable) return;
      event.preventDefault();
      openGlobalSearch(ctx);
    });
  }

  // --- Seitenleiste: Einklappen, Breite, automatisches Einklappen --------------------------------
  //
  // Stand bis #154 in app.js, also nur in der alten Huelle. Auf einer Razor-Seite fehlte damit der
  // Ziehgriff ganz, und der Einklapp-Knopf war da und tat nichts - er wurde nie verdrahtet. Hier
  // gilt es fuer beide: die Huelle ruft dieselben Funktionen wie jede Seite.
  //
  // Das Aussehen selbst kommt weiterhin vor dem ersten Bild aus app/boot.js (Breite und der
  // eingeklappte Zustand aus localStorage) - sonst waere der Wechsel ein Sprung.
  function toggleSidebar(){
    const collapsed=!root.classList.contains('nav-collapsed');
    root.classList.toggle('nav-collapsed',collapsed);
    localStorage.setItem('finance.navCollapsed',collapsed?'1':'0');
    if(collapsed)root.classList.remove('nav-auto-collapsed');
    syncResponsiveSidebar();
  }
  function sidebarEffectivelyCollapsed(){return root.classList.contains('nav-collapsed')||root.classList.contains('nav-auto-collapsed')}
  // Point the chevron the way it will move (‹ collapses, › expands) and label it for its next action.
  function syncNavToggle(){
    const b=$('#nav-collapse');if(!b)return;
    const manual=root.classList.contains('nav-collapsed');
    const autoOnly=root.classList.contains('nav-auto-collapsed')&&!manual;
    const collapsed=manual||autoOnly;
    b.textContent=collapsed?'›':'‹';
    b.disabled=autoOnly;
    const label=autoOnly
      ? (state.lang==='de'?'Navigation wegen Platz automatisch eingeklappt':'Navigation automatically collapsed for available space')
      : get(collapsed?'nav.expand':'nav.collapse');
    b.setAttribute('aria-label',label);b.title=label;
  }
  function syncResponsiveSidebar(){
    const desktop=window.matchMedia('(min-width:768px)').matches;
    const manual=root.classList.contains('nav-collapsed');
    if(!desktop||manual){
      const changed=root.classList.contains('nav-auto-collapsed');
      root.classList.remove('nav-auto-collapsed');
      syncNavToggle();
      if(changed)queueMicrotask(()=>emitAppEvent('layout:clamp-coach'));
      return;
    }
    const desiredSidebar=Math.max(176,Number(localStorage.getItem(sidebarWidthKey()))||Number(localStorage.getItem('finance.sidebar.width'))||228);
    const coachKey=`finance.coach.dockWidth.${layoutWidthMode()}`;
    const coach=document.body.classList.contains('coach-dock-open')?($('#coach-dock')?.getBoundingClientRect().width||Number(localStorage.getItem(coachKey))||Number(localStorage.getItem('finance.coach.dockWidth'))||0):0;
    const minMain=window.innerWidth<1100?420:520;
    const shouldCollapse=window.innerWidth-desiredSidebar-coach<minMain;
    const changed=root.classList.contains('nav-auto-collapsed')!==shouldCollapse;
    root.classList.toggle('nav-auto-collapsed',shouldCollapse);
    syncNavToggle();
    if(changed)queueMicrotask(()=>emitAppEvent('layout:clamp-coach'));
  }
  onAppEvent('layout:sync-sidebar',syncResponsiveSidebar);

  function layoutWidthMode(){return window.innerWidth>=1024?'desktop':'tablet'}
  function sidebarWidthKey(){return `finance.sidebar.width.${layoutWidthMode()}`}
  function resetLayout(){
    ['finance.sidebar.width','finance.sidebar.width.desktop','finance.sidebar.width.tablet','finance.coach.dockWidth','finance.coach.dockWidth.desktop','finance.coach.dockWidth.tablet'].forEach(key=>localStorage.removeItem(key));
    localStorage.setItem('finance.navCollapsed','0');
    root.classList.remove('nav-collapsed','nav-auto-collapsed');
    document.documentElement.style.removeProperty('--sidebar-w');
    document.documentElement.style.removeProperty('--coach-dock-w');
    window.dispatchEvent(new CustomEvent('fullworth:layout-reset'));
    window.dispatchEvent(new Event('resize'));
    syncResponsiveSidebar();
    ctx.toast(state.lang==='de'?'Layout zurückgesetzt':'Layout reset');
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
    const handle=document.createElement('div');
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
    onAppEvent('layout:clamp-sidebar',()=>apply(savedWidth()));
  }


  // Die Auswahl fuer Farbschema und Sprache steht auf der Einstellungsseite, also gehoert ihr auch
  // das Umschalten - dafuer braucht sie applyTheme. Frueher stand beides in app.js, weil dort das
  // Markup lag; seit #154 liegt es bei der Seite, und app.js griff ins Leere.
  ctx.applyTheme = applyTheme;

  return {
    ctx,
    bind,
    renderUserBlock,
    applyTheme,
    renderPageHeader,
    syncResponsiveSidebar,
    syncNavToggle,
    resetLayout,
    initResizableSidebar,
    syncPrivacyToggle,
    syncThemeToggle,
    openMoreSheet,
    openTopbarMenu,
    /** Sitzungssperre: nach zehn Minuten Ruhe, und danach wird die Oberfläche neu gezeichnet. */
    startLock: () => initLock(ctx, { onUnlock: reload })
  };
}

/**
 * Der Start einer Razor-Seite: Möbel aufstellen, Seite zeichnen lassen.
 *
 * `render` bekommt den gemeinsamen Kontext und ist die Seite selbst. Die Reihenfolge ist Absicht —
 * Sprache und Rahmen zuerst, damit die Seite in eine fertige Umgebung zeichnet und nichts
 * nachträglich springt.
 */
export async function startShellPage(render, { primaryAction = () => null } = {}) {
  const shell = createShell({ primaryAction, reload: () => location.reload() });
  installNavigation((view, options = {}) => {
    const target = pathForView(view);
    const query = options.query ? String(options.query).replace(/^\?/, '') : '';
    location.assign(query ? `${target}?${query}` : target);
  });
  installTopbarMetrics();

  await i18n.load(state.lang);
  i18n.apply(document);
  shell.renderPageHeader();
  shell.bind();
  shell.syncThemeToggle();
  shell.syncPrivacyToggle();
  onPrivacyChange(() => { shell.syncPrivacyToggle(); location.reload(); });

  await loadSession(shell.ctx.toast);
  shell.renderUserBlock();
  await render(shell.ctx);
  shell.startLock();
  return shell;
}
