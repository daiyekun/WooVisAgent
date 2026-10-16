import {
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
  type HubConnection,
} from '@microsoft/signalr'
import { onBeforeUnmount, ref, shallowRef } from 'vue'

import { getAuthToken } from '@/api'
import { useAppStore } from '@/stores/appStore'
import { useTaskStore } from '@/stores/taskStore'
import type { TaskAsset, TaskStatus } from '@/types'

/** 任务中心 Hub 的相对路径，需与后端 app.MapHub<TaskHub>() 注册的地址一致 */
export const TASK_HUB_PATH = '/hubs/tasks'

function resolveHubUrl(path: string): string {
  const base = (import.meta.env.VITE_SIGNALR_BASE_URL || '').replace(/\/+$/, '')
  return `${base}${path}`
}

/** 创建一条带自动重连的任务 Hub 连接 */
export function createTaskHubConnection(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(resolveHubUrl(TASK_HUB_PATH), {
      // 与 IdentityService 签发的 JWT 对齐，未登录时传空串
      accessTokenFactory: () => getAuthToken() ?? '',
    })
    // 断线后按 0s / 2s / 5s / 10s / 20s 的间隔重试
    .withAutomaticReconnect([0, 2000, 5000, 10_000, 20_000])
    .configureLogging(import.meta.env.DEV ? LogLevel.Information : LogLevel.Warning)
    .build()
}

/**
 * 在布局组件中调用一次即可：
 * 建立 SignalR 连接并把任务进度事件写进 taskStore，
 * 页面从 store 读取即可，不需要各自订阅。
 */
export function useTaskRealtime() {
  const appStore = useAppStore()
  const taskStore = useTaskStore()

  const connection = shallowRef<HubConnection | null>(null)
  const started = ref(false)

  function registerHandlers(conn: HubConnection): void {
    // 后端推送新任务：列表处于第一页且无筛选时直接刷新，保证实时性
    conn.on('TaskCreated', () => {
      if (taskStore.query.page === 1) {
        void taskStore.loadTasks()
      }
    })

    conn.on(
      'TaskStatusChanged',
      (payload: { taskId: string; status: TaskStatus; progress?: number; message?: string }) => {
        taskStore.applyRealtimePayload({
          taskId: payload.taskId,
          status: payload.status,
          progress: payload.progress ?? 0,
          message: payload.message,
        })
      },
    )

    conn.on('TaskProgress', (payload: { taskId: string; progress: number; message?: string }) => {
      const current = taskStore.tasks.find((item) => item.id === payload.taskId)
      taskStore.applyRealtimePayload({
        taskId: payload.taskId,
        status: current?.status ?? 'running',
        progress: payload.progress,
        message: payload.message,
      })
    })

    conn.on('TaskCompleted', (payload: { taskId: string; outputs?: TaskAsset[] }) => {
      taskStore.applyRealtimePayload({
        taskId: payload.taskId,
        status: 'succeeded',
        progress: 100,
        outputs: payload.outputs,
      })
    })

    conn.on('TaskFailed', (payload: { taskId: string; message?: string }) => {
      taskStore.applyRealtimePayload({
        taskId: payload.taskId,
        status: 'failed',
        progress: 100,
        message: payload.message,
      })
    })

    conn.onreconnecting(() => appStore.setRealtimeState('reconnecting'))
    conn.onreconnected(() => appStore.setRealtimeState('connected'))
    conn.onclose(() => {
      started.value = false
      appStore.setRealtimeState('disconnected')
    })
  }

  async function start(): Promise<void> {
    if (!connection.value) {
      const conn = createTaskHubConnection()
      registerHandlers(conn)
      connection.value = conn
    }

    const conn = connection.value
    if (!conn || conn.state !== HubConnectionState.Disconnected) return

    appStore.setRealtimeState('connecting')
    try {
      await conn.start()
      started.value = true
      appStore.setRealtimeState('connected')
    } catch (error) {
      // 后端未启动或未实现 Hub 时不应阻塞页面，降级为定时刷新即可
      started.value = false
      appStore.setRealtimeState('disconnected')
      console.warn('[signalr] 任务实时通道连接失败：', error)
    }
  }

  async function stop(): Promise<void> {
    const conn = connection.value
    if (!conn) return

    try {
      await conn.stop()
    } catch {
      // 忽略停止时的异常
    } finally {
      started.value = false
      appStore.setRealtimeState('disconnected')
    }
  }

  onBeforeUnmount(() => {
    void stop()
  })

  return { connection, started, start, stop }
}
