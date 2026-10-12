import {useEffect,useRef,useState} from 'react';
import {api} from './api';
import {Dialog} from './Dialog';

export function SimulatorPairing({onClose,onComplete}:{onClose:()=>void;onComplete:()=>void}){
 const completion=useRef(onComplete);completion.current=onComplete;
 const [pairing,setPairing]=useState<{code:string;serverUrl:string;expiresAt:number}|null>(null),[error,setError]=useState(''),[busy,setBusy]=useState(false),[now,setNow]=useState(Date.now());
 useEffect(()=>{if(!pairing)return;let alive=true;
  const timer=setInterval(()=>{setNow(Date.now());void api<{body:null|{kind?:string}}>('/robot/presence').then(value=>{if(alive&&value.body?.kind==='simulator')completion.current();}).catch(failure=>{if(alive)setError(failure instanceof Error?failure.message:'Could not check pairing.');});},2000);
  return()=>{alive=false;clearInterval(timer);};
 },[pairing]);
 async function create(){setBusy(true);setError('');try{setPairing(await api('/robot/simulator/pairing','POST',{}));setNow(Date.now());}catch(failure){setError(failure instanceof Error?failure.message:'Could not create pairing code.');}finally{setBusy(false);}}
 function close(){void api('/robot/simulator/pairing','DELETE').catch(()=>{});onClose();}
 const seconds=pairing?Math.max(0,Math.ceil((pairing.expiresAt-now)/1000)):0;
 return <Dialog title="Pair simulator" className="pet-setup-dialog simulator-pairing-dialog" onClose={close}><div className="dialog-body">
  <p>Open the Swift Marvin Simulator, choose <strong>Simulation → Connect to Marvin…</strong>, and enter the server address and one-time code below.</p>
  {pairing&&<div className="simulator-pairing-fields"><label className="field">Marvin server<input readOnly value={pairing.serverUrl} onFocus={event=>event.target.select()}/></label><label className="field">Simulator pairing code<input readOnly value={pairing.code} autoComplete="off" spellCheck={false} onFocus={event=>event.target.select()}/></label><p role="status">{seconds?`Waiting for your simulator. Code expires in ${Math.ceil(seconds/60)} minutes.`:'This code expired. Create a new one.'}</p></div>}
  {(!pairing||!seconds)&&<button className="button primary full" disabled={busy} onClick={()=>void create()}>{busy?'Creating code…':pairing?'Create a new code':'Create pairing code'}</button>}
  <p className="fine-print">Drive Marvin and adjust his head and eyes with the existing Desktop Pet controls. No Bluetooth or Wi-Fi setup is required.</p>
  {error&&<p className="error-message" role="alert">{error}</p>}
 </div></Dialog>;
}
