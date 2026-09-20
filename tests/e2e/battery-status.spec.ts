import {test,expect} from '@playwright/test';

test('header shows live battery level and is ready for charging status',async({page})=>{
 let battery={levelPercent:74,voltageMv:3950,charging:null as boolean|null};
 const body={device_id:'00000000-0000-4000-8000-000000000004',network:'Studio',simulated:0};
 await page.route('**/api/settings',async route=>{const response=await route.fetch();await route.fulfill({json:{...await response.json(),body}});});
 await page.route('**/api/robot/presence',route=>route.fulfill({json:{body,presence:{deviceId:body.device_id,status:'online',capabilities:[],audioSettings:null,headCalibration:null,batteryStatus:battery}}}));
 await page.goto('/login');await page.getByRole('button',{name:'Enter local preview'}).click();
 await expect(page.getByLabel('Desktop Pet battery 74%')).toBeVisible();
 battery={...battery,charging:true};
 await expect(page.getByLabel('Desktop Pet battery 74%, charging')).toBeVisible({timeout:3000});
 battery={levelPercent:19,voltageMv:3735,charging:false};
 await expect(page.getByLabel('Desktop Pet battery 19%')).toBeVisible({timeout:3000});
});
