const openCompensation=()=>{location.href='/compensation.html'};

document.querySelectorAll('[data-compensation-link]').forEach(button=>button.addEventListener('click',openCompensation));

const observer=new MutationObserver(()=>{
  const list=document.querySelector('.more-sheet .more-list');
  if(!list||list.querySelector('[data-compensation-mobile]'))return;
  const button=document.createElement('button');
  button.type='button';
  button.dataset.compensationMobile='1';
  button.innerHTML='<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 18V8m5 10V5m5 13v-7m5 7V9"/><path d="M3 21h18"/></svg><span>Gehalt &amp; Benefits</span>';
  button.addEventListener('click',openCompensation);
  list.appendChild(button);
});
observer.observe(document.body,{childList:true,subtree:true});
