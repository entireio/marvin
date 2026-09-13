import {readdirSync} from 'node:fs';
import {spawnSync} from 'node:child_process';
// Each suite gets its own server and rate-limit window. Keep production limits intact.
for(const file of readdirSync('tests/e2e').filter(name=>!name.startsWith('.')&&name.endsWith('.spec.ts')).sort()){
 const result=spawnSync(process.execPath,['node_modules/@playwright/test/cli.js','test',file],{stdio:'inherit',env:{...process.env,E2E_DATABASE_PATH:`./work/e2e-${Date.now()}-${file}.sqlite`}});
 if(result.error)throw result.error;
 if(result.status!==0)process.exit(result.status??1);
}
