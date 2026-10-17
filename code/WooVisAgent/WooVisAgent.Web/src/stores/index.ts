import type { App } from 'vue'
import { createPinia } from 'pinia'

export const pinia = createPinia()

/** 在 main.ts 中调用，注册状态管理 */
export function setupStore(app: App): void {
  app.use(pinia)
}

export * from './appStore'
export * from './authStore'
export * from './conversationStore'
export * from './materialStore'
export * from './modelStore'
export * from './taskStore'
