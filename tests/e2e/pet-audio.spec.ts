import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

test('connected Pet audio controls stay synchronized and speech lives in the remote',async({page})=>{
 let audio={volume:60,muted:false,microphoneGainDb:30,allowPlaybackMic:false,followupSeconds:5},battery={levelPercent:74,voltageMv:3950,charging:null as boolean|null};
 let calibration={yawCenter:90,pitchCenter:90,yawReversed:true,pitchReversed:true};
 const speeches:string[]=[];
 await page.route('**/api/config',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),voiceAvailable:true,voiceStatus:'ready'}});});
 await page.route('**/api/settings',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),body:{device_id:'00000000-0000-4000-8000-000000000004',network:'Studio',simulated:0}}});});
 await page.route('**/api/robot/presence',route=>route.fulfill({json:{body:{device_id:'00000000-0000-4000-8000-000000000004',network:'Studio',simulated:0},presence:{deviceId:'00000000-0000-4000-8000-000000000004',status:'online',capabilities:['voice','head'],audioSettings:audio,headCalibration:calibration,batteryStatus:battery}}}));
 await page.route('**/api/robot/audio',async route=>{audio=JSON.parse(route.request().postData()??'{}');await route.fulfill({json:audio});});
 await page.route('**/api/robot/head-calibration',async route=>{calibration=JSON.parse(route.request().postData()??'{}');await route.fulfill({json:calibration});});
 await page.route('**/api/robot/speak',async route=>{speeches.push(JSON.parse(route.request().postData()??'{}').text);await route.fulfill({json:{accepted:true,state:'playing',requestId:'00000000-0000-4000-8000-000000000099'}});});
 await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();
 const headerVolume=page.getByLabel('Desktop Pet volume').first();await expect(headerVolume).toBeVisible();await headerVolume.fill('35');
 await page.getByRole('button',{name:'Mute Desktop Pet microphone'}).first().click();await expect.poll(()=>audio).toEqual({volume:35,muted:true,microphoneGainDb:30,allowPlaybackMic:false,followupSeconds:5});
 await page.getByRole('button',{name:'Desktop Pet Online'}).click();const dialog=page.getByRole('dialog');await expect(dialog.getByText('Synced with your Desktop Pet')).toBeVisible();await expect(dialog.getByLabel('Desktop Pet volume')).toHaveValue('35');await expect(dialog.getByRole('button',{name:'Unmute Desktop Pet microphone'})).toBeVisible();
 await expect(dialog.getByLabel('What should Marvin say?')).toHaveCount(0);
 await expect(page.getByRole('banner').getByLabel('Desktop Pet microphone sensitivity')).toHaveCount(0);await dialog.getByLabel('Desktop Pet microphone sensitivity').fill('12');await expect.poll(()=>audio).toEqual({volume:35,muted:true,microphoneGainDb:12,allowPlaybackMic:false,followupSeconds:5});
 await dialog.getByLabel('Desktop Pet follow-up window').fill('8');await expect.poll(()=>audio.followupSeconds).toBe(8);await dialog.getByLabel('Allow Desktop Pet microphone during playback').check();await expect.poll(()=>audio.allowPlaybackMic).toBe(true);
 await expect(dialog.getByText('Head servo calibration')).toBeVisible();await dialog.getByLabel('Head left and right center').fill('84');await dialog.getByLabel('Reverse left and right').uncheck();await dialog.getByRole('button',{name:'Save and center head'}).click();await expect.poll(()=>calibration).toEqual({yawCenter:84,pitchCenter:90,yawReversed:false,pitchReversed:true});await expect(dialog.getByText('Calibration saved on your Desktop Pet.')).toBeVisible();
 await dialog.getByRole('button',{name:'Close dialog'}).click();await expect(dialog).toBeHidden();await page.getByRole('button',{name:'Open remote control'}).click();const remote=page.getByRole('dialog',{name:'Remote control'});await expect(remote.getByText('Make Marvin say something')).toBeVisible();
 const speech=remote.getByLabel('What should Marvin say?');await speech.fill('First line');await speech.press('Shift+Enter');await speech.type('Second line');await speech.press('Meta+Enter');await speech.type('Third line');await expect(speech).toHaveValue('First line\nSecond line\nThird line');await speech.press('Enter');await expect.poll(()=>speeches).toEqual(['First line\nSecond line\nThird line']);await expect(speech).toHaveValue('');
 const report=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();expect(report.violations.filter(v=>['critical','serious'].includes(v.impact??'')).map(v=>v.id)).toEqual([]);
});
