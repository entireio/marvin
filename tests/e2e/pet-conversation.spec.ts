import { test,expect,type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
// New conversation creates and selects it asynchronously, and the app's first conversation is already empty: take the
// created conversation's id from the response and wait until its row is the selected one.
async function newConversation(page:Page){const created=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/conversations'&&r.request().method()==='POST');await page.getByLabel('New conversation',{exact:true}).click();const {id}=await (await created).json() as {id:string};await expect(page.locator(`.history-row[data-conversation-id="${id}"] .history-conversation`)).toHaveClass(/\bactive\b/);return id;}
async function fresh(page:Page){await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();await expect(page.getByLabel('Message Marvin')).toBeVisible();if(await page.getByLabel('Open navigation').isVisible())await page.getByLabel('Open navigation').click();const id=await newConversation(page);await expect(page.getByText('What’s on your mind?')).toBeVisible();return id;}
async function audit(page:Page){await page.evaluate(()=>Promise.all(document.getAnimations().filter(animation=>Number.isFinite(animation.effect?.getComputedTiming().endTime??Infinity)).map(animation=>animation.finished.catch(()=>undefined))));const report=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();expect(report.violations.filter(v=>['critical','serious'].includes(v.impact??'')).map(v=>({id:v.id,nodes:v.nodes.map(n=>n.target)}))).toEqual([]);}
test('one-click Desktop Pet link follows the chosen conversation independently of viewing it',async({page})=>{
 const firstId=await fresh(page);
 const first=page.locator(`.history-row[data-conversation-id="${firstId}"]`);
 await first.getByRole('button',{name:/Link Desktop Pet to/}).click();
 await expect(first.getByRole('button',{name:/Remove Desktop Pet link/})).toHaveAttribute('aria-pressed','true');
 const secondId=await newConversation(page);
 expect(secondId).not.toBe(firstId);
 const second=page.locator(`.history-row[data-conversation-id="${secondId}"]`);
 await expect(second.getByRole('button',{name:/Link Desktop Pet to/})).toHaveAttribute('aria-pressed','false');
 await second.getByRole('button',{name:/Link Desktop Pet to/}).click();
 await expect(second.getByRole('button',{name:/Remove Desktop Pet link/})).toHaveAttribute('aria-pressed','true');
 await expect(first.getByRole('button',{name:/Link Desktop Pet to/})).toHaveAttribute('aria-pressed','false');
 await page.reload();
 await expect(second.getByRole('button',{name:/Remove Desktop Pet link/})).toBeVisible();
 await audit(page);
});
