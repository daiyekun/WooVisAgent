/** 统一的错误对象，http 拦截器会把各种异常收敛成这个结构 */
export interface ApiError {
  /** HTTP 状态码，网络异常时为 undefined */
  status?: number
  /** 业务错误码（后端返回时才有） */
  code?: string
  /** 可直接展示给用户的文案 */
  message: string
  /** 原始错误信息，排查用 */
  details?: unknown
}

/** ASP.NET Core ProblemDetails 结构 */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  /** 模型校验失败时的字段级错误 */
  errors?: Record<string, string[]>
}

/** 单次请求的可选项 */
export interface RequestOptions {
  /** 为 true 时不弹出全局错误提示，由调用方自行处理 */
  silent?: boolean
  /** 覆盖默认超时时间（毫秒） */
  timeout?: number
  signal?: AbortSignal
}

/** 上传文件的返回结构 */
export interface UploadResult {
  id: string
  url: string
  fileName: string
  fileSize: number
  contentType?: string
}
