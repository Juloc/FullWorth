export const ButtonRole = Object.freeze({
  Primary: 'primary',
  Secondary: 'secondary',
  Danger: 'danger'
});

const ROLE_CLASS = Object.freeze({
  [ButtonRole.Primary]: 'btn-primary',
  [ButtonRole.Secondary]: 'btn-secondary',
  [ButtonRole.Danger]: 'btn-danger'
});

export function buttonClass(role = ButtonRole.Secondary, extra = '') {
  const roleClass = ROLE_CLASS[role];
  if (!roleClass) throw new Error(`Unknown button role: ${role}`);
  return ['btn', roleClass, extra].filter(Boolean).join(' ');
}

export function applyButtonRole(button, role = ButtonRole.Secondary) {
  if (!button) return button;
  button.classList.remove('btn', 'btn-primary', 'btn-secondary', 'btn-danger');
  button.classList.add(...buttonClass(role).split(' '));
  return button;
}
