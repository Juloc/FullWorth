// Klappt die Gruppen zu, die zuletzt zu waren — während das Dokument geparst wird, also bevor das
// erste Bild steht. Deshalb steht dieses <script> direkt hinter dem Menü und ist kein Modul: ein
// Modul liefe verzögert, und der Nutzer sähe die Leiste einmal offen und dann zuklappen.
try {
  const closed = (localStorage.getItem('finance.navClosedGroups') || '').split(' ');
  for (const head of document.querySelectorAll('.nav-group-head')) {
    if (closed.includes(head.dataset.group)) head.setAttribute('aria-expanded', 'false');
  }
} catch { /* Ohne localStorage bleibt alles offen. */ }
