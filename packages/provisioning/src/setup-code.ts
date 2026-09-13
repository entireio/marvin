import {z} from 'zod';
import {DomainError} from '../../contracts/src/index.js';
const Card=z.object({deviceId:z.string().regex(/^marvin_[a-f0-9]{32}$/),username:z.string().min(1).max(64),password:z.string().min(16).max(96)}).strict();
export function decodeSetupCode(value:string){try{const text=value.trim();if(!/^marvin1\.[A-Za-z0-9_-]{1,512}$/.test(text))throw new Error();const raw=text.slice(8).replace(/-/g,'+').replace(/_/g,'/');return Card.parse(JSON.parse(new TextDecoder('utf-8',{fatal:true}).decode(Uint8Array.from(atob(raw),c=>c.charCodeAt(0)))));}catch{throw new DomainError('SETUP_CODE_INVALID','Use the complete setup code from your Marvin’s setup card.');}}
