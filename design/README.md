# design/ — 设计稿源文件

这里是设计画板的源文件备份，用于版本管理和灾难恢复。设计规范本身在仓库根目录的 [`../DESIGN.md`](../DESIGN.md)，取值请看那份。

在线画板：<https://claude.ai/artifact/BeXBbTPMfy1zRjJs7mkyQS>（私有，需登录）

## 文件

43 张画板 + 1 个布局文件。画板清单与尺寸见 [`../DESIGN.md`](../DESIGN.md) 第 12 节，那里也写了各张画板画的是什么。

| 分组 | 文件 |
|---|---|
| 基础 | `Main.dc.html` `Card.dc.html` `Style.dc.html` `Icon.dc.html` |
| 功能界面 | `Selection.dc.html` `Ocr.dc.html` `Voice.dc.html` `Transcribe.dc.html` `Tray.dc.html` `Error.dc.html` |
| 设置界面 | `SetGeneral` `SetGeneralB` `SetHotkeys` `SetEngines` `SetOcr` `SetAI` `SetPrompt` `SetSpeech` `SetSpeechB` `SetVocab` `SetNetwork` `SetAbout`（均为 `.dc.html`） |
| 深色主题 | `DarkStyle.dc.html`（token 表与切换要求）+ 以上除 `Style` / `Icon` 外每张画板的 `Dark*` 版，共 21 张 |
| 布局 | `canvas.json` —— 画板在画布上的坐标、标题、批注 |

对应线上画板第 17 版（2026-09-19，按 `REVISIONS.md` 同步 PLAN 第四版 D-52–D-57；此前改动见 `RECORD.md` D-51）。

**同步状态**：第四版 D-52–D-57 已按 [`REVISIONS.md`](REVISIONS.md) 完成第 17 版同步，取舍见 RECORD D-58。后续 A1 架构补充的状态/排序/窗口行为见 [`../DESIGN.md`](../DESIGN.md) 第 13 节；这些新增细节尚未重新出画板，开发按该节实现验收。本次只补实施文档，不直接修改画板存档。

`Dark*.dc.html` 是脚本从浅色画板按 `DESIGN.md` 第 2 节的 token 对照表逐色映射生成的（只有 `DarkStyle` 手写）。改浅色画板后要重新生成深色版，不要单独手改深色版。

## 这些文件不能直接在浏览器里正常渲染

它们是 Design Component 格式，依赖画板运行时：

- `<script src="./support.js">` 指向运行时，仓库里没有这个文件
- 内容包裹在 `<x-dc>` 自定义元素里
- `{{accent}}` 是模板占位符，由文件底部 `<script data-dc-script>` 的 `renderVals()` 提供

直接双击打开只能看到个大概——强调色边框会因为 `{{accent}}` 不是合法 CSS 值而消失，底部脚本会因为 `DCLogic` 未定义而报错（无害）。要看真实效果请开线上画板。

> 我之前说这些文件可以直接在浏览器打开，说错了。如果需要一份不依赖运行时、双击就能看的静态版，可以再导出。

## 恢复画板

万一线上画板丢了，用 Claude Code 的 Artifact 工具重建：

1. 以 Design 类型新建一个画布（`type_url` 指向 Design 类型），拿到新的 url
2. 把这里所有文件发布到该 url，路径统一加 `project/` 前缀，例如 `project/Main.dc.html`、`project/canvas.json`

`canvas.json` 里 `boards` 的键就是这里的文件名，坐标和批注会一起恢复。

## 修改约定

线上画板是唯一的编辑入口，改完之后把源文件同步回这里，不要只改仓库里的副本——两边会对不上。

后续新界面往同一个画板加 artboard，不要另起画布。布局约定：同一行画板之间留 80px，行与行之间留 120px 以上，行标题（`kind: "title1"`）要在本行上方留 223px 以上。

`REVISIONS.md` 是第四版同步清单，已全部落实。`../DESIGN.md` 第 12 节末尾的「尚未画的」现在是空的。
