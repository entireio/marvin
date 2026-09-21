import Fastify from 'fastify';
import {FirmwareRelease} from '../../../packages/operations/src/firmware-release.js';
import {Readable} from 'node:stream';
import { randomUUID } from 'node:crypto';
import {AnthropicTextProvider} from '../../../packages/runtime/src/anthropic.js';
import {exportPage,deleteAccount,purgeExpiredHistory} from '../../../packages/persistence/src/privacy.js';
import { DeviceVoice } from '../../../packages/device/src/voice.js';
import { DeviceStore } from '../../../packages/device/src/store.js';
import { DeviceGateway } from '../../../packages/device/src/gateway.js';
import { registerVoice } from './voice.js';
import {DeepgramVoiceProvider} from '../../../packages/runtime/src/deepgram-voice.js';
import { OpenAIVoiceProvider } from '../../../packages/runtime/src/openai-voice.js';
import { RoboticVoiceProvider } from '../../../packages/runtime/src/robot-voice.js';
import type { VoiceProvider } from '../../../packages/runtime/src/voice.js';
import cookie from '@fastify/cookie';
import websocket from '@fastify/websocket';
import rateLimit from '@fastify/rate-limit';
import staticFiles from '@fastify/static';
import { EnrollmentService } from '../../../packages/enrollment/src/service.js';
import { existsSync,readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { z, ZodError } from 'zod';
import { Store } from '../../../packages/persistence/src/store.js';
import { sqliteDatabase,postgresDatabase,type Database } from '../../../packages/persistence/src/database.js';
import { migrate } from '../../../packages/persistence/src/migrations.js';
import { Runtime } from '../../../packages/runtime/src/runtime.js';
import { EntireCli } from '../../../packages/runtime/src/entire-cli.js';
import { EntireHttp } from '../../../packages/runtime/src/entire-http.js';
import type { RepositoryIntegration } from '../../../packages/runtime/src/repository-integration.js';
import { authorizeTool } from '../../../packages/runtime/src/policy.js';
import { FixtureEntire,fixtureRepositories } from '../../../packages/runtime/src/entire.js';
import { FixtureTextProvider,OpenAITextProvider,type TextProvider } from '../../../packages/runtime/src/provider.js';
import { SendTurn, ClientEvent, RemoteIntent, DomainError, Id, RepositorySelection, type AgentEvent } from '../../../packages/contracts/src/index.js';
import { HeadCalibration,PetAudioSettings,PetEyeSettings,PetSpeech } from '../../../packages/contracts/src/device.js';
import { Identity,verifyPassword,safeReturnTo } from './auth.js';
import type { Config } from './config.js';
export async function createApp(cfg:Config,options:{database?:Database;provider?:TextProvider;logger?:boolean;entire?:RepositoryIntegration;voice?:VoiceProvider;voiceLimits?:{idleMs:number;maxMs:number;heartbeatMs:number}}={}){
 let voiceDiagnostic:string|undefined;
 const firmwareRelease=cfg.FIRMWARE_ROLLOUT_FILE?new FirmwareRelease(cfg.FIRMWARE_ROLLOUT_FILE):undefined;
 const app=Fastify({trustProxy:cfg.TRUST_PROXY_HOPS===1?(_address:string,hop:number)=>hop<1:false,bodyLimit:32768,logger:options.logger?{level:'info',redact:['req.headers.cookie','req.headers.authorization','req.headers["x-csrf-token"]'],serializers:{req:r=>({method:r.method,url:r.url?.split('?')[0],id:r.id}),res:r=>({statusCode:r.statusCode})}}:false,disableRequestLogging:true});
 const db=options.database??(cfg.DATABASE_URL?postgresDatabase(cfg.DATABASE_URL):sqliteDatabase(cfg.SQLITE_PATH));await migrate(db);const store=new Store(db);await store.recoverExpired();
 const realEntire=options.entire??(cfg.ENTIRE_CLI_PATH&&cfg.ENTIRE_BINDINGS_FILE?new EntireCli(cfg.ENTIRE_CLI_PATH,cfg.ENTIRE_BINDINGS_FILE):cfg.ENTIRE_COMPANION_URL&&cfg.ENTIRE_COMPANION_TOKEN?new EntireHttp(cfg.ENTIRE_COMPANION_URL,cfg.ENTIRE_COMPANION_TOKEN):undefined);
 const runtime=new Runtime(store,options.provider??(cfg.MODEL_PROVIDER==='anthropic'?new AnthropicTextProvider(cfg.ANTHROPIC_API_KEY!,cfg.ANTHROPIC_MODEL!):cfg.MODEL_PROVIDER==='openai'?new OpenAITextProvider(cfg.OPENAI_API_KEY!,cfg.OPENAI_MODEL!,undefined,cfg.OPENAI_REPOSITORY_MODEL):new FixtureTextProvider()),new FixtureEntire(),realEntire);
 const devices=new DeviceGateway(new DeviceStore(store));
 const enrollment=cfg.ENROLLMENT_KEYS_FILE?(()=>{const keys=z.object({privateKey:z.string(),receiptKey:z.string().regex(/^[a-f0-9]{64}$/)}).strict().parse(JSON.parse(readFileSync(cfg.ENROLLMENT_KEYS_FILE,'utf8')));return new EnrollmentService(store,keys.privateKey,Buffer.from(keys.receiptKey,'hex'),cfg.DEVICE_PUBLIC_ORIGIN!);})():undefined;
 const localDevCard=cfg.LOCAL_DEV_SETUP_CARDS_FILE?(()=>{
  const card=z.object({deviceId:z.string().regex(/^marvin_[a-f0-9]{32}$/),username:z.string().min(1).max(64),password:z.string().min(8).max(128),security:z.literal(2).optional(),patch:z.literal(1).optional()}).strict(),loaded=z.union([card,z.array(card).length(1)]).parse(JSON.parse(readFileSync(cfg.LOCAL_DEV_SETUP_CARDS_FILE,'utf8')));
  return Array.isArray(loaded)?loaded[0]!:loaded;
 })():undefined;
 const localDevPets=cfg.LOCAL_DEV_PETS_FILE?(()=>{
  const card=z.object({deviceId:z.string().regex(/^marvin_[a-f0-9]{32}$/),username:z.string().min(1).max(64),password:z.string().min(8).max(128),security:z.literal(2).optional(),patch:z.literal(1).optional()}).strict(),entry=z.object({label:z.string().min(1).max(64),cardFile:z.string().startsWith('/'),devicePublicKeyFile:z.string().startsWith('/')}).strict(),entries=z.array(entry).min(1).max(8).parse(JSON.parse(readFileSync(cfg.LOCAL_DEV_PETS_FILE,'utf8'))),pets=entries.map(item=>({...item,card:card.parse(JSON.parse(readFileSync(item.cardFile,'utf8')))}));
  if(new Set(pets.map(pet=>pet.card.deviceId)).size!==pets.length)throw new Error('LOCAL_DEV_PETS_FILE contains duplicate device identities.');
  return pets;
 })():[];
 if(cfg.LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE&&localDevCard){
  const registered=await enrollment!.registerDevice(readFileSync(cfg.LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE,'utf8'));
  if(registered!==localDevCard.deviceId)throw new Error('LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE does not match LOCAL_DEV_SETUP_CARDS_FILE.');
 }
 for(const pet of localDevPets){const registered=await enrollment!.registerDevice(readFileSync(pet.devicePublicKeyFile,'utf8'));if(registered!==pet.card.deviceId)throw new Error('A LOCAL_DEV_PETS_FILE public key does not match its setup card.');}
 runtime.devices=devices;
 const identity=new Identity(cfg,store), secure=cfg.NODE_ENV==='production'||cfg.APP_ORIGIN.startsWith('https:');
 const cookieOptions={path:'/',httpOnly:true,sameSite:'lax' as const,secure,maxAge:8*60*60};
 await app.register(cookie);await app.register(rateLimit,{max:200,timeWindow:'1 minute',allowList:req=>{const path=req.url.split('?')[0]!;return !path.startsWith('/api/')||path==='/api/remote';}});await app.register(websocket,{options:{maxPayload:16384}});
 app.setErrorHandler((error,req,reply)=>{
  const e=error as Error & {statusCode?:number};
  const status=e instanceof DomainError?e.status:e instanceof ZodError?400:e.statusCode&&e.statusCode<500?e.statusCode:500;
  if(status>=500)app.log.error({requestId:req.id,errorType:e.name},'Request failed');
  reply.code(status).send({error:{code:e instanceof DomainError?e.code:status===400?'INVALID_REQUEST':status===429?'RATE_LIMITED':'REQUEST_FAILED',message:e instanceof DomainError?e.message:status===400?'Check the request and try again.':status===429?'Too many attempts. Please wait a moment.':'The request could not be completed.'}});
 });
 app.addHook('onRequest',async(req,reply)=>{
  reply.header('X-Content-Type-Options','nosniff').header('Referrer-Policy','same-origin').header('X-Frame-Options','DENY');
  if(req.url.startsWith('/api/'))reply.header('Cache-Control','no-store');
  if(cfg.NODE_ENV==='production')reply.header('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'").header('Strict-Transport-Security','max-age=31536000');
  if(!req.url.startsWith('/api/'))return;
  if(req.url==='/api/device/enrollment/redeem'){if(req.headers.origin||req.headers.cookie)throw new DomainError('DEVICE_ONLY','Setup completion must come directly from your Desktop Pet.',403);return;}
  if(req.method==='GET'&&/^\/api\/device\/firmware(?:\/|$)/.test(req.url.split('?')[0]!)){if(req.headers.origin||req.headers.cookie)throw new DomainError('DEVICE_ONLY','Firmware downloads require direct device authorization.',403);return;}
  const mutating=!['GET','HEAD','OPTIONS'].includes(req.method);
  if(mutating&&req.headers.origin!==cfg.APP_ORIGIN)throw new DomainError('ORIGIN_FORBIDDEN','This request did not originate from the Marvin web app.',403);
  if(req.url.startsWith('/api/auth/')||req.url.split('?')[0]==='/api/config'||req.url==='/api/health'||req.url==='/api/device/socket')return;
  const session=await store.session(req.cookies.marvin_session);if(!session)throw new DomainError('UNAUTHENTICATED','Please sign in to continue.',401);
  if(mutating&&req.headers['x-csrf-token']!==session.csrf)throw new DomainError('CSRF_INVALID','Refresh the page and try again.',403);
 });
 const session=async(req:{cookies:Record<string,string|undefined>})=>{const s=await store.session(req.cookies.marvin_session);if(!s)throw new DomainError('UNAUTHENTICATED','Please sign in to continue.',401);return s;};
 app.get('/api/health',async()=>({status:'ok',schema:5}));
 const firmwareDevice=async(authorization:string|undefined)=>{const token=/^Bearer ([A-Za-z0-9_-]{20,200})$/.exec(authorization??'')?.[1];if(!token)throw new DomainError('DEVICE_UNAUTHENTICATED','Device authorization required.',401);return devices.persistence.authenticate(token);};
 app.get('/api/device/firmware',{config:{rateLimit:{max:10,timeWindow:'1 minute'}}},async(req,reply)=>{const device=await firmwareDevice(req.headers.authorization);if(!firmwareRelease?.offeredTo(device.deviceId))return reply.code(204).send();return firmwareRelease.offer();});
 app.get('/api/device/firmware/:sequence/:part',{config:{rateLimit:{max:10,timeWindow:'1 minute'}}},async(req,reply)=>{const device=await firmwareDevice(req.headers.authorization),params=z.object({sequence:z.string().regex(/^[1-9][0-9]{0,9}$/),part:z.enum(['manifest','image'])}).parse(req.params);if(!firmwareRelease?.offeredTo(device.deviceId)||String(firmwareRelease.sequence)!==params.sequence)throw new DomainError('NOT_FOUND','Firmware release not available.',404);return reply.type('application/octet-stream').send(firmwareRelease.bytes(params.part));});

 app.get('/api/config',async()=>({authMode:cfg.AUTH_MODE,oidcLabel:cfg.OIDC_LABEL,provider:runtime.provider.name,development:cfg.AUTH_MODE==='development',deploymentMode:cfg.DEPLOYMENT_MODE,localDevSetupAvailable:!!localDevCard||localDevPets.length>0,docsUrl:cfg.PUBLIC_DOCS_URL,voiceAvailable:!!options.voice||cfg.VOICE_PROVIDER!=='disabled',voiceStatus:voiceDiagnostic==='credit_balance_exhausted'?'billing_required':voiceDiagnostic?'unavailable':'ready',hardwareProvisioningAvailable:cfg.HARDWARE_PROVISIONING_ENABLED==='true'&&!!enrollment}));
 app.get('/api/auth/session',async(req)=>{const s=await store.session(req.cookies.marvin_session);return s?{owner:await store.owner(s.ownerId),csrf:s.csrf}:null;});
 app.post('/api/auth/login',{config:{rateLimit:{max:10,timeWindow:'1 minute'}}},async(req,reply)=>{
  const body=z.object({password:z.string().max(1024).optional(),returnTo:z.string().max(150).optional()}).strict().parse(req.body);
  if(cfg.AUTH_MODE==='oidc')throw new DomainError('USE_IDENTITY_PROVIDER','Use your identity provider to sign in.',400);
  if(cfg.AUTH_MODE==='development'&&!['127.0.0.1','::1','::ffff:127.0.0.1'].includes(req.ip))throw new DomainError('LOCAL_ONLY','Development sign-in is local only.',403);
  if(cfg.AUTH_MODE==='local'&&!await verifyPassword(body.password??'',cfg.LOCAL_PASSWORD_HASH!))throw new DomainError('INVALID_LOGIN','The passphrase is incorrect.',401);
  const o=await store.ensureOwner(cfg.AUTH_MODE==='development'?'development':'local','owner',cfg.AUTH_MODE==='development'?'Local developer':'Marvin owner');
  if(req.cookies.marvin_session)await store.logout(req.cookies.marvin_session);
  const s=await store.createSession(o.id);reply.setCookie('marvin_session',s.token,cookieOptions);return {owner:o,csrf:s.csrf,returnTo:safeReturnTo(body.returnTo)};
 });
 app.post('/api/auth/logout',async(req,reply)=>{const s=await session(req);if(req.headers['x-csrf-token']!==s.csrf)throw new DomainError('CSRF_INVALID','Refresh and retry.',403);await store.logout(req.cookies.marvin_session!);reply.clearCookie('marvin_session',{path:'/'});return {ok:true};});
 app.get('/api/local-dev/setup-card',async(req)=>{
  /* Chromium may omit Origin on a same-origin GET fetch. Accept only its
   * same-origin subresource metadata as the equivalent proof; a navigation or
   * a cross-site fetch still cannot read a developer setup credential. */
  const sameOriginFetch=req.headers['sec-fetch-site']==='same-origin'&&['cors','same-origin'].includes(req.headers['sec-fetch-mode']??'');
  if(req.headers.origin!==cfg.APP_ORIGIN&&!sameOriginFetch)throw new DomainError('ORIGIN_FORBIDDEN','This request did not originate from the Marvin web app.',403);
  await session(req);
  if(cfg.DEPLOYMENT_MODE!=='local-dev'||!localDevCard)throw new DomainError('LOCAL_DEV_SETUP_UNAVAILABLE','Automatic single-Pet setup is not enabled on this server.',404);
  return {card:localDevCard};
 });
 app.get('/api/local-dev/setup-pets',async(req)=>{const sameOriginFetch=req.headers['sec-fetch-site']==='same-origin'&&['cors','same-origin'].includes(req.headers['sec-fetch-mode']??'');if(req.headers.origin!==cfg.APP_ORIGIN&&!sameOriginFetch)throw new DomainError('ORIGIN_FORBIDDEN','This request did not originate from the Marvin web app.',403);await session(req);if(cfg.DEPLOYMENT_MODE!=='local-dev'||!localDevPets.length)throw new DomainError('LOCAL_DEV_SETUP_UNAVAILABLE','Automatic multi-Pet setup is not enabled on this server.',404);return {pets:localDevPets.map(pet=>({deviceId:pet.card.deviceId,label:pet.label}))};});
 app.get('/api/local-dev/setup-card/:deviceId',async(req)=>{const sameOriginFetch=req.headers['sec-fetch-site']==='same-origin'&&['cors','same-origin'].includes(req.headers['sec-fetch-mode']??'');if(req.headers.origin!==cfg.APP_ORIGIN&&!sameOriginFetch)throw new DomainError('ORIGIN_FORBIDDEN','This request did not originate from the Marvin web app.',403);await session(req);const {deviceId}=z.object({deviceId:Id}).parse(req.params),pet=localDevPets.find(item=>item.card.deviceId===deviceId);if(cfg.DEPLOYMENT_MODE!=='local-dev'||!pet)throw new DomainError('LOCAL_DEV_SETUP_UNAVAILABLE','That Desktop Pet is not enabled for automatic setup.',404);return {card:pet.card};});
 app.get('/api/auth/start',async(req,reply)=>{if(cfg.AUTH_MODE!=='oidc')throw new DomainError('IDENTITY_UNCONFIGURED','Entire sign-in is not configured. Use the available local sign-in.',409);const query=req.query as Record<string,string>;const flow=await identity.begin(query.returnTo);reply.setCookie('marvin_auth',flow.token,{...cookieOptions,maxAge:300,path:'/api/auth'});return reply.redirect(flow.url);});
 app.get('/api/auth/callback',async(req,reply)=>{reply.clearCookie('marvin_auth',{path:'/api/auth'});try{const callback=new URL(cfg.OIDC_REDIRECT_URI!);callback.search=new URL(req.url,'http://localhost').search;const result=await identity.callback(req.cookies.marvin_auth,callback);const s=await store.createSession(result.owner.id);reply.setCookie('marvin_session',s.token,cookieOptions);return reply.redirect(cfg.APP_ORIGIN+result.returnTo);}catch{return reply.redirect(cfg.APP_ORIGIN+'/login?error=signin_failed');}});
 const requireEnrollment=()=>{if(!enrollment)throw new DomainError('ENROLLMENT_UNAVAILABLE','Device setup is not configured on this server.',503);return enrollment;};
 app.post('/api/robot/enrollment',{config:{rateLimit:{max:10,timeWindow:'1 minute'}}},async(req)=>requireEnrollment().ticket((await session(req)).ownerId,req.body));
 app.delete('/api/robot/enrollment/:id',async(req)=>{const {id}=z.object({id:Id}).parse(req.params);await requireEnrollment().cancel((await session(req)).ownerId,id);return {ok:true};});
 app.post('/api/robot/reconciliation/ack',async(req)=>{const b=z.object({deviceId:Id}).strict().parse(req.body),s=await session(req);if(!await store.acknowledgeRevocation(s.ownerId,b.deviceId))throw new DomainError('RECOVERY_NOT_AUTHORIZED','No pending ownership cleanup was found for this account.',403);return {ok:true};});
 app.post('/api/device/enrollment/redeem',{config:{rateLimit:{max:20,timeWindow:'1 minute'}}},async(req)=>requireEnrollment().redeem(req.body));
 app.get('/api/robot/presence',async(req)=>({body:await store.body((await session(req)).ownerId),presence:devices.presence((await session(req)).ownerId)}));
 app.get('/api/robot/conversation',async(req)=>({conversationId:(await store.owner((await session(req)).ownerId)).petConversation}));
 app.put('/api/robot/conversation',async(req)=>{const b=z.object({conversationId:Id.nullable()}).strict().parse(req.body),s=await session(req);const result=await store.setPetConversation(s.ownerId,b.conversationId);runtime.events.emit('conversation_link',s.ownerId);return result;});
 app.patch('/api/robot/audio',async(req)=>devices.setAudioSettings((await session(req)).ownerId,PetAudioSettings.parse(req.body)));
 app.patch('/api/robot/head-calibration',async(req)=>devices.setHeadCalibration((await session(req)).ownerId,HeadCalibration.parse(req.body)));
 app.patch('/api/robot/eyes',async(req)=>devices.setEyeSettings((await session(req)).ownerId,PetEyeSettings.parse(req.body)));
 app.post('/api/robot/speak',{config:{rateLimit:{max:8,timeWindow:'1 minute'}}},async(req)=>{const b=PetSpeech.parse(req.body),s=await session(req);return devices.speak(s.ownerId,b.text);});
 app.post('/api/robot/control',async(req)=>{
  const b=z.discriminatedUnion('action',[
   z.object({action:z.literal('head'),yaw:z.number().min(-40).max(40),pitch:z.number().min(-30).max(30),durationMs:z.number().int().min(100).max(3000)}).strict(),
   z.object({action:z.literal('tracks'),left:z.number().min(-.3).max(.3),right:z.number().min(-.3).max(.3),durationMs:z.number().int().min(50).max(30000)}).strict(),
   z.object({action:z.literal('remote'),throttle:z.number().min(-1).max(1),turn:z.number().min(-1).max(1),headYaw:z.number().min(-1).max(1),headPitch:z.number().min(-1).max(1),autonomousHead:z.boolean(),sequence:z.number().int().nonnegative()}).strict()
  ]).parse(req.body),s=await session(req),id=randomUUID();
  return devices.dispatchRemote(s.ownerId,b.action,b.action==='head'?{yaw:b.yaw,pitch:b.pitch,durationMs:b.durationMs}:b.action==='tracks'?{left:b.left,right:b.right,durationMs:b.durationMs}:{throttle:b.throttle,turn:b.turn,headYaw:b.headYaw,headPitch:b.headPitch,autonomousHead:b.autonomousHead,sequence:b.sequence},id,b.action==='remote'?1000:b.durationMs+1000);
 });
 app.get('/api/robot/diagnostics',async(req)=>({diagnostics:devices.diagnostics((await session(req)).ownerId)}));
 app.get('/api/conversations',async(req)=>store.listConversations((await session(req)).ownerId));
 app.post('/api/conversations',async(req)=>{z.object({}).strict().parse(req.body);return store.createConversation((await session(req)).ownerId);});
 app.get('/api/conversations/:id',async(req)=>{const {id}=z.object({id:Id}).parse(req.params),s=await session(req);return {conversation:await store.getConversation(s.ownerId,id),turns:await store.turns(s.ownerId,id)};});
 app.post('/api/conversations/:id/activate',async(req)=>{const {id}=z.object({id:Id}).parse(req.params);await store.activate((await session(req)).ownerId,id);return {ok:true};});
 app.patch('/api/conversations/:id/repository',async(req)=>{const {id}=z.object({id:Id}).parse(req.params),b=RepositorySelection.parse(req.body),s=await session(req);if(b.repositoryId){const o=await store.owner(s.ownerId);if(o.entireState==='disconnected')throw new DomainError('CONNECT_ENTIRE','Connect a repository source first.',409);if(o.entireState==='fixture'&&!fixtureRepositories.some(r=>r.id===b.repositoryId))throw new DomainError('REPOSITORY_FORBIDDEN','Repository unavailable.',403);if(o.entireState==='connected'){if(!realEntire)throw new DomainError('ENTIRE_NOT_CONFIGURED','Entire is unavailable on this server.',409);await realEntire.check(s.ownerId,b.repositoryId);}}await store.setRepository(s.ownerId,id,b.repositoryId);return {ok:true};});
 app.post('/api/turns',async(req,reply)=>{const b=SendTurn.parse(req.body),s=await session(req);const ctx=await runtime.context(s.ownerId,b.conversationId,b.interactionId,b.routeId);const result=await runtime.start(ctx,b.text);reply.code(result.created?202:200);return result;});
 app.post('/api/turns/:id/cancel',async(req)=>{const {id}=z.object({id:Id}).parse(req.params);return {cancelled:await runtime.cancel((await session(req)).ownerId,id)};});
 app.get('/api/settings',async(req)=>{const s=await session(req),o=await store.owner(s.ownerId);let available=false,repositories=o.entireState==='fixture'?fixtureRepositories:[],entireError:string|undefined;
  if(realEntire)try{const configured=await realEntire.configured(s.ownerId);available=configured.length>0;if(o.entireState==='connected')repositories=configured;}catch(e){entireError=(e as Error).message;}
  const entireStatus=o.entireState==='connected'?'connected':!realEntire?'host_unavailable':entireError?'configuration_error':available?'ready':'repository_required';
  return {entireState:o.entireState,entireStatus,entireAvailable:available,entireError,body:await store.body(s.ownerId),repositories,provider:runtime.provider.name};});
 app.post('/api/entire/connect',async(req)=>{z.object({}).strict().parse(req.body);const s=await session(req);if(!realEntire)throw new DomainError('ENTIRE_NOT_CONFIGURED','Configure your Entire CLI connection on the Marvin server first.',409);const repositories=await realEntire.connect(s.ownerId);await store.setEntireState(s.ownerId,'connected');return {repositories};});
 app.get('/api/entire/repositories',async(req)=>{const s=await session(req);if(!realEntire||(await store.owner(s.ownerId)).entireState!=='connected')throw new DomainError('CONNECT_ENTIRE','Connect Entire first.',409);const q=z.object({cursor:z.string().max(2048).optional()}).parse(req.query);return realEntire.discover(s.ownerId,q.cursor);});
 app.post('/api/conversations/:id/repository/read',async(req)=>{const {id}=z.object({id:Id}).parse(req.params),s=await session(req),b=z.object({tool:z.string(),args:z.record(z.string(),z.unknown())}).strict().parse(req.body);const ctx=await runtime.context(s.ownerId,id,'read','browser');authorizeTool(ctx,b.tool,b.args);if(!realEntire||ctx.entireState!=='connected'||!ctx.repositoryId||!b.tool.startsWith('repository_'))throw new DomainError('CONNECT_ENTIRE','Select a connected Entire repository first.',409);const card=await realEntire.read(s.ownerId,ctx.repositoryId,b.tool,b.args,AbortSignal.timeout(20000));const after=await runtime.context(s.ownerId,id,'read','browser');authorizeTool(after,b.tool,b.args);if(after.entireState!==ctx.entireState||after.repositoryId!==ctx.repositoryId)throw new DomainError('CONTEXT_CHANGED','Repository access changed. Retry with the current selection.',409);return card;});
 app.post('/api/settings/entire',async(req)=>{const b=z.object({action:z.enum(['fixture','disconnect'])}).strict().parse(req.body);if(b.action==='fixture'&&cfg.NODE_ENV==='production')throw new DomainError('FIXTURE_DISABLED','Sample data is disabled.',403);const s=await session(req);await store.setEntireState(s.ownerId,b.action==='fixture'?'fixture':'disconnected');realEntire?.invalidate(s.ownerId);return {ok:true};});
 // These endpoints are explicit UI fixtures. They never provision a real robot or accept Wi-Fi passwords.
 const requireFixture=()=>{if(cfg.AUTH_MODE!=='development'||cfg.NODE_ENV==='production')throw new DomainError('SIMULATION_DISABLED','Robot simulation is available only in local development.',403);};
 app.post('/api/simulation/robot',async(req)=>{requireFixture();const b=z.object({deviceId:Id,enrollmentId:Id,network:z.string().min(1).max(128)}).strict().parse(req.body),s=await session(req);await store.reserve(s.ownerId,b.deviceId,b.enrollmentId,true);await store.redeem(s.ownerId,b.deviceId,b.enrollmentId,b.network);return {ok:true};});
 app.post('/api/simulation/network',async(req)=>{requireFixture();const b=z.object({deviceId:Id,network:z.string().min(1).max(128)}).strict().parse(req.body);await store.changeNetwork((await session(req)).ownerId,b.deviceId,b.network);return {ok:true};});
 app.get('/api/account/privacy',async(req)=>{const s=await session(req);return (await db.query<{retention_days:number|null}>('SELECT retention_days FROM owners WHERE id=?',[s.ownerId]))[0];});
 app.patch('/api/account/privacy',async(req)=>{const s=await session(req),b=z.object({retentionDays:z.union([z.literal(30),z.literal(90),z.literal(365),z.null()])}).strict().parse(req.body);await db.query('UPDATE owners SET retention_days=? WHERE id=?',[b.retentionDays,s.ownerId]);return {retention_days:b.retentionDays};});
 app.get('/api/account/export/download',async(req,reply)=>{const s=await session(req),cutoff=Date.now();const stream=async function*(){let after:string|undefined,count=0;yield JSON.stringify({format:'marvin-conversations-v1',exportedAt:cutoff})+'\n';do{await session(req);const page=await exportPage(store,s.ownerId,after,cutoff);for(const turn of page.turns){count++;yield JSON.stringify(turn)+'\n';}after=page.nextCursor??undefined;}while(after);yield JSON.stringify({complete:true,exportedTurns:count})+'\n';};reply.header('Content-Type','application/x-ndjson').header('Content-Disposition','attachment; filename="marvin-conversations.jsonl"');return reply.send(Readable.from(stream()));});
 app.get('/api/account/export',async(req)=>{const query=z.object({after:Id.optional()}).strict().parse(req.query);return exportPage(store,(await session(req)).ownerId,query.after);});
 app.delete('/api/account',async(req,reply)=>{const s=await session(req);z.object({confirmation:z.literal('DELETE MY ACCOUNT')}).strict().parse(req.body);if(Date.now()-s.authenticatedAt>15*60*1000)throw new DomainError('REAUTH_REQUIRED','Sign in again before deleting your account.',401);const running=await store.db.query<{id:string}>("SELECT id FROM turns WHERE owner_id=? AND status='running'",[s.ownerId]);for(const t of running)await runtime.cancel(s.ownerId,t.id);devices.revoke(s.ownerId);for(const t of running)await runtime.waitTurn(t.id);realEntire?.invalidate(s.ownerId);await deleteAccount(store,s.ownerId);reply.clearCookie('marvin_session',{path:'/'});return {deleted:true,deviceErasureConfirmed:false,backups:'Deletion from backups follows the operator’s documented backup expiration policy.'};});
 app.delete('/api/robot',async(req)=>{const s=await session(req);if(Date.now()-s.authenticatedAt>15*60*1000)throw new DomainError('REAUTH_REQUIRED','Please sign in again before unlinking your Desktop Pet.',401);const result=await store.unlink(s.ownerId);devices.revoke(s.ownerId);return {ok:true,deviceId:result?.deviceId,deviceErasureConfirmed:false};});
 let eventConnections=0;
 /* A remote-control lease is intentionally separate from the conversation
    event stream. It prevents an old tab from stopping a newer controller. */
 const remoteLeases=new Map<string,string>();
 app.get('/api/remote',{websocket:true},(socket,req)=>{
  if(req.headers.origin!==cfg.APP_ORIGIN){socket.close(4403,'Invalid origin');return;}
  let closed=false,ownerId='',deviceId='',lease='',latest:RemoteIntent|undefined,lastSequence=-1,windowStarted=Date.now(),frames=0;
  const close=(code:number,message:string)=>{if(!closed){closed=true;socket.close(code,message);}};
  const stop=()=>{
   if(!ownerId||!deviceId||remoteLeases.get(deviceId)!==lease)return;
   remoteLeases.delete(deviceId);
   /* A neutral frame is best effort; the Pet's 250 ms watchdog remains the
      independent failsafe if the server-to-Pet transport also disappeared. */
   void devices.dispatchRemote(ownerId,'remote',{throttle:0,turn:0,headYaw:0,headPitch:0,autonomousHead:true,sequence:lastSequence+1},randomUUID(),1000).catch(()=>{});
  };
  const initialize=async()=>{
   const s=await session(req),presence=devices.presence(s.ownerId);
   if(!presence||!presence.capabilities.includes('remote'))throw new DomainError('REMOTE_UNAVAILABLE','Your Desktop Pet does not support remote control.',409);
   ownerId=s.ownerId;deviceId=presence.deviceId;lease=randomUUID();remoteLeases.set(deviceId,lease);
   socket.send(JSON.stringify({type:'ready',lease}));
  };
  void initialize().catch(()=>close(4401,'Sign in again'));
  const flush=setInterval(()=>{
   const intent=latest;latest=undefined;
   if(!intent||closed||remoteLeases.get(deviceId)!==lease)return;
   void devices.dispatchRemote(ownerId,'remote',{throttle:intent.throttle,turn:intent.turn,headYaw:intent.headYaw,headPitch:intent.headPitch,autonomousHead:intent.autonomousHead,sequence:intent.sequence},randomUUID(),1000).catch(error=>{if(error instanceof DomainError)close(4409,error.message);else close(1011,'Remote control failed');});
  },40);
  const heartbeat=setInterval(()=>{void session(req).catch(()=>close(4401,'Sign in again'));},5000);
  socket.on('message',data=>{
   if(closed||!ownerId)return;
   const now=Date.now();if(now-windowStarted>=1000){windowStarted=now;frames=0;}
   if(++frames>30){close(4429,'Remote input rate exceeded');return;}
   try{const intent=RemoteIntent.parse(JSON.parse(data.toString()));if(intent.sequence<=lastSequence)return;lastSequence=intent.sequence;latest=intent;}catch{close(4400,'Invalid remote input');}
  });
  socket.on('error',()=>close(1011,'Remote connection failed'));
  socket.on('close',()=>{closed=true;clearInterval(flush);clearInterval(heartbeat);stop();});
 });
 app.get('/api/events',{websocket:true},(socket,req)=>{
  if(req.headers.origin!==cfg.APP_ORIGIN){socket.close(4403,'Invalid origin');return;}
  if(eventConnections>=160){socket.close(1013,'Server busy; reconnect shortly');return;}
  eventConnections++;
  let subscribed:{conversationId:string;routeId:string;ownerId:string}|null=null,closed=false;
  let delivery=Promise.resolve(),control=Promise.resolve(),queued=0,pending=0;
  const send=(data:unknown)=>{
   if(closed)return;if(++queued>256){closed=true;socket.close(1013,'Reconnect to resume');return;}
   delivery=delivery.then(async()=>{if(closed)return;await session(req);if(closed)return;if(socket.bufferedAmount>262144){closed=true;socket.close(1013,'Reconnect to resume');return;}socket.send(JSON.stringify(data));}).catch(()=>socket.close(4401,'Sign in again')).finally(()=>{queued--;});
  };
  const startTimer=setTimeout(()=>{if(!subscribed)socket.close(4408,'Subscribe after signing in');},15000);
  const event=(e:AgentEvent,ownerId:string)=>{if(subscribed&&subscribed.ownerId===ownerId&&subscribed.conversationId===e.conversationId)send(e);};
  const refresh=(id:string,ownerId:string)=>{if(subscribed?.ownerId===ownerId&&subscribed.conversationId===id)send({type:'refresh'});};
  const link=(ownerId:string)=>{if(subscribed?.ownerId===ownerId)send({type:'conversation_link'});};
  runtime.events.on('event',event);runtime.events.on('refresh',refresh);runtime.events.on('conversation_link',link);
  const heartbeat=setInterval(()=>{void session(req).then(()=>send({type:'heartbeat'})).catch(()=>socket.close(4401,'Sign in again'));},5000);
  socket.on('message',data=>{if(closed)return;if(++pending>32){closed=true;socket.close(1013,'Too many pending subscriptions');return;}control=control.then(async()=>{
   if(closed)return;const s=await session(req),message=ClientEvent.parse(JSON.parse(data.toString()));if(message.type==='ping'){send({type:'pong'});return;}
   await store.getConversation(s.ownerId,message.conversationId);subscribed={ownerId:s.ownerId,conversationId:message.conversationId,routeId:message.routeId};clearTimeout(startTimer);
   for(const e of await store.replay(s.ownerId,message.conversationId,message.routeId,message.after))send(e);
   send({type:'subscribed'});
  }).catch(()=>socket.close(4400,'Invalid or unauthorized subscription')).finally(()=>{pending--;});});
  socket.on('close',()=>{closed=true;eventConnections--;clearTimeout(startTimer);clearInterval(heartbeat);runtime.events.off('event',event);runtime.events.off('refresh',refresh);runtime.events.off('conversation_link',link);});
 });
 devices.register(app);
 const naturalVoiceProvider=options.voice??(cfg.VOICE_PROVIDER==='openai'?new OpenAIVoiceProvider(cfg.OPENAI_API_KEY!,cfg.OPENAI_REALTIME_MODEL!,cfg.OPENAI_TRANSCRIPTION_MODEL,cfg.OPENAI_VOICE,undefined,undefined,code=>{voiceDiagnostic=code;app.log.warn({code},'Voice provider diagnostic');}):cfg.VOICE_PROVIDER==='deepgram'?new DeepgramVoiceProvider(cfg.DEEPGRAM_API_KEY!,cfg.DEEPGRAM_STT_MODEL!,cfg.DEEPGRAM_TTS_MODEL!,runtime.provider):undefined);
 const voiceProvider=naturalVoiceProvider&&cfg.VOICE_EFFECT==='subtle-robotic'?new RoboticVoiceProvider(naturalVoiceProvider):naturalVoiceProvider;
 devices.voice=new DeviceVoice(runtime,voiceProvider);
 const voice=registerVoice(app,runtime,cfg.APP_ORIGIN,voiceProvider,options.voiceLimits);
 let retentionAfter='';let retentionWork:Promise<void>|undefined;
 const retention=setInterval(()=>{if(retentionWork)return;retentionWork=(async()=>{const owners=await db.query<{id:string;retention_days:number}>('SELECT id,retention_days FROM owners WHERE retention_days IS NOT NULL AND id>? ORDER BY id LIMIT 100',[retentionAfter]);for(const o of owners)await purgeExpiredHistory(store,o.id,Date.now()-o.retention_days*86400000);retentionAfter=owners.length===100?owners.at(-1)!.id:'';})().catch(()=>app.log.error('History retention maintenance failed')).finally(()=>{retentionWork=undefined;});},60000);
 const recover=setInterval(()=>{void store.recoverExpired().catch(()=>{});},10000);
 const webRoot=resolve('dist/web');if(existsSync(webRoot)){await app.register(staticFiles,{root:webRoot});app.setNotFoundHandler((req,reply)=>req.url.startsWith('/api/')?reply.code(404).send({error:{code:'NOT_FOUND',message:'Unknown endpoint.'}}):reply.sendFile('index.html'));}
 app.addHook('onClose',async()=>{clearInterval(recover);clearInterval(retention);voice.closeAll();await devices.voice?.close();devices.close();await runtime.close();await retentionWork;await db.close();});
 return {app,store,runtime,devices,enrollment};
}
