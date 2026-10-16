<script setup lang="ts">
import { NCheckbox, NTooltip } from 'naive-ui'
import { computed } from 'vue'

import AppIcon from '@/components/common/AppIcon.vue'
import { TASK_STATUS_META, TASK_TYPE_META } from '@/constants'
import type { GenerationTask, TaskAsset, TaskStatus } from '@/types'
import { formatDateTime, formatElapsed } from '@/utils'

const props = withDefaults(
  defineProps<{
    task: GenerationTask
    selectable?: boolean
    selected?: boolean
  }>(),
  {
    selectable: false,
    selected: false,
  },
)

const emit = defineEmits<{
  (e: 'update:selected', value: boolean): void
  (e: 'cancel', task: GenerationTask): void
  (e: 'retry', task: GenerationTask): void
  (e: 'remove', task: GenerationTask): void
  (e: 'preview', asset: TaskAsset, task: GenerationTask): void
  (e: 'copy-prompt', task: GenerationTask): void
}>()

const statusMeta = computed(() => TASK_STATUS_META[props.task.status])
const typeMeta = computed(() => TASK_TYPE_META[props.task.type])

/** 状态标签配色，对齐设计系统的 tag-* 系列 */
const STATUS_CLASS: Record<TaskStatus, string> = {
  pending: 'tag-dim',
  queued: 'tag-cyan',
  running: 'tag-warn',
  succeeded: 'tag-ok',
  failed: 'tag-err',
  canceled: 'tag-dim',
}

const statusClass = computed(() => STATUS_CLASS[props.task.status])

/** 未结束的任务才允许取消 */
const cancellable = computed(() => !statusMeta.value.isFinal)
/** 已结束的任务才允许重试 */
const retryable = computed(() => statusMeta.value.isFinal)

const elapsed = computed(() =>
  formatElapsed(props.task.startedAt ?? props.task.createdAt, props.task.finishedAt),
)

/** 缩略图最多展示 4 个，其余折叠 */
const thumbnails = computed(() => (props.task.outputs ?? []).slice(0, 4))
const restCount = computed(() => Math.max(0, (props.task.outputs?.length ?? 0) - 4))
</script>

<template>
  <div class="task-card card card-pad flex h-full flex-col gap-3" :class="{ 'is-selected': selected }">
    <!-- 头部：类型 / 状态 / 勾选 -->
    <div class="flex items-start gap-2">
      <n-checkbox
        v-if="selectable"
        :checked="selected"
        class="mt-0.5"
        @update:checked="(value: boolean) => emit('update:selected', value)"
      />

      <div class="min-w-0 flex-1">
        <div class="flex items-center gap-2">
          <app-icon :name="typeMeta.icon" :size="15" class="t-sub" />
          <span class="truncate text-sm font-semibold">{{ task.name || '未命名任务' }}</span>
        </div>
        <div class="mt-1 flex items-center gap-2 text-xs t-dim">
          <span>{{ typeMeta.label }}</span>
          <span v-if="task.modelName">· {{ task.modelName }}</span>
        </div>
      </div>

      <span class="tag" :class="statusClass">{{ statusMeta.label }}</span>
    </div>

    <!-- 提示词 -->
    <div class="prompt-box">
      <p class="line-clamp-3 break-all">{{ task.prompt || '（无提示词）' }}</p>
      <button
        class="btn btn-ghost btn-sm mt-2"
        type="button"
        @click="emit('copy-prompt', task)"
      >
        <app-icon name="copy" :size="12" />
        复制提示词
      </button>
    </div>

    <!-- 进度 -->
    <div v-if="!statusMeta.isFinal" class="flex items-center gap-2">
      <div class="bar flex-1">
        <i :style="{ width: `${task.progress}%` }" />
      </div>
      <span class="text-xs font-bold tabular-nums" style="color: var(--brand-strong)">
        {{ task.progress }}%
      </span>
    </div>

    <!-- 失败原因 -->
    <div v-if="task.status === 'failed' && task.errorMessage" class="fail-box">
      <app-icon name="alert" :size="14" class="mt-0.5 shrink-0" />
      <span class="break-all">{{ task.errorMessage }}</span>
    </div>

    <!-- 产物缩略图 -->
    <div v-if="thumbnails.length" class="grid grid-cols-4 gap-2">
      <button
        v-for="(asset, index) in thumbnails"
        :key="asset.id"
        type="button"
        class="thumb"
        @click="emit('preview', asset, task)"
      >
        <img
          v-if="asset.type === 'image'"
          :src="asset.thumbnailUrl || asset.url"
          :alt="asset.fileName"
          loading="lazy"
        />
        <span v-else class="grid h-full w-full place-items-center t-dim">
          <app-icon name="video" :size="20" />
        </span>

        <span
          v-if="index === 3 && restCount > 0"
          class="absolute inset-0 grid place-items-center bg-black/55 text-xs font-semibold text-white"
        >
          +{{ restCount }}
        </span>
      </button>
    </div>

    <!-- 底部信息与操作 -->
    <div class="mt-auto flex items-center gap-2 pt-1">
      <div class="min-w-0 flex-1 text-xs t-dim">
        <n-tooltip trigger="hover">
          <template #trigger>
            <span>{{ formatDateTime(task.createdAt, 'MM-DD HH:mm') }} · 耗时 {{ elapsed }}</span>
          </template>
          {{ formatDateTime(task.createdAt) }}
        </n-tooltip>
      </div>

      <div class="flex items-center gap-1">
        <button
          v-if="cancellable"
          class="btn btn-ghost btn-sm"
          type="button"
          @click="emit('cancel', task)"
        >
          取消
        </button>
        <button
          v-if="retryable"
          class="btn btn-ghost btn-sm"
          type="button"
          @click="emit('retry', task)"
        >
          重试
        </button>
        <button
          class="btn-icon"
          type="button"
          title="删除"
          @click="emit('remove', task)"
        >
          <app-icon name="trash" :size="15" />
        </button>
      </div>
    </div>
  </div>
</template>
