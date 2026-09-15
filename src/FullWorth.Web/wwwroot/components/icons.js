// Die Symbole, die eine Kategorie ueberall gleich aussehen lassen.
//
// Es gab drei Bestaende: die Navigation in app/menu.js, diese Sammlung in features/ux-kit.js und
// noch einen seitenlokalen in pages/accounts/presentation.js. Nur der hier kannte Kategorien, und
// genau deshalb hatte die Buchungsliste Symbole - die Kategorie-Auswahl und die Kategorieverwaltung
// zeigten stattdessen den blanken Schluessel als Text, also \groceries\ statt eines Einkaufswagens.
//
// Der Bestand liegt jetzt in components/, weil components/ weder eine Seite noch den Server kennen
// darf - features/ux-kit.js holt ihn sich von hier, nicht umgekehrt.

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

// True for a categoryIconKey that is a literal emoji (some categories store an emoji in their Icon field).
// Uses explicit ES6 code-point ranges (\u{…} with /u) rather than the \p{…} property escape — the latter
// is ES2018 and, being a regex literal parsed eagerly, would throw at module load on an engine that lacks
// it (the surrounding try/catch, which only wraps .test(), could not catch that).
const EMOJI_RE = /[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{2B00}-\u{2BFF}]/u;
export function isEmoji(s) { try { return EMOJI_RE.test(String(s || '')); } catch { return false; } }

export function categoryGlyph(iconKey) {
  if (!iconKey) return null;
  const key = String(iconKey).trim();
  const path = CATEGORY_ICONS[key] || CATEGORY_ICONS[key.toLowerCase()] || CATEGORY_ICONS[key.split(/[.\-_ ]/).pop().toLowerCase()] || CATEGORY_ICONS[key.split(/[.\-_ ]/)[0].toLowerCase()];
  return path ? `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="${path}"/></svg>` : null;
}

/**
 * Das innere Markup eines Kategoriesymbols: ein Emoji, ein bekanntes Glyph als SVG - oder null.
 * Dieselbe Sammlung und dieselbe Emoji-Erkennung wie identityIcon, damit eine Kategorie ueberall
 * gleich aussieht.
 */
export function categoryIconInner(iconKey) {
  if (iconKey && isEmoji(iconKey)) return escapeHtml(iconKey);
  return categoryGlyph(iconKey);
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[c]));
}

/** Die Schluessel, aus denen ein Mensch waehlen kann - ohne die Alias-Eintraege. */
export const CATEGORY_ICON_KEYS = [
  'income', 'salary', 'housing', 'rent', 'mortgage', 'groceries', 'food', 'restaurants',
  'transport', 'car', 'fuel', 'vehicle', 'electricity', 'utilities', 'internet', 'health',
  'shopping', 'leisure', 'savings', 'insurance', 'travel', 'subscriptions', 'education',
  'pets', 'fees', 'taxes', 'donations', 'debt', 'transfers', 'cash', 'family', 'other',
];


/**
 * Eine Auswahl aus den bekannten Kategoriesymbolen, direkt im Formular statt als zweiter Dialog.
 *
 * Vorher stand an dieser Stelle ein Textfeld mit `maxlength="8"` - in das `groceries` gar nicht
 * hineinpasste. Wer ein Symbol setzen wollte, musste den Schluessel kennen UND er durfte hoechstens
 * acht Zeichen lang sein; praktisch ging nur ein Emoji.
 *
 * Gibt ein Element zurueck; der gewaehlte Schluessel steht in `.dataset.icon` des aktiven Knopfes
 * bzw. ist ueber `selectedIconKey(element)` zu lesen. Kein Symbol ist eine gueltige Wahl.
 */
export function categoryIconPicker(currentKey, labels = {}) {
  const wrap = document.createElement('div');
  wrap.className = 'category-icon-picker';

  const choice = (key, inner, title) => {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'category-icon-choice';
    button.dataset.icon = key;
    button.innerHTML = inner;
    button.title = title;
    button.setAttribute('aria-label', title);
    button.setAttribute('aria-pressed', String(key === (currentKey || '')));
    button.classList.toggle('selected', key === (currentKey || ''));
    button.addEventListener('click', () => {
      for (const other of wrap.querySelectorAll('.category-icon-choice')) {
        other.classList.remove('selected');
        other.setAttribute('aria-pressed', 'false');
      }
      button.classList.add('selected');
      button.setAttribute('aria-pressed', 'true');
    });
    return button;
  };

  wrap.append(choice('', '—', labels.none || 'Kein Symbol'));
  for (const key of CATEGORY_ICON_KEYS) wrap.append(choice(key, categoryGlyph(key) || '', key));

  // Ein Emoji, das schon an der Kategorie haengt, bleibt waehlbar - sonst verloere das Speichern es.
  if (currentKey && isEmoji(currentKey)) {
    const custom = choice(currentKey, escapeHtml(currentKey), currentKey);
    wrap.prepend(custom);
  }
  return wrap;
}

/** Der gewaehlte Schluessel, oder null fuer "kein Symbol". */
export function selectedIconKey(pickerElement) {
  return pickerElement?.querySelector('.category-icon-choice.selected')?.dataset.icon || null;
}

/**
 * Der Papierkorb. Ein SVG und kein Emoji: ein Emoji sieht auf jedem Betriebssystem anders aus,
 * traegt seine eigene Grundlinie mit und laesst sich nicht einfaerben - neben den uebrigen
 * Strichsymbolen faellt es auf, ohne etwas zu sagen.
 *
 * Er lag als Konstante in pages/accounts/page.js, wo ihn nur diese eine Seite hatte. Dieselbe
 * Handlung braucht dasselbe Symbol.
 */
export const TRASH_ICON =
  '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2m2 0v12a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V7"/></svg>';
