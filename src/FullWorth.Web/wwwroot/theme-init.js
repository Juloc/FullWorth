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

  const visualTheme = localStorage.getItem('finance.visualTheme') || 'clean';
  document.documentElement.dataset.visualTheme = ['clean', 'cute'].includes(visualTheme) ? visualTheme : 'clean';

  const font = localStorage.getItem('finance.font') || 'default';
  document.documentElement.dataset.font = ['default', 'fredoka'].includes(font) ? font : 'default';
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
