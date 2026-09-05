// Shared UX-rework render helpers (UX rework §10). Framework-free; consumed by transactions, contracts,
// analytics and net-worth so the same identity, card, cycle and trend primitives look identical everywhere.
// All colours/spacing come from the app.css design tokens and the `.fw-*` classes defined there.

export function esc(v) { return String(v ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c])); }

// Deterministic hue (0–359) from a name, so a merchant/category keeps the same monogram tint everywhere.
export function monogramHue(name) { let h = 0; const s = String(name || ''); for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0; return h % 360; }

// A small set of category glyphs keyed by stable semantic category keys (or the last/first segment of a
// dotted key). Used as the middle identity tier when a booking has no brand logo but a known category
// icon. Unknown keys fall through to the monogram. Line-art matching the rest of the icon set.
const CATEGORY_ICONS = {
  income: 'M12 5v14M5 12l7-7 7 7', salary: 'M12 5v14M5 12l7-7 7 7',
  housing: 'M3 11.5 12 4l9 7.5M5.5 10.5V20h13v-9.5', rent: 'M3 11.5 12 4l9 7.5M5.5 10.5V20h13v-9.5', mortgage: 'M3 11.5 12 4l9 7.5M5.5 10.5V20h13v-9.5',
  groceries: 'M6 6h15l-1.5 9h-12L6 6ZM6 6 5 3H2M9 20a1 1 0 1 0 0-2 1 1 0 0 0 0 2m8 0a1 1 0 1 0 0-2 1 1 0 0 0 0 2', food: 'M6 3v8a3 3 0 0 0 6 0V3M9 3v18M17 3c-1.5 0-2 2-2 5s.5 5 2 5v8', restaurants: 'M6 3v8a3 3 0 0 0 6 0V3M9 3v18M17 3c-1.5 0-2 2-2 5s.5 5 2 5v8',
  transport: 'M5 17h14l1-5-2-4H6l-2 4-1 5ZM7 18v2M17 18v2', car: 'M5 17h14l1-5-2-4H6l-2 4-1 5ZM7 18v2M17 18v2', fuel: 'M5 17h14l1-5-2-4H6l-2 4-1 5ZM7 18v2M17 18v2',
  electricity: 'M13 2 4 14h7l-1 8 9-12h-7l1-8Z', utilities: 'M13 2 4 14h7l-1 8 9-12h-7l1-8Z', internet: 'M2 8.5a15 15 0 0 1 20 0M5 12a10 10 0 0 1 14 0M8.5 15.5a5 5 0 0 1 7 0M12 19h.01',
  health: 'M12 21s-7-4.5-9.5-9A5 5 0 0 1 12 6a5 5 0 0 1 9.5 6C19 16.5 12 21 12 21Z', shopping: 'M6 6h12v14l-3-2-3 2-3-2-3 2Z', leisure: 'M4 5h16v11H4zM8 20h8M12 16v4',
  savings: 'M4 8a4 4 0 0 1 4-4h8a4 4 0 0 1 4 4v8a4 4 0 0 1-4 4H8a4 4 0 0 1-4-4V8ZM8 11h.01', insurance: 'M12 3 4 6v6c0 5 8 9 8 9s8-4 8-9V6l-8-3Z', travel: 'M2 16l20-7-7 20-3-8-8-3Z',
};
function categoryGlyph(iconKey) {
  if (!iconKey) return null;
  const key = String(iconKey).trim();
  const path = CATEGORY_ICONS[key] || CATEGORY_ICONS[key.toLowerCase()] || CATEGORY_ICONS[key.split(/[.\-_ ]/).pop().toLowerCase()] || CATEGORY_ICONS[key.split(/[.\-_ ]/)[0].toLowerCase()];
  return path ? `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="${path}"/></svg>` : null;
}
// True for a categoryIconKey that is a literal emoji (some categories store an emoji in their Icon field).
// Uses explicit ES6 code-point ranges (\u{…} with /u) rather than the \p{…} property escape — the latter
// is ES2018 and, being a regex literal parsed eagerly, would throw at module load on an engine that lacks
// it (the surrounding try/catch, which only wraps .test(), could not catch that).
const EMOJI_RE = /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{2B00}-\u{2BFF}]/u;
function isEmoji(s) { try { return EMOJI_RE.test(String(s || '')); } catch { return false; } }

// Left identity (UX rework §4): curated local brand logo → category icon → category-tinted monogram, with
// a transfer glyph override. No external/third-party logo lookups.
// `opts`: {logoAssetPath, categoryIconKey, isTransfer, isSavings}.
export function identityIcon(name, opts = {}) {
  if (opts.isTransfer) return `<span class="fw-ident fw-ident-transfer" aria-hidden="true">${opts.isSavings ? '↑' : '⇄'}</span>`;
  if (opts.logoAssetPath) return `<span class="fw-ident"><img class="fw-ident-logo" src="${esc(opts.logoAssetPath)}" alt="" loading="lazy" onerror="this.closest('.fw-ident').classList.add('fw-ident-failed');this.remove()"></span>`;
  const iconKey = opts.categoryIconKey;
  if (iconKey && isEmoji(iconKey)) return `<span class="fw-ident fw-ident-cat" aria-hidden="true">${esc(iconKey)}</span>`;
  const glyph = categoryGlyph(iconKey);
  if (glyph) return `<span class="fw-ident fw-ident-cat" aria-hidden="true">${glyph}</span>`;
  const initial = (String(name || '?').trim()[0] || '?').toUpperCase();
  return `<span class="fw-ident fw-monogram" style="--ident-h:${monogramHue(name)}" aria-hidden="true">${esc(initial)}</span>`;
}

// A titled elevated card (UX rework §9/§10 SectionCard). `opts`: {sub, action:{label,attr}, className}.
// Returns an <article> string; caller fills `body` and wires any [data-action] via the returned markup.
export function sectionCard(title, body, opts = {}) {
  const action = opts.action ? `<button type="button" class="fw-card-action" ${opts.action.attr || ''}>${esc(opts.action.label)}</button>` : '';
  const sub = opts.sub ? `<p class="fw-card-sub">${esc(opts.sub)}</p>` : '';
  const head = title || action ? `<div class="fw-card-head"><div><h3 class="fw-card-title">${esc(title || '')}</h3>${sub}</div>${action}</div>` : '';
  return `<article class="fw-card ${opts.className || ''}">${head}${body || ''}</article>`;
}

// Trend badge: green when a change is "good", red when "bad". `goodWhenUp` flips the semantics
// (income up = good; spending up = bad). Renders an arrow + rounded percent.
export function trendBadge(pct, goodWhenUp = false) {
  const p = Number(pct) || 0;
  if (!isFinite(p) || Math.round(p) === 0) return `<span class="fw-trend fw-trend-flat">•&nbsp;0%</span>`;
  const up = p > 0;
  const good = goodWhenUp ? up : !up;
  return `<span class="fw-trend ${good ? 'fw-trend-good' : 'fw-trend-bad'}">${up ? '▲' : '▼'}&nbsp;${Math.abs(Math.round(p))}%</span>`;
}

// Period cycle windows (UX rework §6). Woche→last 12 weeks, Monat→12 months, Quartal→8 quarters,
// Jahr→5 years, shifted by `offset` whole windows (prev/next navigation). Returns {from,to,granularity,label}.
export const CYCLES = ['week', 'month', 'quarter', 'year'];
export function cycleWindow(cycle, offset = 0, lang = 'de') {
  const de = lang !== 'en';
  const iso = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  const cfg = { week: { n: 12 }, month: { n: 12 }, quarter: { n: 8 }, year: { n: 5 } }[cycle] || { n: 12 };
  const n = cfg.n;
  const end = new Date(); end.setHours(12, 0, 0, 0);
  const start = new Date(end);
  if (cycle === 'week') { const day = (end.getDay() + 6) % 7; end.setDate(end.getDate() - day + 6); start.setTime(end.getTime()); start.setDate(start.getDate() - (n * 7) + 1); }
  else if (cycle === 'quarter') { const q = Math.floor(end.getMonth() / 3); end.setMonth(q * 3 + 3, 0); start.setTime(end.getTime()); start.setMonth(start.getMonth() - n * 3 + 1, 1); }
  else if (cycle === 'year') { end.setMonth(11, 31); start.setTime(end.getTime()); start.setFullYear(start.getFullYear() - n + 1); start.setMonth(0, 1); }
  else { end.setMonth(end.getMonth() + 1, 0); start.setTime(end.getTime()); start.setMonth(start.getMonth() - n + 1, 1); }
  // Shift by whole windows for prev/next. Shift the (always day-1 / week-start) `start`, which is safe,
  // then DERIVE `end` from it — shifting the last-day-of-month `end` directly would overflow when the
  // source day (e.g. Feb 29) doesn't exist in the target month, drifting `to` by a day at leap boundaries.
  if (offset) {
    if (cycle === 'week') start.setDate(start.getDate() + offset * n * 7);
    else if (cycle === 'quarter') start.setMonth(start.getMonth() + offset * n * 3);
    else if (cycle === 'year') start.setFullYear(start.getFullYear() + offset * n);
    else start.setMonth(start.getMonth() + offset * n);
    end.setTime(start.getTime());
    if (cycle === 'week') end.setDate(end.getDate() + n * 7 - 1);
    else if (cycle === 'quarter') end.setMonth(end.getMonth() + n * 3, 0);
    else if (cycle === 'year') end.setFullYear(end.getFullYear() + n - 1, 11, 31);
    else end.setMonth(end.getMonth() + n, 0);
  }
  const label = cycle === 'week' ? (de ? `Letzte ${n} Wochen` : `Last ${n} weeks`)
    : cycle === 'month' ? (de ? `Letzte ${n} Monate` : `Last ${n} months`)
      : cycle === 'quarter' ? (de ? `Letzte ${n} Quartale` : `Last ${n} quarters`)
        : (de ? `Letzte ${n} Jahre` : `Last ${n} years`);
  return { from: iso(start), to: iso(end), granularity: cycle, label: offset ? `${label} (${iso(start)} – ${iso(end)})` : label };
}
