<script setup lang="ts">
import { NButton, NCheckbox, NModal, NPagination, NSelect, NSpin } from 'naive-ui'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import AppIcon from '@/components/common/AppIcon.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import TaskCard from '@/components/TaskCard.vue'
import VideoPlayer from '@/components/VideoPlayer.vue'
import { DEFAULT_PAGE_SIZE, PAGE_SIZE_OPTIONS, TASK_STATUS_OPTIONS } from '@/constants'
import { useTaskStore } from '@/stores/taskStore'
import { useAppStore } from '@/stores/appStore'
import type { GenerationTask, TaskAsset } from '@/types'
import { copyText, dialog, formatFileSize, message } from '@/utils'

const router = useRouter()
const taskStore = useTaskStore()
const appStore = useAppStore()

const keyword = ref('')
const typeFilter = ref<GenerationTask['type'] | null>(null)
const statusFilter = ref<GenerationTask['status'] | null>(null)

/** 产物预览弹窗 */
const previewVisible = ref(false)
const previewAsset = ref<TaskAsset | null>(null)

const selectedCount = computed(() => taskStore.selectedIds.length)
const allSelected = computed(
  () => taskStore.tasks.length > 0 && selectedCount.value === taskStore.tasks.length,
)

/** 选中项中是否存在未结束的任务（用于禁用批量删除） */
const hasRunningSelected = computed(() =>
  taskStore.tasks.some(
    (item) => taskStore.selectedIds.includes(item.id) && item.status === 'running',
  ),
)

function isSelected(id: string): boolean {
  return taskStore.selectedIds.includes(id)
}

function toggleSelect(id: string, checked: boolean): void {
  taskStore.selectedIds = checked
    ? [...taskStore.selectedIds, id]
    : taskStore.selectedIds.filter((item) => item !== id)
}

function toggleSelectAll(checked: boolean): void {
  taskStore.selectedIds = checked ? taskStore.tasks.map((item) => item.id) : []
}

async function search(): Promise<void> {
  await taskStore.loadTasks({
    page: 1,
    keyword: keyword.value.trim(),
    type: typeFilter.value ?? undefined,
    status: statusFilter.value ?? undefined,
  })
}

async function setType(value: GenerationTask['type'] | null): Promise<void> {
  typeFilter.value = value
  await search()
}

async function resetFilters(): Promise<void> {
  keyword.value = ''
  typeFilter.value = null
  statusFilter.value = null
  await taskStore.resetAndReload()
}

async function refresh(): Promise<void> {
  await taskStore.loadTasks()
}

async function handlePageChange(page: number): Promise<void> {
  await taskStore.loadTasks({ page })
}

async function handlePageSizeChange(pageSize: number): Promise<void> {
  await taskStore.loadTasks({ page: 1, pageSize })
}

function handleCancel(task: GenerationTask): void {
  dialog.warning({
    title: '取消任务',
    content: `确认取消「${task.name || task.id}」？取消后已生成的部分可能不会保留。`,
    positiveText: '确认取消',
    negativeText: '再想想',
    onPositiveClick: async () => {
      await taskStore.cancelTask(task.id)
      message.success('已提交取消请求')
    },
  })
}

async function handleRetry(task: GenerationTask): Promise<void> {
  await taskStore.retryTask(task.id)
  message.success('已按原参数重新提交')
}

function handleRemove(task: GenerationTask): void {
  dialog.error({
    title: '删除任务',
    content: `确认删除「${task.name || task.id}」？该操作不可恢复。`,
    positiveText: '删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      await taskStore.removeTask(task.id)
      message.success('已删除')
    },
  })
}

function handleBatchRemove(): void {
  const ids = [...taskStore.selectedIds]
  if (!ids.length) return

  dialog.error({
    title: '批量删除',
    content: `确认删除选中的 ${ids.length} 个任务？该操作不可恢复。`,
    positiveText: '删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      await taskStore.removeTasks(ids)
      message.success(`已删除 ${ids.length} 个任务`)
    },
  })
}

async function handleCopyPrompt(task: GenerationTask): Promise<void> {
  const ok = await copyText(task.prompt ?? '')
  ok ? message.success('提示词已复制') : message.error('复制失败')
}

function handlePreview(asset: TaskAsset): void {
  previewAsset.value = asset
  previewVisible.value = true
}

/** SignalR 断开时按设置的时间间隔兜底轮询 */
let timer: ReturnType<typeof setInterval> | undefined

function stopPolling(): void {
  if (timer) {
    clearInterval(timer)
    timer = undefined
  }
}

function startPolling(): void {
  stopPolling()
  const interval = appStore.autoRefreshInterval
  if (interval > 0 && appStore.realtimeState !== 'connected') {
    timer = setInterval(() => void taskStore.loadTasks(), interval)
  }
}

watch(
  () => [appStore.realtimeState, appStore.autoRefreshInterval] as const,
  () => startPolling(),
)

onMounted(async () => {
  /**
   * 侧边栏「最近会话」会先用 pageSize: 8 预热一次列表，
   * 这里显式回到默认分页大小，否则分页器会停在一个并不存在的档位上。
   */
  await taskStore.loadTasks({ page: 1, pageSize: DEFAULT_PAGE_SIZE })
  startPolling()
})

onBeforeUnmount(stopPolling)
</script>

<template>
  <div>
    <!-- 页面标题条 -->
    <div class="page-head">
      <h1>
        任务列表
        <small>
          共 {{ taskStore.total }} 条
          <template v-if="taskStore.runningCount">
            · {{ taskStore.runningCount }} 个进行中
          </template>
        </small>
      </h1>

      <div class="flex flex-wrap items-center gap-2">
        <n-button size="small" quaternary :loading="taskStore.loading" @click="refresh">
          <template #icon><app-icon name="refresh" :size="15" /></template>
          刷新
        </n-button>
        <button class="btn btn-primary btn-sm" type="button" @click="router.push({ name: 'TaskCreate' })">
          <app-icon name="plus" :size="15" />
          新建任务
        </button>
      </div>
    </div>

    <!-- 筛选 -->
    <div class="filters">
      <div class="group">
        <button class="pill" :class="{ on: typeFilter === null }" @click="setType(null)">
          全部类型
        </button>
        <button class="pill" :class="{ on: typeFilter === 'image' }" @click="setType('image')">
          图片生成
        </button>
        <button class="pill" :class="{ on: typeFilter === 'video' }" @click="setType('video')">
          视频生成
        </button>
      </div>

      <div class="group">
        <n-select
          v-model:value="statusFilter"
          :options="TASK_STATUS_OPTIONS"
          placeholder="全部状态"
          clearable
          size="small"
          style="width: 8.5rem"
          @update:value="search"
        />
      </div>

      <button v-if="typeFilter || statusFilter || keyword" class="pill" @click="resetFilters">
        重置
      </button>

      <div class="spacer" />

      <n-checkbox
        v-if="taskStore.tasks.length"
        :checked="allSelected"
        @update:checked="toggleSelectAll"
      >
        全选
      </n-checkbox>

      <button
        v-if="selectedCount"
        class="btn btn-danger btn-sm"
        type="button"
        :disabled="hasRunningSelected"
        @click="handleBatchRemove"
      >
        <app-icon name="trash" :size="14" />
        删除选中({{ selectedCount }})
      </button>

      <div class="search">
        <span class="search-icon"><app-icon name="search" :size="15" /></span>
        <input v-model="keyword" placeholder="搜索任务名称或提示词" @keyup.enter="search" />
      </div>
    </div>

    <!-- 列表 -->
    <n-spin :show="taskStore.loading">
      <div
        v-if="taskStore.tasks.length"
        class="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4"
      >
        <task-card
          v-for="task in taskStore.tasks"
          :key="task.id"
          :task="task"
          selectable
          :selected="isSelected(task.id)"
          @update:selected="(value: boolean) => toggleSelect(task.id, value)"
          @cancel="handleCancel"
          @retry="handleRetry"
          @remove="handleRemove"
          @preview="handlePreview"
          @copy-prompt="handleCopyPrompt"
        />
      </div>

      <empty-state
        v-else-if="!taskStore.loading"
        icon="inbox"
        title="还没有生成任务"
        description="点击右上角「新建任务」，输入提示词即可开始生成图片或视频"
      >
        <button class="btn btn-primary btn-sm" type="button" @click="router.push({ name: 'TaskCreate' })">
          <app-icon name="plus" :size="15" />
          新建任务
        </button>
      </empty-state>

      <div v-else class="py-24" />
    </n-spin>

    <!-- 分页 -->
    <div v-if="taskStore.total > 0" class="flex justify-end pt-5">
      <n-pagination
        :page="taskStore.query.page"
        :page-size="taskStore.query.pageSize"
        :item-count="taskStore.total"
        :page-sizes="PAGE_SIZE_OPTIONS"
        show-size-picker
        show-quick-jumper
        @update:page="handlePageChange"
        @update:page-size="handlePageSizeChange"
      />
    </div>

    <!-- 产物预览 -->
    <n-modal
      v-model:show="previewVisible"
      preset="card"
      :title="previewAsset?.fileName || '产物预览'"
      style="max-width: 900px"
      :bordered="false"
    >
      <template v-if="previewAsset">
        <img
          v-if="previewAsset.type === 'image'"
          :src="previewAsset.url"
          :alt="previewAsset.fileName"
          class="mx-auto max-h-[65vh] rounded-lg object-contain"
        />
        <video-player
          v-else
          :src="previewAsset.url"
          :poster="previewAsset.thumbnailUrl"
          :duration="previewAsset.duration"
          :file-size="previewAsset.fileSize"
          :file-name="previewAsset.fileName"
        />

        <div class="mt-3 flex items-center gap-3 text-xs t-dim">
          <span v-if="previewAsset.width && previewAsset.height">
            {{ previewAsset.width }} × {{ previewAsset.height }}
          </span>
          <span v-if="previewAsset.fileSize">{{ formatFileSize(previewAsset.fileSize) }}</span>
        </div>
      </template>
    </n-modal>
  </div>
</template>
