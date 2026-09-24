import { useState } from 'react';
import { RemoteControl } from './App';

export function RemotePreview(){
 const [speech,setSpeech]=useState('');
 const [notice,setNotice]=useState('');
 return <RemoteControl simulated connection="online" capabilities={['remote','voice','head','eyes']} batteryStatus={{levelPercent:74,voltageMv:3950,charging:false}} voiceAvailable voiceStatus="ready" speech={speech} speaking={false} speechNotice={notice} onSpeechChange={value=>{setSpeech(value);setNotice('');}} onSpeak={()=>{if(speech.trim()){setNotice(`Simulated speech: “${speech.trim()}”`);setSpeech('');}}} onError={error=>setNotice(String(error))} onClose={()=>location.assign('/app')}/>;
}
