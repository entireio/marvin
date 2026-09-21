/** Host-only bridge between the LAN container and the user's authenticated Entire CLI. */
import Fastify from 'fastify';
import {timingSafeEqual} from 'node:crypto';
import {isAbsolute} from 'node:path';
import {z} from 'zod';
import {EntireCli} from '../packages/runtime/src/entire-cli.js';
import {DomainError,Id} from '../packages/contracts/src/index.js';

try{process.loadEnvFile('.env');}catch{}
const CompanionEnv=z.object({ENTIRE_CLI_PATH:z.string().refine(isAbsolute),ENTIRE_BINDINGS_FILE:z.string().refine(isAbsolute),ENTIRE_COMPANION_TOKEN:z.string().min(32).max(256),ENTIRE_COMPANION_HOST:z.string().default('127.0.0.1'),ENTIRE_COMPANION_PORT:z.coerce.number().int().min(1).max(65535).default(4311)});
const cfg=CompanionEnv.parse(process.env),integration=new EntireCli(cfg.ENTIRE_CLI_PATH,cfg.ENTIRE_BINDINGS_FILE),app=Fastify({logger:{level:'info',redact:['req.headers.authorization']},bodyLimit:32768,disableRequestLogging:true});
const expected=Buffer.from(`Bearer ${cfg.ENTIRE_COMPANION_TOKEN}`);
app.addHook('onRequest',async request=>{const actual=Buffer.from(request.headers.authorization??'');if(actual.length!==expected.length||!timingSafeEqual(actual,expected))throw new DomainError('COMPANION_UNAUTHORIZED','Host connection authorization failed.',401);});
app.setErrorHandler((failure,_request,reply)=>{const status=failure instanceof DomainError?failure.status:failure instanceof z.ZodError?400:500;reply.code(status).send({error:{code:failure instanceof DomainError?failure.code:'COMPANION_REQUEST_FAILED',message:failure instanceof DomainError?failure.message:'The host connection could not complete this request.',status}});});
const Owner=z.object({ownerId:Id}).strict();
app.post('/v1/configured',async request=>integration.configured(Owner.parse(request.body).ownerId));
app.post('/v1/connect',async request=>integration.connect(Owner.parse(request.body).ownerId));
app.post('/v1/check',async request=>{const body=Owner.extend({repositoryId:Id}).strict().parse(request.body);return integration.check(body.ownerId,body.repositoryId);});
app.post('/v1/discover',async request=>{const body=Owner.extend({cursor:z.string().max(2048).optional()}).strict().parse(request.body);return integration.discover(body.ownerId,body.cursor);});
app.post('/v1/read',async request=>{const body=Owner.extend({repositoryId:Id,tool:z.string().max(80),args:z.record(z.string(),z.unknown())}).strict().parse(request.body);return integration.read(body.ownerId,body.repositoryId,body.tool,body.args,AbortSignal.timeout(20000));});
app.post('/v1/invalidate',async request=>{integration.invalidate(Owner.parse(request.body).ownerId);return {ok:true};});
await app.listen({host:cfg.ENTIRE_COMPANION_HOST,port:cfg.ENTIRE_COMPANION_PORT});
console.log(`Entire companion ready on ${cfg.ENTIRE_COMPANION_HOST}:${cfg.ENTIRE_COMPANION_PORT}`);
for(const signal of ['SIGTERM','SIGINT'] as const)process.on(signal,()=>{void app.close().then(()=>process.exit(0));});
