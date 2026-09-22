import { scrypt, timingSafeEqual, randomBytes } from 'node:crypto';
import { promisify } from 'node:util';
import * as oidc from 'openid-client';
import { z } from 'zod';
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

type GithubUser={id:number;login:string;name:string|null};
export class GithubIdentity {
 constructor(private cfg:Config,private store:Store,private request:typeof fetch=fetch){}
 private redirectUri(){return this.cfg.APP_ORIGIN+'/auth/callback';}
 async begin(returnTo:unknown){
  const state=oidc.randomState(),token=randomBytes(32).toString('base64url');
  await this.store.db.query('DELETE FROM identity_flows WHERE expires_at<=?',[Date.now()]);
  await this.store.db.query('INSERT INTO identity_flows(hash,state,verifier,nonce,return_to,expires_at) VALUES (?,?,?,?,?,?)',[hash(token),state,'github','github',safeReturnTo(returnTo),Date.now()+300000]);
  const url=new URL('https://github.com/login/oauth/authorize');
  url.searchParams.set('client_id',this.cfg.GITHUB_CLIENT_ID!);url.searchParams.set('redirect_uri',this.redirectUri());url.searchParams.set('state',state);
  return {token,url:url.href};
 }
 async callback(token:string|undefined,url:URL){
  if(!token)throw new DomainError('LOGIN_EXPIRED','Sign-in expired. Please try again.',401);
  const state=url.searchParams.get('state'),code=url.searchParams.get('code');
  if(!state||!code)throw new DomainError('LOGIN_FAILED','GitHub sign-in was not completed.',401);
  const rows=await this.store.db.query<Record<string,any>>('DELETE FROM identity_flows WHERE hash=? AND state=? AND expires_at>? RETURNING *',[hash(token),state,Date.now()]);const flow=rows[0];
  if(!flow)throw new DomainError('LOGIN_EXPIRED','Sign-in expired. Please try again.',401);
  const exchanged=await this.request('https://github.com/login/oauth/access_token',{method:'POST',headers:{Accept:'application/json','Content-Type':'application/json','User-Agent':'Marvin'},body:JSON.stringify({client_id:this.cfg.GITHUB_CLIENT_ID,client_secret:this.cfg.GITHUB_CLIENT_SECRET,code,redirect_uri:this.redirectUri()})});
  if(!exchanged.ok)throw new DomainError('LOGIN_FAILED','GitHub sign-in could not be verified.',401);
  const access=z.object({access_token:z.string().min(1)}).safeParse(await exchanged.json());
  if(!access.success)throw new DomainError('LOGIN_FAILED','GitHub sign-in could not be verified.',401);
  const profileResponse=await this.request('https://api.github.com/user',{headers:{Accept:'application/vnd.github+json',Authorization:`Bearer ${access.data.access_token}`,'User-Agent':'Marvin','X-GitHub-Api-Version':'2022-11-28'}});
  if(!profileResponse.ok)throw new DomainError('LOGIN_FAILED','GitHub sign-in could not be verified.',401);
  const profile=z.object({id:z.number().int().positive(),login:z.string().min(1),name:z.string().nullable()}).parse(await profileResponse.json()) as GithubUser;
  const allowed=new Set(this.cfg.GITHUB_ALLOWED_USERS.split(',').map(x=>x.trim().toLowerCase()).filter(Boolean));
  if(!allowed.has(profile.login.toLowerCase()))throw new DomainError('IDENTITY_FORBIDDEN','This GitHub account does not have access to Marvin.',403);
  const owner=await this.store.ensureOwner('https://github.com',String(profile.id),profile.name?.trim()||profile.login);
  return {owner,returnTo:flow.return_to as string};
 }
}
