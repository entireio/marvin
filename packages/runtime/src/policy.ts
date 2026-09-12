import { z } from 'zod';
import { DomainError, type InteractionContext } from '../../contracts/src/index.js';
export const tools={
 repository_summary:{name:'repository_summary',description:'Read a bounded repository summary from the authorized active repository. Returns explicitly labeled sample data when using fixtures.',parameters:{type:'object',properties:{},additionalProperties:false,required:[]}},
 physical_head:{name:'physical_head',description:'Request a bounded head gesture through the originating physical Marvin.',parameters:{type:'object',properties:{},additionalProperties:false,required:[]}}
};
export function allowedTools(ctx:InteractionContext){
 const result=[];
 if(ctx.entireState!=='disconnected' && ctx.repositoryId) result.push(tools.repository_summary);
 if(ctx.surface==='body_voice' && ctx.body?.status==='online' && ctx.body.capabilities.includes('head')) result.push(tools.physical_head);
 return result;
}
export function authorizeTool(ctx:InteractionContext,name:string,args:unknown){
 if(!allowedTools(ctx).some(t=>t.name===name)) throw new DomainError('TOOL_FORBIDDEN','This action is not available in this interaction.',403);
 z.object({}).strict().parse(args);
}
export function bodyExplanation(ctx:InteractionContext){return !ctx.body?'No physical Marvin is linked. You can set one up in Settings → Your Marvin.':ctx.body.status==='offline'?'Your Marvin is offline. You can check its connection in Settings → Your Marvin.':'Ask me through the Marvin on your desk for physical actions. Web conversations cannot move the robot.';}
