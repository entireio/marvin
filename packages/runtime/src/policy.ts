import { Actions } from '../../contracts/src/device.js';
import { z } from 'zod';
import { DomainError, type InteractionContext } from '../../contracts/src/index.js';
const query=z.string().trim().min(1).max(300).refine(s=>!s.startsWith('-')&&!/(?:^|\s)(?:repo|author|branch|date)\s*:/i.test(s),'Use a plain query without scope filters.');
export const repositoryArguments={
 repository_summary:z.object({}).strict(),
 repository_search:z.object({query,mode:z.enum(['code','history']),page:z.number().int().min(1).max(20)}).strict(),
 repository_source:z.object({path:z.string().min(1).max(300),start:z.number().int().min(1).max(100000),end:z.number().int().min(1).max(100000)}).strict().refine(v=>v.end>=v.start&&v.end-v.start<160,'Read at most160 lines.'),
 repository_history:z.object({}).strict(),
 repository_checkpoint:z.object({id:z.string().regex(/^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/)}).strict(),
 repository_session:z.object({id:z.string().regex(/^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/)}).strict(),
 repository_graph:z.object({query}).strict()
};
export function validateRepositoryArguments(name:keyof typeof repositoryArguments,args:unknown){
 const parsed=repositoryArguments[name].safeParse(args);
 if(!parsed.success)throw new DomainError('TOOL_ARGUMENTS',name==='repository_source'?'Invalid source range or arguments. Provide a relative file path and integer start/end lines, with 1 <= start <= end and end - start < 160. For a first read use start=1, end=160; request the next range separately.':'Invalid repository tool arguments. Retry using the tool schema; use plain search text without scope filters.',400);
 return parsed.data;
}
const params=(properties:Record<string,unknown>,required:string[])=>({type:'object',properties,required,additionalProperties:false});
export const tools={
 repository_search:{name:'repository_search',description:'Search the authorized repository with Entire. Code search matches literal text: use a short code identifier or single distinctive keyword, not a natural-language question. It returns indexed exact-text matches; history returns paginated checkpoint/session/commit excerpts. Do not assume index freshness.',parameters:params({query:{type:'string',maxLength:300},mode:{type:'string',enum:['code','history']},page:{type:'integer',minimum:1,maximum:20}},['query','mode','page'])},
 repository_source:{name:'repository_source',description:'Read committed source after locating a path. Use start=1, end=160 for a first read; end-start MUST be less than160. Read up to160 lines from a regular file in the committed local checkout. Result includes the exact revision. Secret paths, binaries and symlinks are excluded.',parameters:params({path:{type:'string'},start:{type:'integer',minimum:1},end:{type:'integer',minimum:1}},['path','start','end'])},
 repository_history:{name:'repository_history',description:'List up to10 stored Entire checkpoints for the current local branch. An empty list is not evidence of no development activity.',parameters:params({},[])},
 repository_checkpoint:{name:'repository_checkpoint',description:'Read stored metadata and summaries for a complete checkpoint ID returned by history. Does not generate summaries or retrieve full transcripts.',parameters:params({id:{type:'string'}},['id'])},
 repository_session:{name:'repository_session',description:'Read bounded local Entire session metadata by complete ID from a checkpoint; excludes transcripts.',parameters:params({id:{type:'string'}},['id'])},
 repository_graph:{name:'repository_graph',description:'Search committed-tree code relationships with the configured Entire graph plugin.',parameters:params({query:{type:'string',maxLength:300}},['query'])},
 repository_summary:{name:'repository_summary',description:'Read a bounded repository summary from the authorized active repository. Returns explicitly labeled sample data when using fixtures.',parameters:{type:'object',properties:{},additionalProperties:false,required:[]}},
 physical_eyes:{name:'physical_eyes',description:'Set this robot’s expression. A sent command is not proof of physical completion.',parameters:z.toJSONSchema(Actions.eyes)},
 physical_gaze:{name:'physical_gaze',description:'Request bounded eye gaze on the originating robot.',parameters:z.toJSONSchema(Actions.gaze)},
 physical_head:{name:'physical_head',description:'Request a bounded head movement on the originating robot. Report the returned status accurately; sent does not mean completed.',parameters:z.toJSONSchema(Actions.head)},
 physical_tracks:{name:'physical_tracks',description:'Request slow, brief track motion. Local safety may refuse it. Never claim completion from a sent status.',parameters:z.toJSONSchema(Actions.tracks)}
};
export function allowedTools(ctx:InteractionContext){
 const result=[];
 if(ctx.entireState==='fixture' && ctx.repositoryId) result.push(tools.repository_summary);
 if(ctx.entireState==='connected'&&ctx.repositoryId)for(const capability of ctx.repositoryCapabilities??[]){const key=('repository_'+(capability==='code_search'?'search':capability)) as keyof typeof tools; if(key in tools&&!result.includes(tools[key]))result.push(tools[key]);}
 if(ctx.surface==='body_voice'&&ctx.body?.status==='online')for(const action of ['eyes','gaze','head','tracks'] as const)if(ctx.body.capabilities.includes(action))result.push(tools[`physical_${action}`]);
 return result;
}
export function authorizeTool(ctx:InteractionContext,name:string,args:unknown){
 if(!allowedTools(ctx).some(t=>t.name===name)) throw new DomainError('TOOL_FORBIDDEN','This action is not available in this interaction.',403);
 if(name in repositoryArguments)validateRepositoryArguments(name as keyof typeof repositoryArguments,args);else if(name.startsWith('physical_'))Actions[name.slice(9) as keyof typeof Actions]?.parse(args);else z.object({}).strict().parse(args);
}
export function bodyExplanation(ctx:InteractionContext){return !ctx.body?'No physical Marvin is linked. You can set one up in Settings → Your Marvin.':ctx.body.status==='offline'?'Your Marvin is offline. You can check its connection in Settings → Your Marvin.':ctx.surface==='body_voice'?'This interaction originates from your Marvin. Only its reported physical capabilities are available; local safety can refuse actions.':'Ask me through the Marvin on your desk for physical actions. Web conversations cannot move the robot.';}
