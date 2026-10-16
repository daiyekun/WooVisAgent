<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import { APP_NAME, APP_SLOGAN, STORAGE_KEYS } from '@/constants'
import { useAuthStore } from '@/stores/authStore'
import { message } from '@/utils'

const router = useRouter()
const route = useRoute()
const authStore = useAuthStore()

const loading = ref(false)
const agreed = ref(false)
const remember = ref(false)
const showPassword = ref(false)

const form = reactive({
  account: '',
  password: '',
})

const errors = reactive({
  account: '',
  password: '',
})

onMounted(() => {
  const cached = localStorage.getItem(STORAGE_KEYS.rememberedAccount)
  if (cached) {
    form.account = cached
    remember.value = true
  }
})

function validate(): boolean {
  errors.account = form.account.trim() ? '' : '请输入邮箱或用户名'
  if (!form.password) errors.password = '请输入密码'
  else if (form.password.length < 8) errors.password = '密码长度至少 8 位'
  else errors.password = ''
  return !errors.account && !errors.password
}

/**
 * 登录成功后的回跳地址：由路由守卫写入 ?redirect=/tasks/create。
 * 没有回跳地址、或回跳地址是站点根路径时，统一落到「工作台」——
 * 对话窗口不在这里自动打开，需要用户自己点「新建创作」。
 */
function resolveRedirect(): string | { name: string } {
  const redirect = route.query.redirect
  if (typeof redirect === 'string' && redirect.startsWith('/') && redirect !== '/') {
    return redirect
  }
  return { name: 'Workspace' }
}

function handleForget(): void {
  message.info('密码重置功能尚未开放，请联系管理员协助处理')
}

async function handleSubmit(): Promise<void> {
  if (!validate()) return

  if (!agreed.value) {
    message.warning('请先阅读并同意用户服务协议与隐私政策')
    return
  }

  loading.value = true
  try {
    await authStore.login({ account: form.account.trim(), password: form.password })

    if (remember.value) localStorage.setItem(STORAGE_KEYS.rememberedAccount, form.account.trim())
    else localStorage.removeItem(STORAGE_KEYS.rememberedAccount)

    message.success('登录成功')
    await router.replace(resolveRedirect())
  } catch {
    // 具体错误文案由 api/http.ts 的响应拦截器统一提示
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="auth">
    <!-- ============ 左：品牌视觉 ============ -->
    <aside class="auth-visual">
      <div class="vhead">
        <div class="brand">
          <span class="brand-logo">
            <svg viewBox="0 0 24 24" fill="none" width="19" height="19">
              <path
                d="M8.4 6.4v11.2a1.1 1.1 0 0 0 1.68.94l8.8-5.6a1.1 1.1 0 0 0 0-1.88l-8.8-5.6A1.1 1.1 0 0 0 8.4 6.4z"
                fill="#fff"
              />
              <path
                d="M3.6 9.4v5.2"
                stroke="#fff"
                stroke-width="2.2"
                stroke-linecap="round"
                opacity=".72"
              />
            </svg>
          </span>
          <span class="brand-txt">
            <b>{{ APP_NAME }}</b>
            <small>{{ APP_SLOGAN }}</small>
          </span>
        </div>
      </div>

      <div class="vbody">
        <span class="eyebrow">
          <span class="dot" style="background: var(--brand)" />
          图片 · 视频 · 一个工作台
        </span>

        <h1>把想法说出来<br /><em>剩下的交给模型</em></h1>

        <p>
          在对话里直接描述你要的画面或镜头，平台会调用你自己配置的模型完成生成。所有作品自动归档，随时回看与复用。
        </p>

        <div class="vfeats">
          <div class="vfeat">
            <span class="fi"><app-icon name="image" :size="15" /></span>
            对话式生成，支持参考图与首尾帧
          </div>
          <div class="vfeat">
            <span class="fi"><app-icon name="sliders" :size="15" /></span>
            模型自由接入，密钥与参数由你掌控
          </div>
          <div class="vfeat">
            <span class="fi"><app-icon name="folder" :size="15" /></span>
            作品自动归集，按图 / 视频分类检索
          </div>
        </div>
      </div>

      <div class="vpreview">
        <div class="pv" style="background: linear-gradient(150deg, #0b2a3a, #0e7490)">🎨</div>
        <div class="pv" style="background: linear-gradient(150deg, #241b4d, #5b21b6)">🎬</div>
        <div class="pv" style="background: linear-gradient(150deg, #3a1d0b, #c2410c)">🥤</div>
      </div>

      <div class="vfoot">© 2026 {{ APP_NAME }} · {{ APP_SLOGAN }}</div>
    </aside>

    <!-- ============ 右：登录表单 ============ -->
    <section class="auth-panel">
      <div class="auth-card">
        <div class="m-brand brand">
          <span class="brand-logo">
            <svg viewBox="0 0 24 24" fill="none" width="19" height="19">
              <path
                d="M8.4 6.4v11.2a1.1 1.1 0 0 0 1.68.94l8.8-5.6a1.1 1.1 0 0 0 0-1.88l-8.8-5.6A1.1 1.1 0 0 0 8.4 6.4z"
                fill="#fff"
              />
              <path
                d="M3.6 9.4v5.2"
                stroke="#fff"
                stroke-width="2.2"
                stroke-linecap="round"
                opacity=".72"
              />
            </svg>
          </span>
          <span class="brand-txt">
            <b>{{ APP_NAME }}</b>
            <small>{{ APP_SLOGAN }}</small>
          </span>
        </div>

        <h2>欢迎回来</h2>
        <p class="lead">登录后即可使用图片与视频生成能力</p>

        <form class="auth-form" @submit.prevent="handleSubmit">
          <div class="field">
            <label for="account">账号</label>
            <div class="input-group">
              <span class="ig-icon"><app-icon name="user" :size="16" /></span>
              <input
                id="account"
                v-model="form.account"
                class="input"
                :class="{ 'is-error': errors.account }"
                type="text"
                placeholder="邮箱或用户名"
                autocomplete="username"
                name="account"
                @input="errors.account = ''"
              />
            </div>
            <span v-if="errors.account" class="field-error">{{ errors.account }}</span>
          </div>

          <div class="field">
            <label for="pwd">密码</label>
            <div class="input-group">
              <span class="ig-icon"><app-icon name="lock" :size="16" /></span>
              <input
                id="pwd"
                v-model="form.password"
                class="input"
                :class="{ 'is-error': errors.password }"
                :type="showPassword ? 'text' : 'password'"
                placeholder="请输入密码"
                autocomplete="current-password"
                name="password"
                @input="errors.password = ''"
              />
              <span class="suffix">
                <button
                  class="peek"
                  type="button"
                  :title="showPassword ? '隐藏密码' : '显示密码'"
                  @click="showPassword = !showPassword"
                >
                  <app-icon :name="showPassword ? 'eye-off' : 'eye'" :size="16" />
                </button>
              </span>
            </div>
            <span v-if="errors.password" class="field-error">{{ errors.password }}</span>
          </div>

          <div class="row-between">
            <label class="check">
              <input v-model="remember" type="checkbox" />
              <span>记住账号</span>
            </label>
            <a href="#" @click.prevent="handleForget">忘记密码？</a>
          </div>

          <button class="btn btn-primary btn-lg btn-block" type="submit" :disabled="loading">
            <span v-if="loading" class="spinner" />
            {{ loading ? '登录中…' : '登录' }}
          </button>

          <label class="check">
            <input v-model="agreed" type="checkbox" />
            <span>
              我已阅读并同意 <a href="#" @click.prevent>《用户服务协议》</a> 与
              <a href="#" @click.prevent>《隐私政策》</a>
            </span>
          </label>
        </form>

        <div class="auth-switch">
          还没有账号？
          <router-link :to="{ name: 'Register' }">立即注册</router-link>
        </div>
      </div>
    </section>
  </div>
</template>
