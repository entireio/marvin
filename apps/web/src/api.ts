export let csrf='';
export function setCsrf(value:string){csrf=value;}
export class ApiError extends Error {constructor(public code:string,message:string,public status:number){super(message);}}
export async function api<T>(path:string,method='GET',body?:unknown):Promise<T>{
 const res=await fetch('/api'+path,{method,headers:{...(body!==undefined?{'Content-Type':'application/json'}:{}),...(method!=='GET'?{'X-CSRF-Token':csrf}:{})},body:body===undefined?undefined:JSON.stringify(body),credentials:'same-origin'});
 const data=await res.json();if(!res.ok)throw new ApiError(data.error?.code??'ERROR',data.error?.message??'Something went wrong. Try again.',res.status);return data as T;
}
