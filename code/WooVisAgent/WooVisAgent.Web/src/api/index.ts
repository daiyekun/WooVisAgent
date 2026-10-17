/**
 * API 统一出口
 *
 * 用法：
 *   import { taskApi, modelApi } from '@/api'
 *   const list = await taskApi.fetchTasks({ page: 1, pageSize: 12 })
 *
 * 或者按需引入具体方法：
 *   import { fetchTasks } from '@/api/task'
 */
export * from './http'
export * from './types'

export * as authApi from './auth'
export * as fileApi from './file'
export * as imageApi from './image'
export * as videoApi from './video'
export * as taskApi from './task'
export * as modelApi from './model'
export * as materialApi from './material'
export * as conversationApi from './conversation'

export type { AuthSession, AuthUser, EmailCodeResult, LoginInput, RegisterInput } from './auth'
export type { ImageGenerationInput, ImageGenerationResult } from './image'
export type { VideoGenerationInput, VideoGenerationResult } from './video'
