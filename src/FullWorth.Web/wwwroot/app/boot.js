// Was vor dem ersten Zeichnen feststehen muss. Klassisches <script> im <head>, absichtlich kein
// Modul: ein Modul wird verzögert ausgeführt, und alles hier drin entscheidet über das erste Bild.
//
// Regel: hierher gehört nur, was sonst einen Sprung verursacht - Theme, Farben, die Breite der
// Seitenleiste und welche Menügruppen zu sind. Typografie kommt ausschließlich aus tokens.css/reset.css.
//
// Die Farbmathematik selbst steht nicht mehr hier - sie steht genau einmal in app/theme.js, geladen
// als klassisches <script> VOR diesem hier (siehe index.html). Ein <script type="module"> waere hier
// keine Option gewesen: der Browser behandelt Module wie "defer" und wuerde das Parsen NICHT blocken,
// also genau die Vor-dem-ersten-Bild-Garantie verlieren, die dieser ganze Block hat. Ein klassisches
// Script dagegen laeuft synchron und VOR jedem Modul - window.FullWorthTheme steht darum hier
// garantiert schon bereit, ohne dass diese Datei die Formeln ein zweites Mal mitbringen muesste.

function applyThemeChrome(theme) {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', theme === 'dark' ? '#121416' : '#f5f6f7');
}

try {
  const state = window.FullWorthTheme.readThemeState();
  const applied = window.FullWorthTheme.applyTheme(state);
  applyThemeChrome(applied.mode);
} catch {
  const actualTheme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  document.documentElement.dataset.theme = actualTheme;
  applyThemeChrome(actualTheme);
}

// Die Seitenleiste. Sie stand früher in app.js und wurde damit erst nach dem ersten Bild
// wiederhergestellt - wer eingeklappt hatte, sah die Hauptspalte um gut 130px springen.
try {
  const root = document.documentElement;
  if (localStorage.getItem('finance.navCollapsed') === '1') root.classList.add('nav-collapsed');

  // Gleicher Schlüssel wie in app.js: die Breite hängt davon ab, ob gerade Desktop oder Tablet.
  const width = Number(localStorage.getItem('finance.sidebar.width.' + (innerWidth >= 1024 ? 'desktop' : 'tablet')));
  if (width > 0) root.style.setProperty('--sidebar-w', width + 'px');

  // Der Privatmodus-Schalter steht nur in der Leiste, solange er AN ist - ein ausgeschalteter
  // Schalter sagt nichts. Ob er an ist, weiß man hier schon; dieselbe Reihenfolge wie in
  // components/privacy.js: erst die Sitzung, dann die Voreinstellung.
  const session = sessionStorage.getItem('finance.privacy');
  const privacy = session === 'on' || session === 'off'
    ? session === 'on'
    : localStorage.getItem('finance.privacy.default') === 'on';
  root.dataset.privacy = privacy ? 'on' : 'off';
} catch { /* Ohne localStorage bleibt es bei den Standardwerten. */ }

// Verhalten, kein Aussehen - das darf warten, bis das Dokument steht.
window.addEventListener('DOMContentLoaded', async () => {
  const [appearance, mobileInteractions] = await Promise.all([
    import('/app/appearance.js'),
    import('/components/mobile-interactions.js')
  ]);
  appearance.initAppearance();
  mobileInteractions.initMobileInteractions();
});

// Die gewählte Sprache in ein Cookie spiegeln (#154).
//
// Der Server muss sie kennen, sonst liefert er deutsches Markup an eine englische Sitzung, und
// JavaScript tauscht nach dem ersten Bild jede Beschriftung aus - "Hinzufügen" wird "Add", und die
// halbe Seite rutscht. In der alten Hülle fiel das nicht auf, weil dort ALLES beim Start übersetzt
// wurde, während noch keine Ansicht zu sehen war.
//
// localStorage kann der Server nicht lesen, ein Cookie schon. Es trägt keine Kennung und keinen
// Inhalt - nur "de" oder "en" - und wird hier gesetzt, weil dieses Skript ohnehin vor dem ersten
// Bild läuft.
try {
  const stored = localStorage.getItem('finance.language');
  const language = stored || ((navigator.language || 'de').startsWith('de') ? 'de' : 'en');
  if (document.cookie.split('; ').every(part => part !== `fw.lang=${language}`))
    document.cookie = `fw.lang=${language}; path=/; max-age=31536000; SameSite=Lax`;
} catch { /* Ohne localStorage bleibt es beim serverseitigen Standard. */ }
