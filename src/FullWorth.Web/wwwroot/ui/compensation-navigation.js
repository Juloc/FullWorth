export function openCompensation() {
  location.href = '/compensation.html';
}

export function bindCompensationNavigation(root = document) {
  root.querySelectorAll('[data-compensation-link]').forEach(button => {
    if (button.dataset.compensationBound === '1') return;
    button.dataset.compensationBound = '1';
    button.addEventListener('click', openCompensation);
  });
}

export function compensationMoreButton(label) {
  return `<button type="button" data-compensation-link data-compensation-mobile>
    <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 18V8m5 10V5m5 13v-7m5 7V9"/><path d="M3 21h18"/></svg>
    <span>${label}</span>
  </button>`;
}
