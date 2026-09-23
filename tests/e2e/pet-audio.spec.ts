import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

test('an expired API session returns to sign-in instead of reporting the Pet offline',async({page})=>{
 const body={device_id:'00000000-0000-4000-8000-000000000099',network:'Studio',simulated:0};let checks=0;
 await page.route('**/api/settings',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),body}});});
 await page.route('**/api/robot/presence',route=>{checks++;return checks===1?route.fulfill({json:{body,presence:{deviceId:body.device_id,status:'online',capabilities:[],audioSettings:null,headCalibration:null,headCalibrationPreview:false,batteryStatus:null,eyeSettings:null}}}):route.fulfill({status:401,json:{error:{code:'UNAUTHENTICATED',message:'Please sign in to continue.'}}});});
 await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();await expect(page.getByRole('button',{name:'Desktop Pet Online'})).toBeVisible();await expect(page.getByRole('link',{name:'Continue with GitHub'})).toHaveCount(0);await expect(page.getByText('Your session expired. Sign in to continue.')).toBeVisible({timeout:5000});await expect(page.getByRole('button',{name:'Desktop Pet Offline'})).toHaveCount(0);
});

test('connected Pet audio controls stay synchronized and speech lives in the remote',async({page})=>{
 let audio={volume:60,muted:false,microphoneGainDb:30,allowPlaybackMic:false,followupSeconds:5},battery={levelPercent:74,voltageMv:3950,charging:null as boolean|null};
 let calibration={yawCenter:90,pitchCenter:90,yawReversed:true,pitchReversed:true};
 const calibrationPreviews:typeof calibration[]=[];
 let eyes={design:'classic'};
 const speeches:string[]=[];
 await page.route('**/api/config',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),voiceAvailable:true,voiceStatus:'ready'}});});
 await page.route('**/api/settings',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),body:{device_id:'00000000-0000-4000-8000-000000000004',network:'Studio',simulated:0}}});});
 await page.route('**/api/robot/presence',route=>route.fulfill({json:{body:{device_id:'00000000-0000-4000-8000-000000000004',network:'Studio',simulated:0},presence:{deviceId:'00000000-0000-4000-8000-000000000004',status:'online',capabilities:['voice','head','eyes'],audioSettings:audio,headCalibration:calibration,headCalibrationPreview:true,batteryStatus:battery,eyeSettings:eyes}}}));
 await page.route('**/api/robot/audio',async route=>{audio=JSON.parse(route.request().postData()??'{}');await route.fulfill({json:audio});});
 await page.route('**/api/robot/head-calibration',async route=>{calibration=JSON.parse(route.request().postData()??'{}');await route.fulfill({json:calibration});});
 await page.route('**/api/robot/head-calibration/preview',async route=>{calibrationPreviews.push(JSON.parse(route.request().postData()??'{}'));await route.fulfill({json:{accepted:true}});});
 await page.route('**/api/robot/eyes',async route=>{eyes=JSON.parse(route.request().postData()??'{}');await route.fulfill({json:eyes});});
 await page.route('**/api/robot/speak',async route=>{speeches.push(JSON.parse(route.request().postData()??'{}').text);await route.fulfill({json:{accepted:true,state:'playing',requestId:'00000000-0000-4000-8000-000000000099'}});});
 await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();
 const headerVolume=page.getByLabel('Desktop Pet volume').first();await expect(headerVolume).toBeVisible();await headerVolume.fill('35');
 await page.getByRole('button',{name:'Mute Desktop Pet microphone'}).first().click();await expect.poll(()=>audio).toEqual({volume:35,muted:true,microphoneGainDb:30,allowPlaybackMic:false,followupSeconds:5});
 await page.getByRole('button',{name:'Desktop Pet Online'}).click();const dialog=page.getByRole('dialog');await expect(dialog.getByText('Synced with your Desktop Pet')).toBeVisible();await expect(dialog.getByLabel('Desktop Pet volume')).toHaveValue('35');await expect(dialog.getByRole('button',{name:'Unmute Desktop Pet microphone'})).toBeVisible();
 await expect(dialog.getByLabel('What should Marvin say?')).toHaveCount(0);
 await expect(page.getByRole('banner').getByLabel('Desktop Pet microphone sensitivity')).toHaveCount(0);await dialog.getByLabel('Desktop Pet microphone sensitivity').fill('12');await expect.poll(()=>audio).toEqual({volume:35,muted:true,microphoneGainDb:12,allowPlaybackMic:false,followupSeconds:5});
 await dialog.getByLabel('Desktop Pet follow-up window').fill('8');await expect.poll(()=>audio.followupSeconds).toBe(8);await dialog.getByLabel('Allow Desktop Pet microphone during playback').check();await expect.poll(()=>audio.allowPlaybackMic).toBe(true);
 await expect(dialog.getByLabel('Desktop Pet eye design')).toHaveValue('classic');await dialog.getByLabel('Desktop Pet eye design').selectOption('friendly');await expect.poll(()=>eyes).toEqual({design:'friendly'});
 await expect(dialog.getByText('Head servo calibration')).toBeVisible();await dialog.getByLabel('Head left and right center').fill('84');await expect.poll(()=>calibrationPreviews.at(-1)).toEqual({yawCenter:84,pitchCenter:90,yawReversed:true,pitchReversed:true});expect(calibration.yawCenter).toBe(90);await dialog.getByLabel('Reverse left and right').uncheck();await dialog.getByRole('button',{name:'Save and center head'}).click();await expect.poll(()=>calibration).toEqual({yawCenter:84,pitchCenter:90,yawReversed:false,pitchReversed:true});await expect(dialog.getByText('Calibration saved on your Desktop Pet.')).toBeVisible();
 await dialog.getByLabel('Head up and down center').fill('98');await expect.poll(()=>calibrationPreviews.at(-1)?.pitchCenter).toBe(98);await dialog.getByRole('button',{name:'Close dialog'}).click();await expect(dialog).toBeHidden();await expect.poll(()=>calibrationPreviews.at(-1)).toEqual(calibration);await page.getByRole('button',{name:'Open remote control'}).click();const remote=page.getByRole('dialog',{name:'Remote control'});await expect(remote.getByText('Make Marvin say something')).toBeVisible();
 const speech=remote.getByLabel('What should Marvin say?');await speech.fill('First line');await speech.press('Shift+Enter');await speech.type('Second line');await speech.press('Meta+Enter');await speech.type('Third line');await expect(speech).toHaveValue('First line\nSecond line\nThird line');await speech.press('Enter');await expect.poll(()=>speeches).toEqual(['First line\nSecond line\nThird line']);await expect(speech).toHaveValue('');
 const report=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();expect(report.violations.filter(v=>['critical','serious'].includes(v.impact??'')).map(v=>v.id)).toEqual([]);
});

test('mobile remote is a single-screen touch controller in portrait and landscape',async({page})=>{
 const body={device_id:'00000000-0000-4000-8000-000000000014',network:'Studio',simulated:0};
 await page.setViewportSize({width:390,height:844});
 await page.route('**/api/settings',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),body}});});
 await page.route('**/api/robot/presence',route=>route.fulfill({json:{body,presence:{deviceId:body.device_id,status:'online',capabilities:['remote'],audioSettings:null,headCalibration:null,batteryStatus:null}}}));
 await page.routeWebSocket('**/api/remote',ws=>ws.send(JSON.stringify({v:1,type:'ready'})));
 await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();await page.getByRole('button',{name:'Open remote control'}).click();
 const remote=page.getByRole('dialog',{name:'Remote control'});await expect(remote.getByLabel('Drive joystick')).toHaveAttribute('aria-disabled','false');
 const fits=()=>page.evaluate(()=>{const remote=document.querySelector('.remote-screen')!,main=document.querySelector('.remote-main')!;return remote.scrollHeight<=remote.clientHeight&&main.scrollHeight<=main.clientHeight&&document.documentElement.scrollWidth<=innerWidth;});
 await expect.poll(fits).toBe(true);for(const label of ['Drive joystick','Head joystick']){const box=await page.getByLabel(label).boundingBox();expect(box).not.toBeNull();expect(box!.x).toBeGreaterThanOrEqual(0);expect(box!.y).toBeGreaterThanOrEqual(0);expect(box!.x+box!.width).toBeLessThanOrEqual(390);expect(box!.y+box!.height).toBeLessThanOrEqual(844);}
 await page.screenshot({path:'work/screenshots/remote-mobile-portrait.png'});
 await page.setViewportSize({width:844,height:390});await expect.poll(fits).toBe(true);await page.screenshot({path:'work/screenshots/remote-mobile-landscape.png'});
});
