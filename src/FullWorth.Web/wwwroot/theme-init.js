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

  const modules = [
    ['/features/category-intelligence-ui.js', 'Category Intelligence'],
    ['/features/feature-parity-ui.js', 'Feature parity UI'],
    ['/features/parity-completion-ui.js', 'Parity completion UI'],
    ['/features/parity-final-ui.js', 'Final parity UI'],
    ['/features/investment-performance-ui.js', 'Investment performance UI'],
    ['/features/investment-import-ui.js', 'Investment import UI'],
    ['/features/fullworth-space-switcher-ui.js', 'FullWorth space switcher'],
    ['/features/mobile-review-ui.js', 'Focused transaction review'],
    ['/features/purchase-intelligence-ui.js', 'Purchase intelligence UI'],
    ['/features/purchase-discount-analytics-ui.js', 'Purchase discount analytics UI'],
    ['/features/export-portability-ui.js', 'Export portability UI'],
    ['/features/category-merge-ui.js', 'Category merge UI'],
    ['/features/advanced-transaction-bulk-ui.js', 'Advanced transaction bulk UI'],
    ['/features/category-order-ui.js', 'Category order UI']
  ];

  await Promise.allSettled(modules.map(async ([path, label]) => {
    try {
      await import(path);
    } catch (error) {
      console.error(`${label} failed to load.`, error);
      throw error;
    }
  }));

  // parity-completion-ui predates dynamic loading and registers its own DOMContentLoaded listener.
  // Because this loader itself runs during DOMContentLoaded, initialize its exported hooks explicitly.
  try {
    const completion = await import('/features/parity-completion-ui.js');
    completion.initParityCompletionUi?.();
  } catch (error) {
    console.error('Parity completion initialization failed.', error);
  }

  // Load capability visibility after all feature enhancers have installed their initial controls.
  // The guard also observes later dynamic UI, while backend authorization remains authoritative.
  import('/features/capability-ui-guard.js').catch(error =>
    console.error('Capability UI guard failed to load.', error));
});
