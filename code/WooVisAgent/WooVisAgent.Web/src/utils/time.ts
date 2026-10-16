import dayjs from 'dayjs'
import relativeTimePlugin from 'dayjs/plugin/relativeTime'
import 'dayjs/locale/zh-cn'

dayjs.extend(relativeTimePlugin)
dayjs.locale('zh-cn')

export type DateLike = string | number | Date | null | undefined

/** 格式化为「2026-10-16 14:37:55」 */
export function formatDateTime(value: DateLike, template = 'YYYY-MM-DD HH:mm:ss'): string {
  if (!value) return '-'
  const d = dayjs(value)
  return d.isValid() ? d.format(template) : '-'
}

/** 格式化为「2026-10-16」 */
export function formatDate(value: DateLike): string {
  return formatDateTime(value, 'YYYY-MM-DD')
}

/** 相对时间，如「3 分钟前」 */
export function formatRelativeTime(value: DateLike): string {
  if (!value) return '-'
  const d = dayjs(value)
  return d.isValid() ? d.fromNow() : '-'
}

/** 秒数格式化为 00:00 / 01:02:03 */
export function formatDurationSeconds(seconds?: number | null): string {
  if (seconds === undefined || seconds === null || Number.isNaN(seconds)) return '00:00'

  const total = Math.max(0, Math.floor(seconds))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = total % 60
  const pad = (n: number) => String(n).padStart(2, '0')

  return h > 0 ? `${pad(h)}:${pad(m)}:${pad(s)}` : `${pad(m)}:${pad(s)}`
}

/**
 * 计算任务生成耗时
 * 未结束时以「当前时间 - 开始时间」计算，返回如「12.5s」「1m 20s」
 */
export function formatElapsed(start?: DateLike, end?: DateLike): string {
  if (!start) return '-'

  const startAt = dayjs(start)
  if (!startAt.isValid()) return '-'

  const endAt = end ? dayjs(end) : dayjs()
  const ms = endAt.diff(startAt)
  if (ms <= 0) return '0s'

  const seconds = ms / 1000
  if (seconds < 60) return `${seconds.toFixed(1)}s`

  const minutes = Math.floor(seconds / 60)
  const rest = Math.round(seconds % 60)
  return `${minutes}m ${rest}s`
}

/** 毫秒耗时展示，用于模型连通性测试等场景 */
export function formatLatency(ms?: number | null): string {
  if (ms === undefined || ms === null) return '-'
  return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(2)} s`
}

export { dayjs }
