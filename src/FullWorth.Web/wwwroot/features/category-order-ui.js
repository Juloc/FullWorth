const $=(s,r=document)=>r.querySelector(s);
const $$=(s,r=document)=>[...r.querySelectorAll(s)];
const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const sid=()=>localStorage.getItem('finance.space')||'';
const en=()=>document.documentElement.lang?.startsWith('en');
const t=(de,enText)=>en()?enText:de;
function withSpace(path){const [base,q='']=path.split('?');const p=new URLSearchParams(q);if(sid()&&!p.has('fullWorthSpaceId'))p.set('fullWorthSpaceId',sid());return `${base}?${p}`}
async function api(path,opt){const r=await fetch(`/bff/backend/${withSpace(path.replace(/^\//,''))}`,opt);if(!r.ok){let m=`${r.status}`;try{const b=await r.json();m=b.error||b.title||m}catch{}throw new Error(m)}if(r.status===204)return null;return r.json()}
const json=(method,body)=>({method,headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
function toast(message){const el=$('#toast');if(!el)return;el.textContent=message;el.classList.add('show');clearTimeout(toast.timer);toast.timer=setTimeout(()=>el.classList.remove('show'),3200)}
function ensureCss(){if(document.querySelector('link[data-co-css]'))return;const l=document.createElement('link');l.rel='stylesheet';l.href='/category-order.css';l.dataset.coCss='1';document.head.appendChild(l)}ensureCss();

async function openOrder(){
  let categories;try{categories=(await api('api/categories?includeArchived=false'))||[]}catch(e){toast(e.message);return}
  const byId=new Map(categories.map(c=>[c.id,c]));
  const children=new Map();
  for(const c of categories){const key=c.parentId||'root';if(!children.has(key))children.set(key,[]);children.get(key).push(c.id)}
  for(const ids of children.values())ids.sort((a,b)=>(byId.get(a).sortOrder??0)-(byId.get(b).sortOrder??0)||byId.get(a).name.localeCompare(byId.get(b).name));

  const d=document.createElement('dialog');d.className='co-dialog';d.innerHTML=`<div class="co-card"><div class="co-head"><div><h2>${esc(t('Kategorien sortieren','Reorder categories'))}</h2><p>${esc(t('Drag & Drop auf Desktop oder die Pfeile auf jedem Gerät. Parent-Wechsel erfolgt weiterhin im Kategorie-Editor.','Use drag & drop on desktop or the arrow buttons on any device. Parent changes remain in the category editor.'))}</p></div><button type="button" data-close aria-label="${esc(t('Schließen','Close'))}">×</button></div><div data-tree class="co-tree"></div><div class="co-actions"><button type="button" class="ghost" data-cancel>${esc(t('Abbrechen','Cancel'))}</button><button type="button" data-save>${esc(t('Reihenfolge speichern','Save order'))}</button></div></div>`;document.body.appendChild(d);
  const tree=$('[data-tree]',d);let dragged=null;
  function renderBranch(parentId,depth=0){const key=parentId||'root';return (children.get(key)||[]).map((id,index)=>{const c=byId.get(id);return `<div class="co-node" data-id="${id}" data-parent="${parentId||''}" draggable="true" style="--co-depth:${depth}"><div class="co-row"><span class="co-handle" aria-hidden="true">⠿</span><span class="co-name">${c.icon?`${esc(c.icon)} `:''}${esc(c.name)}</span><div class="co-buttons"><button type="button" class="ghost" data-up ${index===0?'disabled':''} aria-label="${esc(t('Nach oben','Move up'))}">↑</button><button type="button" class="ghost" data-down ${index===(children.get(key)||[]).length-1?'disabled':''} aria-label="${esc(t('Nach unten','Move down'))}">↓</button></div></div>${renderBranch(id,depth+1)}</div>`}).join('')}
  function render(){tree.innerHTML=renderBranch(null);$$('.co-node',tree).forEach(node=>{
    const id=node.dataset.id,parent=node.dataset.parent||null;
    $('[data-up]',node).onclick=e=>{e.stopPropagation();move(id,parent,-1)};
    $('[data-down]',node).onclick=e=>{e.stopPropagation();move(id,parent,1)};
    node.ondragstart=e=>{e.stopPropagation();dragged={id,parent};node.classList.add('dragging');e.dataTransfer.effectAllowed='move';e.dataTransfer.setData('text/plain',id)};
    node.ondragend=e=>{e.stopPropagation();node.classList.remove('dragging');dragged=null;$$('.co-drop',tree).forEach(x=>x.classList.remove('co-drop'))};
    $('.co-row',node).ondragover=e=>{if(!dragged||dragged.parent!==parent||dragged.id===id)return;e.preventDefault();e.stopPropagation();e.dataTransfer.dropEffect='move';$('.co-row',node).classList.add('co-drop')};
    $('.co-row',node).ondragleave=e=>{e.stopPropagation();$('.co-row',node).classList.remove('co-drop')};
    $('.co-row',node).ondrop=e=>{e.preventDefault();e.stopPropagation();$('.co-row',node).classList.remove('co-drop');if(!dragged||dragged.parent!==parent||dragged.id===id)return;const list=children.get(parent||'root')||[];const from=list.indexOf(dragged.id),to=list.indexOf(id);if(from<0||to<0)return;list.splice(from,1);list.splice(to,0,dragged.id);render()};
  })}
  function move(id,parent,delta){const list=children.get(parent||'root')||[];const index=list.indexOf(id),next=index+delta;if(index<0||next<0||next>=list.length)return;[list[index],list[next]]=[list[next],list[index]];render()}
  render();
  $('[data-close]',d).onclick=$('[data-cancel]',d).onclick=()=>d.close();d.addEventListener('close',()=>d.remove());
  $('[data-save]',d).onclick=async()=>{const items=[];for(const [parentKey,ids] of children.entries())ids.forEach((id,index)=>items.push({id,parentId:parentKey==='root'?null:parentKey,sortOrder:(index+1)*10}));const save=$('[data-save]',d);save.disabled=true;try{const result=await api('api/category-order',json('PUT',{items}));toast(t(`${result.changed} Kategorien aktualisiert.`,`${result.changed} categories updated.`));d.close();$('#cat-archived')?.dispatchEvent(new Event('change'))}catch(e){toast(e.message);save.disabled=false}};
  d.showModal();
}

function install(){const tree=$('#categories-tree');if(!tree)return;const panel=tree.closest('.panel')||tree.parentElement;const head=panel?.querySelector('.panel-head');if(!head||head.querySelector('[data-category-order]'))return;const b=document.createElement('button');b.type='button';b.className='ghost';b.dataset.categoryOrder='1';b.textContent=t('Sortieren','Reorder');b.onclick=openOrder;const actions=head.querySelector('.panel-head-actions');(actions||head).appendChild(b)}
function boot(){install();new MutationObserver(()=>{clearTimeout(boot.timer);boot.timer=setTimeout(install,100)}).observe(document.body,{childList:true,subtree:true})}boot();
