<script setup lang="ts">
import {
  NButton,
  NEmpty,
  NForm,
  NFormItem,
  NInput,
  NModal,
  NPopconfirm,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NSpace,
  NSpin,
  NSwitch,
} from 'naive-ui'
import { computed, onMounted, reactive, ref } from 'vue'

import AppIcon from '@/components/common/AppIcon.vue'
import EmptyState from '@/components/common/EmptyState.vue'
import ModelSettingForm from '@/components/ModelSettingForm.vue'
import { MODEL_CATEGORY_OPTIONS, MODEL_PROVIDER_OPTIONS } from '@/constants'
import { useModelStore } from '@/stores/modelStore'
import type { ModelCategory, ModelConfig, ModelParamSpec, SaveModelInput } from '@/types'
import { dialog, formatDateTime, formatLatency, message } from '@/utils'

const modelStore = useModelStore()

/** 'all' 表示不筛选 */
const categoryFilter = ref<ModelCategory | 'all'>('all')
const keyword = ref('')

const modalVisible = ref(false)
const editingId = ref<string | null>(null)
const submitting = ref(false)

const PARAM_TYPE_OPTIONS = [
  { label: '数值', value: 'number' },
  { label: '文本', value: 'string' },
  { label: '开关', value: 'boolean' },
  { label: '下拉', value: 'select' },
]

/** 各分类的图标与配色，用于列表左侧的模型标识块 */
const CATEGORY_STYLE: Record<ModelCategory, { icon: string; gradient: string }> = {
  image: { icon: 'image', gradient: 'linear-gradient(135deg,#12B5E5,#0891B2)' },
  video: { icon: 'video', gradient: 'linear-gradient(135deg,#7C5CFC,#A78BFA)' },
  llm: { icon: 'sparkles', gradient: 'linear-gradient(135deg,#F97316,#FBBF24)' },
}

function createEmptyDraft(): SaveModelInput {
  return {
    name: '',
    provider: 'OpenAI',
    category: 'image',
    modelKey: '',
    endpoint: '',
    apiKey: '',
    enabled: true,
    isDefault: false,
    params: [],
    remark: '',
  }
}

const draft = reactive<SaveModelInput>(createEmptyDraft())

const filteredModels = computed(() => {
  const kw = keyword.value.trim().toLowerCase()
  return modelStore.models.filter((item) => {
    const matchCategory = categoryFilter.value === 'all' || item.category === categoryFilter.value
    const matchKeyword =
      !kw ||
      item.name.toLowerCase().includes(kw) ||
      item.provider.toLowerCase().includes(kw) ||
      (item.modelKey ?? '').toLowerCase().includes(kw)
    return matchCategory && matchKeyword
  })
})

const modalTitle = computed(() => (editingId.value ? '编辑模型配置' : '新增模型配置'))

const draftPreviewParams = computed(() => draft.params ?? [])
const draftPreviewValues = ref<Record<string, unknown>>({})

function countOf(category: ModelCategory): number {
  return modelStore.models.filter((item) => item.category === category).length
}

function styleOf(category: ModelCategory) {
  return CATEGORY_STYLE[category] ?? CATEGORY_STYLE.llm
}

function openCreate(): void {
  editingId.value = null
  Object.assign(draft, createEmptyDraft())
  draftPreviewValues.value = {}
  modalVisible.value = true
}

function openEdit(model: ModelConfig): void {
  editingId.value = model.id
  Object.assign(draft, {
    id: model.id,
    name: model.name,
    provider: model.provider,
    category: model.category,
    modelKey: model.modelKey ?? '',
    endpoint: model.endpoint ?? '',
    // 后端不回传明文密钥，编辑时留空表示不修改
    apiKey: '',
    enabled: model.enabled,
    isDefault: model.isDefault,
    params: model.params?.map((item) => ({ ...item })) ?? [],
    remark: model.remark ?? '',
  })
  draftPreviewValues.value = {}
  modalVisible.value = true
}

function addParam(): void {
  draft.params = [
    ...(draft.params ?? []),
    { key: '', label: '', type: 'number', defaultValue: undefined },
  ]
}

function removeParam(index: number): void {
  draft.params = (draft.params ?? []).filter((_, i) => i !== index)
}

function patchParam(index: number, patch: Partial<ModelParamSpec>): void {
  draft.params = (draft.params ?? []).map((item, i) => (i === index ? { ...item, ...patch } : item))
}

/** 下拉类型的可选项用逗号分隔的文本编辑 */
function getOptionsText(spec: ModelParamSpec): string {
  return (spec.options ?? []).map((item) => String(item.value)).join(',')
}

function setOptionsText(index: number, text: string): void {
  const options = text
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)
    .map((item) => ({ label: item, value: item }))

  patchParam(index, { options })
}

/** 把标量默认值按类型解析成正确的类型 */
function setDefaultValue(index: number, raw: unknown, type: ModelParamSpec['type']): void {
  let value: string | number | boolean | undefined

  if (raw === null || raw === undefined || raw === '') {
    value = undefined
  } else if (type === 'number') {
    const parsed = Number(raw)
    value = Number.isNaN(parsed) ? undefined : parsed
  } else if (type === 'boolean') {
    value = raw === true || raw === 'true'
  } else {
    value = String(raw)
  }

  patchParam(index, { defaultValue: value })
}

async function submit(): Promise<void> {
  if (!draft.name.trim()) {
    message.warning('请填写模型名称')
    return
  }

  const invalidParam = (draft.params ?? []).find((item) => !item.key.trim() || !item.label.trim())
  if (invalidParam) {
    message.warning('参数 key 与显示名称不能为空')
    return
  }

  submitting.value = true
  try {
    // 空密钥表示沿用原值，提交前移除该字段
    const payload: SaveModelInput = { ...draft }
    if (!payload.apiKey) delete payload.apiKey

    await modelStore.saveModel(payload)
    message.success(editingId.value ? '已保存修改' : '已新增模型')
    modalVisible.value = false
  } finally {
    submitting.value = false
  }
}

function handleRemove(model: ModelConfig): void {
  dialog.error({
    title: '删除模型配置',
    content: `确认删除「${model.name}」？删除后引用该模型的任务将无法重试。`,
    positiveText: '删除',
    negativeText: '取消',
    onPositiveClick: async () => {
      await modelStore.removeModel(model.id)
      message.success('已删除')
    },
  })
}

async function handleTest(model: ModelConfig): Promise<void> {
  const result = await modelStore.testModel(model.id)
  if (result.success) {
    message.success(`连通正常，耗时 ${formatLatency(result.latency)}`)
  } else {
    message.error(result.message || '连通失败，请检查密钥与地址')
  }
}

async function handleToggle(model: ModelConfig, enabled: boolean): Promise<void> {
  await modelStore.toggleModel(model.id, enabled)
  message.success(enabled ? '已启用' : '已停用')
}

async function handleSetDefault(model: ModelConfig): Promise<void> {
  await modelStore.setDefaultModel(model.id)
  message.success(`已把「${model.name}」设为默认模型`)
}

function categoryLabel(category: ModelCategory): string {
  return MODEL_CATEGORY_OPTIONS.find((item) => item.value === category)?.label ?? category
}

onMounted(() => {
  void modelStore.loadModels()
})
</script>

<template>
  <div>
    <!-- 页面标题条 -->
    <div class="page-head">
      <h1>
        模型配置
        <small>接入你自己的模型服务，密钥仅保存在本平台后端</small>
      </h1>

      <div class="flex flex-wrap items-center gap-2">
        <n-button size="small" quaternary :loading="modelStore.loading" @click="modelStore.loadModels()">
          <template #icon><app-icon name="refresh" :size="15" /></template>
          刷新
        </n-button>
        <button class="btn btn-primary btn-sm" type="button" @click="openCreate">
          <app-icon name="plus" :size="15" />
          新增模型
        </button>
      </div>
    </div>

    <!-- 分类标签 -->
    <div class="model-tabs">
      <button type="button" :class="{ on: categoryFilter === 'all' }" @click="categoryFilter = 'all'">
        全部
        <span class="cnt">{{ modelStore.models.length }}</span>
      </button>
      <button
        v-for="item in MODEL_CATEGORY_OPTIONS"
        :key="item.value"
        type="button"
        :class="{ on: categoryFilter === item.value }"
        @click="categoryFilter = item.value"
      >
        <app-icon :name="styleOf(item.value).icon" :size="16" />
        {{ item.label }}
        <span class="cnt">{{ countOf(item.value) }}</span>
      </button>
    </div>

    <!-- 搜索 -->
    <div class="filters">
      <div class="search">
        <span class="search-icon"><app-icon name="search" :size="15" /></span>
        <input v-model="keyword" placeholder="搜索名称 / 供应方" />
      </div>
      <div class="spacer" />
      <span class="tag tag-dim">共 {{ filteredModels.length }} 个模型</span>
    </div>

    <!-- 列表 -->
    <n-spin :show="modelStore.loading">
      <div v-if="filteredModels.length" class="model-list">
        <div v-for="model in filteredModels" :key="model.id" class="model-row">
          <span class="mlogo" :style="{ background: styleOf(model.category).gradient }">
            <app-icon :name="styleOf(model.category).icon" :size="18" />
          </span>

          <div class="minfo">
            <b>
              {{ model.name }}
              <span v-if="model.isDefault" class="tag tag-ok">默认</span>
              <span v-if="!model.enabled" class="tag tag-dim">已停用</span>
            </b>
            <small>
              {{ categoryLabel(model.category) }} · {{ model.provider }}
              <template v-if="model.modelKey"> · {{ model.modelKey }}</template>
              · 更新于 {{ formatDateTime(model.updatedAt, 'MM-DD HH:mm') }}
            </small>
          </div>

          <div class="mtags">
            <span class="tag tag-dim">{{ model.params?.length ?? 0 }} 个参数</span>
          </div>

          <div class="mact">
            <n-switch
              :value="model.enabled"
              size="small"
              @update:value="(value: boolean) => handleToggle(model, value)"
            />

            <button
              class="btn btn-ghost btn-sm"
              type="button"
              :disabled="modelStore.isTesting(model.id)"
              @click="handleTest(model)"
            >
              <app-icon name="zap" :size="14" />
              {{ modelStore.isTesting(model.id) ? '测试中…' : '测试' }}
            </button>

            <button
              v-if="!model.isDefault"
              class="btn btn-ghost btn-sm"
              type="button"
              @click="handleSetDefault(model)"
            >
              设为默认
            </button>

            <button class="btn btn-ghost btn-sm" type="button" @click="openEdit(model)">
              <app-icon name="sliders" :size="14" />
              编辑
            </button>

            <n-popconfirm @positive-click="handleRemove(model)">
              <template #trigger>
                <button class="btn-icon" type="button" title="删除">
                  <app-icon name="trash" :size="15" />
                </button>
              </template>
              删除后引用该模型的任务将无法重试，确认删除？
            </n-popconfirm>
          </div>
        </div>
      </div>

      <empty-state
        v-else-if="!modelStore.loading"
        icon="sliders"
        title="还没有模型配置"
        description="先添加一个图片或视频模型，填写接口地址与密钥后即可用于生成任务"
      >
        <button class="btn btn-primary btn-sm" type="button" @click="openCreate">
          <app-icon name="plus" :size="15" />
          新增模型
        </button>
      </empty-state>

      <div v-else class="py-24" />
    </n-spin>

    <!-- 新增 / 编辑 -->
    <n-modal
      v-model:show="modalVisible"
      preset="card"
      :title="modalTitle"
      style="max-width: 820px"
      :bordered="false"
    >
      <n-form label-placement="left" label-width="100" :show-feedback="false">
        <n-form-item label="模型名称">
          <n-input v-model:value="draft.name" placeholder="如 即梦 3.0 文生视频" />
        </n-form-item>

        <n-form-item label="分类">
          <n-radio-group v-model:value="draft.category">
            <n-radio-button
              v-for="item in MODEL_CATEGORY_OPTIONS"
              :key="item.value"
              :value="item.value"
            >
              {{ item.label }}
            </n-radio-button>
          </n-radio-group>
        </n-form-item>

        <n-form-item label="供应方">
          <n-select v-model:value="draft.provider" :options="MODEL_PROVIDER_OPTIONS" filterable tag />
        </n-form-item>

        <n-form-item label="模型标识">
          <n-input v-model:value="draft.modelKey" placeholder="调用后端接口时使用的模型 key" />
        </n-form-item>

        <n-form-item label="接口地址">
          <n-input v-model:value="draft.endpoint" placeholder="https://api.example.com/v1" />
        </n-form-item>

        <n-form-item label="API Key">
          <n-input
            v-model:value="draft.apiKey"
            type="password"
            show-password-on="click"
            :placeholder="editingId ? '留空表示沿用原密钥' : '请输入访问密钥'"
          />
        </n-form-item>

        <n-form-item label="状态">
          <n-space :size="16" align="center">
            <n-space :size="6" align="center">
              <n-switch v-model:value="draft.enabled" size="small" />
              <span class="text-xs t-sub">启用</span>
            </n-space>
            <n-space :size="6" align="center">
              <n-switch v-model:value="draft.isDefault" size="small" />
              <span class="text-xs t-sub">设为该分类默认模型</span>
            </n-space>
          </n-space>
        </n-form-item>

        <n-form-item label="备注">
          <n-input v-model:value="draft.remark" type="textarea" :rows="2" placeholder="可选" />
        </n-form-item>
      </n-form>

      <!-- 参数规格编辑 -->
      <div class="mt-2 border-t pt-4">
        <div class="mb-3 flex items-center gap-2">
          <span class="text-sm font-semibold">参数规格</span>
          <span class="tag tag-cyan">用于渲染任务提交表单</span>
          <div class="flex-1" />
          <button class="btn btn-ghost btn-sm" type="button" @click="addParam">
            <app-icon name="plus" :size="14" />
            添加参数
          </button>
        </div>

        <div v-if="draftPreviewParams.length" class="flex flex-col gap-3">
          <div
            v-for="(spec, index) in draftPreviewParams"
            :key="index"
            class="panel-soft p-3"
          >
            <div class="grid grid-cols-12 gap-2">
              <n-input
                class="col-span-3"
                size="small"
                :value="spec.key"
                placeholder="key（英文）"
                @update:value="(value: string) => patchParam(index, { key: value })"
              />
              <n-input
                class="col-span-3"
                size="small"
                :value="spec.label"
                placeholder="显示名称"
                @update:value="(value: string) => patchParam(index, { label: value })"
              />
              <n-select
                class="col-span-2"
                size="small"
                :value="spec.type"
                :options="PARAM_TYPE_OPTIONS"
                @update:value="
                  (value: unknown) => patchParam(index, { type: value as ModelParamSpec['type'] })
                "
              />

              <!-- 默认值：按类型切换控件 -->
              <n-select
                v-if="spec.type === 'boolean'"
                class="col-span-2"
                size="small"
                :value="spec.defaultValue === undefined ? null : String(spec.defaultValue)"
                :options="[
                  { label: '默认开', value: 'true' },
                  { label: '默认关', value: 'false' },
                ]"
                clearable
                @update:value="(value: unknown) => setDefaultValue(index, value, 'boolean')"
              />
              <n-input
                v-else
                class="col-span-2"
                size="small"
                :value="spec.defaultValue === undefined ? '' : String(spec.defaultValue)"
                placeholder="默认值"
                @update:value="(value: string) => setDefaultValue(index, value, spec.type)"
              />

              <n-input
                v-if="spec.type === 'select'"
                class="col-span-2"
                size="small"
                :value="getOptionsText(spec)"
                placeholder="选项逗号分隔"
                @update:value="(value: string) => setOptionsText(index, value)"
              />
              <div v-else class="col-span-2" />

              <button
                class="btn-icon col-span-1"
                type="button"
                title="移除参数"
                @click="removeParam(index)"
              >
                <app-icon name="close" :size="14" />
              </button>

              <n-input
                class="col-span-12"
                size="small"
                :value="spec.description ?? ''"
                placeholder="参数说明（可选）"
                @update:value="(value: string) => patchParam(index, { description: value })"
              />
            </div>
          </div>
        </div>

        <n-empty v-else size="small" description="尚未定义参数，模型将只使用通用参数" class="py-4" />
      </div>

      <!-- 参数实时预览 -->
      <div v-if="draftPreviewParams.length" class="mt-4 border-t pt-4">
        <div class="mb-3 text-sm font-semibold">表单预览</div>
        <model-setting-form v-model="draftPreviewValues" :specs="draftPreviewParams" />
      </div>

      <template #footer>
        <div class="flex items-center gap-3">
          <div class="flex-1" />
          <button class="btn btn-ghost btn-sm" type="button" @click="modalVisible = false">
            取消
          </button>
          <n-button type="primary" :loading="submitting" @click="submit">
            {{ editingId ? '保存' : '新增' }}
          </n-button>
        </div>
      </template>
    </n-modal>
  </div>
</template>
