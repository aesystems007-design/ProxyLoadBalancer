import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

export default defineConfig({
  plugins: [vue()],
  base: "/_proxy/dashboard/",
  build: {
    outDir: "../LoadBalancer.Proxy/wwwroot/_proxy/dashboard",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/_proxy/api": "http://localhost:5080",
    },
  },
});
