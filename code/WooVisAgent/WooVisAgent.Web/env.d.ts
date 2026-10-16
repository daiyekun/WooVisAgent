/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** 后端 API 根地址，留空则走 Vite 代理 */
  readonly VITE_API_BASE_URL: string
  /** SignalR 服务根地址 */
  readonly VITE_SIGNALR_BASE_URL: string
  /** 应用标题 */
  readonly VITE_APP_TITLE: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
