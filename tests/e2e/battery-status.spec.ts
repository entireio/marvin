import {test,expect} from '@playwright/test';

test('header and remote control show live battery level and charging status',async({page})=>{
 let battery={levelPercent:74,voltageMv:3950,charging:null as boolean|null};
 let display={showBatteryIcon:true};
 const body={device_id:'00000000-0000-4000-8000-000000000004',network:'Studio',simulated:0};
 await page.route('**/api/settings',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),body}});});
 await page.route('**/api/robot/presence',route=>route.fulfill({json:{body,presence:{deviceId:body.device_id,status:'online',capabilities:[],audioSettings:null,headCalibration:null,batteryStatus:battery,displaySettings:display}}}));
 await page.route('**/api/robot/display',async route=>{display=JSON.parse(route.request().postData()??'{}');await route.fulfill({json:display});});
 await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();
 await expect(page.getByLabel('Desktop Pet battery 74%')).toBeVisible();
 await page.getByRole('button',{name:'Account menu'}).click();
 await page.getByLabel('Open settings',{exact:true}).click();
 await page.getByRole('button',{name:'Desktop Pet',exact:true}).click();
 const toggle=page.getByRole('checkbox',{name:'Show Desktop Pet battery icon'});
 await expect(toggle).toBeChecked();await toggle.uncheck();
 await expect(page.getByLabel('Desktop Pet battery 74%')).toHaveCount(0);
 await page.getByRole('button',{name:'Close dialog'}).click();
 await page.getByRole('button',{name:'Open remote control'}).click();
 const remote=page.getByRole('dialog',{name:'Remote control'});
 await expect(remote.getByLabel('Desktop Pet battery 74%')).toHaveCount(0);
 battery={...battery,charging:true};
 await expect(remote.getByLabel('Desktop Pet battery 74%, charging')).toHaveCount(0);
 battery={levelPercent:15,voltageMv:3680,charging:false};
 await expect(remote.getByLabel('Desktop Pet battery 15%, critically low')).toBeVisible({timeout:3000});
});
