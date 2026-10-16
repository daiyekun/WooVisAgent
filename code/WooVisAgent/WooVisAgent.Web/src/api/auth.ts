import { get, post } from './http'
import type { AuthSession, AuthUser, EmailCodeResult, LoginInput, RegisterInput } from '@/types'

/** 对应后端 WooVisAgent.Api/Controllers/AuthController.cs */
const BASE_URL = '/api/auth'

/** 登录：account 支持邮箱或用户名 */
export function login(input: LoginInput) {
  return post<AuthSession>(`${BASE_URL}/login`, input)
}

/** 注册：成功即返回 Token，可直接进入控制台 */
export function register(input: RegisterInput) {
  return post<AuthSession>(`${BASE_URL}/register`, input)
}

/** 发送邮箱验证码 */
export function sendEmailCode(email: string) {
  return post<EmailCodeResult>(`${BASE_URL}/email-code`, { email })
}

/** 读取当前登录用户资料，失败时不弹全局提示（由调用方决定是否清理会话） */
export function fetchCurrentUser(options?: { silent?: boolean }) {
  return get<AuthUser>(`${BASE_URL}/me`, undefined, options)
}

export type { AuthSession, AuthUser, EmailCodeResult, LoginInput, RegisterInput }
