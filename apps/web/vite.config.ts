import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';
export default defineConfig({ root: resolve('apps/web'), plugins: [react()], server: { host: '127.0.0.1', port: 5173, strictPort: true, proxy: { '/api': { target: 'http://127.0.0.1:4310', ws: true } } }, build: { outDir: '../../dist/web', emptyOutDir: true } });
