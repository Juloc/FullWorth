// Maskieren. Das Einzige, was jede Schicht braucht und keine Schicht besitzt.
//
// Es lag in features/ux-kit.js, und deshalb importierte eine Komponente — das Formulardialog —
// aus den Features. Eine Komponente, die eine Seite oder ein Feature kennt, ist keine mehr.
export const esc = value => String(value ?? '').replace(/[&<>'"]/g,
  character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[character]));
