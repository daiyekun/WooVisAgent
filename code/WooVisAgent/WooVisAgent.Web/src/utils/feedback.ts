import { createDiscreteApi } from 'naive-ui'

/**
 * 脱离组件树的消息/对话框实例
 * axios 拦截器、SignalR 回调等非 setup 上下文里需要用到
 */
const { message, dialog, notification, loadingBar } = createDiscreteApi([
  'message',
  'dialog',
  'notification',
  'loadingBar',
])

export { message, dialog, notification, loadingBar }
