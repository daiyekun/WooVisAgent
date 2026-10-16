import { del, get, post, upload } from './http'
import type { MaterialAsset, MaterialQuery, PagedResult } from '@/types'

const BASE_URL = '/api/materials'

/** 分页查询素材 */
export function fetchMaterials(query: MaterialQuery = {}) {
  return get<PagedResult<MaterialAsset>>(BASE_URL, query)
}

/** 素材分组列表 */
export function fetchMaterialGroups() {
  return get<string[]>(`${BASE_URL}/groups`)
}

/** 上传素材 */
export function uploadMaterial(file: File, groupName?: string) {
  return upload(BASE_URL, file, groupName ? { groupName } : undefined)
}

/** 删除素材 */
export function deleteMaterial(id: string) {
  return del<void>(`${BASE_URL}/${id}`)
}

/** 批量删除素材 */
export function batchDeleteMaterials(ids: string[]) {
  return post<void>(`${BASE_URL}/batch-delete`, { ids })
}

/** 把任务产物归档到素材库 */
export function archiveTaskOutput(taskId: string, assetIds: string[], groupName?: string) {
  return post<void>(`${BASE_URL}/archive`, { taskId, assetIds, groupName })
}
