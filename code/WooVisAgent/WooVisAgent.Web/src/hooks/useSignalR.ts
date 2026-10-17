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
import type { TaskAsset, TaskProgressPayload, TaskStatus } from '@/types'

/** 任务中心 Hub 的相对路径，需与后端 app.MapHub<TaskHub>() 注册的地址一致 */
export const TASK_HUB_PATH = '/hubs/tasks'

/** 一次任务推送。字段与后端 GenerationTaskStatusChangedConsumer 推的负载一致 */
export interface TaskEvent {
  taskId: string
  status: TaskStatus
  progress: number
  message?: string
  outputs?: TaskAsset[]
}

type TaskEventHandler = (event: TaskEvent) => void

function resolveHubUrl(path: string): string {
  const base = (import.meta.env.VITE_SIGNALR_BASE_URL || '').replace(/\/+$/, '')
  return `${base}${path}`
}

/* ============================================================================
 * 共享连接
 *
 * 全站只维持一条 Hub 连接：任务列表页、对话页、工作台都要收同一批事件，
 * 各建一条连接不仅浪费，还会让「哪条连接收到了推送」变得不可预期。
 * 连接上的 on() 注册在自动重连后依然有效，因此订阅只需注册一次。
 * ========================================================================== */

let sharedConnection: HubConnection | null = null
let sharedStarting: Promise<boolean> | null = null

/** 订阅者集合。页面各自注册自己的处理函数，互不干扰 */
const handlers = new Set<TaskEventHandler>()

function emit(event: TaskEvent): void {
  handlers.forEach((handler) => {
    try {
      handler(event)
    } catch (error) {
      console.warn('[signalr] 任务事件处理函数抛错：', error)
    }
  })
}

function createConnection(): HubConnection {
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

function registerHandlers(conn: HubConnection): void {
  conn.on('TaskCreated', (payload: { taskId?: string }) => {
    if (payload?.taskId) emit({ taskId: payload.taskId, status: 'queued', progress: 0 })
  })

  // 后端每一条状态事件都会带上 outputs（见 GenerationTaskStatusChangedConsumer），
  // 因此这里必须原样透传：只依赖后续的 TaskCompleted 会在"状态先到、产物后到"时丢图。
  conn.on(
    'TaskStatusChanged',
    (payload: {
      taskId: string
      status: TaskStatus
      progress?: number
      message?: string
      outputs?: TaskAsset[]
    }) => {
      emit({
        taskId: payload.taskId,
        status: payload.status,
        progress: payload.progress ?? 0,
        message: payload.message,
        outputs: payload.outputs,
      })
    },
  )

  conn.on('TaskProgress', (payload: { taskId: string; progress: number; message?: string }) => {
    emit({ taskId: payload.taskId, status: 'running', progress: payload.progress, message: payload.message })
  })

  conn.on('TaskCompleted', (payload: { taskId: string; outputs?: TaskAsset[] }) => {
    emit({ taskId: payload.taskId, status: 'succeeded', progress: 100, outputs: payload.outputs })
  })

  conn.on('TaskFailed', (payload: { taskId: string; message?: string }) => {
    emit({ taskId: payload.taskId, status: 'failed', progress: 100, message: payload.message })
  })
}

/** 取得共享连接（未连接时只创建不启动） */
export function getTaskHubConnection(): HubConnection {
  if (sharedConnection) return sharedConnection

  const conn = createConnection()
  registerHandlers(conn)
  sharedConnection = conn
  return conn
}

/**
 * 确保共享连接已启动。
 * 幂等：并发调用只会真正 start 一次，因此对话页可以直接调用，
 * 不必依赖布局组件先启动完成。
 */
export async function ensureTaskHubStarted(): Promise<boolean> {
  const conn = getTaskHubConnection()

  if (conn.state === HubConnectionState.Connected) return true

  // 正在连接 / 重连中：交给已有流程，调用方走轮询兜底即可
  if (conn.state === HubConnectionState.Connecting || conn.state === HubConnectionState.Reconnecting) {
    return false
  }

  if (sharedStarting) return sharedStarting

  sharedStarting = (async () => {
    try {
      await conn.start()
      return true
    } catch (error) {
      // 后端未启动或未实现 Hub 时不应阻塞页面，降级为定时刷新即可
      console.warn('[signalr] 任务实时通道连接失败：', error)
      return false
    } finally {
      sharedStarting = null
    }
  })()

  return sharedStarting
}

/** 订阅任务事件，返回取消订阅函数 */
export function onTaskEvent(handler: TaskEventHandler): () => void {
  handlers.add(handler)
  return () => handlers.delete(handler)
}

/** 当前连接是否已就绪，供页面决定要不要开轮询兜底 */
export function isTaskHubConnected(): boolean {
  return sharedConnection?.state === HubConnectionState.Connected
}

/**
 * 在组件内订阅任务事件，卸载时自动取消。
 * 对话页用它跟踪自己提交的那几个任务。
 */
export function useTaskEvents(handler: TaskEventHandler): void {
  const unsubscribe = onTaskEvent(handler)
  onBeforeUnmount(unsubscribe)
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

  // 列表页的数据同步：只有这里写 store，避免与页面级订阅重复处理。
  // 卸载时要取消，否则布局重新挂载会多出一份重复的 store 写入。
  let unsubscribe: (() => void) | null = null

  async function start(): Promise<void> {
    const conn = getTaskHubConnection()
    connection.value = conn

    // 连接状态指示灯
    conn.onreconnecting(() => appStore.setRealtimeState('reconnecting'))
    conn.onreconnected(() => appStore.setRealtimeState('connected'))
    conn.onclose(() => {
      started.value = false
      appStore.setRealtimeState('disconnected')
    })

    unsubscribe?.()
    unsubscribe = onTaskEvent((event) => {
      taskStore.applyRealtimePayload({
        taskId: event.taskId,
        status: event.status,
        progress: event.progress,
        message: event.message,
        outputs: event.outputs,
      } satisfies TaskProgressPayload)
    })

    appStore.setRealtimeState('connecting')
    const ok = await ensureTaskHubStarted()
    started.value = ok
    appStore.setRealtimeState(ok ? 'connected' : 'disconnected')
  }

  async function stop(): Promise<void> {
    unsubscribe?.()
    unsubscribe = null

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
