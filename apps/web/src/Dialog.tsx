import { useEffect,useRef,type ReactNode } from 'react';
import { X } from 'lucide-react';
export function Dialog({title,children,onClose,wide=false}:{title:string;children:ReactNode;onClose:()=>void;wide?:boolean}){
 const ref=useRef<HTMLDialogElement>(null);
 useEffect(()=>{const el=ref.current!;const previous=document.activeElement as HTMLElement|null;el.showModal();return()=>{el.close();previous?.focus();};},[]);
 return <dialog ref={ref} className={`dialog ${wide?'wide':''}`} aria-labelledby="dialog-title" onCancel={e=>{e.preventDefault();onClose();}} onClick={e=>{if(e.target===ref.current)onClose();}}><div className="dialog-header"><h2 id="dialog-title">{title}</h2><button className="icon-button" aria-label="Close dialog" onClick={onClose}><X size={19}/></button></div>{children}</dialog>;
}
