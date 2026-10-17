import { del, get, post } from './http'
import type { RequestOptions } from './types'
import type { GenerationTask, PageQuery, PagedResult, TaskAsset, TaskStatus } from '@/types'

const BASE_URL = '/api/image/generations'

/** 图片生成入参 */
export interface ImageGenerationInput {
  /** 不传则由后端按 prompt 截取生成 */
  name?: string
  modelId: string
  prompt: string
  negativePrompt?: string
  width?: number
  height?: number
  /** 一次生成的张数 */
  batchSize?: number
  steps?: number
  cfgScale?: number
  seed?: number
  /** 参考图/垫图 URL */
  referenceImageUrls?: string[]
  /**
   * 所属创作会话。不传则任务不归属任何会话（不出现在最近会话里）；
   * 聊天页始终会带上，这样多轮对话的历史才能完整回放。
   */
  conversationId?: string
  /** 模型特有参数，键值对应 ModelParamSpec.key */
  params?: Record<string, unknown>
}

export interface ImageGenerationResult {
  taskId: string
  status: TaskStatus
  /** 同步返回时直接带上产物，异步场景为空数组 */
  images: TaskAsset[]
}

/** 提交图片生成任务 */
export function generateImage(input: ImageGenerationInput) {
  return post<ImageGenerationResult>(BASE_URL, input)
}

/** 查询图片生成任务列表 */
export function fetchImageTasks(query: PageQuery & Record<string, unknown> = {}) {
  return get<PagedResult<GenerationTask>>(BASE_URL, query)
}

/**
 * 查询单条图片生成任务。
 * 轮询兜底时传 { silent: true }，避免网络抖动时反复弹错误提示。
 */
export function fetchImageTask(id: string, options?: RequestOptions) {
  return get<GenerationTask>(`${BASE_URL}/${id}`, undefined, options)
}

/** 取消图片生成任务 */
export function cancelImageTask(id: string) {
  return post<void>(`${BASE_URL}/${id}/cancel`)
}

/** 删除图片生成记录 */
export function deleteImageTask(id: string) {
  return del<void>(`${BASE_URL}/${id}`)
}
