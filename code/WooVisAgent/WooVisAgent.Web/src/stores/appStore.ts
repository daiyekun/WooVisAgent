import { computed, ref, watch } from 'vue'
import { defineStore } from 'pinia'

import { STORAGE_KEYS } from '@/constants'

/** SignalR 连接状态 */
export type RealtimeState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting'

export interface AppSettings {
  /** 侧边栏是否收起 */
  siderCollapsed: boolean
  /** 是否暗色主题 */
  isDark: boolean
  /** 任务列表自动刷新间隔（毫秒），0 表示关闭 */
  autoRefreshInterval: number
}

const DEFAULT_SETTINGS: AppSettings = {
  siderCollapsed: false,
  // 默认深色：设计系统以原型（docs/prot）的深色科技风为基准
  isDark: true,
  autoRefreshInterval: 0,
}

function loadSettings(): AppSettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEYS.appSettings)
    if (!raw) return { ...DEFAULT_SETTINGS }
    return { ...DEFAULT_SETTINGS, ...(JSON.parse(raw) as Partial<AppSettings>) }
  } catch {
    return { ...DEFAULT_SETTINGS }
  }
}

export const useAppStore = defineStore('app', () => {
  const settings = ref<AppSettings>(loadSettings())

  /** 实时通道状态，供顶栏指示灯展示 */
  const realtimeState = ref<RealtimeState>('disconnected')
  /** 最近一次收到实时推送的时间 */
  const lastRealtimeAt = ref<string | null>(null)

  const isDark = computed(() => settings.value.isDark)
  const siderCollapsed = computed(() => settings.value.siderCollapsed)
  const autoRefreshInterval = computed(() => settings.value.autoRefreshInterval)

  const realtimeLabel = computed(() => {
    const map: Record<RealtimeState, string> = {
      connected: '实时通道已连接',
      connecting: '正在连接实时通道',
      reconnecting: '实时通道重连中',
      disconnected: '实时通道未连接',
    }
    return map[realtimeState.value]
  })

  const realtimeType = computed(() => {
    const map: Record<RealtimeState, 'success' | 'warning' | 'error' | 'default'> = {
      connected: 'success',
      connecting: 'warning',
      reconnecting: 'warning',
      disconnected: 'default',
    }
    return map[realtimeState.value]
  })

  watch(
    settings,
    (value) => {
      localStorage.setItem(STORAGE_KEYS.appSettings, JSON.stringify(value))
    },
    { deep: true },
  )

  // 暗色模式同步到 html 根节点，供 Tailwind 的 dark: 变体使用
  watch(
    () => settings.value.isDark,
    (isDark) => {
      document.documentElement.classList.toggle('dark', isDark)
    },
    { immediate: true },
  )

  function toggleSider(): void {
    settings.value.siderCollapsed = !settings.value.siderCollapsed
  }

  function setSiderCollapsed(collapsed: boolean): void {
    settings.value.siderCollapsed = collapsed
  }

  function toggleTheme(): void {
    settings.value.isDark = !settings.value.isDark
  }

  function setAutoRefreshInterval(interval: number): void {
    settings.value.autoRefreshInterval = interval
  }

  function setRealtimeState(state: RealtimeState): void {
    realtimeState.value = state
    if (state === 'connected') {
      lastRealtimeAt.value = new Date().toISOString()
    }
  }

  return {
    settings,
    realtimeState,
    realtimeLabel,
    realtimeType,
    lastRealtimeAt,
    isDark,
    siderCollapsed,
    autoRefreshInterval,
    toggleSider,
    setSiderCollapsed,
    toggleTheme,
    setAutoRefreshInterval,
    setRealtimeState,
  }
})
