// Die Hinweisseite ohne Verbindung (offline/index.html).
//
// Klassisch und am Ende des <body>: es laeuft beim Parsen, also vor dem ersten Bild - Theme und
// Sprache stehen, bevor etwas zu sehen ist.

// Dieselbe Theme-Engine wie ueberall (app/theme.js); ohne sie gilt die Farbe des Systems.
try {
  window.FullWorthTheme.applyTheme(window.FullWorthTheme.readThemeState());
} catch {
  document.documentElement.dataset.theme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

// Dieselbe Regel wie core/state.js: gespeicherte Wahl, sonst die Sprache des Browsers.
const language = localStorage.getItem('finance.language')
  || ((navigator.language || 'de').startsWith('de') ? 'de' : 'en');
if (language === 'en') {
  document.documentElement.lang = 'en';
  document.title = 'FullWorth – No connection';
  for (const element of document.querySelectorAll('[data-en]')) element.textContent = element.dataset.en;
}

// Erneut versuchen heisst: die Seite laden, auf der man war. Der Worker hat unter ihrer Adresse
// geantwortet, also ist das ein schlichtes Neuladen - und kommt die Verbindung von selbst zurueck,
// passiert es ohne Klick.
document.querySelector('[data-retry]').addEventListener('click', () => location.reload());
addEventListener('online', () => location.reload());
