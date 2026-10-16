<script setup lang="ts">
import { NImage, NImageGroup } from 'naive-ui'
import { computed } from 'vue'

import type { TaskAsset } from '@/types'

const props = withDefaults(
  defineProps<{
    images: TaskAsset[]
    /** 每行列数 */
    columns?: number
    /** 缩略图宽高比，如 '1 / 1'、'16 / 9' */
    aspectRatio?: string
  }>(),
  {
    columns: 4,
    aspectRatio: '1 / 1',
  },
)

const gridStyle = computed(() => ({
  gridTemplateColumns: `repeat(${props.columns}, minmax(0, 1fr))`,
}))
</script>

<template>
  <n-image-group>
    <div class="grid gap-2" :style="gridStyle">
      <n-image
        v-for="item in images"
        :key="item.id"
        :src="item.thumbnailUrl || item.url"
        :preview-src="item.url"
        :alt="item.fileName || '生成结果'"
        object-fit="cover"
        lazy
        class="w-full overflow-hidden rounded-lg bg-black/5"
        :style="{ aspectRatio }"
        :img-props="{ style: 'width:100%;height:100%;object-fit:cover' }"
      />
    </div>
  </n-image-group>
</template>
