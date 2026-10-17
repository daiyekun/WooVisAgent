/**
 * ============================================================================
 * 对话创作 · 静态数据与本地类型
 * ============================================================================
 *
 * 这里的类型只服务对话流本身（一条消息里既有文本、又有参考图、又挂一张生成卡），
 * 与后端的 GenerationTask 不是一回事，因此单独放在 views 层，不污染 src/types。
 *
 * 真实链路：提交任务 → SignalR / 轮询拿进度 → 完成后把产物写回卡片，
 * 全程只依赖 taskId，会话本身不落库（与原型一致：图片/视频共用一条对话流）。
 */

import type { TaskStatus } from '@/types'

export type ChatMode = 'image' | 'video'

/** 输入区里的参考图。url 上传成功后才有值，previewUrl 是本地即时预览 */
export interface ChatAttachment {
  id: string
  name: string
  size: number
  /** 本地 blob 预览地址 */
  previewUrl: string
  /** 上传成功后服务端返回的地址（/uploads/...），提交时用它 */
  url?: string
  uploading?: boolean
  error?: string
  /**
   * 这张参考图来自会话里哪条结果卡（图生图迭代时带上）。
   * 用于在结果卡上标注「改自第几轮」，也让"我从哪张图改过来的"这件事可追溯。
   */
  sourceMessageId?: string
}

/** 生成产物。视频产物额外带封面与时长，渲染时要分开处理 */
export interface ChatImage {
  id: string
  url: string
  /** 产物类型。历史消息恢复时按任务类型推断，缺省视为图片 */
  type?: 'image' | 'video'
  /** 视频封面 */
  thumbnailUrl?: string
  fileName?: string
  fileSize?: number
  width?: number
  height?: number
  /** 视频时长，单位秒 */
  duration?: number
}

/** 图片轮的入参快照，用于「重跑这一轮」 */
export interface ImageChatPayload {
  modelId: string
  prompt: string
  negativePrompt?: string
  width: number
  height: number
  batchSize: number
  steps: number
  cfgScale: number
  seed?: number
}

/** 视频轮的入参快照，用于「重跑这一轮」 */
export interface VideoChatPayload {
  modelId: string
  prompt: string
  negativePrompt?: string
  durationSeconds: number
  fps: number
  resolution: string
  withAudio: boolean
  seed?: number
}

/** 挂在助手消息下的生成卡：既是进度条，也是结果展示区 */
export interface ChatCard {
  kind: ChatMode
  taskId?: string
  status: TaskStatus
  /** 0 - 100 */
  progress: number
  model: string
  /** 右侧摘要，如「1024×1024 · 2 张 · 用时 6.2s」 */
  meta: string
  images: ChatImage[]
  /** 失败原因 */
  error?: string
  /** 提交时刻，用于算用时 */
  startedAt?: number
  /** 后端回传的阶段文案，如「模型正在绘制，等待返回」 */
  stage?: string
  /** 本轮尺寸，卡片自己记住，避免用户中途改比例后把已完成的卡片文案改掉 */
  sizeLabel: string
  /** 本轮张数；视频恒为 1 */
  count: number
  /** 本轮用到的参考图地址（图生图 / 图生视频时非空），用于追溯"这张图是从哪张改来的" */
  references: string[]
  /** 参考图来自哪一轮（值为那张产物所在卡片的消息 id），用于显示"改自上一轮" */
  fromMessageId?: string
  /** 重跑所需的原始入参，按 kind 取对应分支 */
  payload: ImageChatPayload | VideoChatPayload
}

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  time: string
  text: string
  attachments?: ChatAttachment[]
  card?: ChatCard
}

export const NEW_CHAT_TITLE = '新建创作'

/** 空会话时的起始卡片 */
export const starterCards = [
  {
    icon: 'image',
    tone: 'ic-a',
    title: '做一张产品主视觉',
    desc: '极简留白，柔和自然光',
    prompt: '做一张极简风格的产品主视觉，大量留白，柔和自然光，主体居中，右下角留出品牌文案位置。',
    mode: 'image' as ChatMode,
  },
  {
    icon: 'sparkles',
    tone: 'ic-c',
    title: '改一版现有画面',
    desc: '上传参考图，保留构图换氛围',
    prompt: '保留参考图的构图与主体，把氛围光换成傍晚暖调，增加轻微胶片颗粒感。',
    mode: 'image' as ChatMode,
  },
  {
    icon: 'grid',
    tone: 'ic-d',
    title: '一次出四张方案',
    desc: '同一提示词横向对比',
    prompt: '同一个提示词出 4 张不同构图方案，方便我横向对比挑选。',
    mode: 'image' as ChatMode,
  },
  {
    icon: 'images',
    tone: 'ic-e',
    title: '做一张竖版海报',
    desc: '9:16 竖版，适合投放',
    prompt: '做一张 9:16 竖版产品海报，主体居中偏上，留出顶部标题与底部行动按钮的位置，背景干净。',
    mode: 'image' as ChatMode,
  },
  {
    icon: 'video',
    tone: 'ic-b',
    title: '生成一段短视频',
    desc: '文生视频，描述画面与运镜',
    prompt: '清晨的海边，镜头从礁石缓慢向上摇起，阳光洒在海面上泛着细碎的光，节奏舒缓。',
    mode: 'video' as ChatMode,
  },
]

/** 出图比例 → 实际宽高 */
export const RATIO_PRESETS = [
  { label: '1:1', width: 1024, height: 1024 },
  { label: '16:9', width: 1280, height: 720 },
  { label: '9:16', width: 720, height: 1280 },
  { label: '3:4', width: 896, height: 1152 },
  { label: '4:3', width: 1152, height: 896 },
] as const

/** 一次生成的张数 */
export const BATCH_OPTIONS = [1, 2, 4] as const

/**
 * 视频时长可选项（秒）。
 *
 * 各家模型的时长档位不一样，提交时后端会按模型能力兜底，
 * 这里给的是常见交集，避免用户填一个模型根本不认的值。
 */
export const DURATION_OPTIONS = [3, 5, 10] as const

/** 视频帧率 */
export const FPS_OPTIONS = [24, 30] as const

/** 生成中的阶段文案：后端只给 0-100 的进度，这里按区间补一句人话 */
export const IMAGE_STAGES = ['正在排队', '正在解析提示词', '正在构图', '正在细化细节', '正在合成输出']

export function imageStageText(progress: number): string {
  if (progress < 5) return IMAGE_STAGES[0]
  if (progress < 30) return IMAGE_STAGES[1]
  if (progress < 60) return IMAGE_STAGES[2]
  if (progress < 90) return IMAGE_STAGES[3]
  return IMAGE_STAGES[4]
}

/** 视频比图片慢得多，阶段文案单独一套，否则 0-90% 全卡在"正在构图"上 */
export const VIDEO_STAGES = ['正在排队', '正在解析提示词', '正在生成关键帧', '正在渲染画面', '正在编码输出']

export function videoStageText(progress: number): string {
  if (progress < 5) return VIDEO_STAGES[0]
  if (progress < 25) return VIDEO_STAGES[1]
  if (progress < 60) return VIDEO_STAGES[2]
  if (progress < 90) return VIDEO_STAGES[3]
  return VIDEO_STAGES[4]
}

/** 状态 → 卡片上的标签样式与文案 */
export const CHAT_STATUS_META: Record<TaskStatus, { label: string; cls: string }> = {
  pending: { label: '待提交', cls: 'tag-dim' },
  queued: { label: '排队中', cls: 'tag-cyan' },
  running: { label: '生成中', cls: 'tag-cyan' },
  succeeded: { label: '已完成', cls: 'tag-ok' },
  failed: { label: '失败', cls: 'tag-err' },
  canceled: { label: '已取消', cls: 'tag-dim' },
}

/** 参考图体积上限，与后端 FileController 的 10MB 保持一致 */
export const MAX_REFERENCE_SIZE = 10 * 1024 * 1024

/** 一次最多几张参考图，与后端校验保持一致 */
export const MAX_REFERENCE_COUNT = 8
