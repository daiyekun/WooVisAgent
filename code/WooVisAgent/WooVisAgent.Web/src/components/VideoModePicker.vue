<script setup lang="ts">
import { NButton, NSelect, NTooltip } from 'naive-ui'
import { computed, ref } from 'vue'

import { uploadFiles } from '@/api/file'
import AppIcon from '@/components/common/AppIcon.vue'
import { message } from '@/utils'
import type { VideoCapabilitySpec, VideoGenerationMode, VideoReferenceInput, VideoReferenceKind } from '@/types'

/**
 * 视频生成模式 + 参考素材面板。
 *
 * 三种模式与即梦（Seedance）一一对应，后端按 role 映射成 content 数组：
 * <list type="bullet">
 *   <item><b>全能生成</b>：图 / 视频 / 音频任意组合，提示词里用 @图片1 指定用途；不传素材就是文生视频</item>
 *   <item><b>首尾帧</b>：恰好两张图</item>
 *   <item><b>智能多帧</b>：多张关键帧串联</item>
 * </list>
 *
 * 组件只管「收集素材」，不负责提交：调用方拿 references 去拼请求即可。
 */

const props = withDefaults(
  defineProps<{
    modelValue: VideoGenerationMode
    /** 模型能力声明；不传时按最保守的口径（只有全能 + 1 张图）处理 */
    capabilities?: VideoCapabilitySpec | null
    references: VideoReferenceInput[]
  }>(),
  { capabilities: null },
)

const emit = defineEmits<{
  'update:modelValue': [value: VideoGenerationMode]
  'update:references': [value: VideoReferenceInput[]]
  /** 点素材上的 @ 时触发，由调用方把 @图片1 插进提示词 */
  'insert-reference': [label: string]
}>()

const uploading = ref(false)

const caps = computed<Required<VideoCapabilitySpec>>(() => ({
  modes: props.capabilities?.modes?.length ? props.capabilities.modes : ['omni'],
  maxReferenceImages: props.capabilities?.maxReferenceImages ?? 1,
  maxReferenceVideos: props.capabilities?.maxReferenceVideos ?? 0,
  maxReferenceAudios: props.capabilities?.maxReferenceAudios ?? 0,
  maxKeyframes: props.capabilities?.maxKeyframes ?? 0,
  framesRequireAdaptiveRatio: props.capabilities?.framesRequireAdaptiveRatio ?? false,
}))

const MODE_OPTIONS = [
  { value: 'omni', label: '全能生成', hint: '只输入文字就是文生视频；也可以上传图片 / 视频 / 音频，在提示词里用 @图片1、@视频1、@音频1 指定它们的用途。' },
  { value: 'firstLastFrame', label: '首尾帧', hint: '上传两张图：第一张是开头，最后一张是结尾，中间的过程由提示词描述。' },
  { value: 'keyframes', label: '智能多帧', hint: '上传多张图作为关键帧，模型自动插值成一镜到底的视频。' },
] as const

const modeOptions = computed(() =>
  MODE_OPTIONS.map((option) => {
    const supported = caps.value.modes.includes(option.value)
    return {
      label: supported ? option.label : `${option.label}（该模型不支持）`,
      value: option.value,
      disabled: !supported,
    }
  }),
)

const currentHint = computed(() => MODE_OPTIONS.find((x) => x.value === props.modelValue)?.hint ?? '')

/** 素材按上传顺序编号：@图片1、@图片2、视频1……与即梦一致 */
const numbered = computed(() => {
  const counters: Record<VideoReferenceKind, number> = { image: 0, video: 0, audio: 0 }
  const labelOf: Record<VideoReferenceKind, string> = { image: '图片', video: '视频', audio: '音频' }

  return props.references.map((reference) => {
    counters[reference.kind] += 1
    return {
      ...reference,
      index: counters[reference.kind],
      label: `@${labelOf[reference.kind]}${counters[reference.kind]}`,
    }
  })
})

const images = computed(() => props.references.filter((x) => x.kind === 'image'))

/** 当前模式下各类型还能再传几个；-1 表示该类型不支持 */
const remaining = computed(() => {
  const mode = props.modelValue

  if (mode === 'firstLastFrame') return { image: 2 - images.value.length, video: 0, audio: 0 }
  if (mode === 'keyframes') return { image: caps.value.maxKeyframes - images.value.length, video: 0, audio: 0 }

  return {
    image: caps.value.maxReferenceImages - images.value.filter((x) => x.kind === 'image').length,
    video: caps.value.maxReferenceVideos - props.references.filter((x) => x.kind === 'video').length,
    audio: caps.value.maxReferenceAudios - props.references.filter((x) => x.kind === 'audio').length,
  }
})

function canAdd(kind: VideoReferenceKind): boolean {
  if (props.modelValue !== 'omni' && kind !== 'image') return false
  return (remaining.value[kind] ?? 0) > 0
}

function removeAt(index: number): void {
  const next = [...props.references]
  next.splice(index, 1)
  emit('update:references', next)
}

function move(index: number, delta: number): void {
  const target = index + delta
  if (target < 0 || target >= props.references.length) return

  const next = [...props.references]
  const [item] = next.splice(index, 1)
  next.splice(target, 0, item)
  emit('update:references', next)
}

/**
 * 上传素材。
 *
 * 按浏览器给出的 MIME 判定种类：用户从「视频」按钮选的文件一定是视频，
 * 从「音频」按钮选的同理——不该再让用户去别处标注一次。
 */
async function handlePick(kind: VideoReferenceKind, event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  if (!files.length) return

  uploading.value = true
  try {
    const results = await uploadFiles(files)
    const added = results.map((item) => ({ kind, url: item.url })) as VideoReferenceInput[]
    emit('update:references', [...props.references, ...added])
    message.success(`已添加 ${added.length} 个${kind === 'image' ? '图片' : kind === 'video' ? '视频' : '音频'}`)
  } catch (error) {
    const detail = (error as { message?: string })?.message
    message.error(detail || '上传失败，请重试')
  } finally {
    uploading.value = false
  }
}

function trigger(kind: VideoReferenceKind): void {
  const input = document.getElementById(`wv-ref-${kind}`) as HTMLInputElement | null
  input?.click()
}

const ACCEPT: Record<VideoReferenceKind, string> = {
  image: 'image/png,image/jpeg,image/webp,image/gif',
  video: 'video/mp4,video/quicktime,video/webm',
  audio: 'audio/mpeg,audio/wav,audio/mp4,audio/aac',
}

const ICON: Record<VideoReferenceKind, string> = {
  image: 'image',
  video: 'video',
  audio: 'music',
}
</script>

<template>
  <div class="vm">
    <div class="vm-head">
      <span class="vm-label">生成方式</span>
      <n-select
        :value="modelValue"
        :options="modeOptions"
        class="w-[11rem]"
        size="small"
        @update:value="(value: VideoGenerationMode) => emit('update:modelValue', value)"
      />
      <span v-if="currentHint" class="vm-hint">{{ currentHint }}</span>
    </div>

    <!-- 素材区 -->
    <div class="vm-body">
      <div class="vm-slots">
        <div v-for="(item, index) in numbered" :key="item.url + index" class="vm-slot">
          <n-tooltip trigger="hover">
            <template #trigger>
              <button type="button" class="vm-chip" @click="emit('insert-reference', item.label)">
                <app-icon :name="ICON[item.kind]" :size="14" />
                <span class="vm-chip-text">{{ item.label }}</span>
              </button>
            </template>
            点击把 {{ item.label }} 插入提示词
          </n-tooltip>

          <img v-if="item.kind === 'image'" :src="item.url" alt="" class="vm-thumb" />
          <video v-else-if="item.kind === 'video'" :src="item.url" class="vm-thumb" muted preload="metadata" />
          <span v-else class="vm-thumb vm-thumb-audio">
            <app-icon name="music" :size="16" />
          </span>

          <div class="vm-ops">
            <n-button quaternary size="tiny" :disabled="index === 0" @click="move(index, -1)">前移</n-button>
            <n-button quaternary size="tiny" :disabled="index === numbered.length - 1" @click="move(index, 1)">
              后移
            </n-button>
            <n-button quaternary size="tiny" type="error" @click="removeAt(index)">移除</n-button>
          </div>
        </div>

        <div v-if="!numbered.length" class="vm-empty">
          {{
            modelValue === 'omni'
              ? '还没有素材。只输入文字也可以生成（文生视频）。'
              : modelValue === 'keyframes'
                ? '请至少添加 2 张关键帧图片。'
                : '请添加 2 张图片：第一张是开头，第二张是结尾。'
          }}
        </div>
      </div>

      <div class="vm-actions">
        <n-button size="tiny" :loading="uploading" :disabled="!canAdd('image')" @click="trigger('image')">
          添加图片<span v-if="remaining.image > 0">（{{ remaining.image }}）</span>
        </n-button>
        <n-button
          v-if="modelValue === 'omni'"
          size="tiny"
          :loading="uploading"
          :disabled="!canAdd('video')"
          @click="trigger('video')"
        >
          添加视频<span v-if="remaining.video > 0">（{{ remaining.video }}）</span>
        </n-button>
        <n-button
          v-if="modelValue === 'omni'"
          size="tiny"
          :loading="uploading"
          :disabled="!canAdd('audio')"
          @click="trigger('audio')"
        >
          添加音频<span v-if="remaining.audio > 0">（{{ remaining.audio }}）</span>
        </n-button>

        <input
          v-for="kind in (['image', 'video', 'audio'] as VideoReferenceKind[])"
          :id="`wv-ref-${kind}`"
          :key="kind"
          class="hidden"
          type="file"
          :accept="ACCEPT[kind]"
          multiple
          @change="handlePick(kind, $event)"
        />
      </div>

      <p v-if="modelValue === 'omni' && numbered.length" class="vm-tip">
        点素材可把 <code>@图片1</code> 这类引用插进提示词，告诉模型每份素材用来做什么。
      </p>
      <p v-if="modelValue === 'firstLastFrame'" class="vm-tip">
        两张图的顺序有意义：第一张是开头，最后一张是结尾。中间怎么过渡由提示词决定。
      </p>
      <p v-if="modelValue === 'keyframes'" class="vm-tip">
        关键帧按顺序拼接，可用「前移 / 后移」调整顺序；帧与帧之间变化不宜过大。
      </p>
    </div>
  </div>
</template>

<style scoped>
.vm {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px;
  border: 1px solid var(--line);
  border-radius: 10px;
  background: var(--bg-soft);
}

.vm-head {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.vm-label {
  font-size: 13px;
  color: var(--sub);
}

.vm-hint {
  font-size: 12px;
  color: var(--sub);
  line-height: 1.5;
  flex: 1 1 100%;
}

.vm-body {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.vm-slots {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.vm-slot {
  position: relative;
  width: 132px;
  border: 1px solid var(--line);
  border-radius: 8px;
  padding: 6px;
  background: var(--bg);
}

.vm-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 6px;
  border-radius: 999px;
  background: var(--accent-soft);
  color: var(--accent);
  font-size: 12px;
  cursor: pointer;
}

.vm-chip-text {
  font-weight: 600;
}

.vm-thumb {
  width: 100%;
  height: 66px;
  margin-top: 6px;
  object-fit: cover;
  border-radius: 6px;
  background: #000;
}

.vm-thumb-audio {
  display: grid;
  place-items: center;
  background: var(--bg-soft);
  color: var(--sub);
}

.vm-ops {
  display: flex;
  gap: 2px;
  justify-content: center;
  margin-top: 4px;
}

.vm-empty {
  font-size: 12px;
  color: var(--sub);
  padding: 6px 2px;
}

.vm-actions {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.vm-tip {
  margin: 0;
  font-size: 12px;
  color: var(--sub);
  line-height: 1.6;
}

.hidden {
  display: none;
}
</style>