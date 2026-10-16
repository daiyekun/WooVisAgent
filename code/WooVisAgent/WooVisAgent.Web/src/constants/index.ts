import type { ModelCategory, TaskStatus, TaskType } from '@/types'

/** ==================== 任务状态展示 ==================== */

export const TASK_STATUS_META: Record<
  TaskStatus,
  { label: string; type: 'default' | 'info' | 'success' | 'warning' | 'error'; isFinal: boolean }
> = {
  pending: { label: '待提交', type: 'default', isFinal: false },
  queued: { label: '排队中', type: 'info', isFinal: false },
  running: { label: '生成中', type: 'warning', isFinal: false },
  succeeded: { label: '已完成', type: 'success', isFinal: true },
  failed: { label: '失败', type: 'error', isFinal: true },
  canceled: { label: '已取消', type: 'default', isFinal: true },
}

export const TASK_STATUS_OPTIONS = (
  Object.keys(TASK_STATUS_META) as TaskStatus[]
).map((value) => ({ label: TASK_STATUS_META[value].label, value }))

/** ==================== 任务类型展示 ==================== */

export const TASK_TYPE_META: Record<TaskType, { label: string; icon: string }> = {
  image: { label: '图片生成', icon: 'image' },
  video: { label: '视频生成', icon: 'video' },
}

export const TASK_TYPE_OPTIONS = (Object.keys(TASK_TYPE_META) as TaskType[]).map((value) => ({
  label: TASK_TYPE_META[value].label,
  value,
}))

/** ==================== 模型分类展示 ==================== */

export const MODEL_CATEGORY_META: Record<ModelCategory, { label: string }> = {
  image: { label: '图片模型' },
  video: { label: '视频模型' },
  llm: { label: '语言模型' },
}

export const MODEL_CATEGORY_OPTIONS = (Object.keys(MODEL_CATEGORY_META) as ModelCategory[]).map(
  (value) => ({ label: MODEL_CATEGORY_META[value].label, value }),
)

/** 常见供应方，供表单下拉预置 */
export const MODEL_PROVIDER_OPTIONS = [
  { label: 'OpenAI', value: 'OpenAI' },
  { label: '阿里云百炼', value: 'AliyunBailian' },
  { label: '火山引擎即梦', value: 'VolcengineJimeng' },
  { label: '可灵 Kling', value: 'Kling' },
  { label: '本地 ComfyUI', value: 'ComfyUI' },
  { label: '自定义', value: 'Custom' },
]

/** ==================== 其它常量 ==================== */

export const DEFAULT_PAGE_SIZE = 12

export const PAGE_SIZE_OPTIONS = [12, 24, 48, 96].map((value) => ({
  label: `${value} 条/页`,
  value,
}))

/** localStorage 键名统一收口 */
export const STORAGE_KEYS = {
  appSettings: 'woovis:app-settings',
  taskListFilters: 'woovis:task-filters',
  /** JWT，读写封装在 api/http.ts 的 getAuthToken/setAuthToken */
  accessToken: 'woovis:access-token',
  /** 当前登录用户资料，刷新页面时用于立即渲染，再由 /api/auth/me 校正 */
  authUser: 'woovis:auth-user',
  /** 登录页「记住账号」勾选后缓存的账号 */
  rememberedAccount: 'woovis:remembered-account',
} as const

export const APP_TITLE = import.meta.env.VITE_APP_TITLE || 'WooVisAgent 控制台'

/** 产品短名，用于品牌标识与登录页视觉 */
export const APP_NAME = 'WooVisAgent'

/** 产品一句话定位，出现在品牌标识下方 */
export const APP_SLOGAN = '图影创作平台'
