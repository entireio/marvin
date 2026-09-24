import { useEffect,useRef,useState,type ReactNode } from 'react';
import { X } from 'lucide-react';
export function Dialog({title,children,onClose,wide=false,className=''}:{title:string;children:ReactNode;onClose:()=>void;wide?:boolean;className?:string}){
 const ref=useRef<HTMLDialogElement>(null);
 const [closing,setClosing]=useState(false),closeTimer=useRef<ReturnType<typeof setTimeout>|null>(null);
 useEffect(()=>{const el=ref.current!;const previous=document.activeElement as HTMLElement|null;el.showModal();return()=>{if(closeTimer.current)clearTimeout(closeTimer.current);el.close();previous?.focus();};},[]);
 function requestClose(){if(closing)return;if(matchMedia('(prefers-reduced-motion: reduce)').matches){onClose();return;}setClosing(true);closeTimer.current=setTimeout(onClose,180);}
 return <dialog ref={ref} className={`dialog ${wide?'wide':''} ${className} ${closing?'is-closing':''}`} aria-labelledby="dialog-title" onCancel={e=>{e.preventDefault();requestClose();}} onClick={e=>{if(e.target===ref.current)requestClose();}}><div className="dialog-header"><h2 id="dialog-title">{title}</h2><button className="icon-button" aria-label="Close dialog" disabled={closing} onClick={requestClose}><X size={19}/></button></div>{children}</dialog>;
}
