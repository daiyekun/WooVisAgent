import { computed, reactive, ref } from 'vue'
import { defineStore } from 'pinia'

import { conversationApi } from '@/api'
import { DEFAULT_PAGE_SIZE } from '@/constants'
import type {
  Conversation,
  ConversationDetail,
  ConversationSummary,
  DeleteConversationResult,
  TaskType,
} from '@/types'

/** 侧边栏「最近会话」固定只展示这么多条 */
export const RECENT_CONVERSATION_LIMIT = 5

function createDefaultQuery(): { page: number; pageSize: number; type?: TaskType; keyword: string } {
  return {
    page: 1,
    pageSize: DEFAULT_PAGE_SIZE,
    type: undefined,
    keyword: '',
  }
}

export const useConversationStore = defineStore('conversation', () => {
  /** 侧边栏最近会话（固定 5 条） */
  const recent = ref<Conversation[]>([])
  /** 会话总数与分类计数，用于「更多会话(N)」与分类页签 */
  const summary = ref<ConversationSummary>({ total: 0, imageCount: 0, videoCount: 0})

  /** 列表页数据与分页 */
  const conversations = ref<Conversation[]>([])
  const total = ref(0)
  const loading = ref(false)
  const query = reactive(createDefaultQuery())

  /** 当前打开的会话详情 */
  const currentId = ref('')
  const current = ref<ConversationDetail | null>(null)
  const detailLoading = ref(false)

  const totalPages = computed(() => {
    const size = query.pageSize || DEFAULT_PAGE_SIZE
    return Math.max(1, Math.ceil(total.value / size))
  })

  const isEmpty = computed(() => !loading.value && conversations.value.length === 0)

  /**
   * 拉取最近会话 + 总数。
   *
   * 「最近会话」与「更多会话(N)」共用一次请求：
   * 前端只需 slice(0, 5)，总数直接用 total 渲染角标，
   * 避免为了一个角标再多发一次 summary 请求。
   */
  async function loadRecent(): Promise<void> {
    try {
      const result = await conversationApi.fetchConversations({
        page: 1,
        pageSize: RECENT_CONVERSATION_LIMIT,
      })

      recent.value = result.items ?? []
      total.value = result.total ?? 0
    } catch {
      recent.value = []
      total.value = 0
    }
  }

  /** 单独刷分类计数，列表页切换图片 / 视频页签时用 */
  async function loadSummary(): Promise<void> {
    try {
      summary.value = await conversationApi.fetchConversationSummary()
    } catch {
      summary.value = { total: 0, imageCount: 0, videoCount: 0 }
    }
  }

  /** 拉取会话列表（会话列表页） */
  async function loadConversations(patch: Partial<typeof query> = {}): Promise<void> {
    Object.assign(query, patch)
    loading.value = true
    try {
      const result = await conversationApi.fetchConversations({
        page: query.page,
        pageSize: query.pageSize,
        type: query.type,
        keyword: query.keyword || undefined,
      })

      conversations.value = result.items ?? []
      total.value = result.total ?? 0
      summary.value = { ...summary.value, total: result.total ?? 0 }
    } catch {
      conversations.value = []
      total.value = 0
    } finally {
      loading.value = false
    }
  }

  async function search(): Promise<void> {
    query.page = 1
    await loadConversations()
  }

  async function resetAndReload(): Promise<void> {
    Object.assign(query, createDefaultQuery())
    await Promise.all([loadConversations(), loadSummary()])
  }

  /**
   * 取（或复用）一个空会话。
   * 聊天页进入时调用，拿到 id 后每次提交生成都带上它。
   */
  async function ensureConversation(): Promise<Conversation | null> {
    try {
      return await conversationApi.ensureConversation()
    } catch {
      return null
    }
  }

  /**
   * 加载会话详情（会话 + 消息 + 任务）。
   * highlightTaskId 只用于让聊天页知道该往哪滚，这里原样传给后端保持语义完整。
   */
  async function loadDetail(id: string, highlightTaskId?: string): Promise<ConversationDetail | null> {
    detailLoading.value = true
    currentId.value = id
    try {
      const detail = await conversationApi.fetchConversationDetail(id, highlightTaskId)
      current.value = detail
      return detail
    } catch {
      current.value = null
      return null
    } finally {
      detailLoading.value = false
    }
  }

  /** 打开一个已存在的会话；id 为空表示"新建空白会话" */
  async function open(id?: string, highlightTaskId?: string): Promise<ConversationDetail | null> {
    if (!id) {
      const conversation = await ensureConversation()
      if (!conversation) return null
      // 空白会话没有历史，直接给一份空壳，省掉一次必然为空的详情请求
      currentId.value = conversation.id
      current.value = {
        conversation,
        messages: [],
        tasks: [],
      }
      return current.value
    }

    return loadDetail(id, highlightTaskId)
  }

  function closeDetail(): void {
    currentId.value = ''
    current.value = null
  }

  /** 改标题后同步会话本体与最近会话列表，避免两处显示不一致 */
  async function rename(id: string, title: string): Promise<Conversation | null> {
    try {
      const updated = await conversationApi.renameConversation(id, title)

      if (current.value?.conversation.id === id) {
        current.value = { ...current.value, conversation: updated }
      }

      patchLocal(id, updated)
      return updated
    } catch {
      return null
    }
  }

  /**
   * 删除会话。
   * 后端会级联删除消息与任务，已选秀的作品保留；
   * 返回值里的 keptMaterials 让调用方能提示"作品还在"。
   */
  async function remove(id: string): Promise<DeleteConversationResult | null> {
    try {
      const result = await conversationApi.deleteConversation(id)
      removeLocal(id)
      return result
    } catch {
      return null
    }
  }

  /** 局部更新会话信息（列表与最近会话两处同时生效） */
  function patchLocal(id: string, patch: Partial<Conversation>): void {
    const apply = (item: Conversation) => (item.id === id ? { ...item, ...patch } : item)
    recent.value = recent.value.map(apply)
    conversations.value = conversations.value.map(apply)
  }

  function removeLocal(id: string): void {
    recent.value = recent.value.filter((item) => item.id !== id)
    conversations.value = conversations.value.filter((item) => item.id !== id)
    total.value = Math.max(0, total.value - 1)

    if (currentId.value === id) closeDetail()
  }

  return {
    recent,
    summary,
    conversations,
    total,
    loading,
    isEmpty,
    query,
    totalPages,
    currentId,
    current,
    detailLoading,

    loadRecent,
    loadSummary,
    loadConversations,
    search,
    resetAndReload,
    ensureConversation,
    loadDetail,
    open,
    closeDetail,
    rename,
    remove,
  }
})
