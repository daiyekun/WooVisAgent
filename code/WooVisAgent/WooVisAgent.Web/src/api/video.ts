import { del, get, post } from './http'
import type { RequestOptions } from './types'
import type { GenerationTask, PageQuery, PagedResult, TaskAsset, TaskStatus } from '@/types'

const BASE_URL = '/api/video/generations'

/** 视频生成入参 */
export interface VideoGenerationInput {
  name?: string
  modelId: string
  prompt: string
  negativePrompt?: string
  /** 首帧图 URL（图生视频） */
  firstFrameUrl?: string
  /** 尾帧图 URL */
  lastFrameUrl?: string
  /** 参考视频 URL（视频风格迁移） */
  referenceVideoUrl?: string
  /** 时长，单位秒 */
  durationSeconds?: number
  fps?: number
  /** 如 1920x1080、1280x720 */
  resolution?: string
  /** 是否生成音效 */
  withAudio?: boolean
  seed?: number
  /**
   * 所属创作会话。不传则由后端自动新建 / 复用空会话；
   * 聊天页始终会带上，这样多轮对话的历史才能完整回放。
   */
  conversationId?: string
  params?: Record<string, unknown>
}

export interface VideoGenerationResult {
  taskId: string
  status: TaskStatus
  video?: TaskAsset
}

/** 提交视频生成任务，通常为异步，产物由 SignalR 推送 */
export function generateVideo(input: VideoGenerationInput) {
  return post<VideoGenerationResult>(BASE_URL, input)
}

/** 查询视频生成任务列表 */
export function fetchVideoTasks(query: PageQuery & Record<string, unknown> = {}) {
  return get<PagedResult<GenerationTask>>(BASE_URL, query)
}

/**
 * 查询单条视频生成任务。
 * 轮询兜底时传 { silent: true }，避免网络抖动时反复弹错误提示。
 */
export function fetchVideoTask(id: string, options?: RequestOptions) {
  return get<GenerationTask>(`${BASE_URL}/${id}`, undefined, options)
}

/** 取消视频生成任务 */
export function cancelVideoTask(id: string) {
  return post<void>(`${BASE_URL}/${id}/cancel`)
}

/** 删除视频生成记录 */
export function deleteVideoTask(id: string) {
  return del<void>(`${BASE_URL}/${id}`)
}
