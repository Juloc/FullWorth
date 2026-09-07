import '../features/tax.js';
import '../features/tax-review-extra.js';

// FullWorth micro-motion layer.
// Keeps motion generic and non-invasive: no business data access, no extra dependencies,
// and respects prefers-reduced-motion. Numeric animation only touches already-rendered text.

const reduceMotion = matchMedia('(prefers-reduced-motion: reduce)');
const active = new WeakSet();
const generations = new WeakMap();

function numericTarget(node) {
  if (!(node instanceof Element)) return null;
  if (node.closest('#transactions-body,.tx-detail,.refund-candidates,dialog,.fw-row')) return null;
  if (node.matches('.metric strong,.widget-metric strong,.budget-detail .kv strong')) return node;
  return node.closest('.metric strong,.widget-metric strong,.budget-detail .kv strong') || null;
}

function parseNumberText(text) {
  if (!text || text.includes('•')) return null;
  const match = String(text).match(/[+−-]?\s*\d(?:[\d\s\u00a0.,]*\d)?/);
  if (!match) return null;

  const raw = match[0];
  const explicitPlus = raw.trimStart().startsWith('+');
  const compact = raw.replace(/[\s\u00a0]/g, '').replace('−', '-');
  const comma = compact.lastIndexOf(',');
  const dot = compact.lastIndexOf('.');
  let normalized = compact;
  let decimals = 0;

  if (comma >= 0 && dot >= 0) {
    if (comma > dot) {
      decimals = compact.length - comma - 1;
      normalized = compact.replace(/\./g, '').replace(',', '.');
    } else {
      decimals = compact.length - dot - 1;
      normalized = compact.replace(/,/g, '');
    }
  } else if (comma >= 0) {
    const trailing = compact.length - comma - 1;
    if (trailing > 0 && trailing <= 2) {
      decimals = trailing;
      normalized = compact.replace(',', '.');
    } else {
      normalized = compact.replace(/,/g, '');
    }
  } else if (dot >= 0) {
    const trailing = compact.length - dot - 1;
    if (trailing > 0 && trailing <= 2) decimals = trailing;
    else normalized = compact.replace(/\./g, '');
  }

  const value = Number(normalized);
  if (!Number.isFinite(value)) return null;

  return {
    value,
    decimals,
    explicitPlus,
    prefix: text.slice(0, match.index),
    suffix: text.slice((match.index || 0) + raw.length),
  };
}

function locale() {
  return document.documentElement.lang?.toLowerCase().startsWith('de') ? 'de-DE' : 'en-US';
}

function animateNumber(el) {
  if (!el || active.has(el) || reduceMotion.matches) return;
  const parsed = parseNumberText(el.textContent);
  if (!parsed || parsed.value === 0) return;

  const generation = (generations.get(el) || 0) + 1;
  generations.set(el, generation);
  active.add(el);

  const target = parsed.value;
  const formatter = new Intl.NumberFormat(locale(), {
    minimumFractionDigits: parsed.decimals,
    maximumFractionDigits: parsed.decimals,
  });
  const duration = Math.min(900, Math.max(520, Math.log10(Math.abs(target) + 10) * 150));
  const started = performance.now();
  const format = value => `${parsed.prefix}${parsed.explicitPlus && value >= 0 ? '+' : ''}${formatter.format(value)}${parsed.suffix}`;

  const render = now => {
    if (generations.get(el) !== generation || !el.isConnected) {
      active.delete(el);
      return;
    }

    const progress = Math.min(1, (now - started) / duration);
    const eased = 1 - Math.pow(1 - progress, 4);
    el.textContent = format(target * eased);

    if (progress < 1) {
      requestAnimationFrame(render);
      return;
    }

    el.textContent = format(target);
    // Keep the guard through the MutationObserver delivery caused by our final textContent write.
    // A later app-driven value change can animate normally after this turn completes.
    setTimeout(() => active.delete(el), 0);
  };

  requestAnimationFrame(render);
}

function scan(root) {
  if (!(root instanceof Element) && root !== document) return;
  if (root instanceof Element) animateNumber(numericTarget(root));
  root.querySelectorAll?.('.metric strong,.widget-metric strong,.budget-detail .kv strong').forEach(animateNumber);
}

function init() {
  scan(document);
  const observer = new MutationObserver(records => {
    for (const record of records) {
      if (record.type !== 'characterData') continue;
      // Only a live characterData mutation of a node that already held a parsed value animates.
      // Newly inserted elements (e.g. full re-renders that replace textContent) render at their
      // final value immediately, so nothing pops in after first paint.
      const el = numericTarget(record.target.parentElement);
      if (el && !active.has(el)) animateNumber(el);
    }
  });
  observer.observe(document.body, { subtree: true, childList: true, characterData: true });
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init, { once: true });
else init();
