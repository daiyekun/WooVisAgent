<script setup lang="ts">
import {
  NAlert,
  NButton,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NSwitch,
} from 'naive-ui'
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'

import { agentApi, imageApi, videoApi } from '@/api'
import AppIcon from '@/components/common/AppIcon.vue'
import ModelSettingForm from '@/components/ModelSettingForm.vue'
import { useModelStore } from '@/stores/modelStore'
import { useTaskStore } from '@/stores/taskStore'
import type { TaskType } from '@/types'
import { message } from '@/utils'

const router = useRouter()
const modelStore = useModelStore()
const taskStore = useTaskStore()

const taskType = ref<TaskType>('image')
const submitting = ref(false)

/** 通用表单字段 */
const form = reactive({
  name: '',
  modelId: null as string | null,
  prompt: '',
  negativePrompt: '',
})

/** 图片专属参数 */
const imageOptions = reactive({
  width: 1024,
  height: 1024,
  batchSize: 1,
  steps: 30,
  cfgScale: 7,
  seed: null as number | null,
})

/** 视频专属参数 */
const videoOptions = reactive({
  firstFrameUrl: '',
  durationSeconds: 5,
  fps: 24,
  resolution: '1280x720',
  withAudio: false,
})

/** 选中模型的扩展参数 */
const modelParams = ref<Record<string, unknown>>({})

/**
 * 视频分辨率选项。
 *
 * 两组并存是因为两代模型的字段形态不同：
 * - 档位（480P/720P/1080P）：万相 2.6 起用 resolution + ratio，只给清晰度、
 *   比例交给模型自适应（ratio=adaptive），这是官方示例的写法；
 * - 像素尺寸（1280x720 等）：老模型只认 size，比例由宽高推导。
 * 两组都会按当前预设自动换算，用户不需要关心底层字段。
 */
const RESOLUTION_OPTIONS = [
  { label: '480P（自适应比例）', value: '480P' },
  { label: '720P（自适应比例）', value: '720P' },
  { label: '1080P（自适应比例）', value: '1080P' },
  { label: '1280 × 720 (720P 横屏)', value: '1280x720' },
  { label: '720 × 1280 (720P 竖屏)', value: '720x1280' },
  { label: '1024 × 1024 (方形)', value: '1024x1024' },
  { label: '1920 × 1080 (1080P)', value: '1920x1080' },
]

/** 当前类型下可选的模型 */
const modelOptions = computed(() => {
  const list = taskType.value === 'image' ? modelStore.imageModels : modelStore.videoModels
  return list.map((item) => ({
    label: `${item.name}${item.isDefault ? ' (默认)' : ''}`,
    value: item.id,
  }))
})

const currentModel = computed(() => modelStore.getModelById(form.modelId))
const hasModel = computed(() => modelOptions.value.length > 0)

/**
 * 当前模型是否支持图生视频 / 文生视频。
 *
 * 不少视频模型只支持其中一种（例如 wanx2.1-i2v-turbo 只能图生视频）。
 * 若不据此隐藏不适用的输入，用户会得到"提交成功但首帧图被无视"的结果——
 * 对图生视频模型做文生视频，产出画面和提示词毫无关系，用户完全无从判断原因。
 * 这里读不到声明时默认都支持，与后端校验保持一致。
 */
const supportsImageToVideo = computed(() => currentModel.value?.supportsImageToVideo !== false)
const supportsTextToVideo = computed(() => currentModel.value?.supportsTextToVideo !== false)

/** 切换到只支持图生的模型时，已填的首帧图应保留；反过来若模型不支持图生则清掉，避免带脏数据提交 */
watch(supportsImageToVideo, (supported) => {
  if (!supported) videoOptions.firstFrameUrl = ''
})

const canSubmit = computed(
  () => Boolean(form.modelId) && form.prompt.trim().length > 0 && !submitting.value,
)

/** 切换类型时清空模型与扩展参数，避免参数串味 */
watch(taskType, () => {
  form.modelId = modelStore.resolvePreferredModel(taskType.value)?.id ?? null
})

/** 切换模型时按新模型的参数规格重置扩展参数 */
watch(
  () => form.modelId,
  (id) => {
    const model = modelStore.getModelById(id)
    const initial: Record<string, unknown> = {}
    model?.params?.forEach((spec) => {
      if (spec.defaultValue !== undefined) initial[spec.key] = spec.defaultValue
    })
    modelParams.value = initial
  },
)

function resetForm(): void {
  form.name = ''
  form.prompt = ''
  form.negativePrompt = ''
  form.modelId = modelStore.resolvePreferredModel(taskType.value)?.id ?? null
}

async function submit(): Promise<void> {
  if (!form.modelId) {
    message.warning('请先选择模型，或在「模型配置」中新增一个可用模型')
    return
  }
  if (!form.prompt.trim()) {
    message.warning('请输入提示词')
    return
  }

  // 与后端 ValidateCapabilities 同一套判定，前端先拦一道给出即时反馈，
  // 后端仍会再校验一次（前端校验只为准入，不作为安全边界）
  if (taskType.value === 'video' && !supportsImageToVideo.value && !videoOptions.firstFrameUrl.trim()) {
    message.warning('该模型仅支持图生视频，请先填入首帧图地址')
    return
  }

  submitting.value = true
  try {
    if (taskType.value === 'image') {
      const result = await imageApi.generateImage({
        name: form.name.trim() || undefined,
        modelId: form.modelId,
        prompt: form.prompt.trim(),
        negativePrompt: form.negativePrompt.trim() || undefined,
        width: imageOptions.width,
        height: imageOptions.height,
        batchSize: imageOptions.batchSize,
        steps: imageOptions.steps,
        cfgScale: imageOptions.cfgScale,
        seed: imageOptions.seed ?? undefined,
        params: modelParams.value,
      })

      // 把新任务塞进列表，返回列表页能立刻看到
      await taskStore.loadTasks({ page: 1 })
      message.success('已提交图片生成任务')
      console.info('[image] generation submitted', result.taskId)
    } else {
      const result = await videoApi.generateVideo({
        name: form.name.trim() || undefined,
        modelId: form.modelId,
        prompt: form.prompt.trim(),
        negativePrompt: form.negativePrompt.trim() || undefined,
        firstFrameUrl: videoOptions.firstFrameUrl.trim() || undefined,
        durationSeconds: videoOptions.durationSeconds,
        fps: videoOptions.fps,
        resolution: videoOptions.resolution,
        withAudio: videoOptions.withAudio,
        params: modelParams.value,
      })

      await taskStore.loadTasks({ page: 1 })
      message.success('已提交视频生成任务')
      console.info('[video] generation submitted', result.taskId)
    }

    router.push({ name: 'TaskList' })
  } finally {
    submitting.value = false
  }
}

/** 是否显示 Agent 润色入口 */
const agentAvailable = ref(false)
const polishing = ref(false)

async function probeAgent(): Promise<void> {
  try {
    const status = await agentApi.fetchAgentStatus()
    agentAvailable.value = status.enabled && status.available
  } catch {
    agentAvailable.value = false
  }
}

/**
 * 调用 Agent 润色提示词。
 *
 * 注意：这是**纯文本改写**，不会提交任何生成任务，也不会扣配额。
 * 润色结果直接覆盖输入框，用户不满意可以撤销（Ctrl+Z）或重新手写。
 */
async function polishPrompt(): Promise<void> {
  const source = form.prompt.trim()
  if (!source) {
    message.warning('先写一句描述，再让 Agent 润色')
    return
  }

  polishing.value = true
  try {
    const result = await agentApi.polishPrompt({ prompt: source, target: taskType.value })
    form.prompt = result.polished

    // 负向提示词只补空位，不覆盖用户已经写好的内容
    if (!form.negativePrompt.trim() && result.negative) {
      form.negativePrompt = result.negative
    }

    message.success(result.tips[0] ? `已润色：${result.tips[0]}` : '提示词已润色')
  } catch (error) {
    const detail = (error as { message?: string })?.message
    message.error(detail || '润色失败，请稍后重试')
  } finally {
    polishing.value = false
  }
}

onMounted(async () => {
  if (!modelStore.models.length) {
    await modelStore.loadModels()
  }
  form.modelId = modelStore.resolvePreferredModel(taskType.value)?.id ?? null

  // 探一次 Agent 可用性决定是否显示润色按钮。失败按不可用处理，
  // 任何异常都不允许影响本页正常的提交路径
  void probeAgent()
})

</script>

<template>
  <div>
    <!-- 页面标题条 + 类型切换 -->
    <div class="page-head">
      <h1>
        新建任务
        <small>填写提示词与参数，提交后进入队列，进度会实时推送到任务列表</small>
      </h1>

      <div class="seg">
        <button type="button" :class="{ on: taskType === 'image' }" @click="taskType = 'image'">
          <app-icon name="image" :size="14" />
          图片生成
        </button>
        <button type="button" :class="{ on: taskType === 'video' }" @click="taskType = 'video'">
          <app-icon name="video" :size="14" />
          视频生成
        </button>
      </div>
    </div>

    <div class="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
      <!-- 主表单 -->
      <div class="card card-pad">
        <div class="card-head">
          <h3>
            <app-icon name="sliders" :size="17" />
            任务参数
          </h3>
        </div>

        <n-alert v-if="!hasModel" type="warning" :bordered="false" class="mb-4">
          当前没有启用中的{{ taskType === 'image' ? '图片' : '视频' }}模型，请先到「模型配置」中添加并启用。
        </n-alert>

        <n-form label-placement="left" label-width="110" :show-feedback="false">
          <n-form-item label="任务名称">
            <n-input
              v-model:value="form.name"
              placeholder="留空则由后端按提示词自动命名"
              clearable
            />
          </n-form-item>

          <n-form-item label="使用模型">
            <n-select
              v-model:value="form.modelId"
              :options="modelOptions"
              placeholder="请选择模型"
              :disabled="!hasModel"
              clearable
            />
          </n-form-item>

          <n-form-item>
            <template #label>
              <span class="inline-flex items-center gap-1">
                提示词
                <!-- Agent 润色：纯文本改写，不提交任务。没配语言模型时整块不渲染 -->
                <n-button
                  v-if="agentAvailable"
                  text
                  type="primary"
                  size="tiny"
                  :loading="polishing"
                  :disabled="!form.prompt.trim() || polishing"
                  @click="polishPrompt"
                >
                  Agent 润色
                </n-button>
              </span>
            </template>
            <n-input
              v-model:value="form.prompt"
              type="textarea"
              :rows="4"
              :maxlength="2000"
              show-count
              placeholder="描述你想生成的画面，越具体效果越稳定"
            />
          </n-form-item>

          <n-form-item label="负向提示词">
            <n-input
              v-model:value="form.negativePrompt"
              type="textarea"
              :rows="2"
              :maxlength="1000"
              show-count
              placeholder="不希望出现的元素，如：模糊、多余肢体、水印"
            />
          </n-form-item>

          <!-- 图片专属 -->
          <template v-if="taskType === 'image'">
            <n-form-item label="尺寸">
              <n-space :size="8" align="center">
                <n-input-number
                  v-model:value="imageOptions.width"
                  :min="64"
                  :max="4096"
                  :step="64"
                  class="w-[7.5rem]"
                >
                  <template #prefix>W</template>
                </n-input-number>
                <span class="t-dim">×</span>
                <n-input-number
                  v-model:value="imageOptions.height"
                  :min="64"
                  :max="4096"
                  :step="64"
                  class="w-[7.5rem]"
                >
                  <template #prefix>H</template>
                </n-input-number>
              </n-space>
            </n-form-item>

            <n-form-item label="生成数量">
              <n-input-number
                v-model:value="imageOptions.batchSize"
                :min="1"
                :max="8"
                class="w-[7.5rem]"
              />
            </n-form-item>

            <n-form-item label="采样步数">
              <n-input-number
                v-model:value="imageOptions.steps"
                :min="1"
                :max="150"
                class="w-[7.5rem]"
              />
            </n-form-item>

            <n-form-item label="提示词强度">
              <n-input-number
                v-model:value="imageOptions.cfgScale"
                :min="1"
                :max="30"
                :step="0.5"
                class="w-[7.5rem]"
              />
            </n-form-item>

            <n-form-item label="随机种子">
              <n-input-number
                v-model:value="imageOptions.seed"
                :min="0"
                placeholder="留空则随机"
                clearable
                class="w-40"
              />
            </n-form-item>
          </template>

          <!-- 视频专属 -->
          <template v-else>
            <n-alert
              v-if="!supportsImageToVideo && !supportsTextToVideo"
              type="warning"
              :show-icon="true"
            >
              当前模型未声明支持文生视频或图生视频，请先在「模型配置」中勾选其支持的能力。
            </n-alert>

            <n-form-item v-if="supportsImageToVideo" label="首帧图地址">
              <n-input
                v-model:value="videoOptions.firstFrameUrl"
                placeholder="可选，填入图片 URL 实现图生视频"
                clearable
              />
            </n-form-item>

            <n-alert v-else type="info" :show-icon="true" class="mb">
              该模型仅支持文生视频，不接受首帧图。
            </n-alert>

            <n-form-item v-if="!supportsTextToVideo" label="首帧图地址（必填）">
              <n-input
                v-model:value="videoOptions.firstFrameUrl"
                placeholder="该模型仅支持图生视频，请填入首帧图 URL"
                clearable
              />
            </n-form-item>

            <n-form-item label="时长(秒)">
              <n-input-number
                v-model:value="videoOptions.durationSeconds"
                :min="1"
                :max="60"
                class="w-[7.5rem]"
              />
            </n-form-item>

            <n-form-item label="帧率">
              <n-input-number
                v-model:value="videoOptions.fps"
                :min="8"
                :max="60"
                class="w-[7.5rem]"
              />
            </n-form-item>

            <n-form-item label="分辨率">
              <n-select v-model:value="videoOptions.resolution" :options="RESOLUTION_OPTIONS" />
            </n-form-item>

            <n-form-item label="生成音效">
              <n-switch v-model:value="videoOptions.withAudio" />
            </n-form-item>
          </template>
        </n-form>

        <!-- 模型扩展参数 -->
        <div v-if="currentModel?.params?.length" class="mt-2 border-t pt-4">
          <div class="mb-3 text-sm font-semibold">模型扩展参数</div>
          <model-setting-form v-model="modelParams" :specs="currentModel.params" />
        </div>

        <div class="mt-6 flex items-center gap-3 border-t pt-4">
          <button class="btn btn-ghost btn-sm" type="button" @click="resetForm">重置</button>
          <div class="flex-1" />
          <button class="btn btn-ghost btn-sm" type="button" @click="router.back()">取消</button>
          <n-button type="primary" :loading="submitting" :disabled="!canSubmit" @click="submit">
            <template #icon><app-icon name="sparkles" :size="15" /></template>
            提交生成
          </n-button>
        </div>
      </div>

      <!-- 侧栏：说明与提示 -->
      <div class="flex flex-col gap-4">
        <div class="card card-pad">
          <div class="card-head">
            <h3><app-icon name="check" :size="17" />提交预览</h3>
          </div>

          <div class="flex flex-col gap-3 text-xs">
            <div class="flex items-center gap-2">
              <span class="t-dim">类型</span>
              <span class="tag tag-cyan">
                {{ taskType === 'image' ? '图片生成' : '视频生成' }}
              </span>
            </div>
            <div class="flex items-center gap-2">
              <span class="t-dim">模型</span>
              <span class="tag" :class="form.modelId ? 'tag-ok' : 'tag-warn'">
                {{ currentModel?.name || '未选择' }}
              </span>
            </div>
            <div>
              <div class="mb-1 t-dim">提示词</div>
              <p class="panel-soft line-clamp-6 break-all px-3 py-2 leading-relaxed">
                {{ form.prompt || '（尚未填写）' }}
              </p>
            </div>
          </div>
        </div>

        <div class="card card-pad">
          <div class="card-head">
            <h3><app-icon name="zap" :size="17" />使用提示</h3>
          </div>

          <ul class="flex flex-col gap-2 text-xs leading-relaxed t-sub">
            <li>· 任务提交后进入队列，进度会通过实时通道推送到「任务列表」。</li>
            <li>· 视频任务耗时较长，可先离开当前页面，完成后会有状态变化提示。</li>
            <li>· 生成结果会自动归档到关联任务下，可在任务卡片上直接预览或下载。</li>
          </ul>
        </div>
      </div>
    </div>
  </div>
</template>
