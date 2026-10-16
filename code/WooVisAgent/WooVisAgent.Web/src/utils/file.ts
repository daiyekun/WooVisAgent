const FILE_SIZE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB'] as const

/** 字节数转换为可读大小，如 1536 -> 1.5 KB */
export function formatFileSize(bytes?: number | null, fractionDigits = 1): string {
  if (bytes === undefined || bytes === null || Number.isNaN(bytes)) return '-'
  if (bytes <= 0) return '0 B'

  let value = bytes
  let unitIndex = 0

  while (value >= 1024 && unitIndex < FILE_SIZE_UNITS.length - 1) {
    value /= 1024
    unitIndex += 1
  }

  const digits = unitIndex === 0 ? 0 : fractionDigits
  return `${value.toFixed(digits)} ${FILE_SIZE_UNITS[unitIndex]}`
}

/** 取扩展名（小写，不带点） */
export function getFileExtension(fileName?: string | null): string {
  if (!fileName) return ''
  const index = fileName.lastIndexOf('.')
  return index >= 0 ? fileName.slice(index + 1).toLowerCase() : ''
}

const IMAGE_EXTENSIONS = ['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg', 'avif']
const VIDEO_EXTENSIONS = ['mp4', 'webm', 'mov', 'avi', 'mkv', 'm4v']

export function isImageFile(file: File | string): boolean {
  const name = typeof file === 'string' ? file : file.name
  return IMAGE_EXTENSIONS.includes(getFileExtension(name))
}

export function isVideoFile(file: File | string): boolean {
  const name = typeof file === 'string' ? file : file.name
  return VIDEO_EXTENSIONS.includes(getFileExtension(name))
}

/** 从 URL 中解析文件名，解析不出时返回兜底值 */
export function getFileNameFromUrl(url: string, fallback = 'download'): string {
  try {
    const pathname = new URL(url, window.location.origin).pathname
    const name = decodeURIComponent(pathname.split('/').filter(Boolean).pop() ?? '')
    return name || fallback
  } catch {
    return fallback
  }
}

/** 触发浏览器下载 */
export function downloadUrl(url: string, fileName?: string): void {
  const link = document.createElement('a')
  link.href = url
  link.download = fileName || getFileNameFromUrl(url)
  link.target = '_blank'
  link.rel = 'noopener'
  document.body.appendChild(link)
  link.click()
  document.body.removeChild(link)
}

/** 复制文本到剪贴板，兼容非 HTTPS 场景 */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text)
      return true
    }

    const textarea = document.createElement('textarea')
    textarea.value = text
    textarea.style.position = 'fixed'
    textarea.style.opacity = '0'
    document.body.appendChild(textarea)
    textarea.select()
    const ok = document.execCommand('copy')
    document.body.removeChild(textarea)
    return ok
  } catch {
    return false
  }
}
