import { computed, ref } from 'vue'
import { defineStore } from 'pinia'

import { modelApi } from '@/api'
import type { ModelCategory, ModelConfig, ModelTestResult, SaveModelInput } from '@/types'

export const useModelStore = defineStore('model', () => {
  const models = ref<ModelConfig[]>([])
  const loading = ref(false)
  const saving = ref(false)
  /** 正在执行连通性测试的模型 ID */
  const testingIds = ref<string[]>([])

  /** 按分类分组 */
  const grouped = computed<Record<ModelCategory, ModelConfig[]>>(() => {
    const result: Record<ModelCategory, ModelConfig[]> = { image: [], video: [], llm: [] }
    models.value.forEach((item) => {
      const bucket = result[item.category] ?? (result[item.category] = [])
      bucket.push(item)
    })
    return result
  })

  /** 仅启用的模型，供新建任务时选择 */
  const enabledModels = computed(() => models.value.filter((item) => item.enabled))

  const imageModels = computed(() => enabledModels.value.filter((item) => item.category === 'image'))
  const videoModels = computed(() => enabledModels.value.filter((item) => item.category === 'video'))

  /** 各分类的默认模型 */
  const defaultModelIdMap = computed<Partial<Record<ModelCategory, string>>>(() => {
    const map: Partial<Record<ModelCategory, string>> = {}
    models.value.forEach((item) => {
      if (item.isDefault) map[item.category] = item.id
    })
    return map
  })

  function getModelById(id: string | null | undefined): ModelConfig | undefined {
    if (!id) return undefined
    return models.value.find((item) => item.id === id)
  }

  /** 取指定分类下应使用的模型：默认模型 > 第一个启用模型 */
  function resolvePreferredModel(category: ModelCategory): ModelConfig | undefined {
    const defaultId = defaultModelIdMap.value[category]
    return getModelById(defaultId) ?? models.value.find((m) => m.category === category && m.enabled)
  }

  async function loadModels(category?: ModelCategory): Promise<void> {
    loading.value = true
    try {
      models.value = await modelApi.fetchModels(category)
    } catch {
      models.value = []
    } finally {
      loading.value = false
    }
  }

  async function saveModel(input: SaveModelInput): Promise<ModelConfig | null> {
    saving.value = true
    try {
      const saved = await modelApi.saveModel(input)
      const index = models.value.findIndex((item) => item.id === saved.id)
      if (index >= 0) {
        models.value[index] = saved
      } else {
        models.value = [...models.value, saved]
      }
      return saved
    } finally {
      saving.value = false
    }
  }

  async function removeModel(id: string): Promise<void> {
    await modelApi.deleteModel(id)
    models.value = models.value.filter((item) => item.id !== id)
  }

  async function toggleModel(id: string, enabled: boolean): Promise<void> {
    const updated = await modelApi.toggleModel(id, enabled)
    const index = models.value.findIndex((item) => item.id === id)
    if (index >= 0) models.value[index] = updated
  }

  async function setDefaultModel(id: string): Promise<void> {
    await modelApi.setDefaultModel(id)
    const target = getModelById(id)
    if (!target) return

    // 同分类下默认互斥，本地同步一次避免整表刷新
    models.value = models.value.map((item) =>
      item.category === target.category ? { ...item, isDefault: item.id === id } : item,
    )
  }

  async function testModel(id: string): Promise<ModelTestResult> {
    if (!testingIds.value.includes(id)) testingIds.value.push(id)
    try {
      return await modelApi.testModel(id)
    } finally {
      testingIds.value = testingIds.value.filter((item) => item !== id)
    }
  }

  function isTesting(id: string): boolean {
    return testingIds.value.includes(id)
  }

  return {
    models,
    grouped,
    enabledModels,
    imageModels,
    videoModels,
    defaultModelIdMap,
    loading,
    saving,
    testingIds,

    getModelById,
    resolvePreferredModel,
    loadModels,
    saveModel,
    removeModel,
    toggleModel,
    setDefaultModel,
    testModel,
    isTesting,
  }
})
