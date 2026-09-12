import Fastify from 'fastify';
import cookie from '@fastify/cookie';
import websocket from '@fastify/websocket';
import rateLimit from '@fastify/rate-limit';
import staticFiles from '@fastify/static';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { z, ZodError } from 'zod';
import { Store } from '../../../packages/persistence/src/store.js';
import { sqliteDatabase,postgresDatabase,type Database } from '../../../packages/persistence/src/database.js';
import { migrate } from '../../../packages/persistence/src/migrations.js';
import { Runtime } from '../../../packages/runtime/src/runtime.js';
import { FixtureEntire,fixtureRepositories } from '../../../packages/runtime/src/entire.js';
import { FixtureTextProvider,OpenAITextProvider,type TextProvider } from '../../../packages/runtime/src/provider.js';
import { SendTurn, ClientEvent, DomainError, Id, RepositorySelection, type AgentEvent } from '../../../packages/contracts/src/index.js';
import { Identity,verifyPassword,safeReturnTo } from './auth.js';
import type { Config } from './config.js';
export async function createApp(cfg:Config,options:{database?:Database;provider?:TextProvider;logger?:boolean}={}){
 const app=Fastify({bodyLimit:32768,logger:options.logger?{level:'info',redact:['req.headers.cookie','req.headers.authorization','req.headers["x-csrf-token"]'],serializers:{req:r=>({method:r.method,url:r.url?.split('?')[0],id:r.id}),res:r=>({statusCode:r.statusCode})}}:false,disableRequestLogging:true});
 const db=options.database??(cfg.DATABASE_URL?postgresDatabase(cfg.DATABASE_URL):sqliteDatabase(cfg.SQLITE_PATH));await migrate(db);const store=new Store(db);await store.recoverExpired();
 const runtime=new Runtime(store,options.provider??(cfg.MODEL_PROVIDER==='openai'?new OpenAITextProvider(cfg.OPENAI_API_KEY!,cfg.OPENAI_MODEL!):new FixtureTextProvider()),new FixtureEntire());
 const identity=new Identity(cfg,store), secure=cfg.NODE_ENV==='production'||cfg.APP_ORIGIN.startsWith('https:');
 const cookieOptions={path:'/',httpOnly:true,sameSite:'lax' as const,secure,maxAge:8*60*60};
 await app.register(cookie);await app.register(rateLimit,{max:200,timeWindow:'1 minute'});await app.register(websocket,{options:{maxPayload:16384}});
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
  const mutating=!['GET','HEAD','OPTIONS'].includes(req.method);
  if(mutating&&req.headers.origin!==cfg.APP_ORIGIN)throw new DomainError('ORIGIN_FORBIDDEN','This request did not originate from Marvin.',403);
  if(req.url.startsWith('/api/auth/')||req.url.split('?')[0]==='/api/config'||req.url==='/api/health')return;
  const session=await store.session(req.cookies.marvin_session);if(!session)throw new DomainError('UNAUTHENTICATED','Please sign in to continue.',401);
  if(mutating&&req.headers['x-csrf-token']!==session.csrf)throw new DomainError('CSRF_INVALID','Refresh the page and try again.',403);
 });
 const session=async(req:{cookies:Record<string,string|undefined>})=>{const s=await store.session(req.cookies.marvin_session);if(!s)throw new DomainError('UNAUTHENTICATED','Please sign in to continue.',401);return s;};
 app.get('/api/health',async()=>({status:'ok',schema:2}));
 app.get('/api/config',async()=>({authMode:cfg.AUTH_MODE,oidcLabel:cfg.OIDC_LABEL,provider:runtime.provider.name,development:cfg.AUTH_MODE==='development',docsUrl:cfg.PUBLIC_DOCS_URL,voiceAvailable:false,hardwareProvisioningAvailable:false}));
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
 app.get('/api/auth/start',async(req,reply)=>{if(cfg.AUTH_MODE!=='oidc')throw new DomainError('IDENTITY_UNCONFIGURED','Entire sign-in is not configured. Use the available local sign-in.',409);const query=req.query as Record<string,string>;const flow=await identity.begin(query.returnTo);reply.setCookie('marvin_auth',flow.token,{...cookieOptions,maxAge:300,path:'/api/auth'});return reply.redirect(flow.url);});
 app.get('/api/auth/callback',async(req,reply)=>{reply.clearCookie('marvin_auth',{path:'/api/auth'});try{const callback=new URL(cfg.OIDC_REDIRECT_URI!);callback.search=new URL(req.url,'http://localhost').search;const result=await identity.callback(req.cookies.marvin_auth,callback);const s=await store.createSession(result.owner.id);reply.setCookie('marvin_session',s.token,cookieOptions);return reply.redirect(cfg.APP_ORIGIN+result.returnTo);}catch{return reply.redirect(cfg.APP_ORIGIN+'/login?error=signin_failed');}});
 app.get('/api/conversations',async(req)=>store.listConversations((await session(req)).ownerId));
 app.post('/api/conversations',async(req)=>{z.object({}).strict().parse(req.body);return store.createConversation((await session(req)).ownerId);});
 app.get('/api/conversations/:id',async(req)=>{const {id}=z.object({id:Id}).parse(req.params),s=await session(req);return {conversation:await store.getConversation(s.ownerId,id),turns:await store.turns(s.ownerId,id)};});
 app.post('/api/conversations/:id/activate',async(req)=>{const {id}=z.object({id:Id}).parse(req.params);await store.activate((await session(req)).ownerId,id);return {ok:true};});
 app.patch('/api/conversations/:id/repository',async(req)=>{const {id}=z.object({id:Id}).parse(req.params),b=RepositorySelection.parse(req.body),s=await session(req);if(b.repositoryId&&(await store.owner(s.ownerId)).entireState==='disconnected')throw new DomainError('CONNECT_ENTIRE','Connect a repository source first.',409);await store.setRepository(s.ownerId,id,b.repositoryId);return {ok:true};});
 app.post('/api/turns',async(req,reply)=>{const b=SendTurn.parse(req.body),s=await session(req);const ctx=await runtime.context(s.ownerId,b.conversationId,b.interactionId,b.routeId);const result=await runtime.start(ctx,b.text);reply.code(result.created?202:200);return result;});
 app.post('/api/turns/:id/cancel',async(req)=>{const {id}=z.object({id:Id}).parse(req.params);return {cancelled:await runtime.cancel((await session(req)).ownerId,id)};});
 app.get('/api/settings',async(req)=>{const s=await session(req),o=await store.owner(s.ownerId);return {entireState:o.entireState,body:await store.body(s.ownerId),repositories:o.entireState==='fixture'?fixtureRepositories:[],provider:runtime.provider.name};});
 app.post('/api/settings/entire',async(req)=>{const b=z.object({action:z.enum(['fixture','disconnect'])}).strict().parse(req.body);if(b.action==='fixture'&&cfg.NODE_ENV==='production')throw new DomainError('FIXTURE_DISABLED','Sample data is disabled.',403);const s=await session(req);await store.setEntireState(s.ownerId,b.action==='fixture'?'fixture':'disconnected');return {ok:true};});
 // These endpoints are explicit UI fixtures. They never provision a real robot or accept Wi-Fi passwords.
 const requireFixture=()=>{if(cfg.AUTH_MODE!=='development'||cfg.NODE_ENV==='production')throw new DomainError('SIMULATION_DISABLED','Robot simulation is available only in local development.',403);};
 app.post('/api/simulation/robot',async(req)=>{requireFixture();const b=z.object({deviceId:Id,enrollmentId:Id,network:z.string().min(1).max(128)}).strict().parse(req.body),s=await session(req);await store.reserve(s.ownerId,b.deviceId,b.enrollmentId,true);await store.redeem(s.ownerId,b.deviceId,b.enrollmentId,b.network);return {ok:true};});
 app.post('/api/simulation/network',async(req)=>{requireFixture();const b=z.object({deviceId:Id,network:z.string().min(1).max(128)}).strict().parse(req.body);await store.changeNetwork((await session(req)).ownerId,b.deviceId,b.network);return {ok:true};});
 app.delete('/api/robot',async(req)=>{const s=await session(req);if(Date.now()-s.authenticatedAt>15*60*1000)throw new DomainError('REAUTH_REQUIRED','Please sign in again before unlinking Marvin.',401);await store.unlink(s.ownerId);return {ok:true,deviceErasureConfirmed:false};});
 app.get('/api/events',{websocket:true},(socket,req)=>{
  if(req.headers.origin!==cfg.APP_ORIGIN){socket.close(4403,'Invalid origin');return;}
  let subscribed:{conversationId:string;routeId:string;ownerId:string}|null=null,closed=false;
  let delivery=Promise.resolve();
  const send=(data:unknown)=>{delivery=delivery.then(async()=>{if(closed)return;await session(req);if(closed)return;if(socket.bufferedAmount>262144){socket.close(1013,'Reconnect to resume');return;}socket.send(JSON.stringify(data));}).catch(()=>socket.close(4401,'Sign in again'));};
  const event=(e:AgentEvent,ownerId:string)=>{if(subscribed&&subscribed.ownerId===ownerId&&subscribed.conversationId===e.conversationId&&subscribed.routeId===e.routeId)send(e);};
  const refresh=(id:string,ownerId:string)=>{if(subscribed?.ownerId===ownerId&&subscribed.conversationId===id)send({type:'refresh'});};
  runtime.events.on('event',event);runtime.events.on('refresh',refresh);
  const heartbeat=setInterval(()=>{void session(req).then(()=>send({type:'heartbeat'})).catch(()=>socket.close(4401,'Sign in again'));},5000);
  socket.on('message',data=>{void(async()=>{
   const s=await session(req),message=ClientEvent.parse(JSON.parse(data.toString()));if(message.type==='ping'){send({type:'pong'});return;}
   await store.getConversation(s.ownerId,message.conversationId);subscribed={ownerId:s.ownerId,conversationId:message.conversationId,routeId:message.routeId};
   for(const e of await store.replay(s.ownerId,message.conversationId,message.routeId,message.after))send(e);
   send({type:'subscribed'});
  })().catch(()=>socket.close(4400,'Invalid or unauthorized subscription'));});
  socket.on('close',()=>{closed=true;clearInterval(heartbeat);runtime.events.off('event',event);runtime.events.off('refresh',refresh);});
 });
 const recover=setInterval(()=>{void store.recoverExpired().catch(()=>{});},10000);
 const webRoot=resolve('dist/web');if(existsSync(webRoot)){await app.register(staticFiles,{root:webRoot});app.setNotFoundHandler((req,reply)=>req.url.startsWith('/api/')?reply.code(404).send({error:{code:'NOT_FOUND',message:'Unknown endpoint.'}}):reply.sendFile('index.html'));}
 app.addHook('onClose',async()=>{clearInterval(recover);await runtime.close();await db.close();});
 return {app,store,runtime};
}
