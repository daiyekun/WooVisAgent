import { fileURLToPath, URL } from 'node:url'

import { defineConfig, loadEnv } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  // 必须用 loadEnv 读，不能只靠 process.env：
  // 1. .env.development 里的 VITE_PROXY_TARGET 只有经过 loadEnv 才会合并进来；
  // 2. Aspire 的 WithReference 注入的是 services__woovisagent-api__http__0 这类
  //    不带 VITE_ 前缀的变量，vite 的 define/import.meta.env 不会自动暴露它们，
  //    只能显式把 process.env 一起合并进来才拿得到。
  // 之前只读 process.env['services__...'] 时该变量在 ESM 配置文件里恒为 undefined，
  // 代理目标一路兜底到 5210 这个不存在的端口，导致登录 / SignalR 全部 502 或超时。
  const fileEnv = loadEnv(mode, process.cwd(), '')
  const env = { ...process.env, ...fileEnv }

  // 优先用 Aspire 注入的后端地址，其次 .env.development 的 VITE_PROXY_TARGET，
  // 最后才是本地手工起 Api 时的默认端口。
  const aspireApiUrl =
    env['services__woovisagent-api__http__0'] || env['services__woovisagent-api__https__0']
  const proxyTarget = aspireApiUrl || env['VITE_PROXY_TARGET'] || 'http://localhost:5065'

  // Aspire 的 AddViteApp 会把端口通过环境变量 PORT 传进来，
  // 保证和 AppHost 里 WithEndpoint 声明的端口一致。
  const port = Number(env['PORT']) || 5199

  return {
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
  }
})
