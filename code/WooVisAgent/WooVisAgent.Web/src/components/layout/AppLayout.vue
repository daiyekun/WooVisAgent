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

// SignalR 实时通道在整个布局生命周期内只建立一次，
// 任务进度事件会直接写进 taskStore，各页面从 store 读取即可。
const { start } = useTaskRealtime()

onMounted(async () => {
  void start()

  // 侧边栏底部的模型计数与「最近任务」需要在任意页面都可用，
  // 这里做一次兜底加载，已加载过则跳过。
  if (!modelStore.models.length) void modelStore.loadModels()
  if (!taskStore.tasks.length) void taskStore.loadTasks({ page: 1, pageSize: 8 })
})
</script>

<template>
  <div class="shell" :class="{ mini: collapsed }">
    <app-sidebar :collapsed="collapsed" />

    <div class="main">
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
