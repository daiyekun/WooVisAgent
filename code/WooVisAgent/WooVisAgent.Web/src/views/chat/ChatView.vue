<script setup lang="ts">
/**
 * ============================================================================
 * 对话创作 · 图片与视频共用同一会话
 * ============================================================================
 *
 * 版式 1:1 对齐原型 docs/prot/app.html，当前为静态页：
 *   - 会话内容来自 chatMock（照抄原型示例），发送后走一遍本地进度并产出示例结果卡；
 *   - 图片 / 视频只切输入区的参数与模型，两条链路共用同一条对话流；
 *   - 真实接入时把 send() 换成「提交任务 + 订阅 SignalR 进度」即可，模板不用动。
 */
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import { useAuthStore } from '@/stores/authStore'
import { message } from '@/utils'

import {
  NEW_CHAT_TITLE,
  SHOW_DEMO_SESSION,
  composerOptions,
  generatingStages,
  initialMessages,
  starterCards,
  type ChatMode,
  type MockGenCard,
  type MockMessage,
  type MockThumb,
} from './chatMock'

const authStore = useAuthStore()
const route = useRoute()
const router = useRouter()

/** ==================== 会话状态 ==================== */

const title = ref('极简日系护肤海报')
const messages = ref<MockMessage[]>(SHOW_DEMO_SESSION ? initialMessages.map((item) => ({ ...item })) : [])
const draft = ref('')
const mode = ref<ChatMode>('image')

/** 用户头像取登录用户首字，与侧边栏底部保持一致 */
const avatarText = computed(() => authStore.displayName.slice(0, 1) || '我')

const isEmpty = computed(() => messages.value.length === 0)

/** ==================== 输入区参数 ==================== */

const ratioIndex = ref(0)
const resolutionIndex = ref(0)
const durationIndex = ref(0)
const imageModelIndex = ref(0)
const videoModelIndex = ref(0)

const ratio = computed(() => composerOptions.image.ratio[ratioIndex.value])
const imageSize = computed(() => composerOptions.image.size[ratioIndex.value])
const resolution = computed(() => composerOptions.video.resolution[resolutionIndex.value])
const duration = computed(() => composerOptions.video.duration[durationIndex.value])

const imageModel = computed(() => composerOptions.image.models[imageModelIndex.value])
const videoModel = computed(() => composerOptions.video.models[videoModelIndex.value])
const currentModel = computed(() => (mode.value === 'image' ? imageModel.value : videoModel.value))

/** 当前参数摘要，用于生成中卡片的副标题 */
const currentSpec = computed(() =>
  mode.value === 'image' ? `${imageSize.value} · ${ratio.value}` : `${resolution.value} · ${duration.value}`,
)

const placeholder = computed(() =>
  mode.value === 'video'
    ? '描述你想要的视频画面与运镜，例如：一杯冰镇汽水倒入玻璃杯，气泡翻涌，慢镜头推近…'
    : '描述你想要的画面，例如：极简日式风格的护肤品海报，柔和自然光，留白构图…',
)

const sendHint = computed(() => (mode.value === 'video' ? '开始生成视频' : '开始生成图片'))

/** ==================== 生成流程（静态：本地跑一遍进度） ==================== */

const generating = ref(false)
const progress = ref(0)
const stageIndex = ref(0)

/** 生成中卡片上的阶段描述，例如「Sora-2 · 720p · 4s · 正在解码首帧」 */
const loadingStage = computed(
  () => `${currentModel.value} · ${currentSpec.value} · ${generatingStages[mode.value][stageIndex.value]}`,
)

/**
 * 静态版把「比例 / 分辨率 / 时长 / 模型」做成点击循环切换，
 * 接入真实选项后换成下拉即可，模板结构不用动。
 */
function nextRatio(): void {
  ratioIndex.value = (ratioIndex.value + 1) % composerOptions.image.ratio.length
}

function nextResolution(): void {
  resolutionIndex.value = (resolutionIndex.value + 1) % composerOptions.video.resolution.length
}

function nextDuration(): void {
  durationIndex.value = (durationIndex.value + 1) % composerOptions.video.duration.length
}

function nextModel(): void {
  if (mode.value === 'image') {
    imageModelIndex.value = (imageModelIndex.value + 1) % composerOptions.image.models.length
  } else {
    videoModelIndex.value = (videoModelIndex.value + 1) % composerOptions.video.models.length
  }
}

/** ==================== 参考图附件 ==================== */

const attachments = ref<MockThumb[]>([
  { id: 'att-1', glyph: '🧴', bg: 'linear-gradient(150deg,#0F2E22,#047857)' },
])

function removeAttachment(id: string): void {
  attachments.value = attachments.value.filter((item) => item.id !== id)
}

function pickAttachment(kind: string): void {
  message.info(`${kind}上传将在接入接口后开放`)
}

let timer: ReturnType<typeof setInterval> | null = null

const chatEl = ref<HTMLElement | null>(null)
const promptEl = ref<HTMLTextAreaElement | null>(null)

watch(draft, () => void fitTextarea())

async function fitTextarea(): Promise<void> {
  await nextTick()
  const el = promptEl.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 180)}px`
}

async function scrollToBottom(): Promise<void> {
  await nextTick()
  const el = chatEl.value
  if (el) el.scrollTop = el.scrollHeight
}

function stopTimer(): void {
  if (timer) {
    clearInterval(timer)
    timer = null
  }
}

function startGenerating(): void {
  stopTimer()
  generating.value = true
  progress.value = 6
  stageIndex.value = 0

  const total = generatingStages[mode.value].length

  timer = setInterval(() => {
    progress.value = Math.min(100, progress.value + 6 + Math.random() * 14)
    stageIndex.value = Math.min(total - 1, Math.floor(progress.value / (100 / total)))

    if (progress.value >= 100) {
      stopTimer()
      generating.value = false
      pushResult()
    }
  }, 420)

  void scrollToBottom()
}

/** 生成完成后落一条结果卡（静态示例，占位缩略图） */
function pushResult(): void {
  const isVideo = mode.value === 'video'
  const stamp = Date.now()

  const card: MockGenCard = isVideo
    ? {
        kind: 'video',
        title: '视频生成 · 1 条',
        model: videoModel.value,
        meta: `1280×720 · ${duration.value} · 用时 ${Math.round(38 + Math.random() * 20)}s`,
        thumbs: [
          { id: `t-${stamp}`, wide: true, duration: `00:0${duration.value.replace('s', '')}`, glyph: '🌊', bg: 'linear-gradient(150deg,#0B2A3A,#0E7490)' },
        ],
      }
    : {
        kind: 'image',
        title: '图片生成 · 2 张',
        model: imageModel.value,
        meta: `${imageSize.value} · 用时 ${(4 + Math.random() * 4).toFixed(1)}s`,
        thumbs: [
          { id: `t-${stamp}-1`, ...{ glyph: '🧴', bg: 'linear-gradient(150deg,#0F2E22,#047857)' } },
          { id: `t-${stamp}-2`, ...{ glyph: '🌿', bg: 'linear-gradient(150deg,#122634,#0E7490)' } },
        ],
      }

  messages.value.push({
    id: `m-${stamp}`,
    role: 'assistant',
    time: '刚刚',
    text: isVideo
      ? '视频已生成完成，以第一张图作为首帧保持一致，运镜按“缓推 + 光斑横扫”处理。'
      : '已按你的描述生成 2 张方案。左图留白更多，适合做主视觉；右图主体更前置，更像情景图。告诉我要往哪个方向继续调整即可。',
    card,
  })

  void scrollToBottom()
}

/** ==================== 会话操作 ==================== */

function send(): void {
  const text = draft.value.trim()
  if (!text) {
    message.warning('先描述一下你想要的画面')
    return
  }
  if (generating.value) {
    message.warning('当前还有任务在生成中，请稍候')
    return
  }

  messages.value.push({
    id: `m-${Date.now()}`,
    role: 'user',
    time: '刚刚',
    text,
    attachments: attachments.value.length ? [...attachments.value] : undefined,
  })

  draft.value = ''
  attachments.value = []
  if (title.value === NEW_CHAT_TITLE) title.value = text.slice(0, 18)

  void fitTextarea()
  startGenerating()
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  send()
}

function newChat(): void {
  stopTimer()
  generating.value = false
  messages.value = []
  title.value = NEW_CHAT_TITLE
  draft.value = ''
  attachments.value = []
  void fitTextarea()
  void nextTick(() => promptEl.value?.focus())
  message.success('已创建新会话，描述你的想法即可开始')
}

function clearChat(): void {
  stopTimer()
  generating.value = false
  messages.value = []
  message.success('已清空当前会话')
}

function applyStarter(item: (typeof starterCards)[number]): void {
  mode.value = item.mode
  draft.value = item.prompt
  void fitTextarea()
  void nextTick(() => promptEl.value?.focus())
}

/**
 * 侧边栏「新建创作」和「工作台」的入口卡片都用 query 触发，避免为此引入全局状态。
 * 外壳对对话页按路由名复用实例，所以 query 变化不会重置会话。
 *
 * immediate 必须为 true：从别的页面跳过来时 query 在挂载那一刻就已经存在，
 * 不立即执行就会残留上一次的会话内容。
 */
watch(
  () => route.query,
  (query) => {
    if (query.new) newChat()
    // 工作台的「视频创作」入口直接带 mode=video 进来，省得用户再切一次
    if (query.mode === 'image' || query.mode === 'video') mode.value = query.mode
  },
  { immediate: true },
)

/** ==================== 结果卡操作 ==================== */

const favoriteIds = ref<string[]>([])

function toggleFavorite(id: string): void {
  const index = favoriteIds.value.indexOf(id)
  if (index >= 0) {
    favoriteIds.value.splice(index, 1)
    message.info('已取消收藏')
  } else {
    favoriteIds.value.push(id)
    message.success('已加入收藏')
  }
}

function removeMessage(id: string): void {
  messages.value = messages.value.filter((item) => item.id !== id)
  message.success('已删除该结果')
}

/** 复用参数：把该结果对应的那条用户需求填回输入框 */
function reusePrompt(index: number): void {
  for (let i = index - 1; i >= 0; i -= 1) {
    const item = messages.value[i]
    if (item.role === 'user') {
      draft.value = item.text
      void fitTextarea()
      void nextTick(() => promptEl.value?.focus())
      message.success('已带入本条需求，可继续调整')
      return
    }
  }
}

function demo(text: string): void {
  message.info(text)
}

onBeforeUnmount(stopTimer)
</script>

<template>
  <!-- 顶栏：会话标题 + 状态 + 操作 -->
  <header class="topbar">
    <div class="tt">
      <b>{{ title }}</b>
      <small>对话创作 · 图片与视频共用同一会话</small>
    </div>

    <div class="spacer" />

    <span class="tb-chip" title="模型已按你的配置就绪">
      <span class="dot" />
      模型已就绪
    </span>

    <span class="tb-chip hide-sm" @click="demo('导出功能将在接入接口后开放')">
      <app-icon name="download" :size="14" />
      导出
    </span>

    <button class="btn-icon" type="button" title="清空当前会话" @click="clearChat">
      <app-icon name="trash" :size="16" />
    </button>
  </header>

  <!-- 对话流 -->
  <div ref="chatEl" class="chat">
    <div class="chat-in">
      <!-- 空态：起始卡片 -->
      <div v-if="isEmpty" class="welcome">
        <div class="mark">
          <svg viewBox="0 0 24 24" fill="none">
            <path
              d="M8.4 6.4v11.2a1.1 1.1 0 0 0 1.68.94l8.8-5.6a1.1 1.1 0 0 0 0-1.88l-8.8-5.6A1.1 1.1 0 0 0 8.4 6.4z"
              fill="#fff"
            />
          </svg>
        </div>
        <h2>用一句话<em>把画面说出来</em></h2>
        <p>描述你要的图片或视频，参数与模型都在下方输入区里随时可调。</p>

        <div class="starters">
          <button
            v-for="item in starterCards"
            :key="item.title"
            class="starter"
            type="button"
            @click="applyStarter(item)"
          >
            <span class="ic" :class="item.tone">
              <app-icon :name="item.icon" :size="17" />
            </span>
            <span>
              <b>{{ item.title }}</b>
              <small>{{ item.desc }}</small>
            </span>
          </button>
        </div>
      </div>

      <!-- 消息列表 -->
      <template v-for="(item, index) in messages" :key="item.id">
        <!-- 用户 -->
        <div v-if="item.role === 'user'" class="msg user">
          <div class="ava"><span>{{ avatarText }}</span></div>
          <div class="col">
            <div class="meta"><span>{{ item.time }}</span></div>
            <div class="bubble">{{ item.text }}</div>
            <div v-if="item.attachments?.length" class="attach-row" style="margin-bottom: 0">
              <div
                v-for="att in item.attachments"
                :key="att.id"
                class="attach"
                :style="att.bg ? { background: att.bg } : undefined"
              >
                <img v-if="att.url" :src="att.url" :alt="att.glyph" />
                <template v-else>{{ att.glyph }}</template>
              </div>
            </div>
          </div>
        </div>

        <!-- 助手 -->
        <div v-else class="msg">
          <div class="ava">
            <svg viewBox="0 0 24 24" fill="none" width="16" height="16">
              <path
                d="M8.4 6.4v11.2a1.1 1.1 0 0 0 1.68.94l8.8-5.6a1.1 1.1 0 0 0 0-1.88l-8.8-5.6A1.1 1.1 0 0 0 8.4 6.4z"
                fill="#fff"
              />
            </svg>
          </div>
          <div class="col">
            <div class="meta"><b>WooVisAgent</b><span>{{ item.time }}</span></div>
            <div class="bubble">{{ item.text }}</div>

            <div v-if="item.card" class="gen-card">
              <div class="gc-head">
                <app-icon
                  :name="item.card.kind === 'video' ? 'video' : 'image'"
                  :size="15"
                  :style="{ color: item.card.kind === 'video' ? '#a78bfa' : 'var(--brand)' }"
                />
                <span class="name">{{ item.card.title }}</span>
                <span class="tag" :class="item.card.kind === 'video' ? 'tag-violet' : 'tag-cyan'">
                  {{ item.card.model }}
                </span>
                <span class="spacer" />
                <span class="cost">{{ item.card.meta }}</span>
              </div>

              <div class="gc-body">
                <div class="gc-grid" :class="item.card.thumbs.length > 1 ? 'g2' : 'g1'">
                  <div
                    v-for="thumb in item.card.thumbs"
                    :key="thumb.id"
                    class="thumb"
                    :class="{ wide: thumb.wide }"
                    :style="thumb.bg ? { background: thumb.bg } : undefined"
                  >
                    <img v-if="thumb.url" :src="thumb.url" :alt="'生成结果'" />
                    <template v-else>
                      <span class="glyph">{{ thumb.glyph }}</span>
                      <span v-if="thumb.wide" class="play">
                        <i><app-icon name="play" :size="16" /></i>
                      </span>
                      <span v-if="thumb.duration" class="dur">{{ thumb.duration }}</span>
                    </template>
                  </div>
                </div>
              </div>

              <div class="gc-foot">
                <template v-if="item.card.kind === 'video'">
                  <button class="btn btn-ghost btn-sm" type="button" @click="demo('下载将在接入产物地址后可用')">
                    <app-icon name="download" :size="14" />
                    下载 MP4
                  </button>
                  <button class="btn btn-ghost btn-sm" type="button" @click="demo('延长时长将在接入接口后开放')">
                    <app-icon name="plus-circle" :size="14" />
                    延长时长
                  </button>
                  <button class="btn btn-ghost btn-sm" type="button" @click="demo('高清重制将在接入接口后开放')">
                    <app-icon name="sparkles" :size="14" />
                    高清重制
                  </button>
                </template>
                <template v-else>
                  <button class="btn btn-ghost btn-sm" type="button" @click="demo('下载将在接入产物地址后可用')">
                    <app-icon name="download" :size="14" />
                    下载
                  </button>
                  <button class="btn btn-ghost btn-sm" type="button" @click="reusePrompt(index)">
                    <app-icon name="refresh" :size="14" />
                    复用参数
                  </button>
                  <button class="btn btn-ghost btn-sm" type="button" @click="demo('局部重绘将在接入接口后开放')">
                    <app-icon name="pencil" :size="14" />
                    局部重绘
                  </button>
                </template>

                <span class="spacer" />

                <button
                  v-if="item.card.kind !== 'video'"
                  class="btn-icon"
                  type="button"
                  :title="favoriteIds.includes(item.id) ? '取消收藏' : '收藏'"
                  :style="favoriteIds.includes(item.id) ? { color: 'var(--warn)' } : undefined"
                  @click="toggleFavorite(item.id)"
                >
                  <app-icon name="star" :size="16" />
                </button>

                <button class="btn-icon" type="button" title="删除" @click="removeMessage(item.id)">
                  <app-icon name="trash" :size="16" />
                </button>
              </div>
            </div>
          </div>
        </div>
      </template>

      <!-- 生成中 -->
      <div v-if="generating" class="msg">
        <div class="ava">
          <svg viewBox="0 0 24 24" fill="none" width="16" height="16">
            <path
              d="M8.4 6.4v11.2a1.1 1.1 0 0 0 1.68.94l8.8-5.6a1.1 1.1 0 0 0 0-1.88l-8.8-5.6A1.1 1.1 0 0 0 8.4 6.4z"
              fill="#fff"
            />
          </svg>
        </div>
        <div class="col is-wide">
          <div class="meta"><b>WooVisAgent</b><span>正在生成</span></div>
          <div class="gen-loading">
            <div class="gl-top">
              <span class="spinner" />
              <span>
                <b>{{ mode === 'video' ? '正在生成视频' : '正在生成图片' }}</b>
                <small>{{ loadingStage }}</small>
              </span>
              <span class="pct">{{ Math.round(progress) }}%</span>
            </div>
            <div class="bar"><i :style="{ width: `${progress}%` }" /></div>
            <div class="gc-grid g1" style="margin-top: 14px">
              <div
                class="shimmer"
                :style="{ aspectRatio: mode === 'video' ? '16 / 9' : '1 / 1' }"
              />
            </div>
          </div>
        </div>
      </div>
    </div>
  </div>

  <!-- 输入区 -->
  <div class="composer">
    <div class="composer-in">
      <div class="composer-box">
        <!-- 参考图 -->
        <div v-if="attachments.length" class="attach-row">
          <div
            v-for="att in attachments"
            :key="att.id"
            class="attach"
            :style="att.bg ? { background: att.bg } : undefined"
          >
            <img v-if="att.url" :src="att.url" :alt="att.glyph" />
            <template v-else>{{ att.glyph }}</template>
            <span class="x" title="移除" @click="removeAttachment(att.id)">×</span>
          </div>
          <div class="attach add" title="继续添加参考图" @click="pickAttachment('参考图')">＋</div>
        </div>

        <textarea
          ref="promptEl"
          v-model="draft"
          rows="2"
          :placeholder="placeholder"
          @keydown="onKeydown"
        />

        <div class="composer-bar">
          <!-- 图片 / 视频 -->
          <div class="seg">
            <button
              type="button"
              :class="{ on: mode === 'image' }"
              @click="mode = 'image'"
            >
              <app-icon name="image" :size="14" />
              图片
            </button>
            <button
              type="button"
              :class="{ on: mode === 'video' }"
              @click="mode = 'video'"
            >
              <app-icon name="video" :size="14" />
              视频
            </button>
          </div>

          <span class="chip" @click="pickAttachment('参考图')">
            <app-icon name="paperclip" :size="14" />
            参考图
          </span>
          <span v-if="mode === 'video'" class="chip" @click="pickAttachment('首尾帧')">
            <app-icon name="film" :size="14" />
            首尾帧
          </span>

          <span
            v-if="mode === 'image'"
            class="chip"
            title="点击切换出图比例"
            @click="nextRatio"
          >
            比例 {{ ratio }}
            <app-icon name="chevron-down" :size="12" class="caret" />
          </span>
          <span v-else class="chip" title="点击切换分辨率" @click="nextResolution">
            {{ resolution }}
            <app-icon name="chevron-down" :size="12" class="caret" />
          </span>
          <span v-if="mode === 'video'" class="chip" title="点击切换视频时长" @click="nextDuration">
            {{ duration }}
            <app-icon name="chevron-down" :size="12" class="caret" />
          </span>

          <span class="grow" />

          <span
            class="chip chip-ok"
            :title="`当前${mode === 'image' ? '图片' : '视频'}模型，点击切换`"
            @click="nextModel"
          >
            <span class="dot" style="background: var(--ok)" />
            {{ currentModel }}
            <app-icon name="chevron-down" :size="12" class="caret" />
          </span>

          <button class="send" type="button" title="生成" :disabled="generating" @click="send">
            <app-icon name="arrow-up" :size="17" stroke-width="2.2" />
          </button>
        </div>
      </div>

      <div class="composer-tip">
        {{ sendHint }} · 模型与参数可在
        <a style="color: var(--brand)" @click="router.push({ name: 'ModelConfig' })">模型配置</a>
        中随时调整 · <kbd>Enter</kbd> 发送，<kbd>Shift</kbd>+<kbd>Enter</kbd> 换行
        <template v-if="!isEmpty">
          · <a style="color: var(--brand)" @click="newChat">新建创作</a>
        </template>
      </div>
    </div>
  </div>
</template>
