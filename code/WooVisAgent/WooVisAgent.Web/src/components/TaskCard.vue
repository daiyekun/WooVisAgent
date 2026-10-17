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
  (e: 'select', task: GenerationTask): void
  (e: 'remove', task: GenerationTask): void
  (e: 'preview', asset: TaskAsset, task: GenerationTask): void
  (e: 'copy-prompt', task: GenerationTask): void
  /** 双击卡片：带着任务 id 跳进聊天窗口并定位高亮 */
  (e: 'locate', task: GenerationTask): void
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

/** 是否已入选「我的作品」——入选后按钮置灰、重试一并禁用、卡片右上角显示星标 */
const isPicked = computed(() => props.task.isSelected === true)

/** 只有成功完成且尚未入选的任务才能选秀 */
const selectable = computed(
  () => props.task.status === 'succeeded' && !isPicked.value && (props.task.outputs?.length ?? 0) > 0,
)

/**
 * 已结束且未入选的任务才允许重试。
 * 已入选的同样禁掉——重试会产出新图，而作品库里已经引用着旧产物，
 * 用户分不清哪张才是自己选中的那件。
 */
const retryable = computed(() => statusMeta.value.isFinal && !isPicked.value)

const elapsed = computed(() =>
  formatElapsed(props.task.startedAt ?? props.task.createdAt, props.task.finishedAt),
)

/** 缩略图最多展示 4 个，其余折叠 */
const thumbnails = computed(() => (props.task.outputs ?? []).slice(0, 4))
const restCount = computed(() => Math.max(0, (props.task.outputs?.length ?? 0) - 4))
</script>

<template>
  <!--
    双击卡片跳进聊天窗口的定位入口。
    title 里写清双击的作用，因为双击没有 hover 提示之外的可发现性。
    卡片内部的按钮各自 stopPropagation，不会被这里的双击误触发。
  -->
  <div
    class="task-card card card-pad flex h-full flex-col gap-3"
    :class="{ 'is-selected': selected }"
    :title="'双击可跳到该任务所在的会话并定位到它'"
    @dblclick="emit('locate', task)"
  >
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
          <span v-if="isPicked" class="pick-star" title="已入选我的作品">
            <app-icon name="star" :size="12" />
          </span>
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
          v-if="selectable"
          class="btn btn-ghost btn-sm"
          type="button"
          title="把生成结果收录进我的作品"
          @click="emit('select', task)"
        >
          选秀
        </button>
        <button
          v-else-if="isPicked"
          class="btn btn-ghost btn-sm is-disabled"
          type="button"
          disabled
          title="已入选我的作品"
        >
          选秀
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
