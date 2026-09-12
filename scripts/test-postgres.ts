import { spawn } from 'node:child_process';
if(!process.env.TEST_DATABASE_URL)throw new Error('Set TEST_DATABASE_URL to a disposable PostgreSQL database; this suite applies migrations and writes test data. See README.md.');
const child=spawn(process.execPath,['node_modules/vitest/vitest.mjs','run','tests/unit/persistence.test.ts'],{stdio:'inherit',env:process.env});
child.on('exit',code=>{process.exitCode=code??1;});
