import { useEffect, useId, useRef, useState } from 'react';
import { Check, ChevronsUpDown, Search } from 'lucide-react';

export function RepositorySelector({repositories,value,disabled,onChange}:{repositories:{id:string;name:string}[];value:string;disabled:boolean;onChange:(id:string)=>void}){
 const [open,setOpen]=useState(false);
 const [query,setQuery]=useState('');
 const [active,setActive]=useState(0);
 const root=useRef<HTMLDivElement>(null);
 const trigger=useRef<HTMLButtonElement>(null);
 const input=useRef<HTMLInputElement>(null);
 const id=useId();
 const options=[...repositories,{id:'',name:'No repository selected'}].filter(repo=>repo.name.toLowerCase().includes(query.trim().toLowerCase()));
 const index=Math.min(active,options.length-1);
 const selected=repositories.find(repo=>repo.id===value);
 function close(){setOpen(false);trigger.current?.focus();}
 function choose(repositoryId:string){close();if(repositoryId!==value)onChange(repositoryId);}
 function show(){setQuery('');setActive(Math.max(0,repositories.findIndex(repo=>repo.id===value)));setOpen(true);}
 useEffect(()=>{if(open)input.current?.focus();},[open]);
 useEffect(()=>{if(disabled)setOpen(false);},[disabled]);
 useEffect(()=>{
  if(!open)return;
  function outside(event:PointerEvent){if(!root.current?.contains(event.target as Node))setOpen(false);}
  document.addEventListener('pointerdown',outside);
  return()=>document.removeEventListener('pointerdown',outside);
 },[open]);
 useEffect(()=>{if(open&&index>=0)document.getElementById(`${id}-option-${index}`)?.scrollIntoView({block:'nearest'});},[open,index,id]);
 return <div className="repository-picker" ref={root} onBlur={event=>{if(!event.currentTarget.contains(event.relatedTarget))setOpen(false);}} onKeyDown={event=>{if(open&&event.key==='Escape'){event.preventDefault();event.stopPropagation();close();}}}>
  <button ref={trigger} type="button" className="repository-picker-trigger" data-selected={!!selected} aria-label="Active repository" aria-haspopup="listbox" aria-expanded={open} aria-controls={open?`${id}-list`:undefined} disabled={disabled} onClick={()=>open?close():show()} onKeyDown={event=>{if(event.key==='ArrowDown'||event.key==='ArrowUp'){event.preventDefault();show();}}} title={selected?.name??'Select repository'}>
   <span className="repository-picker-name">{selected?.name??'Select repository'}</span><ChevronsUpDown size={16} strokeWidth={1.5} aria-hidden="true"/>
  </button>
  {open&&<div className="repository-picker-popup">
   <div className="repository-picker-search"><Search size={16} aria-hidden="true"/><input ref={input} role="combobox" aria-label="Find repositories" aria-autocomplete="list" aria-expanded="true" aria-controls={`${id}-list`} aria-activedescendant={index>=0?`${id}-option-${index}`:undefined} placeholder="Find repositories" value={query} onChange={event=>{setQuery(event.target.value);setActive(0);}} onKeyDown={event=>{
    if(event.key==='ArrowDown'||event.key==='ArrowUp'){event.preventDefault();setActive(options.length?(index+(event.key==='ArrowDown'?1:-1)+options.length)%options.length:0);}
    if(event.key==='Enter'){event.preventDefault();if(options[index])choose(options[index].id);}
   }}/></div>
   <div id={`${id}-list`} role="listbox" aria-label="Repositories" className="repository-picker-list">{options.map((repo,i)=><div key={repo.id} id={`${id}-option-${i}`} role="option" aria-selected={repo.id===value} className={`repository-picker-option ${i===index?'is-active':''}`} onPointerMove={()=>setActive(i)} onMouseDown={event=>event.preventDefault()} onClick={()=>choose(repo.id)}><span className="repository-picker-check">{repo.id===value&&<Check size={16} aria-hidden="true"/>}</span><span title={repo.name}>{repo.name}</span></div>)}</div>
   {!options.length&&<div role="status" className="repository-picker-empty">No repositories found</div>}
  </div>}
 </div>;
}
