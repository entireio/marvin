import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

test('connected Pet audio controls stay synchronized between the header and Settings',async({page})=>{
 let audio={volume:60,muted:false};
 await page.route('**/api/settings',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),body:{device_id:'00000000-0000-4000-8000-000000000004',network:'Studio',simulated:0}}});});
 await page.route('**/api/robot/presence',route=>route.fulfill({json:{body:{device_id:'00000000-0000-4000-8000-000000000004',network:'Studio',simulated:0},presence:{deviceId:'00000000-0000-4000-8000-000000000004',status:'online',capabilities:['voice'],audioSettings:audio}}}));
 await page.route('**/api/robot/audio',async route=>{audio=JSON.parse(route.request().postData()??'{}');await route.fulfill({json:audio});});
 await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();
 const headerVolume=page.getByLabel('Desktop Pet volume').first();await expect(headerVolume).toBeVisible();await headerVolume.fill('35');
 await page.getByRole('button',{name:'Mute Desktop Pet microphone'}).first().click();await expect.poll(()=>audio).toEqual({volume:35,muted:true});
 await page.getByRole('button',{name:'Desktop Pet Online'}).click();const dialog=page.getByRole('dialog');await expect(dialog.getByText('Synced with your Desktop Pet')).toBeVisible();await expect(dialog.getByLabel('Desktop Pet volume')).toHaveValue('35');await expect(dialog.getByRole('button',{name:'Unmute Desktop Pet microphone'})).toBeVisible();
 const report=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();expect(report.violations.filter(v=>['critical','serious'].includes(v.impact??'')).map(v=>v.id)).toEqual([]);
});
