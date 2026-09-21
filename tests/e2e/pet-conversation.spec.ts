import { test,expect,type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
async function fresh(page:Page){await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();await expect(page.getByLabel('Message Marvin')).toBeVisible();if(await page.getByLabel('Open navigation').isVisible())await page.getByLabel('Open navigation').click();await page.getByLabel('New conversation',{exact:true}).click();await expect(page.getByText('What’s on your mind?')).toBeVisible();}
async function audit(page:Page){await page.evaluate(()=>Promise.all(document.getAnimations().filter(animation=>Number.isFinite(animation.effect?.getComputedTiming().endTime??Infinity)).map(animation=>animation.finished.catch(()=>undefined))));const report=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();expect(report.violations.filter(v=>['critical','serious'].includes(v.impact??'')).map(v=>({id:v.id,nodes:v.nodes.map(n=>n.target)}))).toEqual([]);}
test('one-click Desktop Pet link follows the chosen conversation independently of viewing it',async({page})=>{
 await fresh(page);
 const selected=page.locator('.history-row').filter({has:page.locator('.history-conversation.active')});
 const firstId=await selected.getAttribute('data-conversation-id');
 expect(firstId).toBeTruthy();
 const first=page.locator(`.history-row[data-conversation-id="${firstId}"]`);
 await first.getByRole('button',{name:/Link Desktop Pet to/}).click();
 await expect(first.getByRole('button',{name:/Remove Desktop Pet link/})).toHaveAttribute('aria-pressed','true');
 await page.getByLabel('New conversation',{exact:true}).click();
 const secondSelected=page.locator('.history-row').filter({has:page.locator('.history-conversation.active')});
 const secondId=await secondSelected.getAttribute('data-conversation-id');
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
