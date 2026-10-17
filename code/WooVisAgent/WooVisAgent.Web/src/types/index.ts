/** ==================== 通用 ==================== */

/** 分页查询参数 */
export interface PageQuery {
  page?: number
  pageSize?: number
  keyword?: string
}

/** 分页结果 */
export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

/** 排序方向 */
export type SortOrder = 'asc' | 'desc'

/** ==================== 任务 ==================== */

/** 任务类型 */
export type TaskType = 'image' | 'video'

/** 任务状态，与后端 WooVisAgent.EventBus 中的事件状态保持一致 */
export type TaskStatus = 'pending' | 'queued' | 'running' | 'succeeded' | 'failed' | 'canceled'

/** 素材（任务的输入或产物） */
export interface TaskAsset {
  id: string
  type: 'image' | 'video'
  url: string
  thumbnailUrl?: string
  fileName?: string
  /** 字节 */
  fileSize?: number
  width?: number
  height?: number
  /** 视频时长，单位秒 */
  duration?: number
}

/** 生成任务 */
export interface GenerationTask {
  id: string
  name: string
  type: TaskType
  status: TaskStatus
  /** 0 - 100 */
  progress: number
  modelId: string
  modelName?: string
  prompt: string
  negativePrompt?: string
  /** 模型自定义参数，键值对应 ModelParamSpec.key */
  params: Record<string, unknown>
  /** 输入素材，如首帧图、参考图 */
  inputs: TaskAsset[]
  /** 生成产物 */
  outputs: TaskAsset[]
  errorMessage?: string
  /** ISO 8601 字符串 */
  createdAt: string
  startedAt?: string
  finishedAt?: string
  createdBy?: string
  /** 是否已被「选秀」收录进我的作品；为 true 时前端禁用选秀与重试并显示星标角标 */
  isSelected?: boolean
  /** 所属创作会话；不在任何会话下（从任务列表直接提交）时为空 */
  conversationId?: string
  /** 由哪个任务重试 / 迭代而来；会话里标注"改自上一轮"用 */
  sourceTaskId?: string
}

/** ==================== 会话 ==================== */

/** 会话摘要，侧边栏最近会话与列表页共用 */
export interface Conversation {
  id: string
  title: string
  /** 会话内出现过的生成类型，用于按图片 / 视频分类 */
  type: TaskType
  messageCount: number
  /** 会话内的生成任务数，一次会话可对应多个任务 */
  taskCount: number
  createdAt: string
  /** 最后修改时间，最近会话按它倒序 */
  updatedAt: string
}

/** 会话内的单条消息 */
export interface ConversationMessage {
  id: string
  role: 'user' | 'assistant'
  text: string
  /** 用户消息上是参考图，助手消息上是产物 */
  assets: TaskAsset[]
  /** 本轮生成参数快照，用于重跑 / 作为参考图继续 */
  params: Record<string, unknown>
  /** 关联任务 id；用户消息为空 */
  taskId?: string
  /** 同一次生成里的配对消息 id */
  pairMessageId?: string
  /** 图生图来源任务 id */
  sourceTaskId?: string
  seq: number
  errorMessage?: string
  createdAt: string
}

/** 会话详情：会话 + 消息 + 任务 */
export interface ConversationDetail {
  conversation: Conversation
  messages: ConversationMessage[]
  tasks: GenerationTask[]
}

/** 会话总数与分类计数 */
export interface ConversationSummary {
  total: number
  imageCount: number
  videoCount: number
}

/** 删除会话的影响面 */
export interface DeleteConversationResult {
  deletedMessages: number
  deletedTasks: number
  /** 已选秀、因而在作品库中保留下来的任务数 */
  keptMaterials: number
}

/** 新建任务入参 */
export interface CreateTaskInput {
  name: string
  type: TaskType
  modelId: string
  prompt: string
  negativePrompt?: string
  params?: Record<string, unknown>
  inputAssetIds?: string[]
}

/** 任务列表查询参数 */
export interface TaskQuery extends PageQuery {
  type?: TaskType
  status?: TaskStatus
  modelId?: string
  /** [开始时间, 结束时间]，ISO 8601 */
  dateRange?: [string, string] | null
}

/** ==================== 模型配置 ==================== */

export type ModelCategory = 'image' | 'video' | 'llm'

/** 模型参数规格，用于动态渲染 ModelSettingForm */
export interface ModelParamSpec {
  key: string
  label: string
  type: 'number' | 'string' | 'boolean' | 'select'
  defaultValue?: string | number | boolean
  options?: { label: string; value: string | number }[]
  min?: number
  max?: number
  step?: number
  required?: boolean
  placeholder?: string
  description?: string
}

/** 模型配置 */
export interface ModelConfig {
  id: string
  name: string
  /** 供应方，如 OpenAI / 阿里云百炼 / 本地 ComfyUI */
  provider: string
  category: ModelCategory
  /** 调用时传给后端的模型标识 */
  modelKey?: string
  endpoint?: string
  /** 仅模型详情接口向配置所有者返回，用于编辑时回填 */
  apiKey?: string
  enabled: boolean
  isDefault: boolean
  params: ModelParamSpec[]
  /** 视频交付模板；留空表示按预设自动推断 */
  delivery?: VideoDeliveryTemplate | null
  /** 是否支持文生视频（不传首帧图）。只支持图生的模型应为 false */
  supportsTextToVideo?: boolean
  /** 是否支持图生视频（传首帧图）。只支持文生的模型应为 false */
  supportsImageToVideo?: boolean
  remark?: string
  createdAt: string
  updatedAt: string
}

/**
 * 视频交付模板。字段全部可选，只覆盖与预设不同的部分。
 * 内置预设：openai-videos / dashscope-video / dashscope-legacy-video / siliconflow-video
 */
export interface VideoDeliveryTemplate {
  preset?: string
  submitPath?: string
  statusPath?: string
  statusMethod?: 'GET' | 'POST' | string
  statusIdField?: string
  taskIdPath?: string
  statusValuePath?: string
  succeededStatus?: string
  failedStatuses?: string
  videoUrlPath?: string
  errorPath?: string
  /** 产物取回方式：url（响应体带地址）/ content（另外调接口下二进制）/ base64 */
  outputKind?: 'url' | 'content' | 'base64' | string
  contentPath?: string
  extraHeaders?: Record<string, string>
  /** 请求体字段映射：内部语义 → 请求体点号路径。空串表示该字段不传 */
  requestFields?: {
    model?: string
    prompt?: string
    negativePrompt?: string
    firstFrame?: string
    duration?: string
    resolution?: string
    ratio?: string
    fps?: string
    seed?: string
    extraParamsContainer?: string
  }
  /** 分辨率表达形式：omit / pixel / tier / tierRatio */
  resolutionStyle?: 'omit' | 'pixel' | 'tier' | 'tierRatio' | string
}

/** 新建/编辑模型入参 */
export type SaveModelInput = Omit<ModelConfig, 'id' | 'createdAt' | 'updatedAt'> &
  Partial<Pick<ModelConfig, 'id'>>

/** 模型连通性测试结果 */
export interface ModelTestResult {
  success: boolean
  /** 耗时毫秒 */
  latency?: number
  message?: string
}

/** ==================== 素材库 ==================== */

/** 作品（我的产品）。由任务「选秀」收录，或在作品库手动上传 */
export interface MaterialAsset extends TaskAsset {
  /** 所属分组/文件夹 */
  groupName?: string
  tags?: string[]
  /** 生成该作品的提示词快照 */
  prompt?: string
  /** 来源任务 ID；手动上传的作品没有来源 */
  sourceTaskId?: string
  /** 来源任务名快照 */
  sourceTaskName?: string
  createdAt: string
}

export interface MaterialQuery extends PageQuery {
  type?: 'image' | 'video'
  groupName?: string
}

/** 选秀接口返回结构 */
export interface SelectTaskResult {
  /** 本次入库的作品条数 */
  addedCount: number
  /** 选秀后的任务最新状态 */
  task: GenerationTask
}

/** ==================== 身份与鉴权 ==================== */

/** 登录入参，account 支持邮箱或用户名 */
export interface LoginInput {
  account: string
  password: string
}

/** 注册入参，邮箱验证码是否必填由后端 Identity:RequireEmailCode 决定 */
export interface RegisterInput {
  email: string
  password: string
  emailCode?: string
}

/** 当前登录用户资料，对应后端 GetUserProfileQuery 返回结构 */
export interface AuthUser {
  userId: string
  userName: string
  email?: string | null
  role: string
}

/** 注册 / 登录成功后的会话信息 */
export interface AuthSession extends AuthUser {
  token: string
  /** ISO 8601 字符串 */
  expiresAt: string
}

/** 发送邮箱验证码的返回结构（开发环境会直接回传 code 便于联调） */
export interface EmailCodeResult {
  email: string
  expireSeconds: number
  code?: string | null
}

/** ==================== SignalR ==================== */

/** 任务实时推送事件名，需与后端 Hub 方法名一致 */
export type TaskHubEvent =
  | 'TaskCreated'
  | 'TaskStatusChanged'
  | 'TaskProgress'
  | 'TaskCompleted'
  | 'TaskFailed'

/** 任务进度推送负载 */
export interface TaskProgressPayload {
  taskId: string
  status: TaskStatus
  progress: number
  message?: string
  outputs?: TaskAsset[]
}
