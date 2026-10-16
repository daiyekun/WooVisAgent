<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'

import { useTaskRealtime } from '@/hooks/useSignalR'
import { useAppStore } from '@/stores/appStore'
import { useModelStore } from '@/stores/modelStore'
import { useTaskStore } from '@/stores/taskStore'

import AppHeader from './AppHeader.vue'
import AppSidebar from './AppSidebar.vue'

const appStore = useAppStore()
const modelStore = useModelStore()
const taskStore = useTaskStore()
const route = useRoute()

const collapsed = computed(() => appStore.siderCollapsed)

/**
 * 对话创作页自带顶栏与输入区，是一屏占满的独立分栏，
 * 因此外壳不再给它套 AppHeader 与 .scroll/.pad，也按路由名（而非 fullPath）
 * 复用实例——否则「新建创作」附加的 query 会把整个会话状态重置掉。
 */
const isChat = computed(() => route.meta.chat === true)

// SignalR 实时通道在整个布局生命周期内只建立一次，
// 任务进度事件会直接写进 taskStore，各页面从 store 读取即可。
const { start } = useTaskRealtime()

onMounted(async () => {
  void start()

  // 侧边栏底部的模型计数与「最近会话」需要在任意页面都可用，
  // 这里做一次兜底加载，已加载过则跳过。
  if (!modelStore.models.length) void modelStore.loadModels()
  if (!taskStore.tasks.length) void taskStore.loadTasks({ page: 1, pageSize: 8 })
})
</script>

<template>
  <div class="shell" :class="{ mini: collapsed }">
    <app-sidebar :collapsed="collapsed" />

    <div class="main">
      <router-view v-if="isChat" />

      <template v-else>
        <app-header />

        <div class="scroll">
          <div class="pad">
            <router-view v-slot="{ Component }">
              <transition name="fade-slide" mode="out-in">
                <component :is="Component" :key="route.fullPath" />
              </transition>
            </router-view>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.fade-slide-enter-active,
.fade-slide-leave-active {
  transition:
    opacity 0.18s ease,
    transform 0.18s ease;
}

.fade-slide-enter-from {
  opacity: 0;
  transform: translateY(6px);
}

.fade-slide-leave-to {
  opacity: 0;
  transform: translateY(-4px);
}
</style>
