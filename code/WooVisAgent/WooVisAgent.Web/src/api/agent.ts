import { get, post } from './http'

const BASE_URL = '/api/agent'

/** Agent 可用性：没配语言模型时 available=false，reason 可直接展示 */
export interface AgentStatus {
  enabled: boolean
  available: boolean
  modelName: string | null
  modelKey: string | null
  reason: string | null
}

export interface PromptPolishInput {
  prompt: string
  /** 目标产物，决定润色侧重点（镜头运动 vs 构图光线） */
  target?: 'image' | 'video'
  /** 可选风格偏好，如「电影感」 */
  style?: string | null
}

export interface PromptPolishResult {
  original: string
  polished: string
  negative: string | null
  tips: string[]
}

export interface VideoPlanInput {
  idea: string
  mode?: 'text' | 'image'
}

/** 视频参数建议。只是建议，不会创建任务 */
export interface VideoPlanResult {
  prompt: string
  modelId: string | null
  modelName: string | null
  durationSeconds: number | null
  resolution: string | null
  ratio: string | null
  notes: string[]
}

export function fetchAgentStatus() {
  return get<AgentStatus>(`${BASE_URL}/status`)
}

export function polishPrompt(input: PromptPolishInput) {
  return post<PromptPolishResult>(`${BASE_URL}/polish`, input)
}

export function planVideo(input: VideoPlanInput) {
  return post<VideoPlanResult>(`${BASE_URL}/video-plan`, input)
}
