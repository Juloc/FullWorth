function applyThemeChrome(theme) {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', theme === 'dark' ? '#121416' : '#f5f6f7');
}

function numberInRange(value, min, max, fallback) {
  if (value === null || value === '') return fallback;
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.min(max, Math.max(min, parsed));
}

function applyStoredTypography() {
  const root = document.documentElement;
  const baseSize = numberInRange(localStorage.getItem('finance.typography.baseSize'), 11, 18, 13);
  const weight = numberInRange(localStorage.getItem('finance.typography.weight'), 300, 600, 400);
  const letterSpacing = numberInRange(localStorage.getItem('finance.typography.letterSpacing'), -0.05, 0.12, 0);
  const lineHeight = numberInRange(localStorage.getItem('finance.typography.lineHeight'), 1.1, 1.9, 1.5);

  root.style.setProperty('--font-size-base', `${baseSize}px`);
  root.style.setProperty('--font-weight-base', String(weight));
  root.style.setProperty('--letter-spacing-base', `${letterSpacing}em`);
  root.style.setProperty('--line-height-base', String(lineHeight));
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
  // not briefly render white-on-white. Keep in sync with ui/appearance.js.
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

  const font = localStorage.getItem('finance.font') || 'default';
  const fonts = [
    'default', 'arial-narrow',
    'system', 'segoe', 'aptos', 'helvetica', 'arial', 'verdana', 'tahoma', 'trebuchet', 'century-gothic',
    'fredoka', 'comic',
    'georgia', 'times',
    'mono'
  ];
  document.documentElement.dataset.font = fonts.includes(font) ? font : 'default';
  applyStoredTypography();
} catch {
  const actualTheme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
  document.documentElement.dataset.theme = actualTheme;
  applyThemeChrome(actualTheme);
}

// appearance.css and the shared base styles are loaded as render-blocking <link>s in index.html so the
// page paints once in its final style (no post-load restyle flash). They are intentionally not injected
// here anymore.

window.addEventListener('DOMContentLoaded', async () => {
  try {
    const appearance = await import('/ui/appearance.js');
    appearance.initAppearance?.();
  } catch (error) {
    console.error('Appearance initialization failed.', error);
  }
});
