import { useCallback,useEffect,useRef,useState } from 'react';
import { PhoneOff } from 'lucide-react';
import { Face } from './Face';
import { BrowserVoice,type VoiceView } from './voice-client';

const labels={connecting:'Starting live voice…',listening:"Let’s talk",hearing:'I hear you',thinking:'Thinking with you',speaking:'Marvin is speaking',muted:'Microphone muted',closed:'Voice conversation ended',error:'Voice needs attention'};
const guidance={connecting:'Allow microphone access, then just start talking.',listening:'I’m listening — just start speaking.',hearing:'Keep going. I’m listening.',thinking:'One moment while I think about that.',speaking:'You can interrupt Marvin at any time.',muted:'Unmute whenever you’re ready.',closed:'Your transcript is saved in this conversation.',error:'Your transcript is safe. End voice, then reconnect or continue typing.'};

export type VoiceSession={active:boolean;available:boolean;view:VoiceView;muted:boolean;start:()=>void;end:()=>void;retry:()=>void;toggleMute:()=>void;interrupt:()=>void};

export function useVoiceSession({conversationId,available,onRefresh}:{conversationId:string|undefined;available:boolean;onRefresh:()=>void}):VoiceSession{
 const [active,setActive]=useState(false),[muted,setMuted]=useState(false),[view,setView]=useState<VoiceView>({state:'closed'});
 const client=useRef<BrowserVoice|null>(null),refresh=useRef(onRefresh);refresh.current=onRefresh;
 const stopClient=useCallback(()=>{client.current?.stop();client.current=null;},[]);
 const start=useCallback(()=>{
  if(!conversationId||!available)return;
  stopClient();setActive(true);setMuted(false);setView({state:'connecting',userText:'',assistantText:''});
  const voice=new BrowserVoice(conversationId,next=>setView(previous=>({...previous,...next})),()=>refresh.current());client.current=voice;void voice.start();
 },[available,conversationId,stopClient]);
 const end=useCallback(()=>{stopClient();setActive(false);setMuted(false);setView({state:'closed'});refresh.current();},[stopClient]);
 useEffect(()=>{setActive(false);setMuted(false);setView({state:'closed'});return()=>stopClient();},[conversationId,stopClient]);
 return {active,available,view,muted,start,end,retry:start,toggleMute:()=>{const next=!muted;client.current?.mute(next);setMuted(next);},interrupt:()=>{client.current?.interrupt();setView(previous=>({...previous,state:muted?'muted':'listening'}));}};
}

function VoicePulse({state}:{state:VoiceView['state']}){return <span className={`voice-pulse voice-pulse-${state}`} aria-hidden="true"><i/><i/><i/><i/><i/></span>;}

export function VoiceConversation({session,persisted=false}:{session:VoiceSession;persisted?:boolean}){
 const {view}=session;
 return <section className={`voice-conversation voice-${view.state}`} aria-label="Live voice conversation">
  {!persisted&&(view.userText||view.assistantText)&&<div className="voice-live-transcript" aria-label="Live transcript">{view.userText&&<div className="user-message voice-user-message"><div className="message-label">YOU · LIVE TRANSCRIPT</div><p>{view.userText}</p></div>}{view.assistantText&&<div className="assistant-message"><div className="message-label"><Face/>MARVIN</div><p>{view.assistantText}</p></div>}</div>}
 </section>;
}

export function VoiceComposer({session}:{session:VoiceSession}){
 return <div className={`voice-composer voice-${session.view.state}`}><div className="voice-composer-status" role="status" aria-label="Live voice status" aria-live="polite"><div className="voice-presence-mark"><Face thinking={session.view.state==='connecting'||session.view.state==='thinking'}/><VoicePulse state={session.view.state}/></div><div><strong>{labels[session.view.state]}</strong><span>{session.view.message??guidance[session.view.state]}</span></div></div><span className="live-badge"><i/> LIVE</span><div className="voice-composer-actions">
  <button className="voice-end" type="button" aria-label="End voice conversation" onClick={session.end}><PhoneOff size={18}/><span>End</span></button>
 </div></div>;
}
