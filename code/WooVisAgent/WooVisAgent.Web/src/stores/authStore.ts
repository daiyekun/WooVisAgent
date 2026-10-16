import { computed, ref } from 'vue'
import { defineStore } from 'pinia'

import { authApi, clearAuthToken, getAuthToken, setAuthToken } from '@/api'
import { STORAGE_KEYS } from '@/constants'
import type { AuthSession, AuthUser, LoginInput, RegisterInput } from '@/types'

function loadCachedUser(): AuthUser | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEYS.authUser)
    return raw ? (JSON.parse(raw) as AuthUser) : null
  } catch {
    return null
  }
}

function persistUser(user: AuthUser | null): void {
  if (user) localStorage.setItem(STORAGE_KEYS.authUser, JSON.stringify(user))
  else localStorage.removeItem(STORAGE_KEYS.authUser)
}

/**
 * 登录态统一收口。
 * Token 由 api/http.ts 负责读写并自动注入 Authorization 头，这里只维护内存态与用户资料。
 */
export const useAuthStore = defineStore('auth', () => {
  const token = ref<string | null>(getAuthToken())
  const user = ref<AuthUser | null>(token.value ? loadCachedUser() : null)
  /** 是否已完成过一次资料同步（避免守卫重复请求 /api/auth/me） */
  const profileLoaded = ref(false)

  const isAuthenticated = computed(() => Boolean(token.value))
  const displayName = computed(() => user.value?.userName ?? '未登录')
  const role = computed(() => user.value?.role ?? '')

  function applySession(session: AuthSession): void {
    setAuthToken(session.token)
    token.value = session.token

    const profile: AuthUser = {
      userId: session.userId,
      userName: session.userName,
      email: session.email,
      role: session.role,
    }
    user.value = profile
    persistUser(profile)
    profileLoaded.value = true
  }

  async function login(input: LoginInput): Promise<AuthSession> {
    const session = await authApi.login(input)
    applySession(session)
    return session
  }

  async function register(input: RegisterInput): Promise<AuthSession> {
    const session = await authApi.register(input)
    applySession(session)
    return session
  }

  /** 刷新页面后恢复会话：带上本地 Token 拉取最新资料，失败则视为登录失效 */
  async function loadProfile(force = false): Promise<AuthUser | null> {
    if (!isAuthenticated.value) return null
    if (profileLoaded.value && !force) return user.value

    try {
      const profile = await authApi.fetchCurrentUser({ silent: true })
      user.value = profile
      persistUser(profile)
      profileLoaded.value = true
      return profile
    } catch {
      logout()
      return null
    }
  }

  function logout(): void {
    clearAuthToken()
    persistUser(null)
    token.value = null
    user.value = null
    profileLoaded.value = false
  }

  return {
    token,
    user,
    isAuthenticated,
    displayName,
    role,
    profileLoaded,
    login,
    register,
    loadProfile,
    logout,
  }
})
