import { defineConfig } from 'vitest/config';
export default defineConfig({ test: { include: ['tests/unit/**/*.test.ts'], testTimeout: 15000, hookTimeout: 30000, fileParallelism: false, coverage: { include: ['packages/**/src/**', 'apps/server/src/**'] } } });
