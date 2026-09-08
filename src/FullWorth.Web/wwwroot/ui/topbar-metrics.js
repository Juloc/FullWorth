// Publishes the app topbar's real height as the `--topbar-h` custom property on <html>.
//
// The topbar is `position:sticky;top:0`, so anything else that sticks to `top:0` inside the same
// (document-level) scroll container lands underneath it: either it covers the topbar when it has the
// higher z-index, or it slides under and disappears. Both were real mobile bugs.
//
// A hardcoded offset is not good enough: on phones the topbar wraps (`align-items:flex-start` plus
// `overflow-wrap:anywhere` on the title), so its height depends on the page title and the viewport.
// Measuring it once here means every sticky element can just say `top:var(--topbar-h)` instead of
// inventing its own magic number.
const FALLBACK = 64;

export function installTopbarMetrics(topbar = document.querySelector('.topbar')) {
  if (!topbar) return;
  const publish = () => {
    const height = Math.round(topbar.getBoundingClientRect().height) || FALLBACK;
    document.documentElement.style.setProperty('--topbar-h', `${height}px`);
  };
  publish();
  if (typeof ResizeObserver === 'function') new ResizeObserver(publish).observe(topbar);
  else addEventListener('resize', publish, { passive: true });
}
