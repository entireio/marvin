import {createPrivateKey,createPublicKey,randomUUID,sign} from 'node:crypto';
import {z} from 'zod';
import {Id} from '../../contracts/src/index.js';

export const RecoveryChallenge=z.object({deviceId:Id,nonce:z.string().regex(/^[a-f0-9]{64}$/),operation:z.literal('recover'),issuedAt:z.number().int().positive()}).strict();
export const RecoveryAuthorization=z.object({challenge:RecoveryChallenge,ownerId:Id,epoch:z.number().int().positive(),operatorId:z.string().min(1).max(128),caseId:z.string().min(1).max(128)}).strict();
export const RECOVERY_ISSUER='marvin-fleet-recovery-v1';

export function recoveryPublicKey(privatePem:string){const key=createPrivateKey(privatePem);if(key.asymmetricKeyType!=='ec'||key.asymmetricKeyDetails?.namedCurve!=='prime256v1')throw new Error('Fleet recovery signing requires a dedicated P-256 key.');return createPublicKey(key).export({format:'pem',type:'spki'}).toString();}
export function signRecoveryTicket(privatePem:string,value:unknown,now=Date.now()){
 const input=RecoveryAuthorization.parse(value);if(Math.abs(now-input.challenge.issuedAt)>120000)throw new Error('Recovery challenge expired.');
 const key=createPrivateKey(privatePem);if(key.asymmetricKeyType!=='ec'||key.asymmetricKeyDetails?.namedCurve!=='prime256v1')throw new Error('Fleet recovery signing requires a dedicated P-256 key.');
 const issued=Math.floor(now/1000),header=Buffer.from(JSON.stringify({alg:'ES256',typ:'JWT'})).toString('base64url');
 const claims={iss:RECOVERY_ISSUER,aud:input.challenge.deviceId,sub:input.ownerId,jti:randomUUID(),iat:issued,exp:issued+120,op:'recover',nonce:input.challenge.nonce,epoch:input.epoch,operator:input.operatorId,case:input.caseId};
 const body=Buffer.from(JSON.stringify(claims)).toString('base64url'),message=header+'.'+body;
 return message+'.'+sign('sha256',Buffer.from(message),{key,dsaEncoding:'ieee-p1363'}).toString('base64url');
}
