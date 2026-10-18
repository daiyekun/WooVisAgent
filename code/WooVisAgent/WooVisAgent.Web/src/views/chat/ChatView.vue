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
import VideoPlayer from '@/components/VideoPlayer.vue'
import VideoModePicker from '@/components/VideoModePicker.vue'
import { cancelImageTask, fetchImageTask, generateImage } from '@/api/image'
import { cancelVideoTask, fetchVideoTask, generateVideo } from '@/api/video'
import { uploadFiles } from '@/api/file'
import { ensureTaskHubStarted, useTaskEvents } from '@/hooks/useSignalR'
import { TASK_STATUS_META } from '@/constants'
import { useAuthStore } from '@/stores/authStore'
import { useConversationStore } from '@/stores/conversationStore'
import { useModelStore } from '@/stores/modelStore'
import { createTempId, downloadUrl, formatDateTime, formatElapsed, message } from '@/utils'
import type {
  ConversationDetail,
  GenerationTask,
  ModelConfig,
  TaskAsset,
  TaskStatus,
  VideoGenerationMode,
  VideoReferenceInput,
} from '@/types'

import {
  BATCH_OPTIONS,
  CHAT_STATUS_META,
  DURATION_OPTIONS,
  FPS_OPTIONS,
  MAX_REFERENCE_COUNT,
  MAX_REFERENCE_SIZE,
  NEW_CHAT_TITLE,
  RATIO_PRESETS,
  VIDEO_RESOLUTION_OPTIONS,
  imageStageText,
  starterCards,
  videoStageText,
  type ChatAttachment,
  type ChatCard,
  type ChatImage,
  type ChatMessage,
  type ChatMode,
  type ImageChatPayload,
  type VideoChatPayload,
} from './chatMock'

const authStore = useAuthStore()
const conversationStore = useConversationStore()
const modelStore = useModelStore()
const route = useRoute()
const router = useRouter()

/** ==================== 会话状态 ==================== */

const title = ref(NEW_CHAT_TITLE)
const messages = ref<ChatMessage[]>([])
const draft = ref('')
const mode = ref<ChatMode>('image')

/**
 * 当前会话 id。
 *
 * 会话是持久化的：进入聊天页先拿一个（或打开指定的），之后每次提交生成都带上它，
 * 后端会把这一轮的用户消息 + 助手消息 + 任务一起挂到该会话下。
 * 这样"一次会话多个任务"和历史回放才成立。
 */
const conversationId = ref('')

/**
 * 需要高亮并滚动定位到的任务 id。
 * 双击任务列表某一项时由 query 传进来，加载完历史后滚到对应消息并短暂高亮。
 */
const highlightTaskId = ref('')

/**
 * taskId → 消息 DOM，用于双击任务后滚动定位。
 * 用 Map 而不是 ref 数组：模板里挂 ref 时 Vue 会按 v-for 顺序塞进数组，
 * 拿不到"哪个元素属于哪个任务"，而双击定位必须精确到具体那条消息。
 */
const messageRefs = new Map<string, HTMLElement>()

/** 当前处于高亮态的消息 id，双击任务定位后短暂展示再自动取消 */
const highlightedMessageId = ref('')

function setMessageRef(taskId: string, el: unknown): void {
  if (el instanceof HTMLElement) messageRefs.set(taskId, el)
  else messageRefs.delete(taskId)
}

/** 用户头像取登录用户首字，与侧边栏底部保持一致 */
const avatarText = computed(() => authStore.displayName.slice(0, 1) || '我')

const isEmpty = computed(() => messages.value.length === 0)

/**
 * 当前会话内的任务数。
 * 一次会话可以对应多个任务（多轮对话每轮生成算一个），
 * 顶栏显式写出来，避免用户以为"这个会话只生成了这一张"。
 */
const taskCountInChat = computed(
  () => messages.value.filter((item) => Boolean(item.card?.taskId)).length,
)

/** ==================== 模型 ==================== */

/**
 * 当前模式下该出现哪些模型。
 *
 * 这一条是「点视频却还在下拉里挑图片模型」的根因：
 * 下拉框之前直接绑 imageModels，切到视频后列表原封不动，
 * 于是用户挑不到任何视频模型，也就没法测试视频链路。
 * 这里按 mode 取对应分类的启用模型，两种模式各自闭环。
 */
const availableModels = computed<ModelConfig[]>(() =>
  mode.value === 'video' ? modelStore.videoModels : modelStore.imageModels,
)

/**
 * 图片模式下的模型列表。
 * 只有 submit()（图片提交）用到——那里需要按 modelId 在图片模型里校验，
 * 因为 rerun 复跑的是历史任务，其 modelId 可能已与当前 mode 不同。
 */
const imageModels = computed(() => modelStore.imageModels)

const selectedModelId = ref('')

const currentModel = computed(() =>
  availableModels.value.find((item) => item.id === selectedModelId.value),
)
const hasModel = computed(() => Boolean(currentModel.value))

/** 模型分类名，用于顶栏与提示文案 */
const modeLabel = computed(() => (mode.value === 'video' ? '视频' : '图片'))

/**
 * 模式切换时重置选中项。
 *
 * 图片模型在视频模式下是非法值，不清掉的话 currentModel 会变成 undefined，
 * 用户会看到"未配置图片模型"却无论如何也点不出可用的选项。
 */
watch(mode, (next) => {
  if (availableModels.value.some((item) => item.id === selectedModelId.value)) return
  selectedModelId.value = modelStore.resolvePreferredModel(next)?.id ?? ''
})

// 模型列表首次加载完成、或用户改了默认模型后，自动选中该用的那个
watch(
  availableModels,
  (list) => {
    if (!list.length) {
      selectedModelId.value = ''
      return
    }

    const stillValid = list.some((item) => item.id === selectedModelId.value)
    if (stillValid) return

    selectedModelId.value = modelStore.resolvePreferredModel(mode.value)?.id ?? list[0].id
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

/** 视频专属参数 */
const durationSeconds = ref<number>(DURATION_OPTIONS[1])
const fps = ref<number>(FPS_OPTIONS[0])
const withAudio = ref(false)

const ratio = computed(() => RATIO_PRESETS[ratioIndex.value])
const sizeLabel = computed(() => `${ratio.value.width}×${ratio.value.height}`)

/**
 * 视频侧分辨率独立成一份状态，不复用图片的比例预设。
 *
 * 图片比例是 1024×1024 这类像素尺寸，套到视频上所有选项都会算出同一个档位（720P），
 * 用户根本选不到 480P / 1080P；而万相 3.0 这类模型恰恰要能选档位。
 */
const videoResolution = ref<string>(VIDEO_RESOLUTION_OPTIONS[1].value)

/**
 * 视频的三种生成方式与参考素材（全能生成 / 首尾帧 / 智能多帧）。
 *
 * 默认「全能生成」：不传素材就是文生视频，与改造前行为完全一致，
 * 用户什么都不配也不会被新概念打扰。
 */
const videoMode = ref<VideoGenerationMode>('omni')
const videoReferences = ref<VideoReferenceInput[]>([])

/** 当前视频模型的能力声明；读不到时按"只有全能 + 1 张图"的保守口径处理 */
const videoCapabilities = computed(
  () => modelStore.videoModels.find((item) => item.id === currentModel.value?.id)?.videoCapabilities ?? null,
)

/** 拖拽区的图片 + 模式面板里的素材，图片在前（保证 @图片1 的顺序直觉正确） */
const mergedVideoReferences = computed<VideoReferenceInput[]>(() => {
  const dropped = attachments.value
    .map((item) => item.url)
    .filter((url): url is string => Boolean(url))
    .map((url) => ({ kind: 'image' as const, url }))

  return [...dropped, ...videoReferences.value]
})

/** 把 @图片1 这类引用插进输入框末尾 */
function insertVideoReference(label: string): void {
  const cursor = draft.value.length
  const needsSpace = cursor > 0 && !/\s$/u.test(draft.value)
  draft.value = `${draft.value}${needsSpace ? ' ' : ''}${label}`
  void fitTextarea()
}

/**
 * 卡片进行中的阶段文案。
 * 图片与视频的阶段划分不同（视频有"生成关键帧"这类中间态），共用一套会让视频长时间停在"正在构图"。
 */
function stageTextOf(card: ChatCard, progress: number): string {
  return card.kind === 'video' ? videoStageText(progress) : imageStageText(progress)
}

/** 下拉菜单键：视频多出分辨率、时长与帧率三项 */
type MenuKey = '' | 'ratio' | 'batch' | 'model' | 'duration' | 'fps' | 'vres'
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
      type: item.type,
      thumbnailUrl: item.thumbnailUrl,
      fileName: item.fileName,
      fileSize: item.fileSize,
      width: item.width,
      height: item.height,
      duration: item.duration,
    }))

    const elapsed = card.startedAt ? formatElapsed(card.startedAt) : null
    // 视频只产出一条，文案跟着类型走，否则会出现"1 张视频"这种别扭说法
    const summary =
      card.kind === 'video'
        ? `视频${card.images[0]?.duration ? ` · ${Math.round(card.images[0].duration)}s` : ''}`
        : `${card.images.length} 张`
    card.meta = `${card.sizeLabel} · ${summary}${elapsed ? ` · 用时 ${elapsed}` : ''}`
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

/**
 * 按卡片类型拉任务详情。
 *
 * 图片与视频的详情接口是各自独立的（/api/image/generations/{id} 与
 * /api/video/generations/{id}），但后端两个 Controller 都复用同一个
 * GetGenerationTaskDetailQuery，返回结构完全一致，所以这里按卡片类型分流即可。
 */
function fetchTaskByKind(taskId: string, kind: ChatMode): Promise<GenerationTask> {
  return kind === 'video'
    ? fetchVideoTask(taskId, { silent: true })
    : fetchImageTask(taskId, { silent: true })
}

/** 主动补拉一次任务详情，把产物填回卡片 */
async function fillOutputs(taskId: string): Promise<void> {
  const card = cardByTaskId(taskId)
  if (!card) return

  try {
    const task = await fetchTaskByKind(taskId, card.kind)
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
    target.text =
      card.kind === 'video'
        ? '视频已生成。可以下载后直接使用，或把上面的画面作为首帧图继续迭代下一段。'
        : `已生成 ${card.images.length} 张。想继续微调就点图片下方的「继续改这张」，把它带回输入框再描述要改的地方。`
  } else if (card.status === 'failed') {
    target.text = '这次生成失败了，可以看看下面的原因，或者换个模型重试。'
  } else {
    target.text = '已取消这次生成。'
  }

  stopWatch(taskId)
  void scrollToBottom()
}

/**
 * 最近一次收到该任务推送的时间。
 *
 * 轮询不再因为"推送能通"就整个撤掉——推送是会丢的：
 * Hub 重连、Aspire 多实例时消息被另一个 Api 进程消费、浏览器休眠后 WebSocket 断过，
 * 都可能出现"该来的那条完成通知没到"。之前这里 stopPolling 之后，
 * 一旦最后一条推送丢失，进度就永远停在最后一个收到的数值上（实测卡在 92%）。
 * 现在改成：推送正常时按时间戳跳过无谓的请求，推送断了则轮询自动接管。
 */
const lastPushAt = new Map<string, number>()

/** 推送刚到过就跳过这一轮轮询，兼顾及时性与请求量 */
const PUSH_GRACE_MS = 8_000

function startPolling(taskId: string): void {
  if (pollTimers.has(taskId)) return

  const timer = window.setInterval(() => {
    void (async () => {
      const card = cardByTaskId(taskId)
      if (!card) {
        stopPolling(taskId)
        return
      }

      // 刚刚才收到推送，这一轮不用再查
      const pushed = lastPushAt.get(taskId) ?? 0
      if (Date.now() - pushed < PUSH_GRACE_MS) return

      try {
        const task = await fetchTaskByKind(taskId, card.kind)
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
  lastPushAt.delete(taskId)
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

  // 只记时间戳，不停轮询：漏掉的那一条推送由轮询兜底（见 lastPushAt 注释）
  lastPushAt.set(event.taskId, Date.now())

  applyTaskUpdate(event.taskId, {
    status: event.status,
    progress: event.progress,
    outputs: event.outputs,
    error: event.status === 'failed' ? event.message : undefined,
    stage: event.status === 'succeeded' ? undefined : event.message,
  })
})

/** 提交一次图片生成。多轮迭代与首次生成共用这一条路径 */
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
  // 与视频侧对称：只在图片分类内回退，避免把视频模型 id 发给图片接口
  const model =
    imageModels.value.find((item) => item.id === payload.modelId) ??
    modelStore.resolvePreferredModel('image')
  if (!model) {
    message.warning('还没有可用的图片模型，请先到「模型配置」里添加一个')
    return
  }

  // 实际使用的模型以解析结果为准：原模型可能已被删除或停用，
  // 若提交仍带旧 modelId，卡片显示的模型名与真正跑的那个会对不上，重跑也会一直失败
  const modelId = model.id

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
      modelId,
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
      modelId,
      prompt: payload.prompt,
      negativePrompt: payload.negativePrompt,
      width: payload.width,
      height: payload.height,
      batchSize: payload.batchSize,
      steps: payload.steps,
      cfgScale: payload.cfgScale,
      seed: payload.seed,
      referenceImageUrls: isEdit ? payload.references : undefined,
      conversationId: conversationId.value || undefined,
    })

    card.taskId = result.taskId
    card.status = result.status
    watchTask(result.taskId, assistantId)

    // 从「新建创作」进入时请求不带 conversationId，会话是在提交这一刻才由后端建的。
    // 必须把后端回传的主键写回本地状态，否则后续消息还会继续走「无会话」分支，
    // 表现为会话列表里看不到、历史消息不落在这个会话下。
    if (result.conversationId && !conversationId.value) {
      conversationId.value = result.conversationId
      // 同步到路由，刷新 / 分享链接后仍停留在同一个会话
      if (typeof router !== 'undefined') {
        void router.replace({
          name: route.name ?? undefined,
          params: { ...route.params, id: result.conversationId },
          query: route.query,
        }).catch(() => {
          /* 路由未定义会话参数时忽略，不影响生成流程 */
        })
      }
    }

    // 后端会把这一轮写进会话，提交成功后刷新侧边栏的「最近会话」与角标
    void conversationStore.loadRecent()
  } catch {
    // 错误提示已由拦截器统一弹出，这里只把卡片收尾，避免留下一个永远转圈的进度条
    card.status = 'failed'
    card.error = '提交失败，请检查模型配置后重试。'
    const target = messages.value.find((item) => item.id === assistantId)
    if (target) target.text = '这次没能提交成功，请检查模型配置后重试。'
  }
}

/**
 * 提交一次视频生成。
 *
 * 与图片侧结构对称：先落一张卡片占位，再提交，最后接上推送与轮询。
 * 图生视频时把输入区的第一张参考图当作首帧图传过去——
 * 后端 ValidateCapabilities 会校验模型是否接受首帧图，
 * 只支持文生的模型带了首帧会被直接拒掉，所以这里只传用户真的放了的图。
 */
async function submitVideo(
  payload: VideoChatPayload & {
    firstFrameUrl?: string
    /** 输入模式；重跑旧卡片时可能不带，此时按全能处理 */
    mode?: VideoGenerationMode
    /** 参考素材；重跑旧卡片时可能不带，此时只用首帧 */
    references?: VideoReferenceInput[]
  },
): Promise<void> {
  // 只能回退到视频分类内的模型。之前这里回退到 currentModel，
  // 而重跑旧卡片时当前 mode 可能是图片，于是把图片模型 id 发给了视频接口，
  // 后端会以「该模型不是视频模型」直接拒绝。
  const model =
    modelStore.videoModels.find((item) => item.id === payload.modelId) ??
    modelStore.resolvePreferredModel('video')
  if (!model) {
    message.warning('还没有可用的视频模型，请先到「模型配置」里添加一个')
    return
  }

  // 与图片侧同理：实际使用的模型以解析结果为准，保证卡片、重跑、提交三者一致
  const modelId = model.id

  const isEdit = Boolean(payload.firstFrameUrl)
  const mode: VideoGenerationMode = payload.mode ?? 'omni'
  const references: VideoReferenceInput[] =
    payload.references?.length ? payload.references : isEdit ? [{ kind: 'image', url: payload.firstFrameUrl! }] : []

  messages.value.push({
    id: createTempId(),
    role: 'user',
    time: '刚刚',
    text: payload.prompt,
    attachments: isEdit
      ? [
          {
            id: createTempId(),
            name: '首帧图',
            size: 0,
            previewUrl: payload.firstFrameUrl!,
            url: payload.firstFrameUrl,
          },
        ]
      : undefined,
  })

  if (title.value === NEW_CHAT_TITLE) title.value = payload.prompt.slice(0, 18)

  const card: ChatCard = {
    kind: 'video',
    status: 'queued',
    progress: 0,
    model: model.name,
    meta: `${payload.resolution} · ${payload.durationSeconds}s`,
    images: [],
    startedAt: Date.now(),
    sizeLabel: payload.resolution,
    // 视频一次只产出一条，卡片骨架按 1 个占位渲染
    count: 1,
    references: references.map((item) => item.url),
    payload: {
      modelId,
      prompt: payload.prompt,
      negativePrompt: payload.negativePrompt,
      durationSeconds: payload.durationSeconds,
      fps: payload.fps,
      resolution: payload.resolution,
      withAudio: payload.withAudio,
      seed: payload.seed,
      mode,
      videoReferences: references,
    },
  }

  const assistantId = createTempId()
  messages.value.push({
    id: assistantId,
    role: 'assistant',
    time: '刚刚',
    text: isEdit ? '收到首帧图，正在生成视频…' : '正在按你的描述生成视频…',
    card,
  })

  void fitTextarea()
  void scrollToBottom()

  try {
    const result = await generateVideo({
      modelId,
      prompt: payload.prompt,
      negativePrompt: payload.negativePrompt,
      mode,
      references,
      firstFrameUrl: payload.firstFrameUrl,
      durationSeconds: payload.durationSeconds,
      fps: payload.fps,
      resolution: payload.resolution,
      withAudio: payload.withAudio,
      seed: payload.seed,
      conversationId: conversationId.value || undefined,
    })

    card.taskId = result.taskId
    card.status = result.status
    watchTask(result.taskId, assistantId)

    void conversationStore.loadRecent()
  } catch {
    card.status = 'failed'
    card.error = '提交失败，请检查视频模型配置后重试。'
    const target = messages.value.find((item) => item.id === assistantId)
    if (target) target.text = '这次没能提交成功，请检查视频模型配置后重试。'
  }
}

async function send(): Promise<void> {
  const text = draft.value.trim()

  if (!text) {
    message.warning('先描述一下你想要的画面')
    return
  }

  if (!currentModel.value) {
    message.warning(`还没有可用的${modeLabel.value}模型，请先到「模型配置」里添加一个`)
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

  // 视频只认首帧图，多余的参考图在这里说清楚，
  // 否则用户传了 3 张却只有 1 张生效，会误以为是模型不支持多图
  if (mode.value === 'video' && references.length > 1) {
    message.warning('视频生成只使用第一张参考图作为首帧，其余的已忽略')
  }

  if (mode.value === 'video') {
    await submitVideo({
      modelId: currentModel.value.id,
      prompt: text,
      negativePrompt: negativePrompt.value.trim() || undefined,
      durationSeconds: durationSeconds.value,
      fps: fps.value,
      resolution: videoResolution.value,
      withAudio: withAudio.value,
      seed: seed.value > 0 ? seed.value : undefined,
      // 拖拽区里的图片与模式面板里的素材合并，图片按顺序排在最前，
      // 这样"@图片1"指向的一定是用户最先放进去的那张
      firstFrameUrl: mergedVideoReferences.value.find((x) => x.kind === 'image')?.url,
      mode: videoMode.value,
      references: mergedVideoReferences.value,
    })
  } else {
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
  }

  // 提交成功后才清空输入区，失败时保留内容方便用户改改再发
  if (messages.value[messages.value.length - 1]?.card?.taskId) {
    draft.value = ''
    attachments.value = []
  }
}

/** 重跑：沿用上一轮的模型与参数，不改任何设置再来一次 */
function rerun(card: ChatCard): void {
  if (card.kind === 'video') {
    const payload = card.payload as VideoChatPayload
    // 重跑沿用这张卡片当初的模式与素材，否则首尾帧 / 多帧任务会被降级成单首帧
    void submitVideo({
      ...payload,
      firstFrameUrl: card.references[0],
      references: payload.videoReferences,
    })
    return
  }

  const payload = card.payload as ImageChatPayload
  void submit({
    ...payload,
    references: card.references,
    fromMessageId: card.fromMessageId,
  })
}

async function cancelTask(messageId: string): Promise<void> {
  const card = findCard(messageId)
  if (!card?.taskId) return

  try {
    if (card.kind === 'video') {
      await cancelVideoTask(card.taskId)
    } else {
      await cancelImageTask(card.taskId)
    }
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
  messageRefs.clear()
  highlightedMessageId.value = ''
  title.value = NEW_CHAT_TITLE
  draft.value = ''
  attachments.value = []
  void fitTextarea()
}

/**
 * 从后端会话详情把消息流还原到前端。
 *
 * 关键在"消息 + 任务"两条数据要重新缝合成前端用的 ChatMessage：
 * 消息只存了文本与地址，尺寸 / 张数 / 模型 / 用时都在任务上，
 * 所以每条助手消息都要按 taskId 去 tasks 里找对应任务来补齐卡片信息。
 * 找不到任务的（例如任务已被删但消息还在）就退化成一张无卡的纯文本消息，
 * 而不是直接丢消息 —— 用户的历史不该因为一次删除操作凭空少一块。
 */
function restoreFromDetail(detail: ConversationDetail): void {
  resetSession()

  title.value = detail.conversation.title || NEW_CHAT_TITLE

  const taskMap = new Map(detail.tasks.map((task) => [task.id, task]))
  const restored: ChatMessage[] = []

  detail.messages.forEach((item) => {
    const task = item.taskId ? taskMap.get(item.taskId) : undefined

    const messageItem: ChatMessage = {
      id: item.id,
      role: item.role === 'assistant' ? 'assistant' : 'user',
      time: formatDateTime(item.createdAt, 'MM-DD HH:mm'),
      text: item.text,
    }

    if (messageItem.role === 'user' && item.assets.length) {
      messageItem.attachments = item.assets.map((asset, index) => ({
        id: `${item.id}-${index}`,
        name: asset.fileName || '参考图',
        size: asset.fileSize ?? 0,
        previewUrl: asset.url,
        url: asset.url,
        sourceMessageId: task?.id,
      }))
    }

    if (messageItem.role === 'assistant' && task) {
      const params = task.params ?? {}
      const isVideo = task.type === 'video'

      messageItem.card = isVideo
        ? buildRestoredVideoCard(task, params)
        : buildRestoredImageCard(task, params)

      // 历史里仍在跑的任务重新接上实时推送，
      // 否则关掉页面再回来就看不到进度了
      if (!TASK_STATUS_META[task.status].isFinal) {
        watchTask(task.id, messageItem.id)
      }
    }

    restored.push(messageItem)
  })

  messages.value = restored
}

/** 历史图片卡片的还原。视频与图片的入参结构不同，分开写比在一个函数里塞 if 更清楚 */
function buildRestoredImageCard(task: GenerationTask, params: Record<string, unknown>): ChatCard {
  const width = Number(params.width ?? 0)
  const height = Number(params.height ?? 0)
  const batchSize = Number(params.batchSize ?? task.outputs.length ?? 1)

  return {
    kind: 'image',
    taskId: task.id,
    status: task.status,
    progress: task.progress,
    model: task.modelName || '未知模型',
    meta: `${width && height ? `${width}×${height} · ` : ''}${batchSize || 1} 张`,
    images: task.outputs.map(toChatImage),
    error: task.errorMessage,
    // 历史消息不再显示"生成中"的秒表，避免打开旧会话时满屏跳秒
    startedAt: undefined,
    stage: task.status === 'succeeded' ? undefined : task.errorMessage,
    sizeLabel: width && height ? `${width}×${height}` : '默认尺寸',
    count: batchSize || task.outputs.length || 1,
    references: task.inputs.map((asset) => asset.url),
    fromMessageId: task.sourceTaskId,
    payload: {
      modelId: task.modelId,
      prompt: task.prompt,
      negativePrompt: task.negativePrompt ?? undefined,
      width: width || 1024,
      height: height || 1024,
      batchSize: batchSize || 1,
      steps: Number(params.steps ?? 30),
      cfgScale: Number(params.cfgScale ?? 7),
      seed: Number(params.seed ?? 0),
    },
  }
}

function buildRestoredVideoCard(task: GenerationTask, params: Record<string, unknown>): ChatCard {
  const resolution = String(params.resolution ?? '1280x720')
  const durationSeconds = Number(params.durationSeconds ?? 5)

  return {
    kind: 'video',
    taskId: task.id,
    status: task.status,
    progress: task.progress,
    model: task.modelName || '未知模型',
    meta: `${resolution} · ${durationSeconds}s`,
    images: task.outputs.map(toChatImage),
    error: task.errorMessage,
    startedAt: undefined,
    stage: task.status === 'succeeded' ? undefined : task.errorMessage,
    sizeLabel: resolution,
    count: 1,
    references: task.inputs.map((asset) => asset.url),
    fromMessageId: task.sourceTaskId,
    payload: {
      modelId: task.modelId,
      prompt: task.prompt,
      negativePrompt: task.negativePrompt ?? undefined,
      durationSeconds,
      fps: Number(params.fps ?? 24),
      resolution,
      withAudio: Boolean(params.withAudio),
      seed: Number(params.seed ?? 0),
    },
  }
}

/** 产物统一走这一个转换，避免图片与视频各写一份而漏掉封面、时长这些字段 */
function toChatImage(asset: TaskAsset): ChatImage {
  return {
    id: asset.id,
    url: asset.url,
    type: asset.type,
    thumbnailUrl: asset.thumbnailUrl,
    fileName: asset.fileName,
    fileSize: asset.fileSize,
    width: asset.width,
    height: asset.height,
    duration: asset.duration,
  }
}

async function newChat(): Promise<void> {
  resetSession()
  highlightTaskId.value = ''

  // 拿一个（或复用）空会话，提交生成时才有归属
  const detail = await conversationStore.open()
  conversationId.value = detail?.conversation.id ?? ''

  void nextTick(() => promptEl.value?.focus())
  message.success('已创建新会话，描述你的想法即可开始')
}

/**
 * 打开一个已有会话（来自最近会话、更多会话，或双击任务定位）。
 *
 * highlightTaskId 非空时，加载完历史会滚动到对应消息并短暂高亮，
 * 让用户一眼看到"这个任务是在这轮对话里生成的"。
 */
async function openConversation(id: string, highlight = ''): Promise<void> {
  resetSession()
  highlightTaskId.value = highlight

  const detail = await conversationStore.open(id, highlight || undefined)
  if (!detail) {
    message.error('会话不存在或无权访问')
    return
  }

  conversationId.value = detail.conversation.id
  restoreFromDetail(detail)

  await nextTick()

  if (highlight) {
    scrollToHighlight(highlight)
  } else {
    void scrollToBottom()
  }
}

/**
 * 删除当前会话并回到空白状态。
 * 后端会级联删掉该会话下的消息与任务，已选秀的作品保留。
 */
async function clearChat(): Promise<void> {
  const id = conversationId.value

  if (!id) {
    resetSession()
    message.success('已清空当前会话')
    return
  }

  const result = await conversationStore.remove(id)
  if (!result) return

  resetSession()
  conversationId.value = ''
  await conversationStore.loadRecent()

  if (result.keptMaterials > 0) {
    message.success(
      `已删除会话及 ${result.deletedTasks} 个任务，作品库中的 ${result.keptMaterials} 件产物已保留`,
    )
  } else {
    message.success(`已删除会话及 ${result.deletedTasks} 个任务`)
  }
}

function applyStarter(item: (typeof starterCards)[number]): void {
  mode.value = item.mode
  draft.value = item.prompt
  void fitTextarea()
  void nextTick(() => promptEl.value?.focus())
}

/**
 * 滚动定位到指定任务对应的消息，并短暂高亮。
 *
 * 找不到对应消息时不滚动也不报错：任务可能已从会话里移除，
 * 这时把整段历史看完比弹一个"没找到"更有用。
 */
function scrollToHighlight(taskId: string): void {
  const el = chatEl.value
  const target = messageRefs.get(taskId)
  if (!el || !target) return

  el.scrollTo({
    top: Math.max(0, target.offsetTop - el.clientHeight / 2 + target.clientHeight / 2),
    behavior: 'smooth',
  })

  highlightedMessageId.value = target.dataset.messageId ?? ''
  window.setTimeout(() => {
    if (highlightedMessageId.value === target.dataset.messageId) {
      highlightedMessageId.value = ''
    }
  }, 2400)
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
    // 先应用入口指定的模式；工作台会同时传 new + mode，不能被 new 分支提前 return 掉。
    if (query.mode === 'image' || query.mode === 'video') mode.value = query.mode

    if (query.new) {
      void newChat()
      return
    }

    // 从最近会话 / 更多会话 / 双击任务跳进来：带 conversation 指定打开哪个会话，
    // taskId 用于定位高亮。conversation 变了才重新加载，
    // 否则 query 里其它字段（如 mode）变化也会把已加载的历史刷掉。
    const targetConversation = typeof query.conversation === 'string' ? query.conversation : ''
    const targetTask = typeof query.taskId === 'string' ? query.taskId : ''

    if (targetConversation && targetConversation !== conversationId.value) {
      void openConversation(targetConversation, targetTask)
      return
    }

    // 直接进 /chat 且还没绑定会话时，先拿一个空会话占位，
    // 这样用户一提交生成就直接挂在会话下，不会产生游离任务
    if (!targetConversation && !conversationId.value) {
      void conversationStore.open().then((detail) => {
        conversationId.value = detail?.conversation.id ?? ''
      })
    }
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
  if (!card.fromMessageId) return card.kind === 'video' ? '图生视频' : '图生图'

  // 轮次 = 目标卡片之前出现过多少张结果卡
  let round = 0
  for (const item of messages.value) {
    if (item.id === card.fromMessageId) break
    if (item.card) round += 1
  }

  const kindLabel = card.kind === 'video' ? '图生视频' : '图生图'
  return round > 0 ? `${kindLabel} · 改自第 ${round} 轮` : kindLabel
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

/**
 * 每秒走一格。
 *
 * 视频动辄几分钟，进度条会在某个百分比上停很久。
 * 「已等待 1m 20s」这种会自己跳秒的文案是唯一能证明"还在跑、不是死了"的信号，
 * 而不是响应式的 now 只会渲染一次，看着同样像卡住。
 */
const nowTick = ref(Date.now())
let tickTimer = 0

/** 视频侧等待文案。返回 null 表示这张卡还没开始计时 */
function waitingLabel(card: ChatCard): string | null {
  if (!card.startedAt) return null

  // 读一次 tick 建立响应式依赖，让文案跟着跳秒
  return formatElapsed(card.startedAt, nowTick.value)
}

onMounted(() => {
  // 模型列表在布局里做过兜底加载，这里再补一次，避免直接刷新对话页时选不到模型
  if (!modelStore.models.length) void modelStore.loadModels()
  // 实时通道由布局启动，这里补一次是为了「直接刷新到对话页」的情况
  void ensureTaskHubStarted()
  document.addEventListener('click', closeMenu)
  tickTimer = window.setInterval(() => {
    nowTick.value = Date.now()
  }, 1000)
})

onBeforeUnmount(() => {
  document.removeEventListener('click', closeMenu)
  window.clearInterval(tickTimer)
  watchers.forEach((_messageId, taskId) => stopWatch(taskId))
})
</script>

<template>
  <!-- 顶栏：会话标题 + 状态 + 操作 -->
  <header class="topbar">
    <div class="tt">
      <b>{{ title }}</b>
      <small>
        对话创作 · 图片与视频共用同一会话
        <template v-if="messages.length">
          · 本次会话共 {{ taskCountInChat }} 个任务
        </template>
      </small>
    </div>

    <div class="spacer" />

    <span
      class="tb-chip"
      :class="{ 'is-warn': !hasModel }"
      :title="hasModel ? `当前${modeLabel}模型：${currentModel?.name}` : `还没配置可用的${modeLabel}模型`"
      @click="goModelConfig"
    >
      <span class="dot" />
      {{ hasModel ? currentModel?.name : `未配置${modeLabel}模型` }}
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
        <p>
          描述你要的图片或视频，参数与模型都在下方输入区里随时可调；上传参考图就是图生图 / 图生视频。
        </p>

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
        <div
          v-else
          ref="(el) => item.card?.taskId && setMessageRef(item.card.taskId, el)"
          :data-message-id="item.id"
          class="msg"
          :class="{ 'is-highlight': highlightedMessageId === item.id }"
        >
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

              <!--
                生成中 / 排队：进度条 + 占位骨架。

                这里必须用 isFinalStatus 排除终态，不能只看「有没有图 / 有没有报错」：
                取消（canceled）的任务既没有产物也没有 errorMessage，
                只看这两个条件的话，已取消的卡片会继续走这一分支，
                于是顶部标签写着「已取消」、下面进度条却还在转、还显示「正在生成图片」。
                failed 同理——后端给了 errorMessage 才走失败分支，
                但产物缺失的异常终态（如 settle 兜底）也会落到这里。
              -->
              <div
                v-if="item.card.taskId && !item.card.images.length && !item.card.error && !isFinalStatus(item.card.status)"
                class="gc-body"
              >
              <div class="gl-top">
                <span class="spinner" />
                <span>
                  <b>正在生成{{ item.card.kind === 'video' ? '视频' : '图片' }}</b>
                  <small>
                    {{ item.card.model }} · {{ item.card.meta }} ·
                    {{ item.card.stage || stageTextOf(item.card, item.card.progress) }}
                    <template v-if="waitingLabel(item.card)">
                      · 已等待 {{ waitingLabel(item.card) }}
                    </template>
                  </small>
                </span>
                <span class="pct">{{ Math.round(item.card.progress) }}%</span>
              </div>
              <div class="bar"><i :style="{ width: `${Math.max(4, item.card.progress)}%` }" /></div>
              <!--
                视频在这里停留几分钟是常态，不给预期用户会以为页面死了。
                跳秒的「已等待」证明流程还活着，这句说明还要等多久。
              -->
              <div v-if="item.card.kind === 'video'" class="gl-hint">
                视频生成通常需要 1-6 分钟，排队高峰更久；超过 20 分钟会自动判为失败，可以重跑。
              </div>
              <div class="gc-grid" :class="item.card.count > 1 ? 'g2' : 'g1'" style="margin-top: 14px">
                <div
                  v-for="n in Math.min(item.card.count, 4)"
                  :key="n"
                  class="shimmer"
                  :style="item.card.kind === 'video' ? 'aspect-ratio: 16 / 9' : 'aspect-ratio: 1 / 1'"
                />
              </div>
              </div>

              <!--
                取消：既没有产物也没有 errorMessage，需要独立的分支给出说明，
                否则用户点完取消只看到一片空白，不知道到底停没停。
              -->
              <div v-else-if="item.card.status === 'canceled'" class="gc-body">
                <div class="cancel-box">
                  <app-icon name="alert" :size="14" />
                  <span>已取消，未生成{{ item.card.kind === 'video' ? '视频' : '图片' }}</span>
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
                <!-- 视频走播放器：<img> 渲染 mp4 只会显示一个破图标 -->
                <video-player
                  v-if="item.card.kind === 'video'"
                  :src="item.card.images[0].url"
                  :poster="item.card.images[0].thumbnailUrl"
                  :duration="item.card.images[0].duration"
                  :file-size="item.card.images[0].fileSize"
                  :file-name="item.card.images[0].fileName"
                />

                <image-preview
                  v-else
                  :images="asAssets(item.card.images)"
                  :columns="item.card.images.length > 1 ? 2 : 1"
                  aspect-ratio="1 / 1"
                />

                <!-- 多张时逐张给出口，方便挑一张继续改 -->
                <div v-if="item.card.kind !== 'video' && item.card.images.length > 1" class="pick-row">
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
                    {{ item.card.kind === 'video' ? '下载视频' : '下载全部' }}
                  </button>
                  <button
                    v-if="item.card.kind !== 'video'"
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

        <!--
          视频的三种生成方式与素材区。
          只在视频模式下出现：图片生成没有这些概念，露出来只会让人以为也能多传几张。
        -->
        <video-mode-picker
          v-if="mode === 'video'"
          v-model="videoMode"
          :references="videoReferences"
          :capabilities="videoCapabilities"
          class="mb-2"
          @update:references="videoReferences = $event"
          @insert-reference="insertVideoReference"
        />

        <textarea
          ref="promptEl"
          v-model="draft"
          rows="2"
          :placeholder="
            mode === 'video'
              ? attachments.length
                ? '描述你希望这段视频怎么动，例如：镜头缓慢推进，暖光洒在桌面…'
                : '描述你想要的视频画面与运镜，例如：城市夜景航拍，镜头缓慢推进，霓虹闪烁…'
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
            {{ mode === 'video' ? '首帧图' : '参考图' }}
          </span>

          <span class="chip" :class="{ on: showAdvanced }" @click="showAdvanced = !showAdvanced">
            <app-icon name="sliders" :size="14" />
            高级
          </span>

          <!-- 比例：图片专属。视频改用下面的分辨率，避免两个模式共用一套像素尺寸 -->
          <div v-if="mode === 'image'" class="dd">
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

          <!-- 分辨率：视频专属 -->
          <div v-else class="dd">
            <span class="chip" @click.stop="toggleMenu('vres')">
              {{ videoResolution }}
              <app-icon name="chevron-down" :size="12" class="caret" />
            </span>
            <div v-if="openMenu === 'vres'" class="dd-menu" @click.stop>
              <button
                v-for="option in VIDEO_RESOLUTION_OPTIONS"
                :key="option.value"
                type="button"
                :class="{ on: option.value === videoResolution }"
                @click="videoResolution = option.value; closeMenu()"
              >
                {{ option.label }}
              </button>
            </div>
          </div>

          <!-- 张数：视频一次只产出一条，这一项对视频无意义 -->
          <div v-if="mode === 'image'" class="dd">
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

          <!-- 时长：视频专属 -->
          <div v-else class="dd">
            <span class="chip" @click.stop="toggleMenu('duration')">
              {{ durationSeconds }}s
              <app-icon name="chevron-down" :size="12" class="caret" />
            </span>
            <div v-if="openMenu === 'duration'" class="dd-menu" @click.stop>
              <button
                v-for="option in DURATION_OPTIONS"
                :key="option"
                type="button"
                :class="{ on: option === durationSeconds }"
                @click="durationSeconds = option; closeMenu()"
              >
                {{ option }} 秒
              </button>
            </div>
          </div>

          <!-- 帧率：视频专属 -->
          <div v-if="mode === 'video'" class="dd">
            <span class="chip" @click.stop="toggleMenu('fps')">
              {{ fps }} fps
              <app-icon name="chevron-down" :size="12" class="caret" />
            </span>
            <div v-if="openMenu === 'fps'" class="dd-menu" @click.stop>
              <button
                v-for="option in FPS_OPTIONS"
                :key="option"
                type="button"
                :class="{ on: option === fps }"
                @click="fps = option; closeMenu()"
              >
                {{ option }} fps
              </button>
            </div>
          </div>

          <span
            v-if="mode === 'video'"
            class="chip"
            :class="{ on: withAudio }"
            title="是否一并生成音效"
            @click="withAudio = !withAudio"
          >
            <app-icon name="zap" :size="14" />
            音效
          </span>

          <span class="grow" />

          <!-- 模型 -->
          <div class="dd">
            <span
              class="chip chip-ok"
              :title="`当前${modeLabel}模型：${currentModel?.name ?? '未配置'}`"
              @click.stop="toggleMenu('model')"
            >
              <span class="dot" :style="{ background: hasModel ? 'var(--ok)' : 'var(--warn)' }" />
              {{ currentModel?.name ?? `选择${modeLabel}模型` }}
              <app-icon name="chevron-down" :size="12" class="caret" />
            </span>
            <div v-if="openMenu === 'model'" class="dd-menu dd-menu-right" @click.stop>
              <button
                v-for="model in availableModels"
                :key="model.id"
                type="button"
                :class="{ on: model.id === selectedModelId }"
                @click="selectedModelId = model.id; closeMenu()"
              >
                {{ model.name }}
                <small>{{ model.provider }}</small>
              </button>
              <button v-if="!availableModels.length" type="button" @click="goModelConfig">
                去模型配置添加{{ modeLabel }}模型
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
        <template v-if="mode === 'video'">
          文生视频 · {{ videoResolution }} · {{ durationSeconds }}s · {{ fps }}fps
          <template v-if="attachments.length"> · 已选首帧图</template>
        </template>
        <template v-else>
          {{ attachments.length ? '图生图' : '文生图' }} · {{ sizeLabel }} · {{ batchSize }} 张
        </template>
        · 模型可在
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
