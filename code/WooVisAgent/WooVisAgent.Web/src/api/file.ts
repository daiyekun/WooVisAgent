import { uploadMany } from './http'
import type { UploadResult } from './types'

const BASE_URL = '/api/files'

/**
 * 上传参考图（图生图的输入）。
 *
 * 后端 FileController 落盘后返回 /uploads 下的可访问地址，
 * 把这个地址放进 ImageGenerationInput.referenceImageUrls 即可，
 * Worker 会按同一路径把图读出来交给模型。
 */
export function uploadFiles(files: File[]) {
  return uploadMany(`${BASE_URL}/upload`, files)
}

/** 上传单张参考图 */
export async function uploadImage(file: File): Promise<UploadResult> {
  const results = await uploadFiles([file])
  return results[0]
}
