(() => {
  const standalone = window.matchMedia('(display-mode: standalone)').matches || window.navigator.standalone === true;
  if (!standalone) return;
  document.documentElement.classList.add('pwa-standalone');
  document.querySelector('meta[name="viewport"]')?.setAttribute(
    'content',
    'width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover'
  );
})();
