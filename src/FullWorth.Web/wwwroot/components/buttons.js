export const ButtonRole = Object.freeze({
  Primary: 'primary',
  Secondary: 'secondary',
  Danger: 'danger',
  // Vorbereitung fuer Scheibe 4c (Rollen-Migration): randlose Textaktion ohne Flaeche/Rand.
  Quiet: 'quiet',
  // 36x36 rundes Icon-only-Control - identische Box wie das bestehende .icon-button (shell.css).
  Icon: 'icon'
});

const ROLE_CLASS = Object.freeze({
  [ButtonRole.Primary]: 'btn-primary',
  [ButtonRole.Secondary]: 'btn-secondary',
  [ButtonRole.Danger]: 'btn-danger',
  [ButtonRole.Quiet]: 'btn-quiet',
  [ButtonRole.Icon]: 'btn-icon'
});

const ALL_ROLE_CLASSES = Object.freeze(['btn', ...Object.values(ROLE_CLASS)]);

export function buttonClass(role = ButtonRole.Secondary, extra = '') {
  const roleClass = ROLE_CLASS[role];
  if (!roleClass) throw new Error(`Unknown button role: ${role}`);
  return ['btn', roleClass, extra].filter(Boolean).join(' ');
}

export function applyButtonRole(button, role = ButtonRole.Secondary) {
  if (!button) return button;
  button.classList.remove(...ALL_ROLE_CLASSES);
  button.classList.add(...buttonClass(role).split(' '));
  return button;
}
