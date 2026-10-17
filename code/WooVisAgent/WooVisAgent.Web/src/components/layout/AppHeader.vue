<script setup lang="ts">
import { NDropdown, NTooltip } from 'naive-ui'
import { computed, h } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import { useAppStore } from '@/stores/appStore'
import { useAuthStore } from '@/stores/authStore'
import { useMaterialStore } from '@/stores/materialStore'
import { formatDateTime, message } from '@/utils'

const appStore = useAppStore()
const authStore = useAuthStore()
const materialStore = useMaterialStore()
const route = useRoute()
const router = useRouter()

const pageTitle = computed(() => (route.meta.title as string) ?? 'WooVisAgent')
const pageDesc = computed(() => (route.meta.desc as string) ?? '')

const lastRealtimeText = computed(() =>
  appStore.lastRealtimeAt ? formatDateTime(appStore.lastRealtimeAt, 'HH:mm:ss') : '尚未收到推送',
)

/** 实时通道状态对应的指示灯样式 */
const realtimeClass = computed(() => {
  if (appStore.realtimeState === 'connected') return ''
  if (appStore.realtimeState === 'disconnected') return 'is-off'
  return 'is-warn'
})

const userMenuOptions = [
  {
    key: 'logout',
    label: '退出登录',
    icon: () => h(AppIcon, { name: 'logout', size: 15 }),
  },
]

function handleUserAction(key: string): void {
  if (key !== 'logout') return

  // 退出前清空作品库：作品数据按用户隔离，留着会让下一个登录的人
  // 先看到上一个人的作品数和缩略图，直到下一次请求返回才被纠正。
  materialStore.reset()
  authStore.logout()
  message.success('已退出登录')
  void router.replace({ name: 'Login' })
}
</script>

<template>
  <header class="topbar">
    <div class="tt">
      <b>{{ pageTitle }}</b>
      <small v-if="pageDesc">{{ pageDesc }}</small>
    </div>

    <div class="spacer" />

    <n-tooltip trigger="hover">
      <template #trigger>
        <span class="tb-chip" :class="realtimeClass">
          <span class="dot" />
          {{ appStore.realtimeLabel }}
        </span>
      </template>
      最近推送：{{ lastRealtimeText }}
    </n-tooltip>

    <button
      class="btn-icon"
      type="button"
      :title="appStore.isDark ? '切换到浅色主题' : '切换到深色主题'"
      @click="appStore.toggleTheme()"
    >
      <app-icon :name="appStore.isDark ? 'sun' : 'moon'" :size="16" />
    </button>

    <n-dropdown :options="userMenuOptions" trigger="click" @select="handleUserAction">
      <span class="tb-chip">
        <app-icon name="user" :size="15" />
        <span class="max-w-[9rem] truncate">{{ authStore.displayName }}</span>
        <app-icon name="chevron-down" :size="13" />
      </span>
    </n-dropdown>
  </header>
</template>
