<script setup lang="ts">
/**
 * ============================================================================
 * 对话创作 · 图片生成（文生图 / 图生图）
 * ============================================================================
 *
 * 版式沿用原型 docs/prot/app.html，链路已接真实后端：
 *   - 文生图：直接把提示词提交成任务；
 *   - 图生图：先上传参考图拿到 /uploads 地址，再随任务一起提交（referenceImageUrls）；
 *   - 进度：优先走 SignalR 推送，同时保留轮询兜底（后端没起 Hub 也能出图）；
 *   - 多轮：任一产物图都能「继续改这张」带回输入区，在该画面上继续迭代。
 *
 * 有一条必须守住的规则：**已完成 ≠ 已经拿到产物**。
 * 后端会先推一条「succeeded + 100%」的状态、再推一条带产物的 TaskCompleted，
 * 如果收到前者就把卡片收尾并注销订阅，后一条产物就永远丢了 ——
 * 页面表现是进度条 100%、一直显示"正在生成图片"，但永远不出图。
 * 因此下面的收尾逻辑只在真正拿到产物（或确认拿不到）之后才执行。
 *
 * 视频入口保留了原型的位置，但生成服务尚未接入，点击会明确提示而不是产出假结果。
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import ImagePreview from '@/components/ImagePreview.vue'
import { cancelImageTask, fetchImageTask, generateImage } from '@/api/image'
import { uploadFiles } from '@/api/file'
import { ensureTaskHubStarted, useTaskEvents } from '@/hooks/useSignalR'
import { useAuthStore } from '@/stores/authStore'
import { useModelStore } from '@/stores/modelStore'
import { createTempId, downloadUrl, formatElapsed, message } from '@/utils'
import type { GenerationTask, TaskAsset, TaskStatus } from '@/types'

import {
  BATCH_OPTIONS,
  CHAT_STATUS_META,
  MAX_REFERENCE_COUNT,
  MAX_REFERENCE_SIZE,
  NEW_CHAT_TITLE,
  RATIO_PRESETS,
  imageStageText,
  starterCards,
  type ChatAttachment,
  type ChatCard,
  type ChatImage,
  type ChatMessage,
  type ChatMode,
} from './chatMock'

const authStore = useAuthStore()
const modelStore = useModelStore()
const route = useRoute()
const router = useRouter()

/** ==================== 会话状态 ==================== */

const title = ref(NEW_CHAT_TITLE)
const messages = ref<ChatMessage[]>([])
const draft = ref('')
const mode = ref<ChatMode>('image')

/** 用户头像取登录用户首字，与侧边栏底部保持一致 */
const avatarText = computed(() => authStore.displayName.slice(0, 1) || '我')

const isEmpty = computed(() => messages.value.length === 0)

/** ==================== 模型 ==================== */

const imageModels = computed(() => modelStore.imageModels)
const selectedModelId = ref('')

const currentModel = computed(() => imageModels.value.find((item) => item.id === selectedModelId.value))
const hasModel = computed(() => Boolean(currentModel.value))

// 模型列表首次加载完成、或用户改了默认模型后，自动选中该用的那个
watch(
  imageModels,
  (list) => {
    if (!list.length) {
      selectedModelId.value = ''
      return
    }

    const stillValid = list.some((item) => item.id === selectedModelId.value)
    if (stillValid) return

    selectedModelId.value = modelStore.resolvePreferredModel('image')?.id ?? list[0].id
  },
  { immediate: true },
)

/** ==================== 输入区参数 ==================== */

const ratioIndex = ref(0)
const batchSize = ref<number>(BATCH_OPTIONS[0])
const showAdvanced = ref(false)
const negativePrompt = ref('')
const steps = ref(30)
const cfgScale = ref(7)
const seed = ref(0)

const ratio = computed(() => RATIO_PRESETS[ratioIndex.value])
const sizeLabel = computed(() => `${ratio.value.width}×${ratio.value.height}`)

/** ==================== 下拉菜单 ==================== */

type MenuKey = '' | 'ratio' | 'batch' | 'model'
const openMenu = ref<MenuKey>('')

function toggleMenu(key: Exclude<MenuKey, ''>): void {
  openMenu.value = openMenu.value === key ? '' : key
}

function closeMenu(): void {
  openMenu.value = ''
}

/** ==================== 参考图 ==================== */

const attachments = ref<ChatAttachment[]>([])
const fileInput = ref<HTMLInputElement | null>(null)
const dragOver = ref(false)

const uploading = computed(() => attachments.value.some((item) => item.uploading))
const pendingAttachments = computed(() => attachments.value.filter((item) => !item.url))

function pickFiles(): void {
  fileInput.value?.click()
}

function onFileInputChange(event: Event): void {
  const input = event.target as HTMLInputElement
  void handleFiles(Array.from(input.files ?? []))
  // 清空 value，否则连续选同一个文件不会再触发 change
  input.value = ''
}

function onDrop(event: DragEvent): void {
  dragOver.value = false
  void handleFiles(Array.from(event.dataTransfer?.files ?? []))
}

function onPaste(event: ClipboardEvent): void {
  const files = Array.from(event.clipboardData?.files ?? [])
  if (files.length) {
    event.preventDefault()
    void handleFiles(files)
  }
}

async function handleFiles(files: File[]): Promise<void> {
  if (!files.length) return

  const isImage = (file: File) =>
    file.type.startsWith('image/') || /\.(png|jpe?g|webp|gif|bmp|avif)$/i.test(file.name)

  const images = files.filter(isImage)
  if (!images.length) {
    message.warning('只支持上传图片（PNG / JPG / WebP / GIF / BMP / AVIF）')
    return
  }

  const oversize = images.filter((file) => file.size > MAX_REFERENCE_SIZE)
  if (oversize.length) {
    message.warning(`「${oversize[0].name}」超过 ${MAX_REFERENCE_SIZE / 1024 / 1024} MB，已跳过`)
  }

  const accepted = images.filter((file) => file.size <= MAX_REFERENCE_SIZE)
  const remain = MAX_REFERENCE_COUNT - attachments.value.length

  if (remain <= 0) {
    message.warning(`参考图最多 ${MAX_REFERENCE_COUNT} 张`)
    return
  }

  if (accepted.length > remain) {
    message.warning(`参考图最多 ${MAX_REFERENCE_COUNT} 张，本次只取前 ${remain} 张`)
  }

  const picked = accepted.slice(0, remain)
  const items: ChatAttachment[] = picked.map((file) => ({
    id: createTempId(),
    name: file.name,
    size: file.size,
    previewUrl: URL.createObjectURL(file),
    uploading: true,
  }))

  attachments.value.push(...items)

  try {
    const results = await uploadFiles(picked)
    results.forEach((result, index) => {
      const target = items[index]
      if (!target) return
      target.url = result.url
      target.uploading = false
    })
    message.success(`已上传 ${results.length} 张参考图`)
  } catch {
    items.forEach((item) => {
      item.uploading = false
      item.error = '上传失败'
    })
  }

  void fitTextarea()
}

function removeAttachment(id: string): void {
  const target = attachments.value.find((item) => item.id === id)
  if (target) URL.revokeObjectURL(target.previewUrl)
  attachments.value = attachments.value.filter((item) => item.id !== id)
}

/**
 * 把某一轮产物带回输入区，直接在它基础上继续改（图生图的多轮迭代）。
 *
 * 记下来历（sourceMessageId）是为了在结果卡上显示"改自哪一轮"，
 * 多轮改图时用户很容易忘记当前这版是从哪张一路改过来的。
 */
function useAsReference(image: ChatImage, fromMessageId?: string): void {
  if (attachments.value.length >= MAX_REFERENCE_COUNT) {
    message.warning(`参考图最多 ${MAX_REFERENCE_COUNT} 张`)
    return
  }

  if (attachments.value.some((item) => item.url === image.url)) {
    message.info('这张图已经在参考图里了')
    return
  }

  attachments.value.push({
    id: createTempId(),
    name: image.fileName ?? '参考图',
    size: 0,
    // 产物本来就在服务端，直接用它的地址当预览，不需要再上传一次
    previewUrl: image.url,
    url: image.url,
    sourceMessageId: fromMessageId,
  })

  void fitTextarea()
  void scrollToBottom()
  message.success('已加入参考图，描述你要改的地方即可')
  nextTick(() => promptEl.value?.focus())
}

/** ==================== 生成流程 ==================== */

/** taskId → 消息 id。推送与轮询都靠它定位该更新哪张卡 */
const watchers = new Map<string, string>()
const pollTimers = new Map<string, number>()
/** 已完成但产物还没拿到的兜底计时器，避免卡片永远停在"正在生成图片" */
const settleTimers = new Map<string, number>()

const generating = computed(() =>
  messages.value.some((item) => item.card?.taskId && !isFinalStatus(item.card.status)),
)

function isFinalStatus(status: TaskStatus): boolean {
  return status === 'succeeded' || status === 'failed' || status === 'canceled'
}

function findCard(messageId: string): ChatCard | undefined {
  return messages.value.find((item) => item.id === messageId)?.card
}

function cardByTaskId(taskId: string): ChatCard | undefined {
  const messageId = watchers.get(taskId)
  return messageId ? findCard(messageId) : undefined
}

/**
 * 把任务状态写回卡片。
 * 推送与轮询走同一入口，谁先到算谁的，后到的覆盖前面的——
 * 两条链路给出的字段是对齐的，重复应用不会有副作用。
 */
function applyTaskUpdate(
  taskId: string,
  patch: {
    status: TaskStatus
    progress: number
    outputs?: TaskAsset[]
    error?: string
    stage?: string
  },
): void {
  const messageId = watchers.get(taskId)
  if (!messageId) return

  const target = messages.value.find((item) => item.id === messageId)
  const card = target?.card
  if (!target || !card) return

  card.status = patch.status
  card.progress = patch.progress

  if (patch.stage) card.stage = patch.stage
  if (patch.error) card.error = patch.error

  if (patch.outputs?.length) {
    card.images = patch.outputs.map((item) => ({
      id: item.id,
      url: item.url,
      fileName: item.fileName,
    }))

    const elapsed = card.startedAt ? formatElapsed(card.startedAt) : null
    card.meta = `${card.sizeLabel} · ${card.images.length} 张${elapsed ? ` · 用时 ${elapsed}` : ''}`
  }

  if (!isFinalStatus(patch.status)) {
    void scrollToBottom()
    return
  }

  // 已完成但产物还没到：后端的"状态变更"和"产物"是两条消息，
  // 这里如果直接收尾注销订阅，后一条产物就丢了（页面表现为 100% 却永远不出图）。
  // 所以先主动补拉一次详情，并留一个兜底计时器兜住"确实拿不到产物"的情况。
  if (patch.status === 'succeeded' && !card.images.length) {
    scheduleSettle(taskId)
    void fillOutputs(taskId)
    return
  }

  finishTask(taskId)
}

/** 主动补拉一次任务详情，把产物填回卡片 */
async function fillOutputs(taskId: string): Promise<void> {
  try {
    const task: GenerationTask = await fetchImageTask(taskId, { silent: true })
    applyTaskUpdate(taskId, {
      status: task.status,
      progress: task.progress,
      outputs: task.outputs,
      error: task.errorMessage,
    })
  } catch {
    // 单次查询失败不打断流程，兜底计时器会给出交代
  }
}

/**
 * 兜底：任务已经完成，但十几秒内始终拿不到产物。
 * 与其让进度条永远转下去，不如明确告诉用户发生了什么。
 */
function scheduleSettle(taskId: string): void {
  if (settleTimers.has(taskId)) return

  const timer = window.setTimeout(() => {
    settleTimers.delete(taskId)

    const card = cardByTaskId(taskId)
    if (!card || card.images.length) return

    card.error = '任务已完成，但没有取到产物。可到「任务中心」查看该任务，或换个模型重试。'
    finishTask(taskId)
  }, 15_000)

  settleTimers.set(taskId, timer)
}

/** 收尾：写结语、停轮询、注销订阅 */
function finishTask(taskId: string): void {
  const messageId = watchers.get(taskId)
  if (!messageId) return

  const target = messages.value.find((item) => item.id === messageId)
  const card = target?.card
  if (!target || !card) {
    stopWatch(taskId)
    return
  }

  if (card.status === 'succeeded') {
    target.text = `已生成 ${card.images.length} 张。想继续微调就点图片下方的「继续改这张」，把它带回输入框再描述要改的地方。`
  } else if (card.status === 'failed') {
    target.text = '这次生成失败了，可以看看下面的原因，或者换个模型重试。'
  } else {
    target.text = '已取消这次生成。'
  }

  stopWatch(taskId)
  void scrollToBottom()
}

function startPolling(taskId: string): void {
  if (pollTimers.has(taskId)) return

  const timer = window.setInterval(() => {
    void (async () => {
      try {
        const task: GenerationTask = await fetchImageTask(taskId, { silent: true })
        applyTaskUpdate(taskId, {
          status: task.status,
          progress: task.progress,
          outputs: task.outputs,
          error: task.errorMessage,
        })
      } catch {
        // 单次查询失败不打断流程，等下一轮
      }
    })()
  }, 3000)

  pollTimers.set(taskId, timer)
}

/** 只停轮询，保留订阅关系——后续推送仍要能落回卡片 */
function stopPolling(taskId: string): void {
  const timer = pollTimers.get(taskId)
  if (timer !== undefined) {
    clearInterval(timer)
    pollTimers.delete(taskId)
  }
}

function stopWatch(taskId: string): void {
  watchers.delete(taskId)
  stopPolling(taskId)

  const settle = settleTimers.get(taskId)
  if (settle !== undefined) {
    clearTimeout(settle)
    settleTimers.delete(taskId)
  }
}

function watchTask(taskId: string, messageId: string): void {
  watchers.set(taskId, messageId)
  startPolling(taskId)
}

/** SignalR 推送：只处理本会话自己提交的任务 */
useTaskEvents((event) => {
  if (!watchers.has(event.taskId)) return

  // 推送能通就说明链路是好的，轮询只是断线时的兜底，这里顺手撤掉省掉一半请求
  stopPolling(event.taskId)

  applyTaskUpdate(event.taskId, {
    status: event.status,
    progress: event.progress,
    outputs: event.outputs,
    error: event.status === 'failed' ? event.message : undefined,
    stage: event.status === 'succeeded' ? undefined : event.message,
  })
})

/** 提交一次生成。多轮迭代与首次生成共用这一条路径 */
async function submit(payload: {
  modelId: string
  prompt: string
  negativePrompt?: string
  width: number
  height: number
  batchSize: number
  steps: number
  cfgScale: number
  seed?: number
  references: string[]
  fromMessageId?: string
}): Promise<void> {
  const model = imageModels.value.find((item) => item.id === payload.modelId) ?? currentModel.value
  if (!model) {
    message.warning('还没有可用的图片模型，请先到「模型配置」里添加一个')
    return
  }

  const label = `${payload.width}×${payload.height}`
  const isEdit = payload.references.length > 0

  messages.value.push({
    id: createTempId(),
    role: 'user',
    time: '刚刚',
    text: payload.prompt,
    attachments: isEdit
      ? payload.references.map((url, index) => ({
          id: `${createTempId()}-${index}`,
          name: '参考图',
          size: 0,
          previewUrl: url,
          url,
          sourceMessageId: payload.fromMessageId,
        }))
      : undefined,
  })

  if (title.value === NEW_CHAT_TITLE) title.value = payload.prompt.slice(0, 18)

  const card: ChatCard = {
    kind: 'image',
    status: 'queued',
    progress: 0,
    model: model.name,
    meta: `${label} · ${payload.batchSize} 张`,
    images: [],
    startedAt: Date.now(),
    sizeLabel: label,
    count: payload.batchSize,
    references: [...payload.references],
    fromMessageId: payload.fromMessageId,
    payload: {
      modelId: payload.modelId,
      prompt: payload.prompt,
      negativePrompt: payload.negativePrompt,
      width: payload.width,
      height: payload.height,
      batchSize: payload.batchSize,
      steps: payload.steps,
      cfgScale: payload.cfgScale,
      seed: payload.seed,
    },
  }

  const assistantId = createTempId()
  messages.value.push({
    id: assistantId,
    role: 'assistant',
    time: '刚刚',
    text: isEdit
      ? `收到 ${payload.references.length} 张参考图，正在按你的描述改这张图…`
      : '正在按你的描述生成…',
    card,
  })

  void fitTextarea()
  void scrollToBottom()

  try {
    const result = await generateImage({
      modelId: payload.modelId,
      prompt: payload.prompt,
      negativePrompt: payload.negativePrompt,
      width: payload.width,
      height: payload.height,
      batchSize: payload.batchSize,
      steps: payload.steps,
      cfgScale: payload.cfgScale,
      seed: payload.seed,
      referenceImageUrls: isEdit ? payload.references : undefined,
    })

    card.taskId = result.taskId
    card.status = result.status
    watchTask(result.taskId, assistantId)
  } catch {
    // 错误提示已由拦截器统一弹出，这里只把卡片收尾，避免留下一个永远转圈的进度条
    card.status = 'failed'
    card.error = '提交失败，请检查模型配置后重试。'
    const target = messages.value.find((item) => item.id === assistantId)
    if (target) target.text = '这次没能提交成功，请检查模型配置后重试。'
  }
}

async function send(): Promise<void> {
  const text = draft.value.trim()

  if (!text) {
    message.warning('先描述一下你想要的画面')
    return
  }

  if (mode.value === 'video') {
    message.info('视频生成服务还在接入中，当前可以先用图片生成')
    return
  }

  if (!currentModel.value) {
    message.warning('还没有可用的图片模型，请先到「模型配置」里添加一个')
    return
  }

  if (uploading.value) {
    message.warning('参考图还在上传中，请稍候')
    return
  }

  if (pendingAttachments.value.length) {
    message.warning('有参考图没传成功，移除后再试')
    return
  }

  const references = attachments.value.map((item) => item.url).filter((url): url is string => Boolean(url))
  const fromMessageId = attachments.value.find((item) => item.sourceMessageId)?.sourceMessageId

  await submit({
    modelId: currentModel.value.id,
    prompt: text,
    negativePrompt: negativePrompt.value.trim() || undefined,
    width: ratio.value.width,
    height: ratio.value.height,
    batchSize: batchSize.value,
    steps: steps.value,
    cfgScale: cfgScale.value,
    seed: seed.value > 0 ? seed.value : undefined,
    references,
    fromMessageId,
  })

  // 提交成功后才清空输入区，失败时保留内容方便用户改改再发
  if (messages.value[messages.value.length - 1]?.card?.taskId) {
    draft.value = ''
    attachments.value = []
  }
}

/** 重跑：沿用上一轮的模型与参数，不改任何设置再来一次 */
function rerun(card: ChatCard): void {
  void submit({
    ...card.payload,
    references: card.references,
    fromMessageId: card.fromMessageId,
  })
}

async function cancelTask(messageId: string): Promise<void> {
  const card = findCard(messageId)
  if (!card?.taskId) return

  try {
    await cancelImageTask(card.taskId)
    message.success('已取消这次生成')
    applyTaskUpdate(card.taskId, { status: 'canceled', progress: card.progress })
  } catch {
    // 错误提示已由拦截器统一弹出
  }
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  void send()
}

/** ==================== 会话操作 ==================== */

function resetSession(): void {
  watchers.forEach((_messageId, taskId) => stopWatch(taskId))
  messages.value = []
  title.value = NEW_CHAT_TITLE
  draft.value = ''
  attachments.value = []
  void fitTextarea()
}

function newChat(): void {
  resetSession()
  void nextTick(() => promptEl.value?.focus())
  message.success('已创建新会话，描述你的想法即可开始')
}

function clearChat(): void {
  resetSession()
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
  const target = messages.value.find((item) => item.id === id)
  if (target?.card?.taskId) stopWatch(target.card.taskId)
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

function downloadImages(card: ChatCard): void {
  if (!card.images.length) return
  card.images.forEach((image) => downloadUrl(image.url, image.fileName))
  message.success(`已开始下载 ${card.images.length} 张`)
}

function downloadOne(image: ChatImage): void {
  downloadUrl(image.url, image.fileName)
  message.success('已开始下载')
}

/**
 * 这一轮是不是在某张已有图片的基础上改的。
 * 多轮迭代时很容易忘记"当前这版是从第几轮改过来的"，这里把来历标在卡片头上。
 */
function cardSourceLabel(card: ChatCard): string | null {
  if (!card.references.length) return null
  if (!card.fromMessageId) return '图生图'

  // 轮次 = 目标卡片之前出现过多少张结果卡
  let round = 0
  for (const item of messages.value) {
    if (item.id === card.fromMessageId) break
    if (item.card) round += 1
  }

  return round > 0 ? `图生图 · 改自第 ${round} 轮` : '图生图'
}

/** ImagePreview 需要的是 TaskAsset，这里做一次轻量适配 */
function asAssets(images: ChatImage[]): TaskAsset[] {
  return images.map((image) => ({
    id: image.id,
    type: 'image',
    url: image.url,
    fileName: image.fileName,
  }))
}

function goModelConfig(): void {
  void router.push({ name: 'ModelConfig' })
}

/** ==================== 布局小工具 ==================== */

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

onMounted(() => {
  // 模型列表在布局里做过兜底加载，这里再补一次，避免直接刷新对话页时选不到模型
  if (!modelStore.models.length) void modelStore.loadModels()
  // 实时通道由布局启动，这里补一次是为了「直接刷新到对话页」的情况
  void ensureTaskHubStarted()
  document.addEventListener('click', closeMenu)
})

onBeforeUnmount(() => {
  document.removeEventListener('click', closeMenu)
  watchers.forEach((_messageId, taskId) => stopWatch(taskId))
})
</script>

<template>
  <!-- 顶栏：会话标题 + 状态 + 操作 -->
  <header class="topbar">
    <div class="tt">
      <b>{{ title }}</b>
      <small>对话创作 · 文生图与图生图共用同一会话</small>
    </div>

    <div class="spacer" />

    <span
      class="tb-chip"
      :class="{ 'is-warn': !hasModel }"
      :title="hasModel ? `当前图片模型：${currentModel?.name}` : '还没配置可用的图片模型'"
      @click="goModelConfig"
    >
      <span class="dot" />
      {{ hasModel ? currentModel?.name : '未配置图片模型' }}
    </span>

    <span v-if="generating" class="tb-chip hide-sm">
      <span class="spinner" />
      生成中
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
        <p>描述你要的图片，参数与模型都在下方输入区里随时可调；上传参考图就是图生图。</p>

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
              <div v-for="att in item.attachments" :key="att.id" class="attach">
                <img :src="att.previewUrl" :alt="att.name" />
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
          <div class="col is-wide">
            <div class="meta"><b>WooVisAgent</b><span>{{ item.time }}</span></div>
            <div class="bubble">{{ item.text }}</div>

            <div v-if="item.card" class="gen-card">
              <div class="gc-head">
                <app-icon
                  :name="item.card.kind === 'video' ? 'video' : 'image'"
                  :size="15"
                  :style="{ color: item.card.kind === 'video' ? 'var(--violet-fg)' : 'var(--brand)' }"
                />
                <span class="name">{{ item.card.kind === 'video' ? '视频生成' : '图片生成' }}</span>
                <span class="tag" :class="CHAT_STATUS_META[item.card.status].cls">
                  {{ CHAT_STATUS_META[item.card.status].label }}
                </span>
                <span class="tag tag-dim">{{ item.card.model }}</span>
                <span v-if="cardSourceLabel(item.card)" class="tag tag-cyan">
                  {{ cardSourceLabel(item.card) }}
                </span>
                <span class="spacer" />
                <span class="cost">{{ item.card.meta }}</span>
              </div>

              <!-- 生成中 / 排队：进度条 + 占位骨架 -->
              <div
                v-if="item.card.taskId && !item.card.images.length && !item.card.error"
                class="gc-body"
              >
                <div class="gl-top">
                  <span class="spinner" />
                  <span>
                    <b>正在生成图片</b>
                    <small>
                      {{ item.card.model }} · {{ item.card.meta }} ·
                      {{ item.card.stage || imageStageText(item.card.progress) }}
                    </small>
                  </span>
                  <span class="pct">{{ Math.round(item.card.progress) }}%</span>
                </div>
                <div class="bar"><i :style="{ width: `${Math.max(4, item.card.progress)}%` }" /></div>
                <div class="gc-grid" :class="item.card.count > 1 ? 'g2' : 'g1'" style="margin-top: 14px">
                  <div v-for="n in Math.min(item.card.count, 4)" :key="n" class="shimmer" style="aspect-ratio: 1 / 1" />
                </div>
              </div>

              <!-- 失败 -->
              <div v-else-if="item.card.error" class="gc-body">
                <div class="fail-box">
                  <app-icon name="alert" :size="14" />
                  <span>{{ item.card.error }}</span>
                </div>
              </div>

              <!-- 产物 -->
              <div v-else-if="item.card.images.length" class="gc-body">
                <image-preview
                  :images="asAssets(item.card.images)"
                  :columns="item.card.images.length > 1 ? 2 : 1"
                  aspect-ratio="1 / 1"
                />

                <!-- 多张时逐张给出口，方便挑一张继续改 -->
                <div v-if="item.card.images.length > 1" class="pick-row">
                  <div v-for="image in item.card.images" :key="image.id" class="pick">
                    <img :src="image.url" :alt="image.fileName || '生成结果'" />
                    <span class="pick-acts">
                      <button class="btn btn-ghost btn-sm" type="button" @click="useAsReference(image, item.id)">
                        继续改这张
                      </button>
                      <button class="btn btn-ghost btn-sm" type="button" @click="downloadOne(image)">
                        下载
                      </button>
                    </span>
                  </div>
                </div>
              </div>

              <div class="gc-foot">
                <template v-if="item.card.images.length">
                  <button class="btn btn-ghost btn-sm" type="button" @click="downloadImages(item.card!)">
                    <app-icon name="download" :size="14" />
                    下载全部
                  </button>
                  <button
                    class="btn btn-ghost btn-sm"
                    type="button"
                    @click="useAsReference(item.card!.images[0], item.id)"
                  >
                    <app-icon name="paperclip" :size="14" />
                    作为参考图继续
                  </button>
                  <button class="btn btn-ghost btn-sm" type="button" @click="rerun(item.card!)">
                    <app-icon name="refresh" :size="14" />
                    重跑这一轮
                  </button>
                  <button class="btn btn-ghost btn-sm" type="button" @click="reusePrompt(index)">
                    <app-icon name="sparkles" :size="14" />
                    复用提示词
                  </button>
                </template>

                <button
                  v-else-if="item.card.taskId && !item.card.error"
                  class="btn btn-ghost btn-sm"
                  type="button"
                  @click="cancelTask(item.id)"
                >
                  <app-icon name="close" :size="14" />
                  取消生成
                </button>

                <span class="spacer" />

                <button
                  v-if="item.card.images.length"
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
    </div>
  </div>

  <!-- 输入区 -->
  <div class="composer">
    <div class="composer-in">
      <div
        class="composer-box"
        :class="{ 'is-drag': dragOver }"
        @dragover.prevent="dragOver = true"
        @dragleave.prevent="dragOver = false"
        @drop.prevent="onDrop"
      >
        <!-- 参考图 -->
        <div v-if="attachments.length" class="attach-row">
          <div v-for="att in attachments" :key="att.id" class="attach" :title="att.name">
            <img :src="att.previewUrl" :alt="att.name" />
            <span v-if="att.uploading" class="attach-mask">上传中…</span>
            <span v-else-if="att.error" class="attach-mask is-err">{{ att.error }}</span>
            <span v-else-if="att.sourceMessageId" class="attach-badge">改这张</span>
            <span class="x" title="移除" @click="removeAttachment(att.id)">×</span>
          </div>
          <div
            v-if="attachments.length < MAX_REFERENCE_COUNT"
            class="attach add"
            title="继续添加参考图"
            @click="pickFiles"
          >
            ＋
          </div>
        </div>

        <textarea
          ref="promptEl"
          v-model="draft"
          rows="2"
          :placeholder="
            mode === 'video'
              ? '视频生成还在接入中，先切换到图片试试'
              : attachments.length
                ? '描述你想在这张参考图上改什么，例如：保留构图，把光换成傍晚暖调…'
                : '描述你想要的画面，例如：极简日式风格的护肤品海报，柔和自然光，留白构图…'
          "
          @keydown="onKeydown"
          @paste="onPaste"
        />

        <!-- 高级参数 -->
        <div v-if="showAdvanced" class="adv">
          <input v-model="negativePrompt" class="input" placeholder="负向提示词：不想出现的内容" />
          <div class="adv-row">
            <label>
              <span>步数</span>
              <input v-model.number="steps" class="input" type="number" min="1" max="150" />
            </label>
            <label>
              <span>引导系数</span>
              <input v-model.number="cfgScale" class="input" type="number" min="1" max="30" step="0.5" />
            </label>
            <label>
              <span>种子</span>
              <input v-model.number="seed" class="input" type="number" min="0" placeholder="0 = 随机" />
            </label>
          </div>
        </div>

        <div class="composer-bar">
          <!-- 图片 / 视频 -->
          <div class="seg">
            <button type="button" :class="{ on: mode === 'image' }" @click="mode = 'image'">
              <app-icon name="image" :size="14" />
              图片
            </button>
            <button type="button" :class="{ on: mode === 'video' }" @click="mode = 'video'">
              <app-icon name="video" :size="14" />
              视频
            </button>
          </div>

          <span class="chip" @click="pickFiles">
            <app-icon name="paperclip" :size="14" />
            参考图
          </span>

          <span class="chip" :class="{ on: showAdvanced }" @click="showAdvanced = !showAdvanced">
            <app-icon name="sliders" :size="14" />
            高级
          </span>

          <!-- 比例 -->
          <div class="dd">
            <span class="chip" @click.stop="toggleMenu('ratio')">
              {{ ratio.label }} · {{ sizeLabel }}
              <app-icon name="chevron-down" :size="12" class="caret" />
            </span>
            <div v-if="openMenu === 'ratio'" class="dd-menu" @click.stop>
              <button
                v-for="(item, i) in RATIO_PRESETS"
                :key="item.label"
                type="button"
                :class="{ on: i === ratioIndex }"
                @click="ratioIndex = i; closeMenu()"
              >
                {{ item.label }} · {{ item.width }}×{{ item.height }}
              </button>
            </div>
          </div>

          <!-- 张数 -->
          <div class="dd">
            <span class="chip" @click.stop="toggleMenu('batch')">
              {{ batchSize }} 张
              <app-icon name="chevron-down" :size="12" class="caret" />
            </span>
            <div v-if="openMenu === 'batch'" class="dd-menu" @click.stop>
              <button
                v-for="option in BATCH_OPTIONS"
                :key="option"
                type="button"
                :class="{ on: option === batchSize }"
                @click="batchSize = option; closeMenu()"
              >
                {{ option }} 张
              </button>
            </div>
          </div>

          <span class="grow" />

          <!-- 模型 -->
          <div class="dd">
            <span class="chip chip-ok" :title="`当前图片模型：${currentModel?.name ?? '未配置'}`" @click.stop="toggleMenu('model')">
              <span class="dot" :style="{ background: hasModel ? 'var(--ok)' : 'var(--warn)' }" />
              {{ currentModel?.name ?? '选择模型' }}
              <app-icon name="chevron-down" :size="12" class="caret" />
            </span>
            <div v-if="openMenu === 'model'" class="dd-menu dd-menu-right" @click.stop>
              <button
                v-for="model in imageModels"
                :key="model.id"
                type="button"
                :class="{ on: model.id === selectedModelId }"
                @click="selectedModelId = model.id; closeMenu()"
              >
                {{ model.name }}
                <small>{{ model.provider }}</small>
              </button>
              <button v-if="!imageModels.length" type="button" @click="goModelConfig">
                去模型配置添加
              </button>
            </div>
          </div>

          <button
            class="send"
            type="button"
            title="生成"
            :disabled="!draft.trim() || uploading"
            @click="send"
          >
            <app-icon name="arrow-up" :size="17" stroke-width="2.2" />
          </button>
        </div>
      </div>

      <div class="composer-tip">
        {{ attachments.length ? '图生图' : '文生图' }} · {{ sizeLabel }} · {{ batchSize }} 张 · 模型可在
        <a style="color: var(--brand)" @click="goModelConfig">模型配置</a>
        中调整 · <kbd>Enter</kbd> 发送，<kbd>Shift</kbd>+<kbd>Enter</kbd> 换行
        <template v-if="!isEmpty">
          · <a style="color: var(--brand)" @click="newChat">新建创作</a>
        </template>
      </div>

      <!-- 隐藏的文件选择框 -->
      <input
        ref="fileInput"
        type="file"
        accept="image/png,image/jpeg,image/webp,image/gif,image/bmp,image/avif"
        multiple
        hidden
        @change="onFileInputChange"
      />
    </div>
  </div>
</template>

<style scoped>
/* 拖入参考图时的高亮 */
.composer-box.is-drag {
  border-color: rgba(18, 181, 229, 0.65);
  box-shadow:
    0 0 0 4px var(--brand-dim),
    var(--sh);
}

/* 输入区下拉：往上弹出，避免被页面底部裁掉 */
.dd {
  position: relative;
}

.dd-menu {
  position: absolute;
  bottom: calc(100% + 8px);
  left: 0;
  z-index: 30;
  min-width: 180px;
  padding: 5px;
  border-radius: var(--r);
  background: var(--card);
  border: 1px solid var(--line-2);
  box-shadow: var(--sh-lg);
  display: flex;
  flex-direction: column;
  gap: 2px;
  max-height: 260px;
  overflow-y: auto;
}

.dd-menu-right {
  left: auto;
  right: 0;
}

.dd-menu button {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 8px 10px;
  border-radius: var(--r-sm);
  font-size: 12.5px;
  font-weight: 600;
  color: var(--sub);
  background: none;
  border: none;
  cursor: pointer;
  text-align: left;
  white-space: nowrap;
  font-family: inherit;
  transition: var(--t);
}

.dd-menu button small {
  font-size: 11px;
  color: var(--dim);
  font-weight: 500;
}

.dd-menu button:hover {
  background: var(--card-2);
  color: var(--text);
}

.dd-menu button.on {
  background: var(--brand-dim);
  color: var(--brand-strong);
}

/* 高级参数 */
.adv {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px dashed var(--line);
}

.adv-row {
  display: flex;
  gap: 8px;
}

.adv-row label {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.adv-row span {
  font-size: 11.5px;
  color: var(--dim);
  font-weight: 600;
}

.adv .input {
  padding: 7px 10px;
  font-size: 12.5px;
}

/* 参考图上传中的遮罩 */
.attach-mask {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 10px;
  font-weight: 600;
  color: #fff;
  background: rgba(4, 7, 12, 0.55);
  text-align: center;
  padding: 2px;
}

.attach-mask.is-err {
  background: rgba(239, 68, 68, 0.7);
}

/* 参考图来自上一轮产物时的角标 */
.attach-badge {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  padding: 2px 0;
  font-size: 10px;
  font-weight: 700;
  color: #fff;
  background: rgba(18, 181, 229, 0.85);
  text-align: center;
}

/* 多张产物时逐张挑选 */
.pick-row {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
  gap: 8px;
  margin-top: 10px;
}

.pick {
  position: relative;
  border-radius: var(--r-sm);
  overflow: hidden;
  border: 1px solid var(--line);
  background: var(--card-2);
}

.pick img {
  width: 100%;
  aspect-ratio: 1 / 1;
  object-fit: cover;
  display: block;
}

.pick-acts {
  display: flex;
  gap: 4px;
  padding: 4px;
}

/* 生成卡的进度行：复用 .gen-loading 的 gl-top 版式 */
.gl-top {
  display: flex;
  align-items: center;
  gap: 11px;
  margin-bottom: 12px;
}

.gl-top b {
  font-size: 12.5px;
  font-weight: 700;
}

.gl-top small {
  font-size: 11.5px;
  color: var(--dim);
  display: block;
  margin-top: 1px;
}

.gl-top .pct {
  margin-left: auto;
  font-size: 13px;
  font-weight: 800;
  color: var(--brand);
  font-variant-numeric: tabular-nums;
}
</style>
