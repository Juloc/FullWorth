// Die Symbole, die eine Kategorie ueberall gleich aussehen lassen.
//
// Es gab drei Bestaende: die Navigation in app/menu.js, diese Sammlung in features/ux-kit.js und
// noch einen seitenlokalen in pages/accounts/presentation.js. Nur der hier kannte Kategorien, und
// genau deshalb hatte die Buchungsliste Symbole - die Kategorie-Auswahl und die Kategorieverwaltung
// zeigten stattdessen den blanken Schluessel als Text, also \groceries\ statt eines Einkaufswagens.
//
// Der Bestand liegt jetzt in components/, weil components/ weder eine Seite noch den Server kennen
// darf - features/ux-kit.js holt ihn sich von hier, nicht umgekehrt.
//
// Die Geometrie steht in icons/sprite.svg (#154); hier steht nur, welcher Schluessel welches Symbol
// zeigt. IconSpriteTests haelt fest, dass jedes genannte Symbol dort existiert - ein <use> auf eine
// fehlende ID zeichnet sonst still nichts.

import { spriteHref } from './sprite.js';

// A small set of category glyphs keyed by stable semantic category keys (or the last/first segment of a
// dotted key). Used as the middle identity tier when a booking has no brand logo but a known category
// icon. Unknown keys fall through to the monogram. Line-art matching the rest of the icon set.
const CATEGORY_ICONS = {
  income: 'cat-income', salary: 'cat-income',
  housing: 'cat-housing', rent: 'cat-housing', mortgage: 'cat-housing',
  groceries: 'cat-groceries',
  food: 'cat-food', restaurants: 'cat-food',
  transport: 'cat-transport', car: 'cat-transport', fuel: 'cat-transport', vehicle: 'cat-transport',
  electricity: 'cat-electricity', utilities: 'cat-electricity',
  internet: 'cat-internet',
  health: 'cat-health', donations: 'cat-health',
  shopping: 'cat-shopping',
  leisure: 'cat-leisure',
  savings: 'cat-savings',
  insurance: 'cat-insurance',
  travel: 'cat-travel',
  subscriptions: 'cat-subscriptions',
  education: 'cat-education',
  pets: 'cat-pets',
  fees: 'cat-fees',
  taxes: 'cat-taxes',
  debt: 'cat-debt',
  transfers: 'cat-transfers',
  cash: 'cat-cash',
  family: 'cat-family',
  other: 'cat-other',
  // Keine Kategorie oder eine ohne eigenes Symbol: das Etikett, das die Buchungsliste dann zeigt.
  untagged: 'cat-untagged',
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
  const symbol = CATEGORY_ICONS[key] || CATEGORY_ICONS[key.toLowerCase()] || CATEGORY_ICONS[key.split(/[.\-_ ]/).pop().toLowerCase()] || CATEGORY_ICONS[key.split(/[.\-_ ]/)[0].toLowerCase()];
  return symbol ? `<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><use href="${spriteHref(symbol)}"></use></svg>` : null;
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
  `<svg viewBox="0 0 24 24" aria-hidden="true"><use href="${spriteHref('ui-trash')}"></use></svg>`;
