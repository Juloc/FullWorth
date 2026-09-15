import { apiClient } from '../core/services.js';
import { categoryGlyph, categoryIconInner, isEmoji } from '../components/icons.js';

// Die Kategoriesymbole liegen in components/icons.js - die Auswahl und die Verwaltung brauchen
// dieselben. Hier nur weiterreichen, damit die bisherigen Aufrufer unveraendert bleiben.
export { categoryIconInner };

// Shared UX-rework render helpers (UX rework §10). Framework-free; consumed by transactions, contracts,
// analytics and net-worth so the same identity, card, cycle and trend primitives look identical everywhere.
// All colours/spacing come from the app.css design tokens and the `.fw-*` classes defined there.

// Importiert UND weitergereicht: ein bloßes 'export … from' holt den Namen nicht in dieses
// Modul, und sectionCard hier benutzt ihn. Das hat kein Test gemeldet - nur die Browserkonsole,
// mit "esc is not defined" auf jeder Seite, die eine Abschnittskarte zeichnet.
import { esc } from '../core/html.js';
export { esc };

// Deterministic hue (0–359) from a name, so a merchant/category keeps the same monogram tint everywhere.
export function monogramHue(name) { let h = 0; const s = String(name || ''); for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0; return h % 360; }


let OFFICIAL_BRAND_LOGOS = [];
let officialBrandCatalogLoad = null;
let officialBrandCatalogLoadedAt = 0;

export function installOfficialBrandCatalog(catalog) {
  const resolveAssetPath = x => {
    const path = x?.assetPath || x?.dataUri || '';
    return path.startsWith('/api/') ? apiClient.backendUrl(path) : path;
  };
  const assets = new Map((catalog?.assets || []).map(x => [String(x.brandKey || '').toLowerCase(), resolveAssetPath(x)]));
  OFFICIAL_BRAND_LOGOS = (catalog?.aliases || [])
    .map(x => ({
      alias: String(x.aliasKey || '').trim().toUpperCase(),
      path: assets.get(String(x.brandKey || '').toLowerCase()),
      priority: Number(x.priority) || 0
    }))
    .filter(x => x.alias && x.path)
    .sort((a, b) => (b.priority - a.priority) || (b.alias.length - a.alias.length));
  officialBrandCatalogLoadedAt = Date.now();
  return OFFICIAL_BRAND_LOGOS.length;
}

export async function ensureOfficialBrandCatalog(api, force = false) {
  const age = Date.now() - officialBrandCatalogLoadedAt;
  if (!force && OFFICIAL_BRAND_LOGOS.length && age < 6 * 60 * 60 * 1000) return true;
  if (!force && !OFFICIAL_BRAND_LOGOS.length && officialBrandCatalogLoadedAt && age < 30 * 1000) return false;
  if (officialBrandCatalogLoad) return officialBrandCatalogLoad;
  officialBrandCatalogLoad = Promise.resolve()
    .then(() => api('api/intelligence/brand-catalog'))
    .then(catalog => installOfficialBrandCatalog(catalog) > 0)
    .catch(() => { officialBrandCatalogLoadedAt = Date.now(); return false; })
    .finally(() => { officialBrandCatalogLoad = null; });
  return officialBrandCatalogLoad;
}

function brandLogoPath(name) {
  const normalized = String(name || '').toUpperCase().normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/[^A-Z0-9]+/g, ' ').trim();
  if (!normalized) return null;
  const padded = ` ${normalized} `;
  for (const brand of OFFICIAL_BRAND_LOGOS) {
    if (padded.includes(` ${brand.alias} `)) return brand.path;
  }
  return null;
}

// Left identity (UX rework §4): installed cloud/custom brand logo → category icon → category-tinted monogram,
// with a transfer glyph override. No third-party logo lookup happens from transaction rendering.
// `opts`: {logoAssetPath, categoryIconKey, isTransfer, isSavings}.
/**
 * Sets the page header's primary action: its label and what KIND of action it is.
 *
 * Mobile renders the two kinds differently (an add glyph versus an edit one), and it used to work
 * that out by running a regular expression over the rendered label - /bearbeit|edit|anpass|customi[sz]/ -
 * from a MutationObserver on the button. Two failure modes that are not worth carrying: a new label or
 * a new language silently falls back to 'add', and reading state out of rendered text is exactly the
 * pattern the frontend architecture guard exists to keep out.
 *
 * Every caller already knows which kind it is setting. `kind` is 'add' or 'edit'.
 */
export function setPrimaryAction(button, label, kind = 'add') {
  if (!button) return;
  button.textContent = label;
  button.dataset.mobileKind = kind;
  // Mobile shows the glyph alone, so the label has to survive as the accessible name.
  if (label) button.setAttribute('aria-label', label);
}

/**
 * Ein Logo, das nicht lädt, nimmt sich selbst heraus — darunter steht das Monogramm schon bereit.
 *
 * Das war ein onerror-Attribut am Bild, und die Auslieferung schickt `script-src 'self'` ohne
 * 'unsafe-inline': der Browser führt es nicht aus. Gemessen gegen die echte Richtlinie — der Handler
 * wird blockiert, das Bild bleibt stehen, und da ein Markenlogo seinen eigenen Untergrund mitbringt,
 * deckt der leere Rahmen den Buchstaben zu. Ein Fehler an einem Bild steigt nicht auf, abfangen lässt
 * er sich trotzdem: ein Zuhörer für die ganze Anwendung statt eines Attributs an jedem Bild.
 */
export function bindIdentityIcons() {
  addEventListener('error', event => {
    if (event.target.classList?.contains('fw-ident-logo')) event.target.remove();
  }, true);
}

export function identityIcon(name, opts = {}) {
  if (opts.isTransfer) return `<span class="fw-ident fw-ident-transfer" aria-hidden="true">${opts.isSavings ? '↑' : '⇄'}</span>`;
  const inferredBrandLogo = !opts.logoAssetPath ? brandLogoPath(name) : null;
  const logoAssetPath = opts.logoAssetPath || inferredBrandLogo;
  // The monogram is rendered UNDER the logo, not after it fails.
  //
  // The onerror used to remove the image and mark the span failed, which left an empty circle - so a
  // MutationObserver watched the whole <main> subtree and filled those circles in afterwards. That is
  // the repair layer the frontend architecture guard forbids, and it was fragile in the obvious way:
  // anything rendered outside <main>, or before the observer was attached, stayed blank.
  //
  // Stacking both in the same grid cell means the fallback is already in place when the image fails.
  // Removing the image is then the whole error handler, and nothing has to watch the DOM.
  // Wer das Bild herausnimmt, steht in bindIdentityIcons - nicht mehr als Attribut hier.
  if (logoAssetPath) {
    const fallback = (String(name || '?').trim()[0] || '?').toUpperCase();
    return `<span class="fw-ident fw-ident-stack fw-monogram" style="--ident-h:${monogramHue(name)}" aria-hidden="true">` +
      `<span class="fw-ident-initial">${esc(fallback)}</span>` +
      `<img class="fw-ident-logo${inferredBrandLogo ? ' fw-ident-brand-logo' : ''}" src="${esc(logoAssetPath)}" alt="" loading="lazy">` +
      `</span>`;
  }
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

// Period cycle windows (UX rework §6). The window is the N-bucket history the chart draws (Woche→12 weeks,
// Monat→12 months, Quartal→8 quarters, Jahr→5 years) and always ENDS at the active bucket. `offset` moves
// the active bucket — and therefore the whole trailing window — by ONE bucket (prev/next = one month/…).
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
    if (cycle === 'week') start.setDate(start.getDate() + offset * 7);
    else if (cycle === 'quarter') start.setMonth(start.getMonth() + offset * 3);
    else if (cycle === 'year') start.setFullYear(start.getFullYear() + offset);
    else start.setMonth(start.getMonth() + offset);
    end.setTime(start.getTime());
    if (cycle === 'week') end.setDate(end.getDate() + n * 7 - 1);
    else if (cycle === 'quarter') end.setMonth(end.getMonth() + n * 3, 0);
    else if (cycle === 'year') end.setFullYear(end.getFullYear() + n - 1, 11, 31);
    else end.setMonth(end.getMonth() + n, 0);
  }
  // Chart preview and comparison average are separate ranges. The preview ends at the selected
  // bucket; the average uses the N completed buckets immediately BEFORE it. A running month therefore
  // never dilutes its own 12-month comparison average.
  const activeEnd = new Date(end);
  const activeStart = new Date(activeEnd);
  if (cycle === 'week') activeStart.setDate(activeEnd.getDate() - 6);
  else if (cycle === 'quarter') activeStart.setMonth(Math.floor(activeEnd.getMonth() / 3) * 3, 1);
  else if (cycle === 'year') activeStart.setMonth(0, 1);
  else activeStart.setDate(1);

  const averageEnd = new Date(activeStart);
  averageEnd.setDate(averageEnd.getDate() - 1);
  const averageStart = new Date(activeStart);
  if (cycle === 'week') averageStart.setDate(averageStart.getDate() - n * 7);
  else if (cycle === 'quarter') averageStart.setMonth(averageStart.getMonth() - n * 3);
  else if (cycle === 'year') averageStart.setFullYear(averageStart.getFullYear() - n);
  else averageStart.setMonth(averageStart.getMonth() - n);

  const today = new Date(); today.setHours(12, 0, 0, 0);
  const label = cycle === 'week' ? (de ? `Letzte ${n} Wochen` : `Last ${n} weeks`)
    : cycle === 'month' ? (de ? `Letzte ${n} Monate` : `Last ${n} months`)
      : cycle === 'quarter' ? (de ? `Letzte ${n} Quartale` : `Last ${n} quarters`)
        : (de ? `Letzte ${n} Jahre` : `Last ${n} years`);
  return {
    from: iso(start),
    to: iso(end),
    granularity: cycle,
    buckets: n,
    label: offset ? `${label} (${iso(start)} – ${iso(end)})` : label,
    activeFrom: iso(activeStart),
    activeTo: iso(activeEnd),
    averageFrom: iso(averageStart),
    averageTo: iso(averageEnd),
    isCurrent: today >= activeStart && today <= activeEnd
  };
}
