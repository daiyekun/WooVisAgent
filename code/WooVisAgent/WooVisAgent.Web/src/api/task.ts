import { del, get, post } from './http'
import type {
  CreateTaskInput,
  GenerationTask,
  PageQuery,
  PagedResult,
  TaskAsset,
  TaskStatus,
} from '@/types'

const BASE_URL = '/api/tasks'

/** 分页查询任务列表 */
export function fetchTasks(query: PageQuery & Record<string, unknown> = {}) {
  return get<PagedResult<GenerationTask>>(BASE_URL, query)
}

/** 查询任务详情（含产物） */
export function fetchTaskDetail(id: string, options?: { silent?: boolean }) {
  return get<GenerationTask>(`${BASE_URL}/${id}`, undefined, options)
}

/** 新建任务 */
export function createTask(input: CreateTaskInput) {
  return post<GenerationTask>(BASE_URL, input)
}

/** 取消进行中的任务 */
export function cancelTask(id: string) {
  return post<void>(`${BASE_URL}/${id}/cancel`)
}

/** 复用原任务参数重新提交 */
export function retryTask(id: string) {
  return post<GenerationTask>(`${BASE_URL}/${id}/retry`)
}

/** 删除单个任务 */
export function deleteTask(id: string) {
  return del<void>(`${BASE_URL}/${id}`)
}

/** 批量删除任务 */
export function batchDeleteTasks(ids: string[]) {
  return post<void>(`${BASE_URL}/batch-delete`, { ids })
}

/** 查询任务产物 */
export function fetchTaskOutputs(id: string) {
  return get<TaskAsset[]>(`${BASE_URL}/${id}/outputs`)
}

/** 轮询兜底：SignalR 不可用时由前端定时拉取任务状态 */
export function fetchTaskStatuses(ids: string[]) {
  return post<Pick<GenerationTask, 'id' | 'status' | 'progress' | 'errorMessage'>[]>(
    `${BASE_URL}/statuses`,
    { ids },
    { silent: true },
  )
}

export type { TaskStatus }
