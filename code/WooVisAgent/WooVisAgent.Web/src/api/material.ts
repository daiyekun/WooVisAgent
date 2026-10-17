import { del, get, post } from './http'
import type { MaterialAsset, MaterialQuery, PagedResult } from '@/types'

const BASE_URL = '/api/materials'

/**
 * 分页查询作品（我的产品）。
 *
 * 作品的写入只有一个入口——任务列表的「选秀」按钮（POST /api/tasks/{id}/select），
 * 因此这里只有读与删，没有 upload / archive。
 */

/** 分页查询作品列表 */
export function fetchMaterials(query: MaterialQuery = {}) {
  return get<PagedResult<MaterialAsset>>(BASE_URL, query)
}

/** 作品分组列表，用于筛选页签 */
export function fetchMaterialGroups() {
  return get<string[]>(`${BASE_URL}/groups`)
}

/** 删除作品 */
export function deleteMaterial(id: string) {
  return del<void>(`${BASE_URL}/${id}`)
}

/** 批量删除作品 */
export function batchDeleteMaterials(ids: string[]) {
  return post<void>(`${BASE_URL}/batch-delete`, { ids })
}