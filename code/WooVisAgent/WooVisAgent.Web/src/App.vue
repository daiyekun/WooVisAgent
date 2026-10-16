<script setup lang="ts">
import { computed } from 'vue'
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
import { useAppStore } from '@/stores/appStore'

const appStore = useAppStore()

const theme = computed(() => (appStore.isDark ? darkTheme : null))
const themeOverrides = computed(() => resolveThemeOverrides(appStore.isDark))
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
