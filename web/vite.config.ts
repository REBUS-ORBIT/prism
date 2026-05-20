import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { fileURLToPath, URL } from 'node:url';

/**
 * One Vite project, two SPA entry points (admin + convert).
 * Both are bundled into `web/dist/` and served as static files by the
 * PRISM Server in production.
 */
export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
      '@shared': fileURLToPath(new URL('./src/shared', import.meta.url)),
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    rollupOptions: {
      input: {
        admin: fileURLToPath(new URL('./src/admin/index.html', import.meta.url)),
        convert: fileURLToPath(new URL('./src/convert/index.html', import.meta.url)),
      },
      output: {
        // Place each entry's HTML at dist/<name>/index.html so the server
        // can serve `/admin/` and `/convert/` directly. JS/CSS chunks go
        // under dist/assets/ regardless.
        entryFileNames: 'assets/[name]-[hash].js',
        chunkFileNames: 'assets/[name]-[hash].js',
        assetFileNames: 'assets/[name]-[hash].[ext]',
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api':       { target: 'http://localhost:8765', changeOrigin: true },
      '/v1':        { target: 'http://localhost:8765', changeOrigin: true },
      '/ws/admin':  { target: 'ws://localhost:8765', ws: true },
    },
  },
});
