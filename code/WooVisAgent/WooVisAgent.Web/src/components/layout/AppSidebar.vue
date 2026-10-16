<script setup lang="ts">
/**
 * 侧边栏 · IA 对齐原型 docs/prot/app.html
 *
 * 结构：品牌 + 折叠 → 新建创作 → 工作区导航 → 最近会话 → 底部账户。
 * 「最近会话」读的是真实任务数据（taskStore），版式与原型 1:1；
 * 等会话模型落地后再换成按会话聚合的结果。
 */
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import { APP_NAME, APP_SLOGAN } from '@/constants'
import { menuRoutes } from '@/router/routes'
import { useAppStore } from '@/stores/appStore'
import { useAuthStore } from '@/stores/authStore'
import { useModelStore } from '@/stores/modelStore'
import { useTaskStore } from '@/stores/taskStore'
import { formatDateTime, message } from '@/utils'

defineProps<{ collapsed?: boolean }>()

const appStore = useAppStore()
const authStore = useAuthStore()
const modelStore = useModelStore()
const taskStore = useTaskStore()
const route = useRoute()
const router = useRouter()

/**
 * 工作区导航，直接由路由表派生，避免两处维护。
 * 这里再挡一道 name：`to={{ name: undefined }}` 会让 router.resolve 抛错，
 * 一抛就是整个布局渲染失败、页面全白，代价太大，宁可少渲染一项。
 */
const navItems = computed(() =>
  menuRoutes
    .filter((item) => Boolean(item.name) && Boolean(item.meta?.title))
    .map((item) => ({
      name: String(item.name),
      title: (item.meta?.title as string) ?? '',
      icon: (item.meta?.icon as string) ?? 'grid',
    })),
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

/** 最近会话：先展示最近若干条任务，点击回到任务列表查看全部 */
const recentTasks = computed(() => taskStore.tasks.slice(0, 5))

function startNewChat(): void {
  void router.push({ name: 'Chat', query: { new: String(Date.now()) } })
}

function handleLogout(): void {
  authStore.logout()
  message.success('已退出登录')
  void router.replace({ name: 'Login' })
}
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
      </router-link>

      <template v-if="recentTasks.length">
        <div class="side-label">
          <span>最近会话</span>
          <span class="mini" @click="router.push({ name: 'TaskList' })">全部</span>
        </div>

        <div
          v-for="task in recentTasks"
          :key="task.id"
          class="conv"
          :title="task.name || task.id"
          @click="router.push({ name: 'TaskList' })"
        >
          <span class="ic">
            <app-icon :name="task.type === 'video' ? 'video' : 'image'" :size="13" />
          </span>
          <span class="tt">{{ task.name || task.prompt || '未命名任务' }}</span>
          <span class="tm">{{ formatDateTime(task.createdAt, 'MM-DD HH:mm') }}</span>
        </div>
      </template>
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
