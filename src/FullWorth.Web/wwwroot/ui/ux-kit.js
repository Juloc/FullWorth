// Shared UX-rework render helpers (UX rework §10). Framework-free; consumed by transactions, contracts,
// analytics and net-worth so the same identity, card, cycle and trend primitives look identical everywhere.
// All colours/spacing come from the app.css design tokens and the `.fw-*` classes defined there.

export function esc(v) { return String(v ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c])); }

// Deterministic hue (0–359) from a name, so a merchant/category keeps the same monogram tint everywhere.
export function monogramHue(name) { let h = 0; const s = String(name || ''); for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0; return h % 360; }

// Left identity (UX rework §4): curated local brand logo → category-tinted monogram → transfer glyph.
// No external/third-party logo lookups. `opts`: {logoAssetPath, isTransfer, isSavings}.
export function identityIcon(name, opts = {}) {
  if (opts.isTransfer) return `<span class="fw-ident fw-ident-transfer" aria-hidden="true">${opts.isSavings ? '↑' : '⇄'}</span>`;
  if (opts.logoAssetPath) return `<span class="fw-ident"><img class="fw-ident-logo" src="${esc(opts.logoAssetPath)}" alt="" loading="lazy" onerror="this.closest('.fw-ident').classList.add('fw-ident-failed');this.remove()"></span>`;
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
  // Shift by whole windows for prev/next.
  if (offset) {
    const shift = (d) => {
      if (cycle === 'week') d.setDate(d.getDate() + offset * n * 7);
      else if (cycle === 'quarter') d.setMonth(d.getMonth() + offset * n * 3);
      else if (cycle === 'year') d.setFullYear(d.getFullYear() + offset * n);
      else d.setMonth(d.getMonth() + offset * n);
    };
    shift(start); shift(end);
  }
  const label = cycle === 'week' ? (de ? `Letzte ${n} Wochen` : `Last ${n} weeks`)
    : cycle === 'month' ? (de ? `Letzte ${n} Monate` : `Last ${n} months`)
      : cycle === 'quarter' ? (de ? `Letzte ${n} Quartale` : `Last ${n} quarters`)
        : (de ? `Letzte ${n} Jahre` : `Last ${n} years`);
  return { from: iso(start), to: iso(end), granularity: cycle, label: offset ? `${label} (${iso(start)} – ${iso(end)})` : label };
}
