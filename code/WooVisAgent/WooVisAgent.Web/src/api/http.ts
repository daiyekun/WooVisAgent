import axios, { AxiosError, type AxiosRequestConfig, type AxiosInstance } from 'axios'

import { STORAGE_KEYS } from '@/constants'
import { message } from '@/utils/feedback'
import type { ApiError, ProblemDetails, RequestOptions, UploadResult } from './types'

/** 留空表示走同源（开发环境由 vite.config.ts 的 proxy 转发） */
export const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || ''

const TOKEN_STORAGE_KEY = STORAGE_KEYS.accessToken

export function getAuthToken(): string | null {
  return localStorage.getItem(TOKEN_STORAGE_KEY)
}

/** 由 WooVisAgentServices.IdentityService 签发，登录成功后写入 */
export function setAuthToken(token: string): void {
  localStorage.setItem(TOKEN_STORAGE_KEY, token)
}

export function clearAuthToken(): void {
  localStorage.removeItem(TOKEN_STORAGE_KEY)
}

const http: AxiosInstance = axios.create({
  baseURL: API_BASE_URL,
  timeout: 30_000,
  headers: {
    'Content-Type': 'application/json',
  },
})

/** 请求拦截：注入鉴权头与链路追踪 ID */
http.interceptors.request.use((config) => {
  const token = getAuthToken()
  if (token) {
    config.headers.Authorization = `Bearer ${token}`
  }

  if (!config.headers['X-Request-Id']) {
    config.headers['X-Request-Id'] = crypto.randomUUID?.() ?? `${Date.now()}`
  }

  return config
})

/** 把 axios 的各种异常收敛成 ApiError */
function normalizeError(error: AxiosError<ProblemDetails>): ApiError {
  if (error.code === 'ERR_CANCELED') {
    return { message: '请求已取消', details: error }
  }

  if (!error.response) {
    return {
      message: error.code === 'ECONNABORTED' ? '请求超时，请稍后重试' : '网络异常，无法连接后端服务',
      details: error,
    }
  }

  const { status, data } = error.response
  const fieldErrors = data?.errors ? Object.values(data.errors).flat().join('；') : ''

  const fallback: Record<number, string> = {
    400: '请求参数有误',
    401: '登录状态已失效，请重新登录',
    403: '没有访问该资源的权限',
    404: '请求的资源不存在',
    409: '资源状态冲突，请刷新后重试',
    500: '服务端异常，请稍后重试',
    502: '网关异常，后端服务可能未启动',
    503: '服务暂不可用，请稍后重试',
  }

  return {
    status,
    message: data?.detail || fieldErrors || data?.title || fallback[status] || `请求失败(${status})`,
    details: data ?? error,
  }
}

/** 响应拦截：直接返回 data；错误统一提示 */
http.interceptors.response.use(
  (response) => response.data,
  (error: AxiosError<ProblemDetails>) => {
    const apiError = normalizeError(error)
    const silent = Boolean((error.config as AxiosRequestConfig & { silent?: boolean })?.silent)

    // 401 时清理本地 token，交给路由守卫处理跳转
    if (apiError.status === 401) {
      clearAuthToken()
    }

    if (!silent) {
      message.error(apiError.message)
    }

    return Promise.reject(apiError)
  },
)

/** 统一请求入口：响应拦截器已把 AxiosResponse 解包成业务数据，这里补上返回类型 */
export function request<T>(config: AxiosRequestConfig, options: RequestOptions = {}): Promise<T> {
  return http.request({
    ...config,
    timeout: options.timeout ?? 30_000,
    signal: options.signal,
    ...(options.silent ? { silent: true } : {}),
  } as AxiosRequestConfig) as unknown as Promise<T>
}

export function get<T>(url: string, params?: unknown, options?: RequestOptions): Promise<T> {
  return request<T>({ method: 'GET', url, params }, options)
}

export function post<T>(url: string, data?: unknown, options?: RequestOptions): Promise<T> {
  return request<T>({ method: 'POST', url, data }, options)
}

export function put<T>(url: string, data?: unknown, options?: RequestOptions): Promise<T> {
  return request<T>({ method: 'PUT', url, data }, options)
}

export function patch<T>(url: string, data?: unknown, options?: RequestOptions): Promise<T> {
  return request<T>({ method: 'PATCH', url, data }, options)
}

export function del<T>(url: string, params?: unknown, options?: RequestOptions): Promise<T> {
  return request<T>({ method: 'DELETE', url, params }, options)
}

/** 文件上传，走 multipart/form-data */
export function upload(
  url: string,
  file: File,
  extra?: Record<string, string>,
  options?: RequestOptions,
): Promise<UploadResult> {
  const formData = new FormData()
  formData.append('file', file)
  Object.entries(extra ?? {}).forEach(([key, value]) => formData.append(key, value))

  return request<UploadResult>(
    {
      method: 'POST',
      url,
      data: formData,
      headers: { 'Content-Type': 'multipart/form-data' },
    },
    options,
  )
}

export default http
