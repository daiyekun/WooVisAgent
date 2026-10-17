<script setup lang="ts">
/**
 * 侧边栏 · IA 对齐原型 docs/prot/app.html
 *
 * 结构：品牌 + 折叠 → 新建创作 → 工作区导航 → 最近会话 → 底部账户。
 *
 * 「最近会话」读的是真实会话数据（conversationStore），固定只展示 5 条：
 * 侧边栏空间有限，多了会把导航挤到需要滚动；要看全部走下面的「更多会话(N)」。
 */
import { computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import { APP_NAME, APP_SLOGAN } from '@/constants'
import { menuRoutes } from '@/router/routes'
import { useAppStore } from '@/stores/appStore'
import { useAuthStore } from '@/stores/authStore'
import { useConversationStore } from '@/stores/conversationStore'
import { useModelStore } from '@/stores/modelStore'
import { useTaskStore } from '@/stores/taskStore'
import { formatDateTime, message } from '@/utils'

defineProps<{ collapsed?: boolean }>()

const appStore = useAppStore()
const authStore = useAuthStore()
const conversationStore = useConversationStore()
const modelStore = useModelStore()
const taskStore = useTaskStore()
const route = useRoute()
const router = useRouter()

/**
 * 工作区导航，直接由路由表派生，避免两处维护。
 * 这里再挡一道 name：`to={{ name: undefined }}` 会让 router.resolve 抛错，
 * 一抛就是整个布局渲染失败、页面全白，代价太大，宁可少渲染一项。
 * 按 meta.order 排序，保证「任务列表」这类后加的菜单落在预期位置。
 */
const navItems = computed(() =>
  menuRoutes
    .filter((item) => Boolean(item.name) && Boolean(item.meta?.title))
    .map((item) => ({
      name: String(item.name),
      title: (item.meta?.title as string) ?? '',
      icon: (item.meta?.icon as string) ?? 'grid',
      order: (item.meta?.order as number) ?? 0,
    }))
    .sort((a, b) => a.order - b.order),
)

const activeKey = computed(() => String(route.name ?? 'Workspace'))

const avatarText = computed(() => authStore.displayName.slice(0, 1).toUpperCase())

const modelSummary = computed(() =>
  modelStore.models.length ? `已配置 ${modelStore.models.length} 个模型` : '尚未配置模型',
)

/**
 * 「我的作品」角标：与工作台的口径保持一致，都按已完成任务的产物条数算。
 * 目前只统计当前分页内的任务，接入作品接口后换成接口返回的 total 更准。
 */
const worksCount = computed(() =>
  taskStore.tasks
    .filter((task) => task.status === 'succeeded')
    .reduce((sum, task) => sum + (task.outputs?.length ?? 0), 0),
)

/** 「任务列表」角标：未结束的任务数，有任务在跑时提示用户可以点进去看进度 */
const runningCount = computed(() => taskStore.runningCount)

/**
 * 最近会话：按最后修改时间倒序取 5 条。
 * 数量由 store 侧的 RECENT_CONVERSATION_LIMIT 决定，这里只做展示兜底。
 */
const recentConversations = computed(() => conversationStore.recent)

/** 「更多会话(N)」的 N，直接用总数，不再单独发一次 summary 请求 */
const conversationTotal = computed(() => conversationStore.summary.total)

/** 当前打开的会话，用于高亮侧边栏里对应的那一条 */
const activeConversationId = computed(() => {
  const queryId = route.query?.conversation
  return typeof queryId === 'string' ? queryId : ''
})

/** 新建创作：让聊天页自己申请一个空会话，避免在侧边栏塞业务逻辑 */
function startNewChat(): void {
  void router.push({ name: 'Chat', query: { new: String(Date.now()) } })
}

/** 点击某个会话：进聊天窗口并加载它的全部历史 */
function openConversation(id: string): void {
  void router.push({ name: 'Chat', query: { conversation: id } })
}

function handleLogout(): void {
  authStore.logout()
  message.success('已退出登录')
  void router.replace({ name: 'Login' })
}

onMounted(() => {
  // 侧边栏是最近会话的唯一入口，布局一挂载就该把数据拉起来；
  // 布局本身因 AppLayout 复用而不会重复挂载，这里不需要额外去重。
  void conversationStore.loadRecent()
  void conversationStore.loadSummary()
})
</script>

<template>
  <aside class="side">
    <!-- 品牌与折叠 -->
    <div class="side-head">
      <router-link class="brand" :to="{ name: 'Workspace' }">
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
      </router-link>

      <button
        class="btn-icon collapse"
        type="button"
        :title="collapsed ? '展开侧边栏' : '收起侧边栏'"
        @click="appStore.toggleSider()"
      >
        <app-icon name="chevron-left" :size="16" />
      </button>
    </div>

    <!-- 主体导航 -->
    <div class="side-body">
      <button
        class="btn btn-primary btn-block"
        style="margin: 6px 2px 2px"
        type="button"
        title="新建创作"
        @click="startNewChat"
      >
        <app-icon name="plus" :size="16" />
        <span>新建创作</span>
      </button>

      <div class="side-label">
        <span>工作区</span>
      </div>

      <router-link
        v-for="item in navItems"
        :key="item.name"
        class="nav-item"
        :class="{ on: activeKey === item.name }"
        :to="{ name: item.name }"
        :title="item.title"
      >
        <app-icon :name="item.icon" :size="17" />
        <span>{{ item.title }}</span>
        <span v-if="item.name === 'Works' && worksCount" class="badge">{{ worksCount }}</span>
        <span v-else-if="item.name === 'TaskList' && runningCount" class="badge">{{ runningCount }}</span>
      </router-link>

      <div class="side-label">
        <span>最近会话</span>
      </div>

      <template v-if="recentConversations.length">
        <div
          v-for="conv in recentConversations"
          :key="conv.id"
          class="conv"
          :class="{ on: conv.id === activeConversationId }"
          :title="conv.title"
          @click="openConversation(conv.id)"
        >
          <span class="ic">
            <app-icon :name="conv.type === 'video' ? 'video' : 'image'" :size="13" />
          </span>
          <span class="tt">{{ conv.title || '未命名会话' }}</span>
          <span class="tm">{{ formatDateTime(conv.updatedAt, 'MM-DD HH:mm') }}</span>
        </div>
      </template>

      <div v-else class="conv conv-empty">暂无会话，去「新建创作」开始</div>

      <!-- 有会话时才显示更多入口；总数来自 store，不用再发一次请求 -->
      <button
        v-if="conversationTotal > 0"
        class="conv-more"
        type="button"
        @click="router.push({ name: 'Conversations' })"
      >
        <app-icon name="list" :size="13" />
        <span>更多会话({{ conversationTotal }})</span>
      </button>
    </div>

    <!-- 底部账户 -->
    <div class="side-foot">
      <span class="avatar">{{ avatarText }}</span>
      <div class="who">
        <b :title="authStore.displayName">{{ authStore.displayName }}</b>
        <small>{{ modelSummary }}</small>
      </div>
      <button class="btn-icon" type="button" title="退出登录" @click="handleLogout">
        <app-icon name="logout" :size="16" />
      </button>
    </div>
  </aside>
</template>
