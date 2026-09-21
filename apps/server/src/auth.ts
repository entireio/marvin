import { scrypt, timingSafeEqual, randomBytes } from 'node:crypto';
import { promisify } from 'node:util';
import * as oidc from 'openid-client';
import type { Config } from './config.js';
import { Store, hash } from '../../../packages/persistence/src/store.js';
import { DomainError } from '../../../packages/contracts/src/index.js';
const derive=promisify(scrypt);
export async function hashPassword(password:string){if(password.length<12)throw new Error('Use at least 12 characters.');const salt=randomBytes(16).toString('hex');const key=await derive(password,salt,64) as Buffer;return `scrypt$${salt}$${key.toString('hex')}`;}
export async function verifyPassword(password:string,stored:string){const [,salt,key]=stored.split('$');if(!salt||!key||password.length>1024)return false;const actual=await derive(password,salt,64) as Buffer;const expected=Buffer.from(key,'hex');return actual.length===expected.length&&timingSafeEqual(actual,expected);}
export function safeReturnTo(value:unknown){return typeof value==='string'&&/^\/app(?:\/[a-zA-Z0-9_-]+)?$/.test(value)?value:'/app';}
export class Identity {
 private client?:Promise<oidc.Configuration>;
 constructor(private cfg:Config,private store:Store){}
 private configuration(){return this.client??=oidc.discovery(new URL(this.cfg.OIDC_ISSUER!),this.cfg.OIDC_CLIENT_ID!,this.cfg.OIDC_CLIENT_SECRET);}
 async begin(returnTo:unknown){
  const client=await this.configuration(),state=oidc.randomState(),nonce=oidc.randomNonce(),verifier=oidc.randomPKCECodeVerifier(),token=randomBytes(32).toString('base64url');
  await this.store.db.query('DELETE FROM identity_flows WHERE expires_at<=?',[Date.now()]);
  await this.store.db.query('INSERT INTO identity_flows(hash,state,verifier,nonce,return_to,expires_at) VALUES (?,?,?,?,?,?)',[hash(token),state,verifier,nonce,safeReturnTo(returnTo),Date.now()+300000]);
  const url=oidc.buildAuthorizationUrl(client,{redirect_uri:this.cfg.OIDC_REDIRECT_URI!,scope:'openid profile',state,nonce,code_challenge:await oidc.calculatePKCECodeChallenge(verifier),code_challenge_method:'S256'});
  return {token,url:url.href};
 }
 async callback(token:string|undefined,url:URL){
  if(!token)throw new DomainError('LOGIN_EXPIRED','Sign-in expired. Please try again.',401);
  const rows=await this.store.db.query<Record<string,any>>('DELETE FROM identity_flows WHERE hash=? AND expires_at>? RETURNING *',[hash(token),Date.now()]);const flow=rows[0];
  if(!flow)throw new DomainError('LOGIN_EXPIRED','Sign-in expired. Please try again.',401);
  const client=await this.configuration();
  const tokens=await oidc.authorizationCodeGrant(client,url,{pkceCodeVerifier:flow.verifier,expectedState:flow.state,expectedNonce:flow.nonce,idTokenExpected:true});
  const claims=tokens.claims();if(!claims?.sub)throw new DomainError('IDENTITY_MISSING','Your provider did not return a verified identity.',401);
  const o=await this.store.ensureOwner(this.cfg.OIDC_ISSUER!,claims.sub,typeof claims.name==='string'?claims.name:'Marvin user');
  // Identity tokens are consumed here, not stored as repository authorization.
  return {owner:o,returnTo:flow.return_to as string};
 }
}
