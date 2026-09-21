/** Visual/accessibility checks for the real setup entry; Bluetooth is an explicit chooser fixture. */
import {mkdir,writeFile} from 'node:fs/promises';
import {chromium} from 'playwright';
import AxeBuilder from '@axe-core/playwright';
import {config} from '../apps/server/src/config.js';
import {createApp} from '../apps/server/src/app.js';
import {sqliteDatabase} from '../packages/persistence/src/database.js';
const origin='http://127.0.0.1:4824',directory='work/m7/physical-ui';await mkdir(directory,{recursive:true});const service=await createApp(config({NODE_ENV:'test',APP_ORIGIN:origin}),{database:sqliteDatabase()});const results:unknown[]=[];let browser:Awaited<ReturnType<typeof chromium.launch>>|undefined;
try{
 await service.app.listen({host:'127.0.0.1',port:4824});browser=await chromium.launch({channel:'chrome',headless:true});const context=await browser.newContext(),owner=await service.store.ensureOwner('test','physical-ui','Setup test'),session=await service.store.createSession(owner.id);await context.addCookies([{name:'marvin_session',value:session.token,url:origin,httpOnly:true,sameSite:'Lax'}]);
 await context.addInitScript({content:"Object.defineProperty(navigator,'bluetooth',{value:{requestDevice:async()=>{throw new DOMException('Chooser cancelled','NotFoundError');}},configurable:true});"});const page=await context.newPage();page.on('pageerror',error=>console.log('Browser error:',error.message));
 await page.route('**/api/config',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),hardwareProvisioningAvailable:true}});});
 for(const width of [360,768,1440]){
  await page.setViewportSize({width,height:1000});await page.goto(origin+'/app');await page.getByLabel('Message Marvin').waitFor();await page.getByRole('button',{name:'Open settings',exact:true}).click();await page.getByRole('button',{name:'Your Marvin',exact:true}).click();await page.getByRole('button',{name:'Set up your Marvin',exact:true}).click();await page.getByLabel('Marvin setup code').waitFor();const audit=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();const serious=audit.violations.filter(v=>['serious','critical'].includes(v.impact??''));const overflow=await page.evaluate(()=>document.querySelector('dialog')!.scrollWidth>document.querySelector('dialog')!.clientWidth);
  await page.screenshot({path:`${directory}/entry-${width}.png`,fullPage:true});await page.getByLabel('Marvin setup code').fill('invalid-code');await page.getByRole('button',{name:'Connect Marvin',exact:true}).click();await page.getByRole('alert').filter({hasText:'complete setup code'}).waitFor();await page.keyboard.press('Escape');await page.getByLabel('Message Marvin').waitFor();results.push({width,seriousAccessibilityIssues:serious.map(v=>v.id),overflow,invalidCodeRecovery:true,escapeReturnsToChat:true});if(serious.length||overflow)throw new Error('Physical setup visual/accessibility failure');
 }
}finally{await browser?.close();await service.app.close();await writeFile(directory+'/results.json',JSON.stringify({at:new Date().toISOString(),results,scope:'Real owner setup entry and error UI in headless Chrome; explicit Bluetooth chooser fixture and server feature flag interception. No hardware or Wi-Fi credentials used.'},null,2));}console.log(JSON.stringify(results));
