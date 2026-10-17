/**
 * 媒体地址解析。
 *
 * 后端两处产物地址的历史写法并不统一：图片存 `/uploads/...`，视频早期存的是
 * `uploads/...`（少了前导斜杠）。相对地址会被浏览器按「当前页面路由」解析，
 * 在 `/chat` 下碰巧能取到、在别的路由或子路径部署下就 404——
 * 播放器表现为一片黑、点了也不动，非常难查。
 *
 * 所以前端统一过一道：有协议（http/https/data/blob）就原样用，
 * 其余一律补成以 `/` 开头的站点绝对路径。
 */
export function resolveMediaUrl(url?: string | null): string {
  const value = (url ?? '').trim()

  if (!value) return ''
  if (/^(https?:|data:|blob:)/i.test(value)) return value

  return value.startsWith('/') ? value : `/${value}`
}
