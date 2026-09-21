// Explicit local QA mode only; excluded from production by the DEV constant.
import axe from 'axe-core';
export function installAudit(){
 const button=document.createElement('button');button.textContent='Run accessibility audit';button.className='button secondary';button.style.cssText='position:fixed;right:12px;bottom:8px;z-index:500;font-size:11px';
 const run=async()=>{const result=await axe.run(document);const violations=result.violations.filter(v=>v.impact==='serious'||v.impact==='critical');console.info('MARVIN_ACCESSIBILITY '+JSON.stringify({url:location.pathname,viewport:{width:innerWidth,height:innerHeight},violations:violations.map(v=>({id:v.id,description:v.description,nodes:v.nodes.map(n=>({target:n.target,summary:n.failureSummary}))})),passes:result.passes.length}));button.textContent=violations.length?`${violations.length} accessibility issues`:'Accessibility audit passed';};
 button.onclick=()=>void run();document.body.append(button);document.addEventListener('keydown',e=>{if(e.altKey&&e.shiftKey&&e.code==='KeyA')void run();});
}
