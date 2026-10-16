import { computed, reactive, ref } from 'vue'
import { defineStore } from 'pinia'

import { taskApi } from '@/api'
import { DEFAULT_PAGE_SIZE, TASK_STATUS_META } from '@/constants'
import type {
  CreateTaskInput,
  GenerationTask,
  TaskAsset,
  TaskProgressPayload,
  TaskQuery,
  TaskStatus,
} from '@/types'

function createDefaultQuery(): Required<Pick<TaskQuery, 'page' | 'pageSize'>> & TaskQuery {
  return {
    page: 1,
    pageSize: DEFAULT_PAGE_SIZE,
    keyword: '',
    type: undefined,
    status: undefined,
    modelId: undefined,
    dateRange: null,
  }
}

export const useTaskStore = defineStore('task', () => {
  /** 列表与分页 */
  const tasks = ref<GenerationTask[]>([])
  const total = ref(0)
  const loading = ref(false)
  const query = reactive(createDefaultQuery())

  /** 详情缓存，key 为任务 ID */
  const detailCache = ref<Record<string, GenerationTask>>({})
  /** 列表勾选 */
  const selectedIds = ref<string[]>([])

  /** 未结束的任务数量，用于展示「进行中」角标 */
  const runningCount = computed(
    () => tasks.value.filter((item) => !TASK_STATUS_META[item.status].isFinal).length,
  )

  const totalPages = computed(() => {
    const size = query.pageSize || DEFAULT_PAGE_SIZE
    return Math.max(1, Math.ceil(total.value / size))
  })

  const isEmpty = computed(() => !loading.value && tasks.value.length === 0)

  /** 拉取任务列表 */
  async function loadTasks(patch: Partial<TaskQuery> = {}): Promise<void> {
    Object.assign(query, patch)
    loading.value = true
    try {
      const result = await taskApi.fetchTasks({ ...query })
      tasks.value = result.items ?? []
      total.value = result.total ?? 0
    } catch {
      // 错误提示已由拦截器统一处理，这里只保证列表状态可控
      tasks.value = []
      total.value = 0
    } finally {
      loading.value = false
    }
  }

  /** 重置筛选条件后重新查询 */
  async function resetAndReload(): Promise<void> {
    Object.assign(query, createDefaultQuery())
    await loadTasks()
  }

  /** 加载详情 */
  async function loadTaskDetail(id: string, force = false): Promise<GenerationTask | null> {
    if (!force && detailCache.value[id]) return detailCache.value[id]

    try {
      const detail = await taskApi.fetchTaskDetail(id)
      detailCache.value = { ...detailCache.value, [id]: detail }
      return detail
    } catch {
      return null
    }
  }

  /** 新建任务并插入列表头部 */
  async function createTask(input: CreateTaskInput): Promise<GenerationTask | null> {
    const created = await taskApi.createTask(input)
    tasks.value = [created, ...tasks.value]
    total.value += 1
    return created
  }

  /** 取消任务 */
  async function cancelTask(id: string): Promise<void> {
    await taskApi.cancelTask(id)
    patchTask(id, { status: 'canceled' })
  }

  /** 复用参数重新提交 */
  async function retryTask(id: string): Promise<GenerationTask | null> {
    const created = await taskApi.retryTask(id)
    tasks.value = [created, ...tasks.value]
    total.value += 1
    return created
  }

  /** 删除单个任务 */
  async function removeTask(id: string): Promise<void> {
    await taskApi.deleteTask(id)
    tasks.value = tasks.value.filter((item) => item.id !== id)
    total.value = Math.max(0, total.value - 1)
    selectedIds.value = selectedIds.value.filter((item) => item !== id)
    delete detailCache.value[id]
  }

  /** 批量删除 */
  async function removeTasks(ids: string[]): Promise<void> {
    if (!ids.length) return
    await taskApi.batchDeleteTasks(ids)
    await loadTasks()
    clearSelection()
  }

  function clearSelection(): void {
    selectedIds.value = []
  }

  /** 局部更新某条任务 */
  function patchTask(id: string, patch: Partial<GenerationTask>): void {
    const index = tasks.value.findIndex((item) => item.id === id)
    if (index >= 0) {
      tasks.value[index] = { ...tasks.value[index], ...patch }
    }

    const cached = detailCache.value[id]
    if (cached) {
      detailCache.value = { ...detailCache.value, [id]: { ...cached, ...patch } }
    }
  }

  /** 追加产物（实时推送返回新图时使用） */
  function appendOutputs(id: string, outputs: TaskAsset[]): void {
    const target = tasks.value.find((item) => item.id === id) ?? detailCache.value[id]
    if (!target) return

    const existed = new Set(target.outputs.map((item) => item.id))
    const merged = [...target.outputs, ...outputs.filter((item) => !existed.has(item.id))]
    patchTask(id, { outputs: merged })
  }

  /** 处理 SignalR 推送的进度/状态事件 */
  function applyRealtimePayload(payload: TaskProgressPayload): void {
    const patch: Partial<GenerationTask> = {
      status: payload.status,
      progress: payload.progress,
    }

    if (payload.outputs?.length) {
      const target = tasks.value.find((item) => item.id === payload.taskId) ?? detailCache.value[payload.taskId]
      const existed = new Set((target?.outputs ?? []).map((item) => item.id))
      patch.outputs = [
        ...(target?.outputs ?? []),
        ...payload.outputs.filter((item) => !existed.has(item.id)),
      ]
    }

    if (payload.status === 'failed' && payload.message) {
      patch.errorMessage = payload.message
    }

    if (TASK_STATUS_META[payload.status]?.isFinal) {
      patch.finishedAt = new Date().toISOString()
    }

    patchTask(payload.taskId, patch)
  }

  /** 按状态分组统计，供看板类展示使用 */
  const statusSummary = computed(() => {
    const summary: Record<TaskStatus, number> = {
      pending: 0,
      queued: 0,
      running: 0,
      succeeded: 0,
      failed: 0,
      canceled: 0,
    }

    tasks.value.forEach((item) => {
      summary[item.status] = (summary[item.status] ?? 0) + 1
    })

    return summary
  })

  return {
    tasks,
    total,
    totalPages,
    loading,
    isEmpty,
    query,
    detailCache,
    selectedIds,
    runningCount,
    statusSummary,

    loadTasks,
    resetAndReload,
    loadTaskDetail,
    createTask,
    cancelTask,
    retryTask,
    removeTask,
    removeTasks,
    clearSelection,
    patchTask,
    appendOutputs,
    applyRealtimePayload,
  }
})
