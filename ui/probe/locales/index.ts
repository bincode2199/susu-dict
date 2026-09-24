export const zh = {
  fixture: 'F00 合成界面 · 功能尚未接入', settings: '设置', main: '输入翻译', selection: '划词翻译',
  ocr: '截图翻译', voice: '语音翻译', transcribe: '视频转写', tray: '托盘菜单', error: '服务错误',
  general: '通用', hotkeys: '快捷键', engines: '翻译服务', ai: 'AI 服务', prompt: '提示语', speech: '语音服务',
  vocab: '生词收藏', network: '网络', about: '关于', source: '原文', target: '简体中文', detect: '自动检测',
  input: '在此输入文字…', translate: '翻译', unavailable: '功能开发中', loading: '正在请求…',
  ready: '完成', failed: '连接失败，请检查服务设置。', unsupported: '尚未实现', language: '界面语言',
  appearance: '外观', light: '浅色', startup: '开机启动', clipboard: '允许借用剪贴板', save: '保存',
  timeout: '请求超时（秒）', proxy: '代理模式', system: '跟随系统', disabled: '关闭', custom: '手动配置',
  account: '服务账户', credential: '写入新凭据', configured: '未配置', cancel: '取消', original: '原文',
  output: '译文', file: '尚未选择视频文件', duration: '00:00', record: '开始录音', capture: '选择截图区域',
  expand: '展开', collapse: '折叠', synthetic: '这是一段合成的测试译文，用于验证字体、换行、卡片布局与窗口内存。',
  explanation: '生产功能未注册。此界面仅用于开发期的布局与性能验证。', result: '翻译结果',
  exit: '退出', latin: 'Su-Su is a small tool for understanding words, sentences, and the world around you.'
};
export type MessageKey = keyof typeof zh;
export const en: Record<MessageKey, string> = {
  fixture: 'F00 synthetic interface · features not connected', settings: 'Settings', main: 'Text translation', selection: 'Selected text',
  ocr: 'Screenshot translation', voice: 'Voice translation', transcribe: 'Video transcription', tray: 'Tray menu', error: 'Service error',
  general: 'General', hotkeys: 'Shortcuts', engines: 'Translation services', ai: 'AI services', prompt: 'Prompts', speech: 'Speech services',
  vocab: 'Vocabulary', network: 'Network', about: 'About', source: 'Source', target: 'Simplified Chinese', detect: 'Detect language',
  input: 'Enter text here…', translate: 'Translate', unavailable: 'Feature in development', loading: 'Requesting…',
  ready: 'Complete', failed: 'Connection failed. Check the service settings.', unsupported: 'Not implemented', language: 'Interface language',
  appearance: 'Appearance', light: 'Light', startup: 'Start with Windows', clipboard: 'Allow temporary clipboard borrowing', save: 'Save',
  timeout: 'Request timeout (seconds)', proxy: 'Proxy mode', system: 'System settings', disabled: 'Off', custom: 'Manual configuration',
  account: 'Service account', credential: 'Write a new credential', configured: 'Not configured', cancel: 'Cancel', original: 'Original',
  output: 'Translation', file: 'No video file selected', duration: '00:00', record: 'Start recording', capture: 'Select screenshot region',
  expand: 'Expand', collapse: 'Collapse', synthetic: 'This is synthetic translated text used to verify typography, wrapping, card layouts, and window memory.',
  explanation: 'Production features are not registered. This interface is for development layout and performance experiments only.', result: 'Translation results',
  exit: 'Exit', latin: zh.latin
};
