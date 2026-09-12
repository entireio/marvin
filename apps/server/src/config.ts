import { z } from 'zod';
export const Env=z.object({
 NODE_ENV:z.enum(['development','test','production']).default('development'),HOST:z.string().default('127.0.0.1'),PORT:z.coerce.number().int().min(1).max(65535).default(4310),APP_ORIGIN:z.url().default('http://127.0.0.1:5173'),
 AUTH_MODE:z.enum(['development','local','oidc']).default('development'),LOCAL_PASSWORD_HASH:z.string().optional(),MODEL_PROVIDER:z.enum(['fixture','openai']).default('fixture'),OPENAI_API_KEY:z.string().optional(),OPENAI_MODEL:z.string().optional(),SQLITE_PATH:z.string().default('./data/marvin.sqlite'),DATABASE_URL:z.string().optional(),
 OIDC_ISSUER:z.url().optional(),OIDC_CLIENT_ID:z.string().optional(),OIDC_CLIENT_SECRET:z.string().optional(),OIDC_LABEL:z.string().default('Identity provider'),OIDC_REDIRECT_URI:z.url().optional(),PUBLIC_DOCS_URL:z.url().default('https://spedemon.github.io/marvin/')
});
export type Config=z.infer<typeof Env>;
export function config(env:NodeJS.ProcessEnv=process.env):Config{
 const c=Env.parse(env), url=new URL(c.APP_ORIGIN);
 if(url.pathname!=='/'||url.search||url.hash||url.username||url.password)throw new Error('APP_ORIGIN must be an origin, with no path or credentials.');
 c.APP_ORIGIN=url.origin;
 if(c.AUTH_MODE==='development'&&(!['127.0.0.1','localhost','::1'].includes(c.HOST)||!['127.0.0.1','localhost','[::1]'].includes(url.hostname)))throw new Error('Development sign-in is restricted to loopback.');
 if(c.AUTH_MODE==='local'&&!/^scrypt\$[a-f0-9]{32}\$[a-f0-9]{128}$/.test(c.LOCAL_PASSWORD_HASH??''))throw new Error('Local authentication requires LOCAL_PASSWORD_HASH from npm run password:hash.');
 if(c.AUTH_MODE==='oidc'&&(!c.OIDC_ISSUER||!c.OIDC_CLIENT_ID||!c.OIDC_REDIRECT_URI))throw new Error('OIDC requires an approved issuer, client ID and redirect URI.');
 if(c.AUTH_MODE==='oidc'&&new URL(c.OIDC_ISSUER!).protocol!=='https:')throw new Error('OIDC issuer must use HTTPS.');
 if(c.MODEL_PROVIDER==='openai'&&(!c.OPENAI_API_KEY||!c.OPENAI_MODEL))throw new Error('OpenAI requires OPENAI_API_KEY and OPENAI_MODEL.');
 if(c.NODE_ENV==='production'&&(url.protocol!=='https:'||c.AUTH_MODE==='development'||c.MODEL_PROVIDER==='fixture'))throw new Error('Production requires HTTPS, real authentication and a real model provider.');
 return c;
}
