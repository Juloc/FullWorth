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
  vehicle: 'M5 17h14l1-5-2-4H6l-2 4-1 5ZM7 18v2M17 18v2', subscriptions: 'M5 7h14v10H5zM9 21h6M12 17v4', education: 'M3 9l9-5 9 5-9 5-9-5Zm4 3v5c3 2 7 2 10 0v-5', pets: 'M8 11c-2 0-3-2-2-3s3 0 3 2m7 1c2 0 3-2 2-3s-3 0-3 2m-3 1c-3 0-5 3-3 6 1 2 5 2 6 0 2-3 0-6-3-6Z', fees: 'M4 6h16v12H4zM8 10h8M8 14h5', taxes: 'M5 3h14v18H5zM8 8h8M8 12h8M8 16h5', donations: 'M12 21s-7-4.5-9.5-9A5 5 0 0 1 12 6a5 5 0 0 1 9.5 6C19 16.5 12 21 12 21Z', debt: 'M4 8h16v12H4zM8 4h8v4M8 13h8', transfers: 'M5 8h12m0 0-3-3m3 3-3 3M19 16H7m0 0 3-3m-3 3 3 3', cash: 'M3 6h18v12H3zM7 12h.01M17 12h.01M12 9v6', family: 'M8 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm8 0a3 3 0 1 0 0-6 3 3 0 0 0 0 6ZM3 20c0-4 2-6 5-6s5 2 5 6m-2 0c0-4 2-6 5-6s5 2 5 6', other: 'M5 12h.01M12 12h.01M19 12h.01',
};
// German category-key aliases → reuse the same line-art glyphs so localized keys (wohnen, strom, …)
// resolve just like the English ones. No new colours/fonts.
Object.assign(CATEGORY_ICONS, {
  wohnen: CATEGORY_ICONS.housing, miete: CATEGORY_ICONS.rent, hausgeld: CATEGORY_ICONS.housing, immobilien: CATEGORY_ICONS.housing,
  supermarkt: CATEGORY_ICONS.groceries, lebensmittel: CATEGORY_ICONS.groceries, einkauf: CATEGORY_ICONS.groceries,
  restaurants: CATEGORY_ICONS.restaurants, essen: CATEGORY_ICONS.food, gastronomie: CATEGORY_ICONS.restaurants,
  strom: CATEGORY_ICONS.electricity, energie: CATEGORY_ICONS.electricity, nebenkosten: CATEGORY_ICONS.utilities,
  tanken: CATEGORY_ICONS.fuel, auto: CATEGORY_ICONS.car, 'mobilität': CATEGORY_ICONS.transport, mobilitaet: CATEGORY_ICONS.transport, fahrzeug: CATEGORY_ICONS.car, verkehr: CATEGORY_ICONS.transport, mobilfunk: CATEGORY_ICONS.internet, telefon: CATEGORY_ICONS.internet,
  reisen: CATEGORY_ICONS.travel, urlaub: CATEGORY_ICONS.travel,
  freizeit: CATEGORY_ICONS.leisure, hobby: CATEGORY_ICONS.leisure, unterhaltung: CATEGORY_ICONS.leisure,
  gesundheit: CATEGORY_ICONS.health, arzt: CATEGORY_ICONS.health, apotheke: CATEGORY_ICONS.health,
  versicherung: CATEGORY_ICONS.insurance, versicherungen: CATEGORY_ICONS.insurance,
  sparen: CATEGORY_ICONS.savings, ersparnisse: CATEGORY_ICONS.savings,
  einkommen: CATEGORY_ICONS.income, gehalt: CATEGORY_ICONS.salary, lohn: CATEGORY_ICONS.salary,
  shopping: CATEGORY_ICONS.shopping, lifestyle: CATEGORY_ICONS.shopping, kleidung: CATEGORY_ICONS.shopping,
  finanzen: CATEGORY_ICONS.income, bank: CATEGORY_ICONS.savings, kredit: CATEGORY_ICONS.savings,
});
function categoryGlyph(iconKey) {
  if (!iconKey) return null;
  const key = String(iconKey).trim();
  const path = CATEGORY_ICONS[key] || CATEGORY_ICONS[key.toLowerCase()] || CATEGORY_ICONS[key.split(/[.\-_ ]/).pop().toLowerCase()] || CATEGORY_ICONS[key.split(/[.\-_ ]/)[0].toLowerCase()];
  return path ? `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="${path}"/></svg>` : null;
}

const BRAND_LOGOS = [
  { aliases: ['VATTENFALL'], path: '/brands/vattenfall.svg' },
  { aliases: ['ENBW'], path: '/brands/enbw.svg' },
  { aliases: ['OBI'], path: '/brands/obi.svg' },
  { aliases: ['LEBARA'], path: '/brands/lebara.svg' },
  { aliases: ['VODAFONE'], path: '/brands/vodafone.svg' },
  { aliases: ['LIDL'], path: '/brands/lidl.svg' },
  { aliases: ['REWE'], path: '/brands/rewe.svg' },
  { aliases: ['EDEKA'], path: '/brands/edeka.svg' },
  { aliases: ['NETFLIX'], path: '/brands/netflix.svg' },
  { aliases: ['SPOTIFY'], path: '/brands/spotify.svg' },
  { aliases: ['DEUTSCHE BAHN', 'DB VERTRIEB', 'DB FERNVERKEHR', 'DB REGIO'], path: '/brands/deutschebahn.svg' },
  { aliases: ['SHELL'], path: '/brands/shell.svg' },
  { aliases: ['ARAL'], path: '/brands/aral.svg' },
  { aliases: ['IKEA'], path: '/brands/ikea.svg' },
  { aliases: ['ROSSMANN'], path: '/brands/rossmann.svg' },
];
function brandLogoPath(name) {
  const normalized = String(name || '').toUpperCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/[^A-Z0-9]+/g, ' ').trim();
  if (!normalized) return null;
  const padded = ` ${normalized} `;
  for (const brand of BRAND_LOGOS) {
    if (brand.aliases.some(alias => padded.includes(` ${alias} `))) return brand.path;
  }
  return null;
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
  const inferredBrandLogo = !opts.logoAssetPath ? brandLogoPath(name) : null;
  const logoAssetPath = opts.logoAssetPath || inferredBrandLogo;
  if (logoAssetPath) return `<span class="fw-ident"><img class="fw-ident-logo${inferredBrandLogo ? ' fw-ident-brand-logo' : ''}" src="${esc(logoAssetPath)}" alt="" loading="lazy" onerror="this.closest('.fw-ident').classList.add('fw-ident-failed');this.remove()"></span>`;
  const iconKey = opts.categoryIconKey;
  if (iconKey && isEmoji(iconKey)) return `<span class="fw-ident fw-ident-cat" aria-hidden="true">${esc(iconKey)}</span>`;
  const glyph = categoryGlyph(iconKey);
  if (glyph) return `<span class="fw-ident fw-ident-cat" aria-hidden="true">${glyph}</span>`;
  const initial = (String(name || '?').trim()[0] || '?').toUpperCase();
  return `<span class="fw-ident fw-monogram" style="--ident-h:${monogramHue(name)}" aria-hidden="true">${esc(initial)}</span>`;
}

// Inner markup for a category icon (used by the transactions list category chip): a literal emoji, a
// known category glyph SVG, or null. Reuses the same CATEGORY_ICONS map + emoji detection as
// identityIcon so a category renders the same icon wherever it appears.
export function categoryIconInner(iconKey) {
  if (iconKey && isEmoji(iconKey)) return esc(iconKey);
  return categoryGlyph(iconKey);
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
