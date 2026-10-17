<script setup lang="ts">
import { NButton, NSpace, NTag } from 'naive-ui'
import { computed, onMounted, ref } from 'vue'

import AppIcon from '@/components/common/AppIcon.vue'
import { copyText, downloadUrl, formatDurationSeconds, formatFileSize, message } from '@/utils'

const props = withDefaults(
  defineProps<{
    src: string
    poster?: string
    /** 时长（秒） */
    duration?: number
    /** 文件大小（字节） */
    fileSize?: number
    /** 下载时的文件名 */
    fileName?: string
    /**
     * 打开即播放。用于「作品库点封面直接看」这类明确由用户点击触发的场景；
     * 默认 false，聊天流里的历史视频仍然保持手动播放，不改变原有体验。
     */
    autoplay?: boolean
  }>(),
  {
    poster: '',
    duration: undefined,
    fileSize: undefined,
    fileName: undefined,
    autoplay: false,
  },
)

const videoRef = ref<HTMLVideoElement | null>(null)
const playing = ref(false)

const metaText = computed(() => {
  const parts: string[] = []
  if (props.duration) parts.push(formatDurationSeconds(props.duration))
  if (props.fileSize) parts.push(formatFileSize(props.fileSize))
  return parts.join(' · ')
})

function togglePlay(): void {
  const el = videoRef.value
  if (!el) return

  if (el.paused) {
    void el.play()
  } else {
    el.pause()
  }
}

function handleDownload(): void {
  downloadUrl(props.src, props.fileName)
}

onMounted(() => {
  if (!props.autoplay) return

  // 只写 autoplay 属性并不可靠（弹窗里挂载的 video 常被浏览器判定为「非用户手势」）。
  // 这里再显式 play() 一次；被策略拦下也不报错，用户点中间的播放按钮即可。
  void videoRef.value?.play().catch(() => undefined)
})

async function handleCopyUrl(): Promise<void> {
  const ok = await copyText(props.src)
  ok ? message.success('视频地址已复制') : message.error('复制失败，请手动复制')
}
</script>

<template>
  <div class="overflow-hidden rounded-xl border">
    <div class="relative bg-black">
      <video
        ref="videoRef"
        :src="src"
        :poster="poster || undefined"
        class="block max-h-[60vh] w-full"
        preload="metadata"
        playsinline
        controls
        @play="playing = true"
        @pause="playing = false"
        @ended="playing = false"
      />

      <!-- 未播放时覆盖一个播放按钮，提升可点击性 -->
      <button
        v-if="!playing"
        type="button"
        class="absolute inset-0 grid place-items-center bg-black/25 transition-colors hover:bg-black/35"
        @click="togglePlay"
      >
        <span class="grid h-14 w-14 place-items-center rounded-full bg-white/90 text-black">
          <app-icon name="play" :size="22" />
        </span>
      </button>
    </div>

    <div class="flex items-center gap-2 px-3 py-2">
      <n-tag v-if="metaText" size="small" :bordered="false">{{ metaText }}</n-tag>
      <div class="flex-1" />

      <n-space :size="6">
        <n-button size="small" quaternary @click="handleCopyUrl">
          <template #icon><app-icon name="link" :size="15" /></template>
          复制地址
        </n-button>
        <n-button size="small" quaternary @click="handleDownload">
          <template #icon><app-icon name="download" :size="15" /></template>
          下载
        </n-button>
      </n-space>
    </div>
  </div>
</template>
