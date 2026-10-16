<script setup lang="ts">
import { NForm, NFormItem, NInput, NInputNumber, NSelect, NSwitch } from 'naive-ui'
import { computed } from 'vue'

import type { ModelParamSpec } from '@/types'

const props = withDefaults(
  defineProps<{
    /** 参数规格，来自模型配置 */
    specs: ModelParamSpec[]
    /** 当前参数值 */
    modelValue?: Record<string, unknown>
    labelPlacement?: 'left' | 'top'
    /** 只读模式，仅展示 */
    readonly?: boolean
  }>(),
  {
    modelValue: () => ({}),
    labelPlacement: 'left',
    readonly: false,
  },
)

const emit = defineEmits<{
  (e: 'update:modelValue', value: Record<string, unknown>): void
}>()

const visibleSpecs = computed(() => props.specs ?? [])

function getValue(spec: ModelParamSpec): unknown {
  const current = props.modelValue?.[spec.key]
  return current === undefined ? spec.defaultValue : current
}

/** 统一走不可变更新，避免直接改动父组件传入的对象 */
function setValue(spec: ModelParamSpec, value: unknown): void {
  emit('update:modelValue', { ...props.modelValue, [spec.key]: value })
}

function toNumber(value: number | null): number | null {
  return value === null || Number.isNaN(value) ? null : value
}

function toStringValue(value: string | null): string {
  return value ?? ''
}
</script>

<template>
  <n-form
    v-if="visibleSpecs.length"
    :label-placement="labelPlacement"
    label-width="120"
    :show-feedback="false"
    size="small"
    :disabled="readonly"
  >
    <n-form-item v-for="spec in visibleSpecs" :key="spec.key" :label="spec.label">
      <div class="w-full">
        <!-- 数值 -->
        <n-input-number
          v-if="spec.type === 'number'"
          class="w-full"
          :value="(getValue(spec) as number | null)"
          :min="spec.min"
          :max="spec.max"
          :step="spec.step ?? 1"
          :placeholder="spec.placeholder || `请输入${spec.label}`"
          clearable
          @update:value="(value: number | null) => setValue(spec, toNumber(value))"
        />

        <!-- 开关 -->
        <n-switch
          v-else-if="spec.type === 'boolean'"
          :value="Boolean(getValue(spec))"
          @update:value="(value: boolean) => setValue(spec, value)"
        />

        <!-- 下拉 -->
        <n-select
          v-else-if="spec.type === 'select'"
          :value="(getValue(spec) as string | number | null)"
          :options="spec.options ?? []"
          :placeholder="spec.placeholder || `请选择${spec.label}`"
          clearable
          @update:value="(value: unknown) => setValue(spec, value)"
        />

        <!-- 文本（含多行） -->
        <n-input
          v-else
          :value="(getValue(spec) as string)"
          :type="spec.description ? 'textarea' : 'text'"
          :rows="3"
          :placeholder="spec.placeholder || `请输入${spec.label}`"
          @update:value="(value: string | null) => setValue(spec, toStringValue(value))"
        />

        <p v-if="spec.description" class="mt-1 text-xs t-dim">{{ spec.description }}</p>
      </div>
    </n-form-item>
  </n-form>

  <div v-else class="py-2 text-xs t-dim">该模型未定义可配置参数</div>
</template>
