export * from './file'
export * from './media'
export * from './time'
export { dialog, loadingBar, message, notification } from './feedback'

/** 简易防抖 */
export function debounce<T extends (...args: never[]) => void>(fn: T, delay = 300) {
  let timer: ReturnType<typeof setTimeout> | undefined

  return (...args: Parameters<T>) => {
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => fn(...args), delay)
  }
}

/** 节流 */
export function throttle<T extends (...args: never[]) => void>(fn: T, interval = 200) {
  let last = 0

  return (...args: Parameters<T>) => {
    const now = Date.now()
    if (now - last >= interval) {
      last = now
      fn(...args)
    }
  }
}

export function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

/** 生成短 ID，用于前端临时对象 */
export function createTempId(): string {
  return `tmp_${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`
}
