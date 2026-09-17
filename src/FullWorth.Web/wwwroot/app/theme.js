// Die eine Theme-Engine. Vorher gab es vier Kopien derselben Idee (boot.js, appearance.js, app.js,
// auth/auth.js) - jede mit eigenem Kontrastformel-Duplikat oder eigener Farblogik. Diese Datei ist
// jetzt die einzige, die einen Sitz (Seed-Farbe) in abgeleitete Tokens verwandelt; alle vier Stellen
// rufen sie auf, keine rechnet mehr selbst.
//
// WICHTIG zur Ladeform: das ist absichtlich KEIN ES-Modul (kein import/export). app/boot.js ist ein
// klassisches, synchrones <script> im <head> - genau deshalb, weil ein Modul-Script vom Browser wie
// "defer" behandelt wird und das Parsen NICHT blockiert. Ein Modul hier zu machen hieße: entweder
// boot.js verliert seine Vor-dem-ersten-Bild-Garantie (Modul erst nach dem Parsen, Sprung möglich),
// oder boot.js bekäme eine zweite, eigene Kopie der Formeln (verboten - keine zweite Engine). Die
// dritte Option, die hier gewählt ist: eine einzige Datei, klassisch als <script src="/app/theme.js">
// geladen (in index.html VOR boot.js, in auth/index.html vor auth.js), die sich selbst unter
// window.FullWorthTheme einhängt. Ein klassisches Script blockt das Parsen und läuft synchron VOR
// jedem Modul-Script und vor DOMContentLoaded - boot.js findet window.FullWorthTheme darum garantiert
// schon vor, und appearance.js/app.js/auth.js (alle drei ohnehin erst nach dem Parsen dran) lesen
// denselben, längst initialisierten Namensraum. Eine Implementierung, vier Aufrufer.
(function () {
  'use strict';

  // ---------------------------------------------------------------------------------------------
  // Farbmathematik (Björn Ottosson, OKLab/OKLCH - CSS Color 4). Reine Funktionen, einzeln testbar.
  // ---------------------------------------------------------------------------------------------

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }

  function srgbToLinear(c) {
    return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
  }

  function linearToSrgb(c) {
    return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.pow(c, 1 / 2.4) - 0.055;
  }

  function hexToRgb01(hex) {
    const clean = hex.replace('#', '');
    return [0, 1, 2].map(index => parseInt(clean.slice(index * 2, index * 2 + 2), 16) / 255);
  }

  function rgb01ToHex(channels) {
    const byte = value => Math.round(clamp(value, 0, 1) * 255).toString(16).padStart(2, '0');
    return `#${channels.map(byte).join('')}`;
  }

  // sRGB-Hex -> OKLCH. Der Umweg über LMS ist kein Stil, sondern die Definition von OKLab: es gibt
  // keinen direkten 3x3-Weg von linearem sRGB nach OKLab.
  function hexToOklch(hex) {
    const [r, g, b] = hexToRgb01(hex).map(srgbToLinear);

    const l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
    const m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
    const s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

    const l_ = Math.cbrt(l);
    const m_ = Math.cbrt(m);
    const s_ = Math.cbrt(s);

    const L = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_;
    const a = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_;
    const b2 = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_;

    const C = Math.sqrt(a * a + b2 * b2);
    let H = Math.atan2(b2, a) * 180 / Math.PI;
    if (H < 0) H += 360;

    return { L, C, H };
  }

  // OKLCH -> sRGB-Hex, die Umkehrung. sRGB kann nicht jeden OKLCH-Punkt darstellen; ausserhalb des
  // Gamuts klemmt der letzte Rundungsschritt (linearToSrgb -> clamp auf [0,1]) den Wert einfach an den
  // Rand. Für diese Slice reicht das - eine echte Gamut-Abbildung (Chroma reduzieren, bis es passt)
  // wäre mehr, als hier gebraucht wird.
  function oklchToHex(oklch) {
    const { L, C, H } = oklch;
    const hr = H * Math.PI / 180;
    const a = C * Math.cos(hr);
    const b = C * Math.sin(hr);

    const l_ = L + 0.3963377774 * a + 0.2158037573 * b;
    const m_ = L - 0.1055613458 * a - 0.0638541728 * b;
    const s_ = L - 0.0894841775 * a - 1.2914855480 * b;

    const l = l_ * l_ * l_;
    const m = m_ * m_ * m_;
    const s = s_ * s_ * s_;

    const r = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
    const g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
    const b3 = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

    return rgb01ToHex([r, g, b3].map(linearToSrgb));
  }

  // "monochrom = Seed.C < 0.025" (Issue #149 §2, wörtlich). Unterhalb dieser Schwelle ist der Farbton
  // (H) reines Messrauschen aus der Hex->OKLCH-Rundung, nicht Absicht - ein Nutzer, der Schwarz/Weiss/
  // Grau wählt, hat sich bewusst gegen einen Farbton entschieden.
  function isMonochrome(oklch) {
    return oklch.C < 0.025;
  }

  // Ersetzt readableTextOn (app/appearance.js) und die zweite Kopie in app/boot.js. Die
  // WCAG-Relativluminanz war in beiden schon richtig - hier nur an einer Stelle statt an zweien.
  function onColor(hex) {
    const [r, g, b] = hexToRgb01(hex);
    const channel = value => value <= 0.03928 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
    const luminance = 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
    return luminance > 0.42 ? '#151719' : '#ffffff';
  }

  // ---------------------------------------------------------------------------------------------
  // Ableitung: ein Sitz -> eine ganze Akzent-/Neutral-/Datenpalette (Issue #149 §3/§4/§7).
  // ---------------------------------------------------------------------------------------------

  // Zielwerte für L je Stufe (Issue #149 §3), fuer farbige Sitze.
  const ACCENT_L = {
    light: { soft: 0.955, border: 0.82, solid: 0.56, hover: 0.50, text: 0.42 },
    dark: { soft: 0.235, border: 0.43, solid: 0.72, hover: 0.78, text: 0.82 }
  };

  // Eigene Helligkeitsstufen fuer den monochromen Fall (Issue #149 §3, woertlich: Light "solid fast
  // schwarz", Dark "solid fast weiß"). Diesselben L-Werte wie beim farbigen Sitz zu verwenden (nur
  // C=0) wuerde bei Licht einen L=0.56-Knopf ergeben - das ist ein mittleres Grau, kein "fast
  // schwarz", und verfehlt genau die Anforderung, die den monochromen Zweig ueberhaupt begruendet.
  // Schwarz/Weiss/Grau sollen als Sitz ein UI ergeben, das wirklich schwarz/weiss wirkt, nicht ein
  // Farbthema mit der Chroma auf 0 gedreht.
  const ACCENT_L_MONO = {
    light: { soft: 0.95, border: 0.78, solid: 0.18, hover: 0.12, text: 0.16 },
    dark: { soft: 0.20, border: 0.42, solid: 0.94, hover: 0.98, text: 0.88 }
  };
  const ACCENT_SOFT_CHROMA_FACTOR = 0.10;   // "sehr geringe Chroma" fuer die Waschung
  const ACCENT_BORDER_CHROMA_FACTOR = 0.30; // "~30% der Akzent-Chroma"
  const ACCENT_TEXT_CHROMA_FACTOR = 0.76;   // "~76% der Akzent-Chroma"

  function accentSolidChroma(seed) {
    return isMonochrome(seed) ? 0 : clamp(seed.C * 0.95, 0.11, 0.21);
  }

  function accentScale(seed, mode) {
    const mono = isMonochrome(seed);
    const steps = mono ? ACCENT_L_MONO[mode] : ACCENT_L[mode];
    const solidC = accentSolidChroma(seed);
    const hue = seed.H;
    // Kein erfundener Farbton: bei C=0 ist H beliebig (atan2(0,0) bzw. Rauschen), aber
    // oklchToHex({C:0}) ignoriert H ohnehin rechnerisch (cos/sin werden mit 0 multipliziert) - der
    // reine Grauton bleibt grau, unabhaengig davon, welches H hier steht.
    const chromaAt = factor => mono ? 0 : solidC * factor;

    return {
      soft: oklchToHex({ L: steps.soft, C: chromaAt(ACCENT_SOFT_CHROMA_FACTOR), H: hue }),
      border: oklchToHex({ L: steps.border, C: chromaAt(ACCENT_BORDER_CHROMA_FACTOR), H: hue }),
      solid: oklchToHex({ L: steps.solid, C: solidC, H: hue }),
      hover: oklchToHex({ L: steps.hover, C: solidC, H: hue }),
      text: oklchToHex({ L: steps.text, C: chromaAt(ACCENT_TEXT_CHROMA_FACTOR), H: hue })
    };
  }

  // Neutral-Skala (Issue #149 §4): dieselbe Idee wie die --tint-* Mischungen, die appearance.css schon
  // kennt, nur jetzt in echtem OKLCH statt per CSS color-mix. Bewusst NEUE, additive Token-Namen
  // (--neutral-*) statt --bg/--surface/--line/--text selbst zu ueberschreiben: die bestehenden Werte
  // sind in tokens.css fein abgestimmt (ThemeParityTests verlangt Licht/Dunkel-Parität), und ein
  // pixelgleicher Default (leerer Sitz) darf durch diese Slice nicht verschieben. Das Verdrahten der
  // neutralen Chrome-Flaechen auf --neutral-* ist eine spaetere Slice.
  const NEUTRAL_L = {
    light: { bg: 0.97, surface: 1.0, border: 0.87, text: 0.15 },
    dark: { bg: 0.19, surface: 0.23, border: 0.30, text: 0.96 }
  };

  function neutralScale(seed, mode) {
    const anchors = NEUTRAL_L[mode];
    const mono = isMonochrome(seed);
    // "neutralChroma = min(0.007, accentChroma*0.035)" (Issue #149 §4, woertlich); 0 im monochromen
    // Fall - ein echtes Grau bleibt ein echtes Grau, es bekommt keinen erfundenen Hauch Farbton.
    const chroma = mono ? 0 : Math.min(0.007, accentSolidChroma(seed) * 0.035);
    const hue = seed.H;

    return {
      bg: oklchToHex({ L: anchors.bg, C: chroma, H: hue }),
      surface: oklchToHex({ L: anchors.surface, C: chroma, H: hue }),
      border: oklchToHex({ L: anchors.border, C: chroma, H: hue }),
      text: oklchToHex({ L: anchors.text, C: chroma, H: hue })
    };
  }

  // Datenpalette (Issue #149 §7): sechs Diagrammfarben. Der monochrome Fall bekommt KEINE graue
  // Palette - ein Kreisdiagramm aus sechs Grauschattierungen ist nicht mehr lesbar, welcher Anteil
  // welcher Kategorie gehoert. Deshalb ein fester, weiterhin bunter Farbton-Satz, unabhaengig vom
  // gewaehlten (grauen) Sitz UND unabhaengig vom Modus - das ist die einzige Stelle im ganzen Modul,
  // an der bewusst doch ein "erfundener" Farbton auftaucht, und zwar absichtlich: Diagrammfarben
  // sind kein Chrome-Element, sie muessen nur weiterhin unterscheidbar sein.
  const DATA_HUE_OFFSETS = [0, 55, 118, 182, 245, 310];
  const DATA_HUES_MONOCHROME = [255, 145, 30, 325, 195, 75];
  const DATA_L = {
    light: [0.56, 0.62, 0.60, 0.58, 0.61, 0.64],
    dark: [0.72, 0.76, 0.74, 0.73, 0.76, 0.78]
  };
  const DATA_C = [0.22, 0.20, 0.19, 0.21, 0.18, 0.17];

  function dataPalette(seed, mode) {
    const mono = isMonochrome(seed);
    const hues = mono
      ? DATA_HUES_MONOCHROME
      : DATA_HUE_OFFSETS.map(offset => (seed.H + offset) % 360);
    const L = DATA_L[mode];
    return hues.map((hue, index) => oklchToHex({ L: L[index], C: DATA_C[index], H: hue }));
  }

  // ---------------------------------------------------------------------------------------------
  // Persistenter Zustand (Issue #149 §12): genau drei Schluessel, nichts Abgeleitetes je gespeichert.
  // ---------------------------------------------------------------------------------------------

  const STORAGE = Object.freeze({
    mode: 'finance.theme',       // bestehender Schluessel, unveraendert
    seed: 'finance.themeSeed',
    logoMode: 'finance.logoMode'
  });

  // Die drei alten Schluessel. finance.color.secondary hat kein Ziel: es gab bisher zwei unabhaengig
  // waehlbare Farben (Primaer -> Knoepfe, Sekundaer -> Links/Fokus/Diagramme); jetzt leitet EIN Sitz
  // den ganzen Akzent-Umfang selbst her (Waschung/Rand/Volltext/Hover/Text), und eine zweite,
  // unabhaengige Farbe hat in diesem Modell keinen Platz mehr - sie zu erhalten hiesse, sie irgendwie
  // in den einen Sitz hineinzumischen (willkuerlich) oder einen zweiten Sitz einzufuehren (widerspricht
  // "EIN Sitz"). Wer vorher eine Primaerfarbe gewaehlt hatte - die dominantere Entscheidung, sie trieb
  // die Knoepfe - behaelt die als neuen Sitz; die Sekundaerfarbe verschwindet ersatzlos.
  const LEGACY_STORAGE = Object.freeze({
    primary: 'finance.color.primary',
    secondary: 'finance.color.secondary',
    tintLogo: 'finance.color.tintLogo'
  });

  const HEX = /^#[0-9a-f]{6}$/i;
  function normalizeHex(value) {
    const text = String(value ?? '').trim().toLowerCase();
    return HEX.test(text) ? text : '';
  }

  // Liest die drei alten Schluessel genau einmal, uebersetzt sie in die neuen und loescht sie danach -
  // kein Dauerzustand mit zwei parallelen Speicherorten. Idempotent: laeuft sie ein zweites Mal, sind
  // die alten Schluessel schon weg und es passiert nichts.
  function migrateLegacyState() {
    try {
      if (localStorage.getItem(STORAGE.seed) === null) {
        const legacySeed = normalizeHex(localStorage.getItem(LEGACY_STORAGE.primary));
        if (legacySeed) localStorage.setItem(STORAGE.seed, legacySeed);
      }
      if (localStorage.getItem(STORAGE.logoMode) === null && localStorage.getItem(LEGACY_STORAGE.tintLogo) !== null) {
        const wasThemed = localStorage.getItem(LEGACY_STORAGE.tintLogo) === 'true';
        localStorage.setItem(STORAGE.logoMode, wasThemed ? 'themed' : 'standard');
      }
      Object.values(LEGACY_STORAGE).forEach(key => localStorage.removeItem(key));
    } catch {
      // Kein localStorage (privater Modus o.ae.) - dann bleibt es beim Standard, es gibt nichts zu migrieren.
    }
  }

  function readThemeState() {
    migrateLegacyState();
    let mode = 'system';
    let seed = '';
    let logoMode = 'standard';
    try {
      mode = localStorage.getItem(STORAGE.mode) || 'system';
      seed = normalizeHex(localStorage.getItem(STORAGE.seed));
      logoMode = localStorage.getItem(STORAGE.logoMode) === 'themed' ? 'themed' : 'standard';
    } catch {
      // s.o.
    }
    return { mode, seed, logoMode };
  }

  // Schreibt NUR die drei kanonischen Schluessel. Kein abgeleiteter Farbwert (Akzent/Neutral/Daten)
  // geht je in den localStorage - er wird bei jedem Laden neu aus dem Sitz gerechnet.
  function writeThemeState(next) {
    try {
      if (next.mode !== undefined) localStorage.setItem(STORAGE.mode, next.mode);
      if (next.seed !== undefined) {
        const seed = normalizeHex(next.seed);
        if (seed) localStorage.setItem(STORAGE.seed, seed); else localStorage.removeItem(STORAGE.seed);
      }
      if (next.logoMode !== undefined) localStorage.setItem(STORAGE.logoMode, next.logoMode === 'themed' ? 'themed' : 'standard');
    } catch {
      // s.o.
    }
  }

  function resolveMode(mode) {
    return mode === 'system'
      ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')
      : mode;
  }

  // ---------------------------------------------------------------------------------------------
  // Logo-Einfaerbung: unveraendert aus app/appearance.js uebernommen (Mechanismus ist nicht Teil
  // dieser Slice), nur an den einen Sitz statt an primary/secondary angepasst.
  // ---------------------------------------------------------------------------------------------

  const BRAND_MARK_URL = '/branding/fullworth-logo.svg';
  const BRAND_MARK_GREYS = Object.freeze(['#C9CDD1', '#A5AAAF', '#878D92', '#2D3235']);
  const BRAND_MARK_MIX = Object.freeze([0.55, 0.35, 0.18, 0]);
  let brandMarkSource = null;

  function mixHex(from, to, amount) {
    const part = index => {
      const a = parseInt(from.slice(1 + index * 2, 3 + index * 2), 16);
      const b = parseInt(to.slice(1 + index * 2, 3 + index * 2), 16);
      return Math.round(a + (b - a) * amount).toString(16).padStart(2, '0');
    };
    return `#${part(0)}${part(1)}${part(2)}`;
  }

  async function tintBrandMark(seed, logoMode, mode) {
    const marks = document.querySelectorAll('.brand-logo');
    if (!marks.length) return;
    const tint = logoMode === 'themed' && seed;

    if (!tint) {
      marks.forEach(mark => { if (mark.dataset.brandOriginal) mark.src = mark.dataset.brandOriginal; });
      return;
    }

    if (brandMarkSource === null) {
      try { brandMarkSource = await (await fetch(BRAND_MARK_URL)).text(); }
      catch { brandMarkSource = ''; }
    }
    if (!brandMarkSource) return;

    const backdrop = mode === 'dark' ? '#121416' : '#ffffff';
    let svg = brandMarkSource;
    BRAND_MARK_GREYS.forEach((grey, index) => {
      svg = svg.replaceAll(grey, mixHex(seed, backdrop, BRAND_MARK_MIX[index]));
    });
    svg = svg.replace(/@media \(prefers-color-scheme: dark\)[^}]*}[^}]*}/, '');
    const url = `data:image/svg+xml;utf8,${encodeURIComponent(svg)}`;
    marks.forEach(mark => {
      if (!mark.dataset.brandOriginal) mark.dataset.brandOriginal = mark.getAttribute('src') || BRAND_MARK_URL;
      mark.src = url;
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Anwenden: die eine Funktion, die alle vier Aufrufer statt ihrer eigenen Logik rufen.
  // ---------------------------------------------------------------------------------------------

  const DERIVED_PROPERTIES = [
    '--brand-primary', '--cta', '--cta-text', '--accent', '--accent-soft',
    '--accent-border', '--accent-hover', '--accent-text',
    '--neutral-bg', '--neutral-surface', '--neutral-border', '--neutral-text',
    '--data-1', '--data-2', '--data-3', '--data-4', '--data-5', '--data-6'
  ];

  function applyTheme(next, targetElement) {
    const state = next || {};
    const root = targetElement || document.documentElement;
    const actualMode = resolveMode(state.mode || 'system');
    root.dataset.theme = actualMode;

    const seed = normalizeHex(state.seed);
    const logoMode = state.logoMode === 'themed' ? 'themed' : 'standard';

    if (!seed) {
      // Kein Sitz gewaehlt: tokens.css bleibt zustaendig, exakt wie vor dieser Slice. "Leer" ist kein
      // Farbwert, der zufaellig dem Standard gleicht, sondern die Abwesenheit einer Entscheidung -
      // removeProperty statt Standardwert setzen, damit Hell/Dunkel je ihren eigenen Wert behalten.
      DERIVED_PROPERTIES.forEach(name => root.style.removeProperty(name));
      root.dataset.brandTint = 'off';
      void tintBrandMark('', 'standard', actualMode);
      return { mode: actualMode, seed: '', logoMode: 'standard' };
    }

    const seedOklch = hexToOklch(seed);
    const accent = accentScale(seedOklch, actualMode);
    const neutral = neutralScale(seedOklch, actualMode);
    const data = dataPalette(seedOklch, actualMode);

    root.style.setProperty('--brand-primary', seed);
    root.style.setProperty('--cta', accent.solid);
    root.style.setProperty('--cta-text', onColor(accent.solid));
    root.style.setProperty('--accent', accent.solid);
    root.style.setProperty('--accent-soft', accent.soft);
    root.style.setProperty('--accent-border', accent.border);
    root.style.setProperty('--accent-hover', accent.hover);
    root.style.setProperty('--accent-text', accent.text);
    root.style.setProperty('--neutral-bg', neutral.bg);
    root.style.setProperty('--neutral-surface', neutral.surface);
    root.style.setProperty('--neutral-border', neutral.border);
    root.style.setProperty('--neutral-text', neutral.text);
    data.forEach((hex, index) => root.style.setProperty(`--data-${index + 1}`, hex));

    root.dataset.brandTint = logoMode === 'themed' ? 'on' : 'off';
    void tintBrandMark(seed, logoMode, actualMode);

    return { mode: actualMode, seed, logoMode };
  }

  window.FullWorthTheme = Object.freeze({
    // Mathematik
    hexToOklch,
    oklchToHex,
    isMonochrome,
    onColor,
    accentScale,
    neutralScale,
    dataPalette,
    // Zustand
    STORAGE_KEYS: STORAGE,
    readThemeState,
    writeThemeState,
    resolveMode,
    // Anwenden
    applyTheme
  });
})();
