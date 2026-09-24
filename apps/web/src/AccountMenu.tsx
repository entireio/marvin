import { useEffect, useRef, useState } from 'react';
import { ArrowUpRight, ChevronRight, Check } from 'lucide-react';

import { getAppearance, saveAppearance } from './appearance';

type Props = { name: string; development: boolean; docsUrl: string; onSettings: () => void; onLogout: () => void };

export function AccountMenu({ name, development, docsUrl, onSettings, onLogout }: Props) {
  const [open, setOpen] = useState(false);
  const [appearanceOpen, setAppearanceOpen] = useState(false);
  const appearanceTrigger = useRef<HTMLButtonElement>(null);
  const appearanceMenu = useRef<HTMLDivElement>(null);
  const [appearance, setAppearance] = useState(getAppearance);
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const initialItem = useRef(0);
  function close(restore = true) { setOpen(false); setAppearanceOpen(false); if (restore) trigger.current?.focus(); }
  useEffect(() => {
    if (!open) { setAppearanceOpen(false); return; }
    const items = menu.current?.querySelectorAll<HTMLElement>('[role="menuitem"], [role="menuitemradio"]');
    setAppearance(getAppearance());
    if (initialItem.current !== 1) items?.[initialItem.current < 0 ? items.length - 1 : 0]?.focus();
    const outside = (event: PointerEvent) => { if (!root.current?.contains(event.target as Node)) setOpen(false); };
    document.addEventListener('pointerdown', outside);
    return () => document.removeEventListener('pointerdown', outside);
  }, [open]);
  return <div className="account-menu" ref={root} onBlur={event => {
    if (!event.currentTarget.contains(event.relatedTarget as Node)) setOpen(false);
  }}>
    <button className="account-menu-trigger" ref={trigger} aria-label="Account menu" title={name}
      aria-haspopup="menu" aria-expanded={open} aria-controls={open ? 'account-menu-items' : undefined}
      onClick={() => { initialItem.current = 1; setOpen(!open); }}
      onKeyDown={event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault(); initialItem.current = event.key === 'ArrowUp' ? -1 : 0; if(open){const items=menu.current?.querySelectorAll<HTMLElement>('[role="menuitem"], [role="menuitemradio"]');items?.[initialItem.current<0?items.length-1:0]?.focus();}else setOpen(true);
        }
      }}><span className="account-avatar" aria-hidden="true">{name.charAt(0).toUpperCase()}</span></button>
    {open && <div className="account-menu-content" id="account-menu-items" role="menu" aria-label="Account" ref={menu}
      onKeyDown={event => {
        const items = [...event.currentTarget.querySelectorAll<HTMLElement>('[role="menuitem"]')];
        const current = items.indexOf(document.activeElement as HTMLElement);
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
          event.preventDefault();
          const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 : (current + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
          items[next]?.focus();
        } else if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); }
        else if (event.key === 'Tab') close();
      }}>
      <div className="account-menu-heading" role="presentation"><strong>{name}</strong><span>{development ? 'Local preview' : 'Personal workspace'}</span></div>
      <button role="menuitem" tabIndex={-1} aria-label="Open settings" onClick={() => { close(); onSettings(); }}>Settings</button>
      <a role="menuitem" tabIndex={-1} href={docsUrl} target="_blank" rel="noreferrer" onClick={() => close()}>Documentation<ArrowUpRight className="account-external" size={14}/></a>
      <div className="account-menu-separator" role="separator"/>
      <div className="account-appearance" onMouseEnter={()=>setAppearanceOpen(true)} onMouseLeave={()=>{if(!appearanceMenu.current?.contains(document.activeElement))setAppearanceOpen(false);}} onBlur={event=>{if(!event.currentTarget.contains(event.relatedTarget))setAppearanceOpen(false);}}>
        <button ref={appearanceTrigger} role="menuitem" tabIndex={-1} aria-haspopup="menu" aria-expanded={appearanceOpen} aria-controls={appearanceOpen?'appearance-menu':undefined} onClick={()=>setAppearanceOpen(true)} onKeyDown={event=>{if(event.key==='ArrowRight'||event.key==='Enter'||event.key===' '){event.preventDefault();event.stopPropagation();setAppearanceOpen(true);requestAnimationFrame(()=>appearanceMenu.current?.querySelector<HTMLElement>('[aria-checked="true"]')?.focus());}}}>Appearance<ChevronRight className="account-external" size={16}/></button>
        {appearanceOpen&&<div ref={appearanceMenu} id="appearance-menu" className="account-appearance-submenu" role="menu" aria-label="Appearance" onKeyDown={event=>{
          event.stopPropagation();
          const items=[...event.currentTarget.querySelectorAll<HTMLElement>('[role="menuitemradio"]')];
          const index=items.indexOf(document.activeElement as HTMLElement);
          if(['ArrowDown','ArrowUp','Home','End'].includes(event.key)){event.preventDefault();items[event.key==='Home'?0:event.key==='End'?items.length-1:(index+(event.key==='ArrowDown'?1:-1)+items.length)%items.length]?.focus();}
          else if(event.key==='Escape'||event.key==='ArrowLeft'){event.preventDefault();setAppearanceOpen(false);appearanceTrigger.current?.focus();}
          else if(event.key==='Tab')close();
        }}>{([{value:'light',label:'Light'},{value:'dark',label:'Dark'},{value:'system',label:'System'}] as const).map(({value,label})=><button key={value} role="menuitemradio" aria-checked={appearance===value} tabIndex={-1} onClick={()=>{saveAppearance(value);setAppearance(value);setAppearanceOpen(false);appearanceTrigger.current?.focus();}}>{label}{appearance===value&&<Check size={16} aria-hidden="true"/>}</button>)}</div>}
      </div>
      <div className="account-menu-separator" role="separator"/>
      <button role="menuitem" tabIndex={-1} onClick={() => { close(); onLogout(); }}>Logout</button>
    </div>}
  </div>;
}
