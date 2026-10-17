<script setup lang="ts">
import { computed, ref } from 'vue'
import {
  NConfigProvider,
  NDialogProvider,
  NLoadingBarProvider,
  NMessageProvider,
  NNotificationProvider,
  darkTheme,
  dateZhCN,
  zhCN,
} from 'naive-ui'

import { resolveThemeOverrides } from '@/assets/styles/naive-theme'
import router from '@/router'
import { useAppStore } from '@/stores/appStore'
import { useAuthStore } from '@/stores/authStore'

const appStore = useAppStore()
const authStore = useAuthStore()

const theme = computed(() => (appStore.isDark ? darkTheme : null))
const themeOverrides = computed(() => resolveThemeOverrides(appStore.isDark))

/**
 * 首次导航会在路由守卫中校验本地 token，并等待 /api/auth/me。
 * API / 数据库冷启动时可能要数秒；router-view 此时尚无组件，原先只剩深色背景，
 * 用户会误以为页面卡死。这里覆盖整个“恢复登录态 + 首屏懒加载”阶段。
 */
const booting = ref(true)
const startupText = computed(() =>
  authStore.isAuthenticated && !authStore.profileLoaded
    ? '正在恢复登录状态…'
    : '正在加载控制台…',
)

void router.isReady().then(
  () => {
    booting.value = false
  },
  () => {
    // 导航失败也必须解除遮罩，不能让错误被永久启动页盖住。
    booting.value = false
  },
)
</script>

<template>
  <n-config-provider
    class="h-full"
    :theme="theme"
    :theme-overrides="themeOverrides"
    :locale="zhCN"
    :date-locale="dateZhCN"
  >
    <!-- 全局背景光效：极光渐变 + 网格，所有页面共享 -->
    <div class="aurora" />
    <div class="mesh" />

    <transition name="startup-fade">
      <div v-if="booting" class="startup-screen" role="status" aria-live="polite">
        <div class="startup-box">
          <strong>WooVisAgent</strong>
          <div class="startup-progress" role="progressbar" :aria-label="startupText">
            <i />
          </div>
          <small>{{ startupText }}</small>
        </div>
      </div>
    </transition>

    <n-loading-bar-provider>
      <n-message-provider>
        <n-dialog-provider>
          <n-notification-provider>
            <router-view />
          </n-notification-provider>
        </n-dialog-provider>
      </n-message-provider>
    </n-loading-bar-provider>
  </n-config-provider>
</template>

<style scoped>
.startup-screen {
  position: fixed;
  inset: 0;
  z-index: 9999;
  display: grid;
  place-items: center;
  background: var(--bg);
}

.startup-box {
  width: min(360px, calc(100vw - 48px));
  text-align: center;
}

.startup-box strong {
  display: block;
  margin-bottom: 18px;
  color: var(--text);
  font-size: 22px;
  letter-spacing: 0.02em;
}

.startup-box small {
  display: block;
  margin-top: 12px;
  color: var(--sub);
}

.startup-progress {
  position: relative;
  height: 4px;
  overflow: hidden;
  border-radius: 999px;
  background: var(--line);
}

.startup-progress > i {
  position: absolute;
  inset-block: 0;
  width: 42%;
  border-radius: inherit;
  background: var(--grad);
  animation: startup-progress 1.1s ease-in-out infinite;
}

@keyframes startup-progress {
  from {
    transform: translateX(-110%);
  }

  to {
    transform: translateX(340%);
  }
}

.startup-fade-leave-active {
  transition: opacity 0.18s ease;
}

.startup-fade-leave-to {
  opacity: 0;
}

@media (prefers-reduced-motion: reduce) {
  .startup-progress > i {
    animation-duration: 2.4s;
  }
}
</style>
