function applyThemeChrome(theme) {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', theme === 'dark' ? '#121416' : '#f5f6f7');
}

function numberInRange(value, min, max, fallback) {
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

  const visualTheme = localStorage.getItem('finance.visualTheme') || 'clean';
  document.documentElement.dataset.visualTheme = ['clean', 'cute'].includes(visualTheme) ? visualTheme : 'clean';

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

// appearance.css and parity-completion.css are loaded as render-blocking <link>s in index.html so the
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
