import { createDialog } from '../ui/dialog.js';

export function createViewHelpers({ state, get, api }) {
  const esc=value=>String(value??'').replace(/[&<>'"]/g,char=>({
    '&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'
  }[char]));

  const date=value=>{
    if(!value)return'—';
    return new Intl.DateTimeFormat(state.lang==='de'?'de-DE':'en-US')
      .format(new Date(`${String(value).slice(0,10)}T12:00:00`));
  };

  const dateTime=value=>{
    if(!value)return'—';
    const raw=String(value);
    if(!/[T ]\d{2}:\d{2}/.test(raw))return date(value);
    const parsed=new Date(raw);
    if(Number.isNaN(parsed.getTime()))return date(value);
    return new Intl.DateTimeFormat(state.lang==='de'?'de-DE':'en-US',{dateStyle:'medium',timeStyle:'medium'}).format(parsed);
  };

  const empty=(element,message)=>{
    element.innerHTML=`<div class="row state-empty"><div class="row-sub">${esc(message||get('common.empty'))}</div></div>`;
  };

  const skeleton=(element,rows=4)=>{
    element.innerHTML=Array.from({length:rows},()=>'<div class="row skel"><div class="skel-bar"></div><div class="skel-bar short"></div></div>').join('');
  };

  const dialog=(html,options={})=>createDialog(html,{closeLabel:get('common.close'),...options});

  const categoryOptions=async selected=>{
    const categories=await api('api/categories');
    const byId=new Map(categories.map(category=>[category.id,category]));
    const path=category=>{
      const chain=[];
      let current=category;
      while(current){
        chain.unshift(current.name);
        current=current.parentId?byId.get(current.parentId):null;
      }
      return chain.join(' › ');
    };
    return categories.map(category=>
      `<option value="${category.id}"${category.id===selected?' selected':''}>${esc(path(category))}</option>`
    ).join('');
  };

  return { categoryOptions, date, dateTime, dialog, empty, esc, skeleton };
}
