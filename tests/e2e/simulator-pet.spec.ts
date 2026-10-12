import {test,expect} from '@playwright/test';
import {DeviceSimulator} from '../../packages/device/src/simulator.js';
import {spawn,type ChildProcess} from 'node:child_process';
import {mkdirSync,writeFileSync,readFileSync,existsSync,rmSync} from 'node:fs';
import {resolve} from 'node:path';
import {randomUUID} from 'node:crypto';

async function issue(page:import('@playwright/test').Page){
 await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();
 await page.getByRole('button',{name:'No Desktop Pet? Use simulator'}).click();await page.getByRole('button',{name:'Create pairing code'}).click();
 await page.getByLabel('Simulator pairing code').waitFor();
 await page.screenshot({path:'work/simulator-pairing-desktop.png'});
 await page.emulateMedia({colorScheme:'dark'});await page.setViewportSize({width:390,height:844});
 await page.screenshot({path:'work/simulator-pairing-mobile-dark.png'});
 await page.emulateMedia({colorScheme:'light'});await page.setViewportSize({width:1280,height:720});
 const code=await page.getByLabel('Simulator pairing code').inputValue(),serverUrl=await page.getByLabel('Marvin server').inputValue();
 return {serverUrl,code};
}
async function pairing(page:import('@playwright/test').Page){
 const {serverUrl,code}=await issue(page);
 const response=await fetch(serverUrl+'/api/device/simulator/pair',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({code})});
 expect(response.status).toBe(200);return {serverUrl,...await response.json() as {deviceId:string;credential:string;credentialExpiresAt:number}};
}
async function unlink(page:import('@playwright/test').Page){
 await page.evaluate(async()=>{ const {csrf}=await (await fetch('/api/auth/session')).json();await fetch('/api/robot',{method:'DELETE',headers:{'x-csrf-token':csrf}}); });
}
test('a connected simulator uses the existing remote and settings UI',async({page})=>{
 const binding=await pairing(page),simulator=new DeviceSimulator(binding.deviceId,randomUUID(),['remote','head','eyes']);
 try {
  await simulator.connect(binding.serverUrl.replace('http','ws')+'/api/device/socket',binding.credential,13);
  await expect(page.getByRole('dialog',{name:'Pair simulator'})).toHaveCount(0);
  await expect(page.getByRole('button',{name:'Simulator Online'})).toBeVisible();
  await page.getByRole('button',{name:'Open remote control'}).click();
  const stick=page.getByRole('group',{name:'Drive joystick'}),box=await stick.boundingBox();if(!box)throw new Error('Missing drive stick');
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();await page.mouse.move(box.x+box.width/2,box.y+box.height*.2);
  await expect.poll(()=>simulator.executions).toBeGreaterThan(0);await page.mouse.up();
  await page.getByRole('button',{name:'Close remote control'}).click();await page.getByRole('button',{name:'Simulator Online'}).click();
  await expect(page.getByRole('button',{name:'Change Wi-Fi'})).toHaveCount(0);
  await page.getByLabel('Desktop Pet eye design').selectOption('solid');await expect.poll(()=>simulator.eyeSettings.design).toBe('solid');
  await page.getByLabel('Show Desktop Pet battery icon').check();await expect.poll(()=>simulator.displaySettings.showBatteryIcon).toBe(true);
  await page.screenshot({path:'work/simulator-pet-web.png',fullPage:true});
  await page.getByRole('button',{name:'Unlink Desktop Pet',exact:true}).click();await page.getByRole('button',{name:'Unlink Desktop Pet',exact:true}).click();
  await expect.poll(()=>simulator.linked).toBe(false);
 } finally { await unlink(page).catch(()=>{});simulator.close() }
});

test('the Swift app visibly drives while the web app owns focus and stops after remote loss',async({page})=>{
 test.skip(!process.env.MARVIN_NATIVE_PET_APP,'Requires the built native Swift app and a macOS display session.');test.setTimeout(45000);
 const code=await issue(page),folder=resolve('work/native-pet-'+randomUUID());mkdirSync(folder,{recursive:true,mode:0o700});
 const file=resolve(folder,'pairing.json'),receipt=resolve(folder,'binding.json');writeFileSync(file,JSON.stringify(code),{mode:0o600});
 let child:ChildProcess|undefined;
 const stopNative=async()=>{if(child&&child.exitCode===null)await new Promise<void>(done=>{child!.once('exit',()=>done());child!.kill('SIGTERM');});};
 const state=()=>JSON.parse(readFileSync(resolve(folder,'state.json'),'utf8')) as {online:boolean;active:boolean;mode:boolean;x:number;z:number;speed:number;yaw:number;eyeDesign:string;showBatteryIcon:boolean};
 try {
  child=spawn(process.env.MARVIN_NATIVE_PET_APP!,['--pet-smoke-test',folder,'--pet-test-pairing',file],{stdio:'ignore'});
  await expect.poll(()=>existsSync(resolve(folder,'state.json'))&&state().online,{timeout:15000}).toBe(true);
  await expect(page.getByRole('dialog',{name:'Pair simulator'})).toHaveCount(0);
  await expect(page.getByRole('button',{name:'Simulator Online'})).toBeVisible();
  await page.bringToFront();await page.getByRole('button',{name:'Open remote control'}).click();
  const initial=state(),stick=page.getByRole('group',{name:'Drive joystick'}),box=await stick.boundingBox();if(!box)throw new Error('Missing drive stick');
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();await page.mouse.move(box.x+box.width/2,box.y+box.height*.2);
  await expect.poll(()=>state().z-initial.z).toBeGreaterThan(.15);
  if(!test.info().project.use.headless)await expect.poll(()=>state().active).toBe(false);
  await page.mouse.up();await expect.poll(()=>Math.abs(state().speed)).toBeLessThan(.01);
  const head=page.getByRole('group',{name:'Head joystick'}),headBox=await head.boundingBox();if(!headBox)throw new Error('Missing head stick');
  await page.mouse.move(headBox.x+headBox.width/2,headBox.y+headBox.height/2);await page.mouse.down();await page.mouse.move(headBox.x+headBox.width*.8,headBox.y+headBox.height/2);
  await expect.poll(()=>Math.abs(state().yaw)).toBeGreaterThan(.2);await page.mouse.up();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();await page.mouse.move(box.x+box.width/2,box.y+box.height*.2);
  await expect.poll(()=>Math.abs(state().speed)).toBeGreaterThan(.05);await page.getByRole('button',{name:'Close remote control'}).press('Enter');await page.mouse.up();
  await expect(page.getByRole('dialog',{name:'Remote control'})).toHaveCount(0);
  await expect.poll(()=>Math.abs(state().speed)).toBeLessThan(.01);
  writeFileSync(resolve(folder,'capture.request'),'');await expect.poll(()=>existsSync(resolve(folder,'pet.png'))).toBe(true);
  await page.getByRole('button',{name:'Simulator Online'}).click();
  await page.getByLabel('Desktop Pet eye design').selectOption('solid');await expect.poll(()=>state().eyeDesign).toBe('solid');
  await page.getByLabel('Show Desktop Pet battery icon').check();await expect.poll(()=>state().showBatteryIcon).toBe(true);
  rmSync(resolve(folder,'pet.png'),{force:true});writeFileSync(resolve(folder,'capture.request'),'');
  await expect.poll(()=>existsSync(resolve(folder,'pet.png'))).toBe(true);
  await page.screenshot({path:resolve(folder,'web.png'),fullPage:true});
  await stopNative();rmSync(resolve(folder,'state.json'),{force:true});
  child=spawn(process.env.MARVIN_NATIVE_PET_APP!,['--pet-smoke-test',folder,'--pet-test-binding',receipt],{stdio:'ignore'});
  await expect.poll(()=>existsSync(resolve(folder,'state.json'))&&state().online,{timeout:15000}).toBe(true);
  expect(state().eyeDesign).toBe('solid');expect(state().showBatteryIcon).toBe(true);expect(state().speed).toBe(0);
  await unlink(page);await expect.poll(()=>state().online).toBe(false);
  console.log('Native simulator evidence:',folder);
 } finally { await unlink(page).catch(()=>{});await stopNative();rmSync(file,{force:true});rmSync(receipt,{force:true}); }
});
