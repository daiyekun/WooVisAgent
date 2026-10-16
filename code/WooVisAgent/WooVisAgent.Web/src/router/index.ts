import { createRouter, createWebHistory } from 'vue-router'

import { APP_TITLE } from '@/constants'
import { pinia, useAuthStore } from '@/stores'
import { routes } from './routes'

declare module 'vue-router' {
  interface RouteMeta {
    /** 页面标题 */
    title?: string
    /** 侧边栏图标名 */
    icon?: string
    /** 菜单排序 */
    order?: number
    /** 是否在菜单中隐藏 */
    hidden?: boolean
    /** 需要登录，未登录时跳转登录页并记录回跳地址 */
    requiresAuth?: boolean
    /** 仅未登录可访问（登录 / 注册页），已登录时跳回控制台 */
    guestOnly?: boolean
  }
}

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
  scrollBehavior: () => ({ top: 0 }),
})

router.beforeEach(async (to) => {
  const authStore = useAuthStore(pinia)

  // 刷新页面后本地只有 Token：先同步一次用户资料，失效则视为未登录
  if (authStore.isAuthenticated && !authStore.profileLoaded) {
    await authStore.loadProfile()
  }

  if (to.meta.requiresAuth && !authStore.isAuthenticated) {
    return { name: 'Login', query: { redirect: to.fullPath } }
  }

  if (to.meta.guestOnly && authStore.isAuthenticated) {
    return { name: 'TaskList' }
  }

  return true
})

router.afterEach((to) => {
  document.title = to.meta.title ? `${to.meta.title} · ${APP_TITLE}` : APP_TITLE
})

export default router
