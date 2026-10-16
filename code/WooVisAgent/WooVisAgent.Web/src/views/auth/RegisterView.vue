<script setup lang="ts">
import { onBeforeUnmount, reactive, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'

import { authApi } from '@/api'
import AppIcon from '@/components/common/AppIcon.vue'
import { APP_NAME, APP_SLOGAN } from '@/constants'
import { useAuthStore } from '@/stores/authStore'
import { message } from '@/utils'

const router = useRouter()
const authStore = useAuthStore()

const loading = ref(false)
const agreed = ref(false)
const sending = ref(false)
const showPassword = ref(false)

/** 获取验证码的 60 秒倒计时 */
const countdown = ref(0)
let countdownTimer: ReturnType<typeof setInterval> | undefined

const form = reactive({
  email: '',
  emailCode: '',
  password: '',
  confirmPassword: '',
})

const errors = reactive({
  email: '',
  password: '',
  confirmPassword: '',
})

function stopCountdown(): void {
  if (countdownTimer) {
    clearInterval(countdownTimer)
    countdownTimer = undefined
  }
}

onBeforeUnmount(stopCountdown)

function validate(): boolean {
  const email = form.email.trim()
  if (!email) errors.email = '请输入邮箱'
  else if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) errors.email = '邮箱格式不正确'
  else errors.email = ''

  if (!form.password) errors.password = '请输入密码'
  else if (form.password.length < 8) errors.password = '密码长度至少 8 位'
  else if (!/^(?=.*[A-Za-z])(?=.*\d).+$/.test(form.password))
    errors.password = '密码需同时包含字母与数字'
  else errors.password = ''

  if (!form.confirmPassword) errors.confirmPassword = '请再次输入密码'
  else if (form.confirmPassword !== form.password) errors.confirmPassword = '两次输入的密码不一致'
  else errors.confirmPassword = ''

  return !errors.email && !errors.password && !errors.confirmPassword
}

async function handleSendCode(): Promise<void> {
  if (sending.value || countdown.value > 0) return

  const email = form.email.trim()
  if (!email || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
    errors.email = '请先填写正确的邮箱'
    return
  }
  errors.email = ''

  sending.value = true
  try {
    const result = await authApi.sendEmailCode(email)
    // 开发环境未接入邮件通道，后端会把验证码直接回传
    if (result.code) message.info(`验证码已生成（开发环境）：${result.code}`, { duration: 8000 })
    else message.success('验证码已发送至邮箱')

    countdown.value = 60
    countdownTimer = setInterval(() => {
      countdown.value -= 1
      if (countdown.value <= 0) stopCountdown()
    }, 1000)
  } catch {
    // 错误提示已由响应拦截器统一处理
  } finally {
    sending.value = false
  }
}

async function handleSubmit(): Promise<void> {
  if (!validate()) return

  if (!agreed.value) {
    message.warning('请先阅读并同意用户服务协议与隐私政策')
    return
  }

  loading.value = true
  try {
    await authStore.register({
      email: form.email.trim(),
      password: form.password,
      emailCode: form.emailCode.trim() || undefined,
    })
    message.success('注册成功，去配置你的第一个模型吧')
    await router.replace({ name: 'ModelConfig' })
  } catch {
    // 错误提示已由响应拦截器统一处理
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
          3 步完成，开始创作
        </span>

        <h1>注册账号<br /><em>即刻开始生成</em></h1>

        <p>
          注册后先配置你自己的模型服务（API Key、Base URL、默认参数），随后就能直接出图、出片。平台不代收费用，用量由你的服务商结算。
        </p>

        <div class="vfeats">
          <div class="vfeat">
            <span class="fi"><app-icon name="check" :size="15" /></span>
            邮箱注册，一步到位
          </div>
          <div class="vfeat">
            <span class="fi"><app-icon name="video" :size="15" /></span>
            图片与视频模型分别配置
          </div>
          <div class="vfeat">
            <span class="fi"><app-icon name="clock" :size="15" /></span>
            作品自动留存，永久可查
          </div>
        </div>
      </div>

      <div class="vpreview">
        <div class="pv" style="background: linear-gradient(150deg, #0b2a3a, #0e7490)">✨</div>
        <div class="pv" style="background: linear-gradient(150deg, #241b4d, #5b21b6)">🎥</div>
        <div class="pv" style="background: linear-gradient(150deg, #0f2e22, #047857)">🧴</div>
      </div>

      <div class="vfoot">© 2026 {{ APP_NAME }} · {{ APP_SLOGAN }}</div>
    </aside>

    <!-- ============ 右：注册表单 ============ -->
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

        <h2>创建账号</h2>
        <p class="lead">
          已有账号？
          <router-link
            :to="{ name: 'Login' }"
            style="color: var(--brand-strong); font-weight: 700"
          >
            直接登录
          </router-link>
        </p>

        <form class="auth-form" @submit.prevent="handleSubmit">
          <div class="field">
            <label for="email">邮箱</label>
            <div class="input-group">
              <span class="ig-icon"><app-icon name="mail" :size="16" /></span>
              <input
                id="email"
                v-model="form.email"
                class="input"
                :class="{ 'is-error': errors.email }"
                type="email"
                placeholder="you@example.com"
                autocomplete="email"
                name="email"
                @input="errors.email = ''"
              />
            </div>
            <span v-if="errors.email" class="field-error">{{ errors.email }}</span>
          </div>

          <div class="field">
            <label for="code">邮箱验证码</label>
            <div class="code-row">
              <input
                id="code"
                v-model="form.emailCode"
                class="input"
                type="text"
                inputmode="numeric"
                placeholder="6 位数字验证码"
                name="emailCode"
              />
              <button
                class="btn btn-ghost"
                type="button"
                :disabled="countdown > 0 || sending"
                @click="handleSendCode"
              >
                {{ countdown > 0 ? `${countdown}s 后重发` : '获取验证码' }}
              </button>
            </div>
          </div>

          <div class="field">
            <label for="pwd">设置密码</label>
            <div class="input-group">
              <span class="ig-icon"><app-icon name="lock" :size="16" /></span>
              <input
                id="pwd"
                v-model="form.password"
                class="input"
                :class="{ 'is-error': errors.password }"
                :type="showPassword ? 'text' : 'password'"
                placeholder="至少 8 位，含字母与数字"
                autocomplete="new-password"
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
            <span v-else class="hint">建议使用 8 位以上、包含大小写字母与数字的组合</span>
          </div>

          <div class="field">
            <label for="pwd2">确认密码</label>
            <div class="input-group">
              <span class="ig-icon"><app-icon name="lock" :size="16" /></span>
              <input
                id="pwd2"
                v-model="form.confirmPassword"
                class="input"
                :class="{ 'is-error': errors.confirmPassword }"
                :type="showPassword ? 'text' : 'password'"
                placeholder="再次输入密码"
                autocomplete="new-password"
                name="confirmPassword"
                @input="errors.confirmPassword = ''"
              />
            </div>
            <span v-if="errors.confirmPassword" class="field-error">
              {{ errors.confirmPassword }}
            </span>
          </div>

          <label class="check">
            <input v-model="agreed" type="checkbox" />
            <span>
              我已阅读并同意 <a href="#" @click.prevent>《用户服务协议》</a> 与
              <a href="#" @click.prevent>《隐私政策》</a>，并知悉生成内容需符合相关法律法规
            </span>
          </label>

          <button class="btn btn-primary btn-lg btn-block" type="submit" :disabled="loading">
            <span v-if="loading" class="spinner" />
            {{ loading ? '注册中…' : '注册并配置模型' }}
          </button>
        </form>

        <div class="auth-switch">
          已有账号？
          <router-link :to="{ name: 'Login' }">立即登录</router-link>
        </div>
      </div>
    </section>
  </div>
</template>
