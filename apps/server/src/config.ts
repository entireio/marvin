import { z } from 'zod';
import { isIP } from 'node:net';
export const Env=z.object({
 TRUST_PROXY_HOPS:z.coerce.number().int().min(0).max(1).default(0),
 NODE_ENV:z.enum(['development','test','production']).default('development'),HOST:z.string().default('127.0.0.1'),PORT:z.coerce.number().int().min(1).max(65535).default(4310),APP_ORIGIN:z.url().default('http://127.0.0.1:5173'),
 DEPLOYMENT_MODE:z.enum(['local-dev','local-secure','cloud']).default('local-dev'),
 AUTH_MODE:z.enum(['development','local','oidc','github']).default('development'),LOCAL_PASSWORD_HASH:z.string().optional(),MODEL_PROVIDER:z.enum(['fixture','openai','anthropic']).default('fixture'),OPENAI_API_KEY:z.string().optional(),OPENAI_MODEL:z.string().optional(),OPENAI_REPOSITORY_MODEL:z.string().optional(),SQLITE_PATH:z.string().default('./data/marvin.sqlite'),DATABASE_URL:z.string().optional(),
 ANTHROPIC_API_KEY:z.string().optional(),ANTHROPIC_MODEL:z.string().optional(),
 DEEPGRAM_API_KEY:z.string().optional(),DEEPGRAM_STT_MODEL:z.string().optional(),DEEPGRAM_TTS_MODEL:z.string().optional(),
 VOICE_PROVIDER:z.enum(['disabled','openai','deepgram']).default('disabled'),VOICE_EFFECT:z.enum(['natural','subtle-robotic']).default('subtle-robotic'),OPENAI_REALTIME_MODEL:z.string().optional(),OPENAI_TRANSCRIPTION_MODEL:z.string().default('gpt-4o-mini-transcribe'),OPENAI_VOICE:z.enum(['marin','cedar','alloy','ash','ballad','coral','echo','sage','shimmer','verse']).default('cedar'),
 HARDWARE_PROVISIONING_ENABLED:z.enum(['true','false']).default('false'),LOCAL_DEV_SETUP_CARDS_FILE:z.string().optional(),LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE:z.string().optional(),LOCAL_DEV_PETS_FILE:z.string().optional(),
 FIRMWARE_ROLLOUT_FILE:z.string().optional(),
 ENROLLMENT_KEYS_FILE:z.string().optional(),DEVICE_PUBLIC_ORIGIN:z.url().optional(),
 ENTIRE_CLI_PATH:z.string().optional(),ENTIRE_BINDINGS_FILE:z.string().optional(),ENTIRE_COMPANION_URL:z.preprocess(value=>value===''?undefined:value,z.url().optional()),ENTIRE_COMPANION_TOKEN:z.preprocess(value=>value===''?undefined:value,z.string().min(32).max(256).optional()),
 ENTIRE_HOSTED_CLI_PATH:z.preprocess(value=>value===''?undefined:value,z.string().optional()),ENTIRE_HOSTED_SECRET_DIR:z.preprocess(value=>value===''?undefined:value,z.string().optional()),ENTIRE_HOSTED_SECRET_KEY:z.preprocess(value=>value===''?undefined:value,z.string().optional()),ENTIRE_CONNECTOR_ENABLED:z.enum(['true','false']).default('true'),
 OIDC_ISSUER:z.url().optional(),OIDC_CLIENT_ID:z.string().optional(),OIDC_CLIENT_SECRET:z.string().optional(),OIDC_LABEL:z.string().default('Identity provider'),OIDC_REDIRECT_URI:z.url().optional(),
 GITHUB_CLIENT_ID:z.string().optional(),GITHUB_CLIENT_SECRET:z.string().optional(),GITHUB_ALLOWED_USERS:z.string().default(''),PUBLIC_DOCS_URL:z.url().default('https://marvin-site-r7vxrettpq-uc.a.run.app/')
});
export type Config=z.infer<typeof Env>;
export function config(env:NodeJS.ProcessEnv=process.env):Config{
 const c=Env.parse(env), url=new URL(c.APP_ORIGIN);
 if(c.FIRMWARE_ROLLOUT_FILE&&!c.FIRMWARE_ROLLOUT_FILE.startsWith('/'))throw new Error('FIRMWARE_ROLLOUT_FILE must be an absolute operator configuration path.');
 if(c.LOCAL_DEV_SETUP_CARDS_FILE&&!c.LOCAL_DEV_SETUP_CARDS_FILE.startsWith('/'))throw new Error('LOCAL_DEV_SETUP_CARDS_FILE must be an absolute private file path.');
 if(c.DEPLOYMENT_MODE!=='local-dev'&&c.LOCAL_DEV_SETUP_CARDS_FILE)throw new Error('LOCAL_DEV_SETUP_CARDS_FILE is available only in local-dev mode.');
 if(c.LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE&&!c.LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE.startsWith('/'))throw new Error('LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE must be an absolute private file path.');
 if(c.LOCAL_DEV_PETS_FILE&&!c.LOCAL_DEV_PETS_FILE.startsWith('/'))throw new Error('LOCAL_DEV_PETS_FILE must be an absolute private file path.');
 if(c.LOCAL_DEV_PETS_FILE&&(c.LOCAL_DEV_SETUP_CARDS_FILE||c.LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE))throw new Error('LOCAL_DEV_PETS_FILE replaces the single-Pet local development settings.');
 if(c.DEPLOYMENT_MODE!=='local-dev'&&c.LOCAL_DEV_PETS_FILE)throw new Error('LOCAL_DEV_PETS_FILE is available only in local-dev mode.');
 if(c.DEPLOYMENT_MODE!=='local-dev'&&c.LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE)throw new Error('LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE is available only in local-dev mode.');
 if(c.LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE&&!c.LOCAL_DEV_SETUP_CARDS_FILE)throw new Error('Automatic local-dev device registration requires the matching setup card.');
 if(c.LOCAL_DEV_DEVICE_PUBLIC_KEY_FILE&&c.HARDWARE_PROVISIONING_ENABLED!=='true')throw new Error('Automatic local-dev device registration requires hardware provisioning.');
 if(c.LOCAL_DEV_PETS_FILE&&c.HARDWARE_PROVISIONING_ENABLED!=='true')throw new Error('Automatic local-dev device registration requires hardware provisioning.');
 if((c.LOCAL_DEV_SETUP_CARDS_FILE||c.LOCAL_DEV_PETS_FILE)&&c.NODE_ENV!=='test'&&c.AUTH_MODE!=='local')throw new Error('Automatic local-dev setup requires password-protected local authentication.');
 if(c.HARDWARE_PROVISIONING_ENABLED==='true'&&!c.ENROLLMENT_KEYS_FILE)throw new Error('Hardware provisioning needs configured enrollment trust.');
 if(!!c.ENROLLMENT_KEYS_FILE!==!!c.DEVICE_PUBLIC_ORIGIN)throw new Error('Device enrollment requires ENROLLMENT_KEYS_FILE and DEVICE_PUBLIC_ORIGIN.');
 if(c.ENROLLMENT_KEYS_FILE&&!c.ENROLLMENT_KEYS_FILE.startsWith('/'))throw new Error('ENROLLMENT_KEYS_FILE must be an absolute private file path.');
 if(!!c.ENTIRE_CLI_PATH!==!!c.ENTIRE_BINDINGS_FILE)throw new Error('Entire requires both ENTIRE_CLI_PATH and ENTIRE_BINDINGS_FILE.');
 if(!!c.ENTIRE_COMPANION_URL!==!!c.ENTIRE_COMPANION_TOKEN)throw new Error('Entire companion requires both ENTIRE_COMPANION_URL and ENTIRE_COMPANION_TOKEN.');
 const hosted=[c.ENTIRE_HOSTED_CLI_PATH,c.ENTIRE_HOSTED_SECRET_DIR,c.ENTIRE_HOSTED_SECRET_KEY];if(hosted.some(Boolean)&&!hosted.every(Boolean))throw new Error('Hosted Entire requires ENTIRE_HOSTED_CLI_PATH, ENTIRE_HOSTED_SECRET_DIR and ENTIRE_HOSTED_SECRET_KEY.');
 if(c.ENTIRE_HOSTED_CLI_PATH&&!c.ENTIRE_HOSTED_CLI_PATH.startsWith('/'))throw new Error('ENTIRE_HOSTED_CLI_PATH must be an absolute executable path.');
 if(c.ENTIRE_HOSTED_SECRET_DIR&&!c.ENTIRE_HOSTED_SECRET_DIR.startsWith('/'))throw new Error('ENTIRE_HOSTED_SECRET_DIR must be an absolute private directory.');
 if(c.ENTIRE_HOSTED_SECRET_KEY){let key:Buffer;try{key=Buffer.from(c.ENTIRE_HOSTED_SECRET_KEY,'base64');}catch{throw new Error('ENTIRE_HOSTED_SECRET_KEY must be base64.');}if(key.length!==32)throw new Error('ENTIRE_HOSTED_SECRET_KEY must decode to exactly 32 bytes.');}
 if(c.ENTIRE_CLI_PATH&&c.AUTH_MODE==='oidc')throw new Error('The Entire CLI adapter supports local deployments only. Hosted OIDC requires an approved Entire server integration.');
 if(c.ENTIRE_CLI_PATH&&!c.ENTIRE_CLI_PATH.startsWith('/'))throw new Error('ENTIRE_CLI_PATH must be an absolute executable path.');
 if(c.ENTIRE_COMPANION_URL){const entireUrl=new URL(c.ENTIRE_COMPANION_URL);if(entireUrl.pathname!=='/'||entireUrl.search||entireUrl.hash||!['http:','https:'].includes(entireUrl.protocol))throw new Error('ENTIRE_COMPANION_URL must be an HTTP origin.');c.ENTIRE_COMPANION_URL=entireUrl.origin;}
 if(url.pathname!=='/'||url.search||url.hash||url.username||url.password)throw new Error('APP_ORIGIN must be an origin, with no path or credentials.');
 c.APP_ORIGIN=url.origin;
 if(c.DEPLOYMENT_MODE!=='local-dev'&&(url.protocol!=='https:'||['localhost','127.0.0.1','[::1]'].includes(url.hostname)||isIP(url.hostname)))throw new Error('local-secure and cloud deployments require a reachable HTTPS hostname, not an IP address.');
 if(c.HARDWARE_PROVISIONING_ENABLED==='true'&&c.DEPLOYMENT_MODE==='local-dev'&&(url.protocol!=='https:'||isIP(url.hostname)))throw new Error('Local device provisioning requires a stable HTTPS hostname, not an IP address.');
 if(c.AUTH_MODE==='development'&&c.TRUST_PROXY_HOPS)throw new Error('Development sign-in must not trust forwarded addresses.');
 if(c.AUTH_MODE==='development'&&(!['127.0.0.1','localhost','::1'].includes(c.HOST)||!['127.0.0.1','localhost','[::1]'].includes(url.hostname)))throw new Error('Development sign-in is restricted to loopback.');
 if(c.AUTH_MODE==='local'&&!/^scrypt\$[a-f0-9]{32}\$[a-f0-9]{128}$/.test(c.LOCAL_PASSWORD_HASH??''))throw new Error('Local authentication requires LOCAL_PASSWORD_HASH from npm run password:hash.');
 if(c.AUTH_MODE==='oidc'&&(!c.OIDC_ISSUER||!c.OIDC_CLIENT_ID||!c.OIDC_REDIRECT_URI))throw new Error('OIDC requires an approved issuer, client ID and redirect URI.');
 if(c.AUTH_MODE==='oidc'&&new URL(c.OIDC_ISSUER!).protocol!=='https:')throw new Error('OIDC issuer must use HTTPS.');
 if(c.AUTH_MODE==='github'&&(!c.GITHUB_CLIENT_ID||!c.GITHUB_CLIENT_SECRET))throw new Error('GitHub authentication requires its OAuth client ID and secret.');
 if(c.AUTH_MODE==='github'&&!c.GITHUB_ALLOWED_USERS.split(',').map(x=>x.trim()).filter(Boolean).length)throw new Error('GitHub authentication requires at least one allowed username.');
 if(c.VOICE_PROVIDER==='deepgram'&&(!c.DEEPGRAM_API_KEY||!c.DEEPGRAM_STT_MODEL||!c.DEEPGRAM_TTS_MODEL||c.MODEL_PROVIDER==='fixture'))throw new Error('Deepgram voice requires its API key, explicit STT/TTS models and a real canonical text provider.');
 if(c.VOICE_PROVIDER==='openai'&&(!c.OPENAI_API_KEY||!c.OPENAI_REALTIME_MODEL))throw new Error('Voice requires OPENAI_API_KEY and OPENAI_REALTIME_MODEL.');
 if(c.MODEL_PROVIDER==='anthropic'&&(!c.ANTHROPIC_API_KEY||!c.ANTHROPIC_MODEL))throw new Error('Anthropic requires ANTHROPIC_API_KEY and ANTHROPIC_MODEL.');
 if(c.MODEL_PROVIDER==='openai'&&(!c.OPENAI_API_KEY||!c.OPENAI_MODEL))throw new Error('OpenAI requires OPENAI_API_KEY and OPENAI_MODEL.');
 if(c.NODE_ENV==='production'&&(url.protocol!=='https:'||c.AUTH_MODE==='development'||c.MODEL_PROVIDER==='fixture'))throw new Error('Production requires HTTPS, real authentication and a real model provider.');
 if(c.DEPLOYMENT_MODE==='cloud'&&c.NODE_ENV!=='production')throw new Error('Cloud mode requires a production build.');
 if(c.DEPLOYMENT_MODE==='cloud'&&!c.DATABASE_URL)throw new Error('Cloud mode requires durable PostgreSQL storage.');
 return c;
}
