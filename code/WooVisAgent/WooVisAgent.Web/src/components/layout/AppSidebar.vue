<script setup lang="ts">
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

/** 侧边栏菜单，直接由路由表派生，避免两处维护 */
const menuItems = computed(() =>
  menuRoutes.map((item) => ({
    name: String(item.name),
    title: (item.meta?.title as string) ?? '',
    icon: (item.meta?.icon as string) ?? 'grid',
  })),
)

/** 子路由高亮父级菜单，例如 /tasks/create 时仍高亮「新建任务」自身 */
const activeKey = computed(() => String(route.name ?? 'TaskList'))

const avatarText = computed(() => authStore.displayName.slice(0, 1).toUpperCase())

const modelSummary = computed(() =>
  modelStore.models.length ? `已配置 ${modelStore.models.length} 个模型` : '尚未配置模型',
)

/** 最近任务：侧边栏只展示最近若干条，点击回到任务列表 */
const recentTasks = computed(() => taskStore.tasks.slice(0, 5))

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
      <router-link class="brand" :to="{ name: 'TaskList' }">
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
      <router-link
        class="btn btn-primary btn-block"
        style="margin: 6px 2px 2px"
        :to="{ name: 'TaskCreate' }"
        title="新建创作"
      >
        <app-icon name="plus" :size="16" />
        <span>新建创作</span>
      </router-link>

      <div class="side-label">
        <span>工作区</span>
      </div>

      <router-link
        v-for="item in menuItems"
        :key="item.name"
        class="nav-item"
        :class="{ on: activeKey === item.name }"
        :to="{ name: item.name }"
        :title="item.title"
      >
        <app-icon :name="item.icon" :size="17" />
        <span>{{ item.title }}</span>
      </router-link>

      <template v-if="recentTasks.length">
        <div class="side-label">
          <span>最近任务</span>
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
