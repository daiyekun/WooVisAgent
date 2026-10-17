import { del, get, post, put } from './http'
import type {
  Conversation,
  ConversationDetail,
  ConversationSummary,
  DeleteConversationResult,
  PageQuery,
  PagedResult,
} from '@/types'

const BASE_URL = '/api/conversations'

/** 分页查询会话列表，可按图片 / 视频分类与关键词筛选 */
export function fetchConversations(query: PageQuery & { type?: string } = {}) {
  return get<PagedResult<Conversation>>(BASE_URL, query)
}

/** 会话总数与图片 / 视频分类计数 */
export function fetchConversationSummary() {
  return get<ConversationSummary>(`${BASE_URL}/summary`)
}

/**
 * 取（或复用）一个空会话。
 * 聊天页进入时先拿 id，这样首条消息提交时已经有会话归属，
 * 也避免了"用户输了几次字又删掉"留下一堆空会话。
 */
export function ensureConversation() {
  return post<Conversation>(`${BASE_URL}/ensure`)
}

/** 新建会话 */
export function createConversation(title?: string) {
  return post<Conversation>(BASE_URL, title ? { title } : {})
}

/** 改会话标题 */
export function renameConversation(id: string, title: string) {
  return put<Conversation>(`${BASE_URL}/${id}`, { title })
}

/**
 * 会话详情：会话 + 全部消息 + 该会话下的任务。
 * highlightTaskId 只影响前端的滚动定位，后端不额外处理。
 */
export function fetchConversationDetail(id: string, highlightTaskId?: string) {
  return get<ConversationDetail>(`${BASE_URL}/${id}`, highlightTaskId ? { highlightTaskId } : {})
}

/**
 * 删除会话，并级联删除其下的消息与任务。
 * 已选秀进「我的作品」的产物会保留。
 */
export function deleteConversation(id: string) {
  return del<DeleteConversationResult>(`${BASE_URL}/${id}`)
}

export const conversationApi = {
  fetchConversations,
  fetchConversationSummary,
  ensureConversation,
  createConversation,
  renameConversation,
  fetchConversationDetail,
  deleteConversation,
}
