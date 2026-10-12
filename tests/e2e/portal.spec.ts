import { test,expect,type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
async function login(page:Page){await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();await expect(page.getByLabel('Message Marvin')).toBeVisible();}
async function fresh(page:Page){await login(page);if(await page.getByLabel('Open navigation').isVisible())await page.getByLabel('Open navigation').click();await page.getByLabel('New conversation',{exact:true}).click();await expect(page.getByText('What’s on your mind?')).toBeVisible();}
async function audit(page:Page){await page.evaluate(()=>Promise.all(document.getAnimations().filter(animation=>Number.isFinite(animation.effect?.getComputedTiming().endTime??Infinity)).map(animation=>animation.finished.catch(()=>undefined))));const report=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();expect(report.violations.filter(v=>['critical','serious'].includes(v.impact??'')).map(v=>({id:v.id,nodes:v.nodes.map(n=>n.target)}))).toEqual([]);}
// The portal's browser tests are split over portal*.spec.ts: each file gets its own server and its 200 API requests a
// minute (scripts/browser-tests.ts), and together these tests make about 190, enough to reach the limit.
for(const width of [360,768,1440])test(`responsive portal and accessibility at ${width}px`,async({page})=>{
 await page.setViewportSize({width,height:1000});await page.goto('/login');await expect(page.getByRole('button',{name:'Enter local preview'})).toBeVisible();await audit(page);await fresh(page);await audit(page);
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);await page.getByLabel('Active repository').click();await expect(page.getByRole('combobox',{name:'Find repositories'})).toBeFocused();await audit(page);const popup=await page.locator('.repository-picker-popup').boundingBox();expect(popup!.x+popup!.width).toBeLessThanOrEqual(width);await page.screenshot({path:`work/screenshots/repository-picker-${width}.png`});await page.keyboard.press('Escape');await expect(page.getByLabel('Active repository')).toBeFocused();
 await page.screenshot({path:`work/screenshots/portal-${width}.png`,fullPage:true});
 await page.getByRole('button',{name:'Account menu'}).click();await page.getByLabel('Open settings',{exact:true}).click();await audit(page);expect(await page.evaluate(()=>document.querySelector('dialog')!.scrollWidth<=document.querySelector('dialog')!.clientWidth)).toBe(true);
 await page.screenshot({path:`work/screenshots/settings-connections-${width}.png`});
 for(const section of ['Voice and models','Privacy and history']){await page.getByRole('button',{name:section,exact:true}).click();await expect(page.getByRole('heading',{name:section,exact:true})).toBeVisible();await audit(page);await page.screenshot({path:`work/screenshots/settings-${section.split(' ')[0].toLowerCase()}-${width}.png`});}
 await page.getByRole('button',{name:'Desktop Pet',exact:true}).click();await audit(page);await page.screenshot({path:`work/screenshots/settings-pet-${width}.png`});await page.keyboard.press('Escape');await expect(page.getByRole('dialog')).not.toBeVisible();await expect(page.getByRole('button',{name:'Account menu'})).toBeFocused();
});
test('unconfigured setup fails closed without attempting Bluetooth',async({page})=>{
 await page.addInitScript(()=>{Object.defineProperty(navigator,'bluetooth',{value:{requestDevice:()=>{throw new Error('Bluetooth must not be called');}},configurable:true});});
 await fresh(page);await page.getByRole('button',{name:'Have a Desktop Pet?'}).click();const dialog=page.getByRole('dialog');await expect(dialog).toContainText('Secure setup is not configured');await expect(dialog).toContainText('no Bluetooth pairing was attempted');await audit(page);await page.getByLabel('Close dialog').click();await expect(dialog).not.toBeVisible();await expect(page.getByText('What’s on your mind?')).toBeVisible();
});
test('unsupported Bluetooth, inactive microphone, and malicious text have truthful safe UX',async({page})=>{
 await page.addInitScript(()=>{Object.defineProperty(navigator,'bluetooth',{value:undefined,configurable:true});});
 await fresh(page);await page.getByRole('button',{name:'Have a Desktop Pet?'}).click();await expect(page.getByRole('dialog')).toBeVisible();await page.getByLabel('Close dialog').click();await expect(page.getByRole('dialog')).not.toBeVisible();const voice=page.getByLabel('Start live voice conversation');if(await voice.isDisabled())await expect(voice).toHaveAttribute('title','Live voice is not configured');
 await page.getByLabel('Message Marvin').fill('<img src=x onerror="window.compromised=true">');await page.getByRole('button',{name:'Send message'}).click();await expect(page.locator('.user-message')).toContainText('<img');expect(await page.evaluate(()=>('compromised' in window))).toBe(false);await expect(page.locator('.messages img')).toHaveCount(0);
});
