import { del, get, post, put } from './http'
import type { ModelCategory, ModelConfig, ModelTestResult, SaveModelInput } from '@/types'

const BASE_URL = '/api/models'

/** 查询模型配置列表 */
export function fetchModels(category?: ModelCategory) {
  return get<ModelConfig[]>(BASE_URL, category ? { category } : undefined)
}

/** 查询模型配置详情 */
export function fetchModelDetail(id: string) {
  return get<ModelConfig>(`${BASE_URL}/${id}`)
}

/** 新建或更新模型配置 */
export function saveModel(input: SaveModelInput) {
  return input.id
    ? put<ModelConfig>(`${BASE_URL}/${input.id}`, input)
    : post<ModelConfig>(BASE_URL, input)
}

/** 删除模型配置 */
export function deleteModel(id: string) {
  return del<void>(`${BASE_URL}/${id}`)
}

/** 启用/停用 */
export function toggleModel(id: string, enabled: boolean) {
  return put<ModelConfig>(`${BASE_URL}/${id}/enabled`, { enabled })
}

/** 设为默认模型（同分类下互斥） */
export function setDefaultModel(id: string) {
  return post<ModelConfig>(`${BASE_URL}/${id}/default`)
}

/** 连通性测试 */
export function testModel(id: string) {
  return post<ModelTestResult>(`${BASE_URL}/${id}/test`)
}

/** 未落库的新配置试连 */
export function testModelDraft(input: SaveModelInput) {
  return post<ModelTestResult>(`${BASE_URL}/test`, input)
}

/** 拉取指定供应方支持的模型清单 */
export function fetchProviderModels(provider: string) {
  return get<{ key: string; name: string }[]>(`${BASE_URL}/providers/${provider}/models`)
}
