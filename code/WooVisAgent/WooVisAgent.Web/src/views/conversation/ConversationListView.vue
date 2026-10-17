<script setup lang="ts">
/**
 * 会话列表（「更多会话(N)」的落地页）。
 *
 * 每行展示标题、创建时间、最后修改时间与该会话内的任务数，
 * 支持按图片 / 视频分类筛选与分页。点击任意一行进入聊天窗口并加载该会话全部历史。
 *
 * 删除是破坏性操作，且会连带删掉会话下的所有任务，因此放在确认弹窗里做二次确认；
 * 后端返回的影响面（删了几个任务、保留了几件作品）会回填到提示里，
 * 让用户明确知道"作品库里的东西不会丢"。
 */
import { NButton, NPagination, NSpin } from 'naive-ui'
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import { PAGE_SIZE_OPTIONS } from '@/constants'
import { useConversationStore } from '@/stores/conversationStore'
import type { Conversation, TaskType } from '@/types'
import { dialog, formatDateTime, message } from '@/utils'

const router = useRouter()
const conversationStore = useConversationStore()

const keyword = ref('')
/** null = 全部，image / video = 按生成类型分类 */
const typeFilter = ref<TaskType | null>(null)

/** 当前打开的会话，用于高亮所在行 */
const activeId = computed(() => {
  const id = router.currentRoute.value.query?.conversation
  return typeof id === 'string' ? id : ''
})

async function search(): Promise<void> {
  await conversationStore.loadConversations({
    page: 1,
    type: typeFilter.value ?? undefined,
    keyword: keyword.value.trim(),
  })
}

async function setType(value: TaskType | null): Promise<void> {
  typeFilter.value = value
  await search()
}

async function resetFilters(): Promise<void> {
  keyword.value = ''
  typeFilter.value = null
  await conversationStore.resetAndReload()
}

async function refresh(): Promise<void> {
  await conversationStore.loadConversations()
}

async function handlePageChange(page: number): Promise<void> {
  await conversationStore.loadConversations({ page })
}

async function handlePageSizeChange(pageSize: number): Promise<void> {
  await conversationStore.loadConversations({ page: 1, pageSize })
}

/** 点击某一行：进聊天窗口并加载该会话全部历史 */
function openConversation(conv: Conversation): void {
  void router.push({ name: 'Chat', query: { conversation: conv.id } })
}

function handleRemove(conv: Conversation): void {
  dialog.error({
    title: '删除会话',
    content:
      `确认删除会话「${conv.title || '未命名会话'}」？` +
      `该会话下的 ${conv.taskCount} 个任务与全部聊天记录会一并删除，操作不可恢复。` +
      '已加入「我的作品」的产物会保留。',
    positiveText: '删除会话',
    negativeText: '取消',
    onPositiveClick: async () => {
      const result = await conversationStore.remove(conv.id)
      if (!result) return

      // 侧边栏的角标与最近会话也要跟着更新
      void conversationStore.loadRecent()
      void conversationStore.loadSummary()

      if (result.keptMaterials > 0) {
        message.success(
          `已删除会话、${result.deletedTasks} 个任务与 ${result.deletedMessages} 条消息；` +
            `作品库中的 ${result.keptMaterials} 件产物已保留`,
        )
      } else {
        message.success(`已删除会话、${result.deletedTasks} 个任务与 ${result.deletedMessages} 条消息`)
      }
    },
  })
}

function startNewChat(): void {
  void router.push({ name: 'Chat', query: { new: String(Date.now()) } })
}

onMounted(async () => {
  // 页面筛选控件默认是“全部”，进入页面时也要显式清掉 store 上次留下的条件。
  await conversationStore.loadConversations({ page: 1, type: undefined, keyword: '' })
  await conversationStore.loadSummary()
})
</script>

<template>
  <div>
    <!-- 页面标题条 -->
    <div class="page-head">
      <h1>
        会话列表
        <small>共 {{ conversationStore.total }} 个会话 · 一个会话可包含多个生成任务</small>
      </h1>

      <div class="flex flex-wrap items-center gap-2">
        <n-button size="small" quaternary :loading="conversationStore.loading" @click="refresh">
          <template #icon><app-icon name="refresh" :size="15" /></template>
          刷新
        </n-button>
        <button class="btn btn-primary btn-sm" type="button" @click="startNewChat">
          <app-icon name="plus" :size="15" />
          新建创作
        </button>
      </div>
    </div>

    <!-- 筛选：按图片 / 视频分类 -->
    <div class="filters">
      <div class="group">
        <button class="pill" :class="{ on: typeFilter === null }" @click="setType(null)">
          全部
        </button>
        <button class="pill" :class="{ on: typeFilter === 'image' }" @click="setType('image')">
          图片({{ conversationStore.summary.imageCount }})
        </button>
        <button class="pill" :class="{ on: typeFilter === 'video' }" @click="setType('video')">
          视频({{ conversationStore.summary.videoCount }})
        </button>
      </div>

      <button v-if="typeFilter || keyword" class="pill" @click="resetFilters">重置</button>

      <div class="spacer" />

      <div class="search">
        <span class="search-icon"><app-icon name="search" :size="15" /></span>
        <input v-model="keyword" placeholder="搜索会话标题" @keyup.enter="search" />
      </div>
    </div>

    <!-- 列表 -->
    <n-spin :show="conversationStore.loading">
      <div v-if="conversationStore.conversations.length" class="conv-list">
        <div
          v-for="conv in conversationStore.conversations"
          :key="conv.id"
          class="conv-row"
          :class="{ on: conv.id === activeId }"
          @click="openConversation(conv)"
        >
          <span class="ci">
            <app-icon :name="conv.type === 'video' ? 'video' : 'image'" :size="15" />
          </span>

          <div class="cm">
            <b>{{ conv.title || '未命名会话' }}</b>
            <small>
              {{ conv.taskCount }} 个任务 · {{ conv.messageCount }} 条消息
            </small>
          </div>

          <div class="ct">
            <small>创建 {{ formatDateTime(conv.createdAt, 'YYYY-MM-DD HH:mm') }}</small>
            <small>修改 {{ formatDateTime(conv.updatedAt, 'YYYY-MM-DD HH:mm') }}</small>
          </div>

          <!-- stopPropagation：删除不该顺带跳进聊天窗口 -->
          <button
            class="btn-icon"
            type="button"
            title="删除会话"
            @click.stop="handleRemove(conv)"
          >
            <app-icon name="trash" :size="15" />
          </button>
        </div>
      </div>

      <empty-state
        v-else-if="!conversationStore.loading"
        icon="chat"
        title="还没有会话"
        description="点击「新建创作」，描述你的想法即可开始第一条对话"
      >
        <button class="btn btn-primary btn-sm" type="button" @click="startNewChat">
          <app-icon name="plus" :size="15" />
          新建创作
        </button>
      </empty-state>

      <div v-else class="py-24" />
    </n-spin>

    <!-- 分页 -->
    <div v-if="conversationStore.total > 0" class="flex justify-end pt-5">
      <n-pagination
        :page="conversationStore.query.page"
        :page-size="conversationStore.query.pageSize"
        :item-count="conversationStore.total"
        :page-sizes="PAGE_SIZE_OPTIONS"
        show-size-picker
        show-quick-jumper
        @update:page="handlePageChange"
        @update:page-size="handlePageSizeChange"
      />
    </div>
  </div>
</template>
