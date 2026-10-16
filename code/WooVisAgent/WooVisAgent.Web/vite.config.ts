import { fileURLToPath, URL } from 'node:url'

import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

// AppHost 的 WithReference(httpapi) 会注入形如 services__woovisagent-api__http__0 的环境变量，
// 优先用它作为后端地址，这样在 Aspire 里联调不需要手写端口。
const aspireApiUrl = process.env['services__woovisagent-api__http__0']
// 单独跑 npm run dev 时用 VITE_PROXY_TARGET 兜底（见 .env.development）
const proxyTarget = aspireApiUrl || process.env.VITE_PROXY_TARGET || 'http://localhost:5210'

// Aspire 的 AddViteApp 会把端口通过环境变量 PORT 传进来，
// 保证和 AppHost 里 WithEndpoint 声明的端口一致。
const port = Number(process.env.PORT) || 5199

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue(), tailwindcss()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    // DCP 会通过本机代理转发请求，需要监听全部网卡
    host: true,
    port,
    // 端口被占用时直接报错，避免 Vite 自动换端口导致 Aspire 代理指向错误地址
    strictPort: true,
    proxy: {
      // 走同源代理，规避后端 CORS 配置
      '/api': {
        target: proxyTarget,
        changeOrigin: true,
      },
      // 生成产物（图片/视频）由后端 /uploads 静态托管，页面直接引用该地址
      '/uploads': {
        target: proxyTarget,
        changeOrigin: true,
      },
      // SignalR 握手 + WebSocket 升级
      '/hubs': {
        target: proxyTarget,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
  },
})
