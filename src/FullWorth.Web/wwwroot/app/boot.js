// Was vor dem ersten Zeichnen feststehen muss. Klassisches <script> im <head>, absichtlich kein
// Modul: ein Modul wird verzögert ausgeführt, und alles hier drin entscheidet über das erste Bild.
//
// Regel: hierher gehört nur, was sonst einen Sprung verursacht - Theme, Farben, die Breite der
// Seitenleiste und welche Menügruppen zu sind. Typografie kommt ausschließlich aus tokens.css/reset.css.

function applyThemeChrome(theme) {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', theme === 'dark' ? '#121416' : '#f5f6f7');
}

try {
  const theme = localStorage.getItem('finance.theme') || 'system';
  const actualTheme = theme === 'system'
    ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
    : theme;
  document.documentElement.dataset.theme = actualTheme;
  applyThemeChrome(actualTheme);

  // The two brand colours, applied before first paint for the same reason the theme is: setting them
  // later means a visible flash of the default colour on every page load. Only well-formed hex is
  // accepted, and an empty value removes nothing - the property is simply never set, which leaves
  // tokens.css in charge. Contrast for the button label is derived here too, so a light primary does
  // not briefly render white-on-white. Keep in sync with app/appearance.js.
  const hex = value => /^#[0-9a-f]{6}$/i.test(String(value || '').trim()) ? value.trim().toLowerCase() : '';
  const primary = hex(localStorage.getItem('finance.color.primary'));
  const secondary = hex(localStorage.getItem('finance.color.secondary'));
  if (primary) {
    const channel = index => {
      const value = parseInt(primary.slice(1 + index * 2, 3 + index * 2), 16) / 255;
      return value <= 0.03928 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
    };
    const luminance = 0.2126 * channel(0) + 0.7152 * channel(1) + 0.0722 * channel(2);
    document.documentElement.style.setProperty('--brand-primary', primary);
    document.documentElement.style.setProperty('--cta', primary);
    document.documentElement.style.setProperty('--cta-text', luminance > 0.42 ? '#151719' : '#ffffff');
  }
  if (secondary) {
    document.documentElement.style.setProperty('--brand-secondary', secondary);
    document.documentElement.style.setProperty('--accent', secondary);
    document.documentElement.style.setProperty('--accent-soft', `color-mix(in srgb, ${secondary} 12%, transparent)`);
  }
  document.documentElement.dataset.brandTint =
    primary && localStorage.getItem('finance.color.tintLogo') === 'true' ? 'on' : 'off';
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
