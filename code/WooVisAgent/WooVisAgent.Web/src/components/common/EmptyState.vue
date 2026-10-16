<script setup lang="ts">
import type { VNode } from 'vue'

import AppIcon from './AppIcon.vue'

withDefaults(
  defineProps<{
    /** 图标名 */
    icon?: string
    title?: string
    description?: string
    /** 是否展示虚线外框 */
    bordered?: boolean
  }>(),
  {
    icon: 'inbox',
    title: '暂无数据',
    description: '',
    bordered: false,
  },
)

defineSlots<{
  default?: () => VNode | VNode[]
}>()
</script>

<template>
  <div class="empty" :class="bordered ? 'rounded-2xl border border-dashed' : ''">
    <span class="ico">
      <app-icon :name="icon" :size="24" :stroke-width="1.6" />
    </span>
    <b>{{ title }}</b>
    <p v-if="description">{{ description }}</p>
    <div v-if="$slots.default" class="mt-2">
      <slot />
    </div>
  </div>
</template>
