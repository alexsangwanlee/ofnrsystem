import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// changeOrigin rewrites Host to 127.0.0.1:47821; the server rejects any other Host.
const server = { target: "http://127.0.0.1:47821", changeOrigin: true };

export default defineConfig({
  plugins: [react()],
  // The bundle is loaded from local disk inside Tauri, so chunk size does not matter.
  build: { chunkSizeWarningLimit: 2000 },
  server: {
    port: 5173,
    strictPort: true,
    proxy: { "/api": server, "/hubs": { ...server, ws: true } },
  },
});
