// Global application state only.
// Feature-local state belongs inside the owning feature module.

export const state = {
  lang: localStorage.getItem('finance.language')
    || ((navigator.language || 'de').startsWith('de') ? 'de' : 'en'),
  theme: localStorage.getItem('finance.theme') || 'system',
  messages: {},
  view: 'dashboard',
  spaces: [],
  space: null,
  capabilities: {
    admin: false,
    twoFactorEnabled: false
  }
};
