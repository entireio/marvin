import { defineConfig } from '@playwright/test';
import { resolve } from 'node:path';
process.env.PLAYWRIGHT_BROWSERS_PATH??=resolve('work/browsers');
export default defineConfig({testDir:'tests/e2e',timeout:30000,fullyParallel:false,workers:1,retries:0,reporter:[['list'],['html',{outputFolder:'work/playwright-report',open:'never'}]],outputDir:'work/playwright-results',use:{baseURL:'http://127.0.0.1:5173',headless:true,trace:'retain-on-failure',screenshot:'only-on-failure'},webServer:{command:'npm run dev',url:'http://127.0.0.1:5173',reuseExistingServer:!process.env.CI,timeout:30000,env:{SQLITE_PATH:'./work/e2e.sqlite',AUTH_MODE:'development',MODEL_PROVIDER:'fixture',APP_ORIGIN:'http://127.0.0.1:5173'}}});
