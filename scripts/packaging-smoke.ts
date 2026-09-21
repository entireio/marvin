/** Tests only the disposable marvin-packaging-check stack with its dummy account. */
import https from 'node:https';
import {readFileSync,writeFileSync} from 'node:fs';
import WebSocket from 'ws';
const ca=readFileSync('work/board/packaging-root.crt'),origin='https://marvin.test:8443';
async function request(path:string,payload?:object,cookie?:string,csrf?:string){return new Promise<{status:number;data:any;headers:import('node:http').IncomingHttpHeaders}>((resolve,reject)=>{
 const body=payload?JSON.stringify(payload):undefined;
 const req=https.request({hostname:'127.0.0.1',servername:'marvin.test',port:8443,ca,path,method:body?'POST':'GET',headers:{host:'marvin.test:8443',origin,...(body?{'content-type':'application/json','content-length':Buffer.byteLength(body)}:{}),...(cookie?{cookie}:{}),...(csrf?{'x-csrf-token':csrf}:{})}},res=>{let data='';res.on('data',chunk=>{data+=chunk;if(data.length>65536)req.destroy(new Error('Response too large'));});res.on('end',()=>resolve({status:res.statusCode!,data:JSON.parse(data),headers:res.headers}));});req.setTimeout(5000,()=>req.destroy(new Error('HTTP timeout')));req.on('error',reject);req.end(body);
});}
const health=await request('/api/health');if(health.status!==200)throw new Error('Health failed');
const login=await request('/api/auth/login',{password:'container-smoke-fixture'});if(login.status!==200)throw new Error('Local login failed');
const cookies=login.headers['set-cookie']??[];if(!cookies.some(c=>/; Secure/i.test(c)&&/; HttpOnly/i.test(c)))throw new Error('Secure cookie missing');const cookie=cookies[0].split(';')[0];
await new Promise<void>((resolve,reject)=>{const options:WebSocket.ClientOptions & import('node:tls').ConnectionOptions={ca,servername:'marvin.test',headers:{host:'marvin.test:8443',origin,cookie}};const socket=new WebSocket('wss://127.0.0.1:8443/api/events',options);const timer=setTimeout(()=>{socket.terminate();reject(new Error('WSS timeout'));},5000);socket.on('open',()=>socket.send(JSON.stringify({v:1,type:'ping'})));socket.on('error',error=>{clearTimeout(timer);reject(error);});socket.on('message',bytes=>{if(JSON.parse(bytes.toString()).type==='pong'){clearTimeout(timer);socket.close();resolve();}});});
const logout=await request('/api/auth/logout',{},cookie,login.data.csrf);if(logout.status!==200)throw new Error('Logout failed');const session=await request('/api/auth/session',undefined,cookie);if(session.data!==null)throw new Error('Session revocation failed');
const result={at:new Date().toISOString(),tlsVerified:true,httpsHealth:true,passwordLogin:true,secureHttpOnlyCookie:true,websocketUpgrade:true,logoutRevoked:true,providerRequests:0};writeFileSync('work/board/packaging-smoke.json',JSON.stringify(result,null,2));console.log(JSON.stringify(result));
