# Su-Su · 项目方案

> 上级：[产品](README.md) · 根目录：[README](../../README.md)  
> 敏感值（IP、账户、UUID、私有链接等）以 `<占位符>` 表示，实际值见本地 [SENSITIVE.md](../SENSITIVE.md)（已 gitignore，不入库）。

**项目开发的执行依据。这里只有最终方案。**

- 为什么这么定、否决过什么方案、调研与竞品实测数据 → [`RECORD.md`](RECORD.md)
- 视觉与交互规格（色彩、字阶、控件、窗口尺寸、卡片结构）→ [`DESIGN.md`](../design/DESIGN.md)，所有取值以它为准，本文件不重复
- 画板源文件 → `design/`，43 张（浅色 22 + 深色 21），线上画板是唯一编辑入口
- 实施架构、模块边界、任务/存储/更新协议 → [`ARCHITECTURE.md`](../architecture/ARCHITECTURE.md)（A1）
- 按功能逐个开发的依赖、工作单元与出口 → [`DEV-PLAN.md`](../development/DEV-PLAN.md)；验收用例 → [`TEST-PLAN.md`](../development/TEST-PLAN.md)

**状态**：第四版 + A1 实施细化（2026-09-19）。R01–R06 已按 D-52–D-57 修订，R07–R13 的设计落点与开发任务已补齐（D-59–D-65）。实施进度见 [PROGRESS](../evidence/PROGRESS.md)。第 4 章与 ARCHITECTURE 的 options/vocab 补充共同构成 API v1 候选，须通过 M1 契约探针后冻结。画板已同步线上第 17 版，源文件已导出（O-20 结清）；A1 新增行为规格见 DESIGN 第 13 节，不声称新增细节已经画入第 17 版。

---

## 1. 产品定义

### 1.1 概要

一款 Windows 11 桌面翻译工具，托盘常驻，多种取词方式进、多引擎卡片列表出。

| 项 | 值 |
|---|---|
| 平台 | Windows 11 单平台 |
| 主题 | 浅色。深色主题的画板与 token 值在设计阶段一并出（第 11 章），首版只实现浅色，token 结构预留 |
| 界面语言 | 简体中文 + 英文 |
| 翻译语言 | 简体中文 ↔ 英文 |
| 授权 | 开源，MIT（暂定，发布前确认）。`LICENSE` 文件加入仓库前不公开仓库 |
| 分发 | NSIS 安装包，per-user，首版不签名 |
| 体积 | 磁盘 15–25 MB，安装包 8–15 MB |
| 常驻 | 托盘闲置态内存 ≤ 25 MiB，界面保温态 ≤ 60 MiB（两态定义与度量口径见 2.4），闲置 CPU < 0.1% |
| 响应 | P95：热键 → 保温态内容可见 ≤ 50 ms，冷态原生等待壳可见 ≤ 150 ms、可交互 WebView 首帧 ≤ 300 ms；本地语言检测路径取词完成 → 首个请求发出 ≤ 50 ms；远端检测 RTT 单列（ARCHITECTURE 11） |

### 1.2 功能清单

每个功能可单独设快捷键。设置时检查应用内冲突；注册失败时必须提示用户换一个。

| 功能 | 入口 | 结果形态 |
|---|---|---|
| 输入翻译 | 热键 / 托盘菜单 | 主窗口 520×700，卡片下拉列表 |
| 划词翻译 | **只有热键**（仅可与剪贴板翻译共用）；选中文本后不出现任何浮标或按钮 | 悬浮窗 380 宽 |
| 剪贴板翻译 | 热键（仅可与划词共用）/ 托盘菜单 | 悬浮窗 380 宽 |
| OCR 识别翻译 | 热键，拖拽框选，松开即完成，选区上没有工具条 | 结果窗 420×620，弹出即开始识别 |
| 语音翻译 | 热键，麦克风 | 悬浮窗，录完转写后回到卡片列表 |
| 音频翻译 | 热键，系统音频回环 | 同上 |
| 视频转写 | 托盘菜单 / 热键，拖文件 | 结果窗 760×580，按时间码的双语列表 |
| 发音 | 热键，仅在有选中文本时 | 无窗口，选中文本旁的发音浮条 |

**卡片列表规则**（除视频转写外的全部功能）：一个服务一张卡片，可单独折叠；**折叠的卡片不发请求**，展开时才发；默认展开数量可配，默认 2。

**服务未配置时的规则**：一个功能依赖的服务类别（OCR、ASR、`dictionary`、生词本 API 同步）里没有任何已启用且凭据齐全的服务时，该功能不可用：托盘菜单项置灰，热键按下无动作，卡片列表里不出现对应形态，设置页对应热键行标注「需先配置 <类别> 服务」。不做首次启动引导，不做空态引导界面。翻译（MyMemory 免配置）与发音（Windows 内置 TTS）始终可用。

ASR 还须满足所选模型的输出能力：语音／音频翻译需要 `text`，视频转写需要 `segments`（4.7）。只有纯文本 ASR 时，视频入口置灰并在对应热键行标注「需配置支持时间码的转写服务」。切换普通录音的服务不会隐式切换视频转写服务。

### 1.3 服务清单

| 类别 | 首版内置 |
|---|---|
| 翻译引擎 | MyMemory、腾讯翻译君、Google、Microsoft、DeepL、Amazon、有道 |
| AI 平台 | OpenAI、GLM、Gemini、Claude、Ollama |
| OCR | 腾讯 OCR、Simple LaTeX |
| 语音合成 | Windows 内置（SAPI）、Microsoft、Google、腾讯 |
| 语音识别 | OpenAI（文本与时间码）、Gemini（首版仅文本）；复用 AI 平台凭据，模型独立配置，批量转写（4.7） |
| 生词收藏 | AnkiConnect、欧路词典（API 同步）。文件导出（欧路 `.txt`、Anki `.apkg`、CSV）是宿主功能，不是服务（6.6） |
| 语言检测 | Windows 内置（ELS，默认），可换成任何声明 `detect` 能力的服务 |

**Provider 只有两种实现**（2.3）：**插件**（第 4 章，内置与第三方走同一条路）和**宿主原生**。原生首版只有 Windows 内置 TTS（SAPI）与语言检测（ELS）。其余 **21 个内置插件**逐项见 DEV-PLAN 第 5 节；多能力不重复算包。

**无需云端 key**：MyMemory、Ollama、Windows 内置 TTS/语言检测，以及本地 AnkiConnect（按其配置可选 API key，仍须批准本地 origin）。其他云服务由用户配置凭据。

几条与实现相关的约定：

- **MyMemory**：`GET https://api.mymemory.translated.net/get`，参数 `q=<文本>`、`langpair=en|zh-CN`；`q` 每次最多 **500 个 UTF-8 字节**（URL 编码前）。所有输入路径按 4.7 切分，不能将视频的多条字幕直接拼成一次请求。匿名 5 000 字符／天**按 IP 计**，在 `de` 参数填邮箱涨到 50 000 字符／天。内置且默认启用，排引擎列表首位。设置页分别显示单次上限与日额度；本机计数不能代表共享 IP 剩余额度。超额时走 `quota`，引导配置正式引擎。
- **腾讯翻译君**与**腾讯 OCR** 共用同一套腾讯云 SecretId / SecretKey，用户配一次拿两个服务；签名由宿主的 `tencent-tc3` 签名器完成（4.5），插件不接触密钥。
- **GLM** 候选默认模型 `GLM-4-Flash`；开发与发布时以官方可用模型和真实探针更新，不承诺永久免费或无限额度。
- **Ollama** 默认地址 `http://127.0.0.1:11434`，地址是配置项，域名白名单通过 `$config` 引用放行（4.6）；连不上时提示用户先装 Ollama。
- **有道**是词典形态（音标／词性／释义）的唯一来源。它的请求签名是 `sha256(appKey + input + salt + curtime + appSecret)`，密钥拼在被哈希的串里，走宿主签名器的 `digest` 方案（4.5）。

### 1.4 托盘与窗口行为

- **最小化后不在任务栏展示**，收进托盘并继续任务；关闭窗口取消该窗口当前任务并隐藏，默认不退出应用。通用设置选择“关闭时退出”则执行全局退出。关闭不等于最小化，任务/录音/视频保留规则见 ARCHITECTURE 5.2。
- **托盘图标**：左键无操作；双击打开设置界面；右键弹出菜单。
- **单实例**：重复启动只唤起已有实例。
- **窗口位置**：见 1.4.1。所有窗口一律不跟随鼠标弹出。
- **托盘右键菜单**（236px，规格见 `DESIGN.md` 第 9 节，画板 `Tray.dc.html`）：

  ```
  输入翻译        Alt+A
  剪贴板翻译      Alt+D      ← 划词仅热键入口（D-51），共享热键先取词再读剪贴板
  OCR 识别翻译    Alt+S
  语音翻译        Alt+V
  音频翻译        Alt+B
  视频转写
  ───────────────
  设置            双击图标
  检查更新
  退出
  ```

#### 1.4.1 窗口位置

**没有任何窗口贴着鼠标弹出。** 规则只有两条：

| 窗口 | 位置 |
|---|---|
| 设置窗口 | **每次打开都居中**于当前屏幕，不记忆位置、不记忆大小 |
| 其余全部窗口（主窗口、划词／剪贴板悬浮窗、语音／音频悬浮窗、OCR 结果窗、视频转写窗） | 关闭时记下位置，下次在**上次关闭的位置**打开；**拿不到上次位置时居中**于当前屏幕 |

- **「当前屏幕」** = 鼠标所在的显示器；取它的工作区（`MonitorFromPoint` + `GetMonitorInfo` 的 `rcWork`，已排除任务栏），按工作区居中。
- **每类窗口各记各的坐标**，互不影响；划词与剪贴板共用同一个悬浮窗壳，因此共用一份坐标。
- **坐标存 SQLite 的 `window_state` 表**（5.3），不进 `settings.yaml`——它是运行时状态不是用户配置，每次关窗都重写 YAML 并重新生成注释既费事又会打断用户手改。
- **「拿不到上次位置」就居中**：首次使用或标题栏中点已不落在任何工作区；即使位置有效也将窗口整体约束在工作区内，小屏缩小并内部滚动，规则见 ARCHITECTURE 9。
- 记位置，**不记大小/最大化**：尺寸以 96-DPI DIP 计；主窗/结果窗/设置窗按设计默认大小打开，可最大化恢复，不提供任意拖边改大小；悬浮高度由内容撑开但不超工作区。
- 这条规则不适用于两个非窗口浮层：**发音浮条**贴着选中文本（6.5），**错误悬浮条**贴着鼠标（`DESIGN.md` 第 9 节）——它们必须指向触发点，没有"上次位置"可言。

### 1.5 首版不做

中英以外的翻译语言、翻译历史、离线 OCR、本地 ASR、流式语音转写、便携版、深色主题的实现（设计在设计阶段完成）、首次启动引导与空态引导、划词选中后的触发浮标、截图选区上的工具条、窗口大小记忆、免费的词典来源、应用内插件商店与索引浏览、第三方功能插件、Pot / Bob 插件兼容、每插件独立进程的严格隔离、多轮对话、术语表、遥测与崩溃上报。

---

## 2. 架构

### 2.1 进程与 WebView 拓扑

```
susu.exe                          裸 Win32 消息循环（NativeAOT，无托管运行时依赖）
  ├─ 托盘 · 全局热键 · 系统能力 · HTTP 栈 · 签名器 · 存储
  ├─ susu.exe --selection-host   按需短暂启动；隔离 UIA/IA2/剪贴板物化阻塞，超时回收
  ├─ 插件宿主子进程       susu.exe --plugin-host，同一个二进制的第二种模式，常驻
  │    AppContainer 进程：禁止直接联网、程序与插件目录只读、容器资源受限
  │    └─ 嵌入式 JS 引擎（基线 QuickJS-NG；对照 Jint，见 2.4）
  │         └─ 每个插件一个独立运行时（独立堆、独立内存上限、可单独中断与销毁）
  │    与主进程之间：命名管道，长度前缀的 JSON 消息（宿主 API 调用、结果、流式块、取消）
  └─ WebView2 环境（单个 CoreWebView2Environment，按需创建）
       └─ 界面 WebView         每个窗口一个；关窗后隐藏保温，闲置后整体释放
            └─ design/ 下的画板 HTML
```

1. **插件与 WebView2 彻底解耦**。WebView2 只承载界面；插件运行时里没有任何浏览器能力。
2. **插件宿主子进程常驻**，应用启动时拉起，不等首次请求。AppContainer 不授予 Internet／局域网客户端或服务端网络能力，不添加 loopback 豁免；程序与插件目录只读，不向用户配置、凭据、数据库和媒体缓存授予访问权。容器自身的数据目录与虚拟注册表可能可写，不承诺整个文件系统零写入；具体资源边界见 2.1.1。子进程挂在主进程的作业对象下，主进程退出时随之退出。操作系统限制直接访问，宿主代理的授权由 4.5.4 强制执行。
3. **插件的隔离层次**：
   - **进程**：全部插件共用这一个子进程，与主进程之间是进程隔离。引擎原生漏洞发生后仍受 AppContainer 限制，但可能访问该子进程内其他运行时及 IPC；不承诺此时仍有插件间隔离，也不承诺远端不会回显密钥。
   - **运行时**：每个插件一个独立引擎运行时（QuickJS 的 `JSRuntime`），独立堆与内存上限，正常 JS 不能互访。死循环由引擎中断处理器打断并重建该运行时，其未完成调用报错；其他运行时保留。该隔离不抵抗引擎内的任意原生代码执行。
   - **崩溃恢复**：子进程异常退出时主进程收到通知，重新拉起子进程、重载已启用插件，进行中的任务逐个报错到卡片，不静默丢失。
4. **界面 WebView 的保温策略**：窗口关闭时 WebView 隐藏而不销毁，页面保持已加载状态；全部窗口关闭后闲置满保温时长（设置项，默认 10 分钟）即释放所有 WebView 与 WebView2 环境，进程组随之消失。保温期内热键到窗口可见不需要冷建；闲置态只剩主进程与插件宿主子进程。
5. 首版不做每插件一个进程（1.5）。

#### 2.1.1 容器边界与 M0 验收

- **允许资源**：EXE、必要原生库、当前安装的插件文件只读；连接宿主专用 IPC；Windows 为该容器提供的私有数据目录与虚拟注册表。JS 不提供直接文件／注册表接口，持久化业务数据仍只走 `$store`。容器私有目录不存凭据，重建 profile 前可清理；卸载时删除 profile 和其数据。
- **禁止资源**：应用配置／凭据／SQLite／截图音频缓存及用户私人文件的直接访问，程序与插件目录写入，直接 Internet／LAN／loopback 连接。能否满足该资源边界必须通过访问探针验证，不能只凭“不授予 capability”推断。
- **启动**：为当前用户创建专用 profile；仅给其 SID 授予所需文件和管道访问权，不授予 Everyone／ALL APPLICATION PACKAGES。启动失败即禁用插件服务并提示，不退化成无沙箱子进程。跨用户或权限级别的实例不能复用此 IPC。
- **IPC**：使用 AppContainer 可访问的本地命名管道，限制 DACL，拒绝远程客户端；父进程核对子进程 PID、令牌和本次启动身份，子进程核对服务器 PID。除必需句柄外不继承句柄，客户端不得创建同名服务实例。调用授权规则见 4.5.4。
- **作业对象**：挂入后才开始执行插件，启用父退出即杀、活动进程数上限 1，禁止插件另起进程；内存上限按启用运行时预算加宿主开销设定，不能无限随 manifest 增长，具体基线由 M0 实测确定。
- **验收**：检查 profile 生命周期与卸载清理、允许的 DLL/插件读取、私有容器资源写入、禁止目录读写、三类网络访问、非目标客户端／服务器接入、句柄泄露、子进程创建和父进程异常退出。再通过恶意插件测试越权文件句柄、凭据绑定与宿主代理请求；直接网络被拒不等于代理安全已验收。

首版威胁模型包含不可信 JS、服务端数据和本地非授权 IPC 客户端；不防御已控制当前用户或管理员账户的恶意程序。引擎原生漏洞时整个插件子进程视为同一失陷域，宿主仍执行启动前固化的授权与任务范围校验，但不能可信地区分其中不同插件。

### 2.2 宿主模块划分

```
Susu.Host            三种启动模式的组合根、退出协调；不直接包含 Win32 实现
Susu.Contracts       IPC/UI DTO、版本、错误协议；生成 TypeScript 类型
Susu.Domain          服务身份、任务状态、调度策略、分片/字幕映射等纯逻辑
Susu.Abstractions    IHotkeyService / IClipboardMonitor / ITextSelection /
                     IScreenCapture / IAudioCapture / ITts / IMediaDecoder /
                     ILanguageDetector / ISecretStore
Susu.Windows         全部 Win32/COM/必要 WinRT 实现，含窗口、消息泵、托盘、WebView2、取词助手、DPAPI
Susu.Plugins         插件包校验与安装、manifest 解析、插件宿主子进程的拉起与监护、IPC 主进程端、宿主 API 的宿主侧实现
Susu.Runtime         插件宿主子进程本体（--plugin-host 模式）：引擎绑定、每插件运行时管理、Web API 补齐、IPC 子进程端、宿主 API 的插件侧桥
Susu.Net             HttpClient 池、代理策略、流式与取消、WebSocket 代管、请求签名器、显式凭据注入
Susu.Storage         settings.yaml、SQLite、DPAPI 凭据、导出导入
Susu.Jobs            Translate/Ocr/Tts/Asr/Video/Vocab 用例、串行状态与调度
Susu.Ui              与界面 WebView 的 JSON 协议、窗口生命周期与保温策略
```

三条硬性约束：

1. 系统能力全部走 `Susu.Abstractions` 的接口，平台实现单独一个项目，宿主其余部分只依赖接口。
2. **插件契约里不许泄露任何 Windows 概念**：路径分隔符、`Ctrl` 还是 `Cmd`、文本编码假设，一律不出现。
3. 设计 token 全部走 CSS 变量，不硬编码。

**实施技术基线**：.NET 10 LTS / Windows 11 x64 / NativeAOT；前端 Vue 3 + TypeScript + Vite，模板构建期编译，产品不带 Node。依赖方向/目录见 ARCHITECTURE 1–2。COM 的 IUnknown 接口优先源生成，IA2/IDispatch 等不支持部分用显式 vtable；SAPI 用 ISpVoice，不用动态 COM。ELS 是原生 Win32 API。M0 验证实际用到的 AOT 互操作、YAML/SQLite/签名库与引擎，不将所有系统能力误归为 WinRT。

**引擎绑定**：QuickJS-NG 是 C 库，经 P/Invoke 与 `UnmanagedCallersOnly` 回调接入，自写一层薄绑定，不依赖现有 NuGet 绑定包；Jint 是纯 C#，直接编入 AOT 镜像，其 AOT 兼容性在 M0 核。两者都要求：模块加载器可替换、每运行时内存上限、执行中断处理器。

### 2.3 服务骨架

翻译、OCR、TTS、ASR、生词本、语言检测六类共用同一套骨架：抽象 `XxxJob` + 统一的 `Provider` 接口。`Provider` 有两种实现，上层只见接口：

| 实现 | 首版成员 | 说明 |
|---|---|---|
| 插件 Provider | 21 个内置插件 + 第三方插件 | 第 4 章的契约，跑在插件宿主子进程里 |
| 宿主原生 Provider | Windows 内置 TTS（SAPI）、Windows 内置语言检测（ELS） | 需要本地系统能力，在 `Susu.Windows` 里实现 |

两种实现在服务列表、设置页、错误分类（4.4）与用量统计上完全同形，落在 `DESIGN.md` 第 10 节的统一服务列表上：拖拽把手 · 名称 · 状态说明 · 开关 · 展开箭头。宿主原生 Provider 不是「内置插件的特例分支」，它是接口的另一条实现路径；**不允许再加第三种**。

### 2.4 常驻开销预算

**度量口径**：应用整树全部进程（含任何残留助手及全部 WebView2 渲染进程）的 **private working set 之和**，统一 MiB（旧版 MB 内存目标按此口径细化）。保温第 5 分钟、默认第 10 分钟释放后再静置 5 分钟分别采样；延迟至少 30 次报告 P50/P95/max，以 P95 判目标。默认与全 21 插件两种负载、全窗口场景都测，详见 ARCHITECTURE 11/TEST-PLAN PER。两态为：

- **闲置态**：全部窗口已关闭且过了保温期，WebView2 环境已释放。
- **保温态**：全部窗口已关闭但仍在保温期内。

| 组成 | 闲置态 | 保温态 |
|---|---|---|
| Win32 宿主（消息泵 + 热键 + 托盘） | ~8–12 MiB | ~8–12 MiB |
| 插件宿主子进程（NativeAOT 基线 + 引擎 + 全部启用插件的运行时） | ~5–10 MiB | ~5–10 MiB |
| WebView2 进程组固定成本（浏览器 + GPU + 网络工具进程） | 0 | 待 M0 实测，预算 ~25–35 MiB |
| 界面 WebView 渲染进程（全部保温窗口的实际进程总数 n） | 0 | 估算 n × 8–15 MiB，按整树实测 |
| **合计目标（估算各项不保证能同时满足）** | **≤ 25 MiB** | **≤ 60 MiB** |
| 闲置 CPU | < 0.1% | < 0.1% |

四条实现要求：

1. **剪贴板必须用 `AddClipboardFormatListener` 事件**，不允许轮询；无事件时消息泵等待，实际闲置 CPU 以整树采样为准，不承诺恒为 0。
2. 保温时长是设置项，默认 10 分钟；到期一次性释放全部 WebView 与 WebView2 环境，不做逐个窗口的半释放。
3. 插件运行时常驻，不做按需创建与空闲释放（4.9）。
4. 插件宿主子进程随应用启动拉起，不等首次请求。

M0 原型必须按上述口径实测两态内存、CPU 与 1.1 的延迟。**引擎选型也在 M0 定**：基线 QuickJS-NG；Jint 作为对照，若 AOT 兼容且各项延迟不劣化，可换 Jint。**退路**：若引擎路线在 M0 过不了延迟指标（子进程拉起、IPC 往返、插件加载），改用 WebView2 承载插件（常驻插件宿主 WebView + 沙箱 iframe + module Worker，方案要点见 `RECORD.md` 3.5 与 3.6），那条路的闲置态预算是 ≤ 60 MiB。若两条路都过不了各自的预算，插件运行时路线重议。

---

## 3. 系统能力

全部由宿主实现，一等公民，零桥接。

### 3.1 划词取词

三级回落。

**第一级 · UI Automation**
`IUIAutomation::GetFocusedElement()` → 向上找支持 `TextPattern` 的祖先 → `GetSelection()` → `TextPatternRange::GetText()`。不碰剪贴板。覆盖 Win32 edit/richedit、WPF / WinUI / UWP、Office、Windows Terminal、Qt 5.12+、新版 Chromium 系（Chrome、Edge、近两年内核的 Electron）。

> Chromium 懒启用无障碍树，对某个进程的首次查询会触发建树。**验收线：首次查询 > 300 ms 的窗口类直接跳第三级。**

**第二级 · IAccessible2**
对焦点窗口 `AccessibleObjectFromWindow` 取 `IAccessible`，经 `IServiceProvider` 请求 IAccessible2 的 `IAccessibleText`，读 `selection`。覆盖旧 Chromium 内核的 Electron 应用（只实现 IA2，不实现 UIA `TextPattern`），以及未启用原生 UIA 的 Firefox。同样不碰剪贴板。

> Firefox 近两年加入了原生 UIA 实现。**M0 要核**当前发行版是否默认启用且 `TextPattern.GetSelection()` 可用（第 12 章移交开发测试计划的实测项）：若可用，第二级只剩老 Electron，实现可推迟到 M2 视取词实测数据决定；若不可用，第二级按 M0 计划实现。IA2 的接口不在 Win32 元数据里，需要手写 `GeneratedComInterface` 声明。

**第三级 · 尽力恢复的模拟复制（设置项「允许借用剪贴板取词」，默认关）**

- **关闭时**（默认）：前两级拿不到就直接失败并提示。
- **打开时**：执行下面流程，每次回落只记录结果类别，不记录剪贴板正文。

1. 记录前台窗口、目标进程、输入活动和 `GetClipboardSequenceNumber`。先把支持的格式物化到宿主自有内存；`OleGetClipboard` 只用来获取数据对象，不能把 `IDataObject` 引用当作快照。首版恢复集合为 Unicode/ANSI/OEM 文本、HTML Format、Rich Text Format、DIB/DIBV5 位图和 CF_HDROP 文件路径列表，以及已有的历史排除标记；合计上限 16 MiB、快照等待上限 200 ms。对可合成格式只保存可无损重建的源格式；其他私有格式、虚拟文件、无法物化的延迟渲染数据、超限或超时均**在发送 Ctrl+C 前取消借用**。快照读取不能阻塞消息泵；超时即停止本次流程，晚到结果丢弃。
2. 快照期间序列号或焦点改变即取消。确认触发热键的修饰键已释放，再向原目标 `SendInput` Ctrl+C，等待最多 300 ms；只接受焦点仍匹配、clipboard owner 属于原目标进程、序列号已变化且含非空文本的候选结果。无法确认来源、再次键鼠输入、多个候选更新或目标退出均中止，不读作选区。
3. **不预先清空剪贴板或写排除标记**：外部程序的复制会替换它们，无法阻止历史／云同步／第三方剪贴板管理器获取本次内容。`WM_CLIPBOARDUPDATE` 只是变化信号，必须与本次状态和序列号一起判定。
4. 成功读取候选后，重新锁定剪贴板并核对序列号、owner 与输入活动；只有仍为本次候选时才还原快照。发生任何新的复制或归属不明时保留最新内容，绝不强行恢复旧数据。超时且没有确认的候选时不写回。恢复采用自有数据，对自身产生的更新做序列号标记并排除。

**用户可见边界**：设置说明固定为「可能进入剪贴板历史、同步到云端或被其他应用读取；会尽力恢复原内容，检测到新复制时保留新内容」。恢复失败另提示「未能恢复剪贴板，当前内容已保留」。序列号和 owner 只能降低竞争风险，不能证明所有相同进程内的复制都来自本应用；不承诺全格式无损或全场景零污染。恢复原数据时保留其原有历史标记，不擅自修改原数据的同步语义。

**硬边界**：UIPI——未提权进程读不到提权窗口的内容，开关打开也无解。

**失败提示分两种，文案不能混**：

| 情况 | 提示 |
|---|---|
| 无损取词失败，开关关着 | 当前程序不支持无损取词 —— 可在设置中允许借用剪贴板取词 |
| 无损取词失败，开关开着但仍拿不到（提权窗口） | 当前程序不支持取词 |

### 3.2 自动语言检测

宿主自带的 ELS 实现是默认检测服务，可被任何声明 `detect` 能力的插件取代。

| 项 | 值 |
|---|---|
| 入口 | `MappingRecognizeText`，`elscore.dll`（`elscore.h` / `elssrvc.h`） |
| 服务 GUID | `ELS_GUID_LANGUAGE_DETECTION` = `{CF7E00B1-909B-4D95-A8F4-611F7C377702}` |
| 可用性 | Windows 7 起系统自带 |
| 输入 | UTF-16（NFC 规范化） |
| 输出 | 双 null 结尾的语言名列表，按相关度排序；多数用中性名（`en`、`ja`），`zh-Hans` / `zh-Hant` / `sr-Cyrl` / `sr-Latn` 用全名 |
| 成本 | 0 体积、0 费用、纯本地、离线可用 |

检测链路：

1. **Unicode 脚本快判**（宿主，永远最先）：CJK 统一表意文字 vs 拉丁字母。首版只有中英两种语言，这一步即可定案，零延迟、零调用。
2. **当前检测服务**：默认 ELS。处理混排文本（中英夹杂、含大量符号或数字）。
3. **回落**：检测返回空或置信不足时，回落到设置里的默认源语言。

> **实现要求**：ELS 的封装与 `detect` 能力的协议必须返回**按相关度排序的候选列表**，不是单个语言码。

### 3.3 其余能力

| 能力 | Windows API | 用在 |
|---|---|---|
| 全局热键 | `RegisterHotKey` | 所有功能 |
| 剪贴板变更监听 | `AddClipboardFormatListener`（事件，不轮询） | 剪贴板翻译 |
| 全屏截图选区 | GDI / DXGI | OCR |
| 麦克风采集 | WASAPI | 语音翻译 |
| 系统音频回环 | WASAPI loopback | 音频翻译 |
| 系统内置 TTS | SAPI / `Windows.Media.SpeechSynthesis` | 发音 |
| 音视频解码 | Media Foundation `IMFSourceReader` | 视频转写取音频 |
| 托盘 / 代理 / 自动更新 | `Shell_NotifyIcon` 等 | 全局 |
| 高 DPI 下的 1px 发丝线 | `WM_NCCALCSIZE` 自绘无边框窗口 | `DESIGN.md` 整套视觉的基础 |

Media Foundation 覆盖 MP4 / MKV / MOV / AVI / WebM / MP3 / AAC / FLAC / WAV。**不打包 ffmpeg。** MKV / WebM 里 Opus、Vorbis 音轨的解码支持在不同 Win11 版本（含 N 版）不一致，M4 用样本集实测（第 12 章移交开发测试计划的实测项）。解不开时的提示要指向出口：Vorbis / Theora / OGG 的解码器在微软商店里免费的「Web Media Extensions」包中，提示文案给出该包名与商店链接，装完重试；N 版缺的是「Media Feature Pack」，同样引导安装。**不自带任何解码器。**

---

## 4. 插件层

### 4.1 范围与原则

插件只有 JS 一档，只有一种包格式（`.susuext`），只有一条加载路径。**内置插件与第三方插件走完全相同的加载、配置渲染、错误处理与生命周期**，宿主里没有「内置插件」的特例分支，也没有任何兼容层。宿主原生 Provider（2.3）不经过本章，它是 `Provider` 接口的另一条实现。

第一版只开放**服务插件**（翻译引擎 / OCR / AI 平台 / 语音合成 / ASR / 生词本 / 语言检测），不开放**功能插件**（输入、划词、剪贴板、OCR、语音、视频转写六个功能由宿主内置，用同一套 manifest 描述、可开关、预留 contribution point）。

四条原则，后面每一节都从这里推出来：

1. **插件是纯函数库**：接收结构化输入，返回结构化输出，没有 UI，没有全局状态，不知道自己跑在哪个平台。
2. **不向插件提供凭据读取接口**：插件描述请求，宿主在已授权位置填入凭据并签名后发送；`ctx.config`、KV 和正常 IPC 不下发明文凭据。凭据输入页、可信远端及引擎失陷的边界见 4.5.4 / 5.4，不承诺可防止恶意远端以任意编码回显密钥。
3. **字节永不进插件**：截图、音频、TTS 结果都由宿主持有，插件只经手句柄。
4. **网络只经宿主**：插件子进程的直接联网受操作系统限制（2.1）；宿主再按用户授予的 origin、凭据绑定和调用范围执行代理授权，manifest 声明本身不构成凭据访问授权。

### 4.2 契约要求

第一版就必须有。

1. **流式**（AI 逐段输出）与**取消**：每个能力函数都收到 `AbortSignal`，宿主在卡片折叠、窗口关闭、超时时触发；插件忽略它也没用，宿主同时中止对应的网络请求，超时后通过引擎中断处理器打断该插件的执行并销毁重建其运行时；子进程整体无响应时重启子进程。
2. **WebSocket**：`$ws` 的契约形状第一版定下来。首版没有调用方（ASR 是批量，4.7），但不能把 `$http` 设计成唯一出口。
3. **配置项按 JSON Schema 语义声明**（写在 `manifest.yaml` 里，YAML 语法），设置界面由宿主自动渲染，插件零 UI 代码。宿主扩展见 4.6。
4. **热键由宿主统一注册和分配**，插件只声明「我要一个热键」和默认建议值（首版服务插件用不到，字段预留）。
5. **权限按 manifest 在安装／配置时授予**：首版 `permissions` 只有网络项 `hosts`（精确 origin，可引用配置字段，见 4.6）。范围之外一律拒绝，没有运行时授权弹窗；凭据额外按 4.5.4 绑定。剪贴板之类的权限位等功能插件开放时再加。
6. **大二进制永不进 JS**：输入用文件句柄，宿主负责原始 body、multipart 和 JSON/Base64 字段编码；输出可保存原始响应或提取 JSON/Base64 字段为句柄（4.5.1），编码字符串也不得作为普通结果传回插件。
7. **错误必须分类**（4.4）：宿主按分类决定文案与是否重试，插件不写用户可见的错误文案。
8. **版本化**：manifest 声明 `apiVersion`，宿主发布对应版本的 `susu-plugin.d.ts`；宿主对不支持的 `apiVersion` 拒绝加载并提示。

### 4.3 运行时

- **模块形式**：`main.js` 是 ES Module，`export default` 一个对象，键是能力名（4.7），值是 async 函数。引擎的模块加载器由宿主实现：`import` 只允许指向包内相对路径（`./lib/...`），解析根是该插件的目录，其余一律拒绝。
- **全局环境**：引擎本身没有任何 I/O 原语。`fetch`、`XMLHttpRequest`、`WebSocket`、文件、进程这些能力在插件运行时里不存在。宿主向每个运行时注入的标准 Web API 只有下面这份清单，随 `susu-plugin.d.ts` 发布，清单外的 Web API 不承诺、插件不能依赖：
  - `setTimeout` / `clearTimeout` / `queueMicrotask`
  - `AbortController` / `AbortSignal`
  - `TextEncoder` / `TextDecoder`
  - `URL` / `URLSearchParams`
  - `structuredClone`
  - `crypto.getRandomValues` / `crypto.randomUUID` / `crypto.subtle.digest`（内容哈希、去重；签名不在插件里做）
  - `console`（映射到 `$log`）

  插件可见的 `eval` 与 `new Function` 禁用；保留宿主受控源码模块编译能力，不接收第三方预编译 bytecode。具体裁剪方式在 M0 验证，不能把模块解析器一同移除。
- **宿主 API 注入**：4.5 的 `$http`、`$ws`、`$file`、`$store`、`$log`、`$i18n` 由子进程经 IPC 桥接到主进程，插件侧是 Promise 包装；流式结果（`$http.stream` 的文本块、`$ws` 的消息）以消息序列传回，随 `ctx.signal` 中止。
- **资源上限**：每插件运行时内存上限默认 64 MB（manifest `limits.memory` 可调，上限 256 MB）；超限时该次调用以 `bad_response` 失败并重建该运行时。
- **超时**：宿主按能力函数设默认超时（翻译 30 s、OCR 60 s、ASR 300 s、TTS 30 s），manifest 可在 `timeouts` 里按能力覆盖，上限 600 s。
- **不提供**：网络、文件系统、子进程、剪贴板、DOM，以及上面清单以外的任何 Web API 与系统能力。

### 4.4 模块接口

`main.js` 的形状（TypeScript 表意，实际发布 `susu-plugin.d.ts`）：

```ts
export default {
  translate?(req: TranslateRequest, ctx: Context): AsyncIterable<TranslateChunk> | Promise<TranslateResult>;
  translateBatch?(req: { items: Array<{ id: string; text: string }> }, ctx: Context): Promise<{ items: Array<{ id: string; text: string }> }>;
  dictionary?(req: DictionaryRequest, ctx: Context): Promise<DictionaryResult>;
  detect?(req: { text: string }, ctx: Context): Promise<Array<{ lang: string; confidence: number }>>;
  ocr?(req: { image: FileHandle; lang?: string }, ctx: Context): Promise<OcrResult>;
  tts?(req: { text: string; lang: string; voice?: string }, ctx: Context): Promise<{ audio: FileHandle }>;
  asr?(req: { audio: FileHandle; model: string; output: "text" | "segments"; lang?: string }, ctx: Context): Promise<AsrResult>;
  vocab?(req: VocabRequest, ctx: Context): Promise<VocabResult>;
  validate?(ctx: Context): Promise<void>;          // 设置页「验证」按钮；抛 PluginError 即失败
  voices?(ctx: Context): Promise<Voice[]>;          // tts 可选：列出可用音色
  options?(req: OptionsRequest, ctx: Context): Promise<OptionsResult>; // 动态模型/牌组等配置选项
}

interface Context {
  config: Readonly<Record<string, unknown>>;   // manifest.config 声明的非密钥配置，已按 schema 校验
  signal: AbortSignal;
  lang: { from: string; to: string };           // 规范码，插件用 manifest.languages 自行映射
  $http, $ws, $file, $store, $log, $i18n;       // 4.5
}
```

`translateBatch` 是 `translate` 能力的可选批量方法，不是新能力；不支持时由宿主逐条调用 `translate`，不把条目拼成字符串。`TranslateRequest` 为 `{ text: string }`，语言与提示等非密钥配置由 `ctx` 提供。请求限制和字幕映射见 4.7。以下字段在 M1 探针通过后冻结。

`OptionsRequest/Result` 的依赖 revision/分页/上限见 ARCHITECTURE 3.1；`VocabRequest/Result` 的 operationId、upsert/lookup、远端确认状态见 ARCHITECTURE 8.3，API v1 一并冻结。它们不增加新的生产 Provider 类型。

**结果形状**：

| 类型 | 字段 |
|---|---|
| `TranslateChunk` | `{ text: string; done?: boolean }`，流式时逐段追加；`done` 后不再有 chunk |
| `TranslateResult` | `{ text: string; detectedFrom?: string; raw?: unknown }` |
| `DictionaryResult` | `{ word; phonetics: Array<{ accent: "us"\|"uk"; ipa: string; audioUrl?: string }>; parts: Array<{ pos: string; means: string[] }>; forms?: Array<{ name; value }>; examples?: Array<{ src; dst }> }`；`audioUrl` 由宿主取，插件不下载 |
| `OcrResult` | `{ blocks: Array<{ text: string; box?: [x, y, w, h] }> }`，坐标归一化到 `[0, 1]` |
| `AsrResult` | `{ kind: "text"; text: string }` 或 `{ kind: "segments"; segments: Array<{ start: number; end: number; text: string }> }`；时间为片内秒。返回种类必须与请求 `output` 相符，校验见 4.7 |

**错误协议**：插件抛出 `PluginError(kind, detail?)`，`kind` 只能是下表之一；其他异常一律视为 `bad_response`。

| `kind` | 宿主动作 | 卡片文案要点 |
|---|---|---|
| `auth` | 不重试；状态说明变「凭据无效」 | 引导去设置页 |
| `quota` | 不重试 | 说清是额度用完，引导配其他服务 |
| `rate_limited` | 按 `retryAfter` 重试一次 | 稍后重试 |
| `network` | 重试一次 | 网络或代理问题 |
| `timeout` | 重试一次 | 服务超时 |
| `unsupported_language` | 不重试；卡片折叠并标注 | 不支持当前语言对 |
| `bad_response` | 不重试；写日志 | 服务返回异常，附「查看日志」 |

表中“重试一次”由 Job 单点执行，两个 attempt 共用 deadline；流式重试先清空旧 attempt 文本，429 自动等待最多 60 s。收藏写入结果不明时先核对远端，不自动重发；细则见 ARCHITECTURE 5/8.3。

用户可见文案全部由宿主的资源表提供（7.1），插件只给 `kind` 与调试用的 `detail`。

### 4.5 宿主 API

| API | 说明 |
|---|---|
| `$http(req)` | 宿主执行并返回 `{ status, headers, body, files, truncated?: boolean, redirectUrl?: string }`；`req = { method, url, headers?, query?, body?, responseType?: "json"\|"text"\|"file", bodyFiles?, responseFiles?, errorPointer?, credentials?, sign? }`。传输、二进制变换和鉴权见下文；任何网络请求先授权再发出 |
| `$http.stream(req)` | 异步返回 `{ status, headers, chunks?: AsyncIterable<string>, error?: { body, truncated } }`；成功时只有 chunks（UTF-8 解码后的文本块，不承诺 SSE 事件边界），非 2xx 时只有有界 error；随 `ctx.signal` 中止，与文件响应变换互斥 |
| `$ws(url, opts)` | 宿主持有连接，JS 只收发文本消息；`send(handle)` 由宿主直推字节 |
| `$file` | 只能查询 `FileHandle` 的只读元数据：`{ id, mime, bytes, durationMs? }`，不提供打开路径或读取字节的方法；句柄传给 HTTP 原始 body、multipart 或 `bodyFiles`，变换与生命周期见 4.5.1 |
| `$store` | 插件私有 KV（`get / set / delete`），落 SQLite `plugin_kv` |
| `$log(level, msg)` | 写宿主日志 |
| `$i18n(key)` | 取 manifest 里的本地化文案 |

#### 4.5.1 二进制变换与 HTTP 结果

文件句柄是宿主生成的不可猜测 ID，不是路径。宿主登记所属调用、插件实例、允许用途、大小和有效期；任何传入 ID 都按本次授权检查，不能通过自报 `mime/bytes` 改变实际数据。输入句柄可授予同一任务的多个调用，各有独立租约；子进程不能扩大授权。

- **原始／multipart 请求**：`body` 用带类型的描述 `{ kind: "file", file }` 或 `{ kind: "multipart", fields: [...] }`；纯 JSON 则为 `{ kind: "json", value }`，纯文本为 `{ kind: "text", value }`。只有这些控制字段解析句柄，JSON `value` 中的类似对象一律视为普通数据。
- **JSON/Base64 请求**：`bodyFiles: [{ pointer: "/ImageBase64", file, encoding: "base64" }]`，仅用于 JSON body。目标 JSON Pointer 必须指向显式预留的 `null` 值，宿主将其替换成标准 Base64；支持已有数组元素，不自动创建路径。禁止重复、重叠路径以及与密钥写入同一位置。二进制编码字符串不经 IPC 返回。
- **JSON/Base64 响应**：`responseType: "json"` + `responseFiles: [{ name: "audio", pointer: "/audioContent", encoding: "base64", mime: "audio/mpeg" }]`；腾讯 TTS 对应 `/Response/Audio`。宿主有界解析、提取解码并落盘，将该字段在 `body` 中置 `null`，在 `files.audio` 返回句柄。插件只见其余 JSON 元数据，不能请求读取暂存响应文件来绕过边界。缺失字段、格式错误、重复/重叠路径或超限均为 `bad_response`，删除未完成文件。
- **原始文件响应**：`responseType: "file"` 时 `body` 是文件句柄。宿主检查 MIME 与播放/解码适配能力，不能把 JSON 错误页当作音频。
- **HTTP 状态**：HTTP 非 2xx 返回状态、白名单响应头及最多 16 KiB 的错误文本/JSON（顶层 `truncated` 标记），不执行成功文件提取；插件分类为 4.4 的错误。文件变换可附 `errorPointer`（如腾讯 `/Response/Error`）：2xx 业务错误必须命中该路径，宿主只返回此错误子树（仍限 16 KiB），不生成音频句柄；目标文件缺失且无合法错误子树则直接 `bad_response`，不能退回整个原始响应。网络连接失败／超时由宿主直接分类。可见头仅含 Content-Type、Retry-After 和插件声明且经宿主允许的请求 ID／用量头，禁止 Set-Cookie、认证头和任意响应头透传。
- **生命周期**：调用持有租约；完成时宿主先校验并接管结果句柄，才能释放调用租约。任务消费、播放或缓存持有独立引用，最后一个引用释放即删除临时文件。取消／超时／插件退出撤销其调用租约，停止 I/O 并清理半成品；同一输入被其他已授权调用使用时不提前删除。
- **硬上限**：普通文本/JSON 解压后最多 4 MiB；单次输入/输出二进制最多 32 MiB；含 Base64 的 JSON 传输体最多 48 MiB，提取后元数据仍受 4 MiB 限制。供应商更低上限优先，最终上传体计入编码和 multipart 开销。按已接收字节执行，不信任 Content-Length；限制压缩展开，流式块和帧受 4.5.4 约束。ASR 靠切片满足限制，不通过提高宿主上限绕过。

#### 4.5.2 显式凭据引用

**普通文本永不做密钥插值**。原文、译文、URL 字符串、JSON 值中即使出现 `{{secret.apiKey}}` 也按字面传输。旧占位符语法不属于 API v1。

凭据只通过单独的控制字段 `credentials: [{ target: { area: "header"|"query"|"json", name?: string, pointer?: string }, parts: Array<{ literal: string } | { secret: string }> }]` 描述。例如 DeepL 的认证头由 `[{ literal: "DeepL-Auth-Key " }, { secret: "apiKey" }]` 组成；有道哈希输入也采用同一 parts 结构。`secret` 是插件清单声明的本地名字，宿主按绑定查实际账户，不接受插件指定任意账户 ID。

header/query 目标必须预先留空且不能重名，JSON 目标必须预留 `null`；禁止改写 method、origin、URL path、Host、Content-Length、代理认证头等路由/传输控制字段。query 通过统一编码器编码，JSON 通过统一序列化器转义，header 拒绝 CR/LF。目标位置必须在用户批准的凭据绑定中，命名签名器写入的认证头同样受绑定约束。普通数据中出现 `{secret: ...}` 对象不会被解析。

**执行顺序**：校验 origin 和调用授权 → 解析/校验请求描述及句柄 → 填入文件字段与凭据字段 → 生成规范请求体／query → digest/hmac 写入其预留目标并重新序列化 → TC3/SigV4 对最终字节签名 → 发送。单次只选一个 `sign` 方案；签名完成后不能再改内容。多步派生签名超出原语能力时增加命名签名器，不把派生密钥交插件。

#### 4.5.3 签名器

`sign` 指定后，宿主在上述顺序中的对应阶段执行；输出只写已授权的请求位置，不作为函数返回值交给插件。首版内置：

| `scheme` | 参数 | 用于 |
|---|---|---|
| `tencent-tc3` | `service`、`region`、`action`、`version`；密钥取 `secret.secretId` / `secret.secretKey` | 腾讯翻译君、腾讯 OCR |
| `aws-sigv4` | `service`、`region`；密钥取 `secret.accessKeyId` / `secret.secretAccessKey` | Amazon 翻译 |
| `bearer` | `secret` 名 | OpenAI、GLM、OpenAI 兼容接口；等价于在认证头写入 literal `Bearer ` 与 secret 引用 |
| `digest` | `alg`（`md5` / `sha1` / `sha256`）、`input`（4.5.2 的 literal/secret parts）、`into`（4.5.2 的 target，目标预留为空）、`encoding`（`hex` / `base64`，默认 `hex`） | 密钥拼进被哈希串的一类：有道、百度 |
| `hmac` | `alg`（`sha1` / `sha256`）、`key`（`secret` 名）、`input`（待签串，由插件拼好，本身不含密钥）、`into`、`encoding` | 以密钥为 HMAC key 的一类：阿里、火山等后续引擎 |

`digest` 与 `hmac` 是原语，不是某家的完整签名流程：参数排序、截断规则、时间戳由插件生成 literal 部分，宿主执行碰密钥的步骤。hmac 的 `input` 是纯文本、`key` 是本地 secret 名、`into` 使用上述 target。头或 query 里直接放 key 的服务不需要 `sign`，通过显式 `credentials` 注入即可。

新的**命名**签名方案（如 `tencent-tc3` 这一级）由宿主版本加入，插件不能自带；两个原语覆盖不了的算法才需要加命名方案。

首版各服务使用的方案：

| 方案 | 首版使用者 |
|---|---|
| 显式 `credentials`，不带 `sign` | Google 翻译、Microsoft 翻译、DeepL、Gemini（翻译与 ASR）、Claude、Simple LaTeX、Microsoft TTS、Google TTS、AnkiConnect（配置了可选 key 时）、欧路词典 |
| 无凭据 | MyMemory（可选 `de` 邮箱是普通配置）、Ollama、AnkiConnect（未设置 key 时） |
| `bearer` | OpenAI（翻译与 ASR）、GLM |
| `tencent-tc3` | 腾讯翻译君、腾讯 OCR、腾讯 TTS |
| `aws-sigv4` | Amazon 翻译 |
| `digest` | 有道 |
| `hmac` | 首版无使用者，契约形状第一版定下 |

#### 4.5.4 凭据、网络及 IPC 的授权边界

1. **账户绑定**：宿主保存 `accountId → 插件身份/本地 secret 名 → 精确 origin + 允许位置/签名器` 的授权。插件身份包含包 ID 与已确认的签名身份或未签名安装实例。首次输入凭据时同时显示其服务和目标；共享腾讯云（翻译/OCR/TTS）或 AI/ASR 账户须在设置页显式选择关联。第三方仅声明同名 secret 或同名平台不能获取既有授权。换地址、扩展 origin、改变签名身份或凭据用途时保存前重新确认，未确认时旧授权不扩大。
2. **网络出口**：HTTPS/WSS 为默认；本机 Ollama、AnkiConnect 可在安装／配置时明确批准精确 loopback HTTP origin。局域网 HTTP 仅作为用户明确选定的无凭据服务例外，携带凭据必须使用 HTTPS；禁止云请求降级明文。规范化 IDN、IPv4/IPv6、端口并拒绝 URL userinfo；云 origin 的 DNS 若解析到 loopback、私有、链路本地或保留地址则拒绝，已批准本地/局域网服务除外。直连时实际连接地址必须是已验证地址，不能校验后再独立解析；走代理时域名/origin 仍校验，但远端 DNS 及路由由用户选择的可信代理负责，不能承诺宿主可验证代理实际出口。
3. **重定向**：携带凭据或签名的请求不自动跟随 3xx；插件可收到状态和经校验的目标信息，后续请求重新授权。无凭据请求最多 5 跳，每跳重新验证 origin、地址与协议，禁止 HTTPS 降级。所有网络出口共用实现：HTTP、WS 握手、词典 audioUrl、插件更新、应用更新及代理测试。更新分别受安装时批准的更新 origin／宿主内置更新策略限制，不继承任意插件权限。`audioUrl` 只能在来源 provider 获准的 origin 内下载，超出范围直接报错，不在 UI 中远程加载。
4. **IPC 能力范围**：主进程在启动调用前生成不可猜测的调用令牌，绑定插件实例、账户授权、能力、输入句柄和有效期；子进程只提交令牌和操作参数，主进程不相信自报 pluginId/路径/权限。KV 命名空间由主进程绑定，不能自由指定；调用终止立即撤销令牌。单帧 JSON ≤1 MiB，流式文本块 ≤64 KiB；普通 JSON 超过一帧时按绑定调用的 transferId/序号分帧，重组总量仍 ≤4 MiB，缺片/超时取消，不能通过分帧绕过总量限制。每调用最多 4 个进行中的宿主网络操作，进程合计最多 32 个；超限排队且队列有界，溢出报错。引擎原生失陷可能盗用同子进程其他活动令牌，这是 2.1.1 的隔离边界，不将其宣传为每插件进程隔离。
5. **返回值与日志**：不返回解析后的请求对象、认证头或签名；响应头按 4.5.1 白名单，已知凭据的原值及标准 URL/Base64 编码出现时拦截结果并报 `bad_response`。该检查只是纵深防御，无法防远端任意变形回显，用户授权的凭据接收方属于信任边界。日志默认仅记请求 ID、状态、耗时、大小和错误码；不记录正文、认证信息、含 query 的完整 URL 或任意插件 detail。插件日志单条最多 4 KiB、每秒最多 20 条，超限丢弃并记录计数；不能依赖密钥名字与 KV 键名不同就证明无泄露。

#### 4.5.5 界面与宿主桥

只加载宿主打包的可信本地页面；设置窗口使用独立 origin，翻译/词典结果以文本节点渲染，不接受服务返回的 HTML 或脚本。第三方图标在宿主校验后光栅化，禁止脚本、外部引用、foreignObject；CSP 禁止外部脚本/连接、eval、内联事件，禁止页面内远程导航、新窗及任意下载，外链由宿主按允许的 HTTPS URL 打开系统浏览器。

每条 WebView 消息检查窗口身份、当前 origin、会话和参数 schema，只开放该窗口所需的具体动作，不提供通用 HTTP/文件/执行代码代理。凭据写入/导入/导出只接受设置窗口的专用动作，结果窗口无法调用；宿主不向页面返回已保存的明文凭据。输入期的短暂持有规则见 5.4。

### 4.6 manifest.yaml

```yaml
id: com.example.deepl            # 反域名，全局唯一
version: "1.0.0"                 # semver，加引号
apiVersion: 1                    # 插件契约版本，对应 susu-plugin.d.ts
minHost: "0.1.0"
name:
  zh-Hans: DeepL
  en: DeepL
description:
  zh-Hans: DeepL 翻译引擎
  en: DeepL translation engine
icon: icon.svg                   # 16px 网格、1.5px 描边，规范见 DESIGN.md 第 6 节
capabilities: [translate]        # 见 4.7
languages:                       # 规范码 → 自家码
  en: EN
  zh-Hans: ZH
permissions:
  hosts: ["https://api.deepl.com", "https://api-free.deepl.com"]
  # 引用配置字段（Ollama 的写法）：hosts: ["$config.baseUrl"]
  # 首版只有 hosts 这一项权限（4.2）
secrets: [apiKey]                # 走加密存储，不进 settings.yaml，插件不可读
credentialUse:                  # 安装/绑定时展示并批准，不因声明就获授权
  - secret: apiKey
    origins: ["https://api.deepl.com", "https://api-free.deepl.com"]
    targets: [{ area: header, name: Authorization }]
    signers: []
translation:                    # 保守的单条模式；不是供应商全部能力的宣称
  inputUnit: utf8Bytes
  maxInput: 16000
  batch: { mode: single, maxItems: 1, maxTotalInput: 16000 }
config:                          # JSON Schema 语义，宿主据此渲染设置页
  type: object
  properties:
    formality:
      type: string
      enum: [default, more, less]
      default: default
      x-susu: { group: advanced }
quota:                           # 可选：宿主据此渲染用量与超额预警
  unit: chars
  free: 500000
  per: month
timeouts: { translate: 30 }      # 可选：按能力覆盖默认超时，秒
limits: { memory: 64 }           # 可选：运行时内存上限，MB，默认 64，上限 256
update: https://example.com/susu/deepl.yaml   # 可选：自有更新清单
entry: main.js
```

**`x-susu` 扩展**（在 JSON Schema 属性上）：`group`、`secret: true`、`showWhen: { field, equals }`、`placeholder`、`help`，以及 `optionsSource: { dependsOn: [...] }`（绑定本实例字段的动态选项，ARCHITECTURE 3.1）。不允许插件返回可执行 UI。

`credentialUse` 中的 secret 必须已声明，origin 必须在 `hosts` 范围内（同样可引用配置 URL），`targets` 是可接受的 4.5.2 写入位置，`signers` 是允许的签名方案及固定 service/action 等约束。密钥仅用于签名时可不列直接注入目标，但必须列 signer；签名输出目标仍须列在 targets。默认全部拒绝。宿主根据这些声明与用户的账户绑定生成授权，声明变化只形成待确认差异，不能自动扩大既有凭据权限。

**`hosts` 的值是精确 origin**：保留字段名，但静态条目必须含 scheme、host、有效端口，不接受裸域名、通配符、路径或 userinfo。`https://example.com` 规范化为默认 443；`http://127.0.0.1:11434` 与本机 8765 是不同授权。引用写成 `"$config.<字段>"` 时，从配置 URL 提取 origin，配置保存页显示「此插件将访问 <origin>」并确认；动态地址每次改变 origin 都要确认，旧授权不自动保留。凭据绑定和本地 HTTP 的附加要求见 4.5.4。

### 4.7 能力模型

一个插件可声明多项能力，宿主按能力把它挂进对应的服务列表。

`translate` · `dictionary` · `detect` · `ocr` · `tts` · `asr` · `vocab`

- `dictionary` 为可选能力，首版只有有道实现，它决定卡片能否进词典形态（6.1）。用户未配置任何 `dictionary` 服务时，词典形态不出现，没有免费替代来源。
- `vocab` 只覆盖 API 同步（AnkiConnect、欧路）；文件导出不经插件，是宿主功能（6.6）。
- `detect` 为可选能力，宿主自带一个实现（ELS），插件声明后可在设置里被选为检测服务。
- `asr` 只做录完/切片后的批量请求，无流式和 partial；输出分 `text` 与 `segments`，按所选模型声明，不把纯文本伪造成时间码（4.7.2）。
- `tts` 的结果是音频句柄，宿主负责播放与缓存；插件可实现 `voices()` 列音色。

#### 4.7.1 翻译限制、分片与字幕 ID

每个翻译服务／模型配置必须提供下列限制；缺失时拒绝激活，不默认假设无限。示例是 MyMemory：

```yaml
translation:
  inputUnit: utf8Bytes       # utf8Bytes 或 unicodeScalars；均为编码前原文
  maxInput: 500              # 每个 item 的上限
  batch: { mode: single, maxItems: 1, maxTotalInput: 500 }
```

`batch.mode: items` 要求实现 `translateBatch`，声明每条、条数、总量三个上限。按 token 限制的模型由插件作者提供保守的 UTF-8 字节上限，预留系统提示、自定义提示与输出预算；不能直接把“token 数”写成字符数。发送前插件还需依据当前模型/提示检查真实请求限制，超限作为本地输入错误返回，不反复发送相同请求。服务端变更限制导致拒绝时显示错误并引导更新插件/调整配置，不静默截断原文。

宿主在所有路径统一按段落／句子／空白优先分片；仍超限则按 Unicode 标量边界切，不切开 UTF-8 序列。保留每片与原始文本的映射、换行和顺序，原文不丢字符、不按翻译后的换行重新分段。硬切可能影响翻译质量，状态提示说明“已分段翻译”。同一卡片分片按序合并，可并发计算但仅按原顺序提交结果；折叠/取消时停止该卡片全部分片请求。

字幕在宿主创建稳定 `segmentId`；过长条目分成 `{segmentId, partIndex}`，映射为插件看到的唯一 `id`。请求/结果只按 ID 对应：允许返回顺序变化，禁止缺项、重复 ID、额外 ID 或错误类型。批量格式失败不显示任何错配结果，将该批降为逐条调用一次；仍失败逐条标错。single 模式由宿主逐条调用并绑定 ID；items 模式由插件把供应商的数组索引或结构化 ID 转成协议 ID，不用换行分隔符猜测。

译文分片合并回原字幕后沿用原 start/end，不能生成新的时间戳。普通输入和视频都先遵循服务限制；视频可再施加“最多 20 条／约 1500 Unicode 标量”的调度软上限，但它不是服务允许的单次大小。

#### 4.7.2 ASR 模型与媒体协商

凭据账户、翻译模型、转写模型独立存储。语音／音频的默认 ASR 与视频 ASR 是两个选择项，可以关联同一账户；模型可用性由插件的 `asr.models` 声明和配置决定。首版验证基线如下，**文档核查不代表真实调用已通过**，M1 用短样本验证，M3/M4 做质量与长媒体验收后才能发布：

| 平台 / 模型 | API 与解析 | 输出能力 / 用途 |
|---|---|---|
| OpenAI / `whisper-1` | `/v1/audio/transcriptions`，multipart，`response_format=verbose_json`，请求 segment 时间码；解析 `text` 或 `segments[].start/end/text` | `text`、`segments`；两类 ASR 选择项的默认值 |
| Gemini / `gemini-2.5-flash` | `generateContent`，JSON inlineData 音频 + 仅转写提示；解析非空文本，并检查拒答、截断与空输出 | 仅 `text`，用于语音／音频；首版不开放视频时间码能力，不采用模型生成的时间码冒充对齐结果 |

模型 ID、API 路径和限制随内置插件版本更新；不得自动使用 AI 翻译页的模型，也不得自动回落到返回形状不同的模型。基线在 M1 不可用时须更新插件模型表并重新验证，不把这一情况当作坏 key。第三方声明 `segments` 时也须通过协议校验，其实际精度由服务自身承担。

OpenAI 基线的 manifest 形状：

```yaml
asr:
  models:
    whisper-1:
      outputs: [text, segments]
      formats:
        - { container: wav, codec: pcm_s16le, mime: audio/wav, sampleRate: 16000, channels: 1 }
      maxBytes: 24000000       # 编码后文件大小，保守低于供应商上限
      maxRequestBytes: 25000000 # 完整上传体，含 multipart 开销
      maxSeconds: 300          # 首版单片上限，不做片间重叠
```

Gemini 首版同样声明上述 WAV 组合，`outputs: [text]`，`maxBytes: 12000000`、`maxRequestBytes: 18000000`、`maxSeconds: 300`；采用内联上传，Base64 膨胀和 JSON 提示均计入完整请求体。暂不需要云端文件上传/清理流程。供应商上限与宿主 4.5.1 上限取较低值。

宿主取 manifest 与本地编码能力的交集，优先 WAV/16 kHz/单声道/16-bit PCM。**只在模型明确声明 AAC 及其容器等完整组合时才可以改编码**；否则减小片长。编码后测量实际文件大小和预计完整上传体，超限重切，不把“有 maxBytes”作为接受 AAC 的依据。交集为空则模型不可选，并提示不支持当前音频格式。

`segments` 返回需满足：数值有限、`0 ≤ start < end ≤ 实际片长`、按 start 单调排列、文本非空；空数组只允许对应无语音片。同一片允许说话重叠，保留重叠区间，不能把 start/end 自动改成等分时长。越界、NaN、缺字段等视为 `bad_response`，不导出坏字幕。首版内置视频基线在带人工起止标注的中英样本上，语音段起止误差的 P95 目标 ≤1 s；M4 若不达标就调整模型/切片并复测，不能仅凭 JSON 合法宣布通过。

### 4.8 包格式与安装

`.susuext` 是一个 zip：

```
manifest.yaml
main.js
icon.svg
lib/                 可选，main.js 只能 import 这里的文件
signature           可选，Ed25519 对 manifest.yaml 与文件哈希清单的签名
```

- **两个插件目录**：内置插件在程序目录 `%LOCALAPPDATA%\Programs\Su-Su\plugins\<id>\`，随应用更新、不可卸载、只能禁用；用户安装的在 `%APPDATA%\Su-Su\plugins\<id>\`。两处都以只读方式映射给插件宿主子进程。
- **同 id 的覆盖规则**：用户目录里存在与内置插件同 id 的包时，版本严格更高才生效并在设置页标注「覆盖内置 x.y.z」；版本相同或更低则忽略并标注原因。卸载用户目录的副本即回到内置版本。第三方插件与内置插件 id 冲突但签名不是宿主公钥签的，安装时拒绝。
- **安装**：设置页「安装插件」选文件，或拖入设置窗口。宿主校验 zip 结构、manifest 合法性、`apiVersion`、`minHost`，列出 `hosts` 让用户确认后解压到用户目录。
- **签名**：内置签名必须属于宿主信任根；第三方无签名仍可确认安装，但有签名时验证并记录其身份，后续不得静默换 signer/退化为未签名。首次第三方公钥只是用户认可的包身份，不冒充宿主背书（ARCHITECTURE 10）。
- **更新**：manifest 有 `update` 时按第 8 章节奏检查；验证来源/包 hash/签名身份/权限差异，暂存后事务激活，失败回退，不直接覆盖。只重启插件宿主，不重启应用；在途任务明确报错。
- **不建市场、不做索引浏览**（1.5）。

### 4.9 加载与激活

**「启用／禁用服务」和「安装／卸载插件」是两件事，不能一起要求重启。**

- 启动时主进程解析**全部**已安装插件的 manifest，据此渲染设置页与服务列表，并拉起插件宿主子进程。
- **已启用**的插件在子进程启动时全部加载，每个一个运行时，此后常驻、不做空闲释放；加载失败的插件在服务列表上标注错误，不影响其他插件。首次调用的开销只剩一次 IPC 往返。未启用的插件不加载。
- 设置开关**立即生效**：一个包首个能力启用时加载，最后一个能力禁用才销毁 runtime；仅禁用某能力只取消它的调用。安装、卸载、更新只重启插件进程，在途任务明确失败、不自动重放，耗时须实测；详见 ARCHITECTURE 3/4/10。

### 4.10 语言码

宿主定义一张 BCP-47 规范语言表（含中文名／英文名／本地名），插件在 manifest 的 `languages` 里声明支持哪些以及自家的码。**宿主只认规范码，映射由插件负责。** 首版表内只有 `zh-Hans` 与 `en`，但表结构必须按可扩展写。

### 4.11 开发者工具

`susu-plugin` 命令行（Node 脚本，随仓库发布，不进安装包）：`init` 脚手架、`check` 校验 manifest 与包结构、`test` 跑契约一致性测试：启动真实的插件宿主子进程（`susu.exe --plugin-host`），CLI 侧充当主进程、经同一条 IPC 提供录制好的宿主 API 响应，对每个声明的能力发一组固定输入，校验结果形状与错误分类。**不用 Node 模拟运行时**。21 个内置插件全部通过 `test` 是 M5 的出口条件之一，M1 先过它那四个。

---

## 5. 数据与存储

### 5.1 目录布局

```
%APPDATA%\Su-Su\
  settings.yaml          非敏感配置
  secrets.dat            DPAPI(CurrentUser) 加密的凭据
  plugins\<id>\          用户安装的插件（内置插件在程序目录，见 4.8）
%LOCALAPPDATA%\Su-Su\
  susu.db                SQLite（WAL）
  logs\                  保留 7 天且总量不超 10 MiB
  cache\                 有租约的临时媒体；结束/启动清理
  webview\               独立 WebView 用户数据目录
  transactions\          配置/导入恢复 journal 与受保护备份
  updates\               已验证的暂存更新
```

程序本体装在 `%LOCALAPPDATA%\Programs\Su-Su`。

显式保留截图的独立副本默认在 Pictures/Su-Su；只清本应用索引文件。临时媒体、日志、TTS 缓存的配额与到期规则见 ARCHITECTURE 8.4。DPAPI 密文仍绑定当前用户，放 AppData 不代表换机漫游可用。

### 5.2 settings.yaml

只放非敏感项：语言设置、默认展开数、热键、代理配置（不含代理密码）、服务启用状态与排序、提示语、主题、开机自启、各插件的 `config` 值。结构带 `schemaVersion`，升级时按版本迁移。

账户/实例/能力独立标识，设置保存使用 revision/hash 校验和临时文件原子替换；配置与 secrets 跨文件修改用 journal 恢复，不冒充单次原子 rename。用户手改非法时保留输入及最后有效配置，不能用默认覆盖。完整模型见 ARCHITECTURE 3/8.1。

**YAML 的使用约束**（`settings.yaml`、`manifest.yaml`、更新清单共用）：

1. 只用 YAML 1.2 的核心子集：映射、序列、标量、注释。**不用锚点、别名、合并键、自定义标签、多文档**。
2. `settings.yaml` 由宿主生成并写回：键顺序固定，每个键的说明注释来自资源表、随每次写回重新生成。用户手改的**值**保留，用户自己加的**注释不保留**，文件头的注释与设置页里都写明这一点。不做保留注释的往返解析。
3. 解析器必须与 NativeAOT 兼容（YamlDotNet 需走静态上下文的源生成模式，不能用运行时反射）；M1 前验证。
4. 时间、版本号一律加引号写成字符串，防止 `1.0` 被读成浮点、`2026-09-18` 被读成日期。

### 5.3 SQLite

| 表 | 用途 |
|---|---|
| `vocab_entries` / `vocab_deliveries` | 本地词条与分目标 outbox，按词条 revision/目标去重，分别保存同步状态 |
| `vocab_exports` / `vocab_export_items` | 每次导出快照/文件哈希与所含词条 revision，独立于同步状态 |
| `usage` | 按 `provider × 年月 × 指标` 计数，支撑设置页的"本月 12.4 万字符""本月 1,208 次"与 manifest `quota` 的超额预警 |
| `usage_events` | 按 attempt 去重的用量事件，不含正文；不等于供应商余额 |
| `plugin_kv` | `$store` 的落盘 |
| `plugin_installations` | 包签名身份、版本与激活/回滚记录 |
| `window_state` | 按窗口类型存上次关闭时的位置（1.4.1）；一类窗口一行 |
| `meta` | schema 版本等 |

`vocab_queue` 为上述收藏表的查询投影，不是承载全部状态的单表。**没有 history 表。** 具体键、限额、迁移前一致备份见 ARCHITECTURE 8.2。

### 5.4 凭据与设置导出

- 凭据用 **DPAPI（CurrentUser 范围）** 加密后写 `secrets.dat`，与 `settings.yaml` 物理分离。**允许可信设置页在用户输入 API Key／代理密码／导入导出密码期间短暂持有新输入值**；保存经专用单向写入消息交宿主，保存成功、取消、切页或关窗时清空控件和 JS 引用，禁止 localStorage/sessionStorage/浏览器表单保存及日志记录。WebView 保温前也清空。不能承诺 JS/浏览器内存被立即安全擦除。
- 已保存凭据不回传任何 WebView 或插件；重新打开时只显示“已保存”，操作仅有替换/删除；眼睛按钮只作用于本次尚未保存的新输入，不能读回旧值。宿主仅在发送请求、签名或用户主动加密导出时解密；`ctx.config` 和 IPC 业务结果不携带凭据。输入 origin、宿主代理、可信远端及已控制当前用户的攻击边界见 2.1.1 / 4.5.4 / 4.5.5。
- **导出**产出 `.susubak`：默认不含凭据；勾选"包含 API Key"时要求用户设导出密码，用 AES-256-GCM 加密，密钥由 PBKDF2-SHA256（≥ 600k 迭代，随机 salt）派生。
- **导入**时若文件含凭据则要求输入密码，解开后按当前机器的 DPAPI 重新加密落盘。

首版导入是“替换设置”，预览影响、自动备份、完整校验后在重启时事务提交；不导入 DB/插件代码/缓存/已有授权。缺失插件保留配置但禁用，账户/origin 重新确认。大小/KDF 上限、错误密码及中断恢复见 ARCHITECTURE 8.1。

---

## 6. 功能流程

### 6.1 输入 / 划词 / 剪贴板翻译

三者共用同一套卡片下拉列表，只是窗壳不同。**仅划词与剪贴板允许共享热键**，输入/OCR/发音等独立注册；共享时宿主仲裁：

```
按下共享热键
  ├─ 取词（3.1 三级策略）拿到非空文本？ → 是 → 直接翻译
  └─ 否 → 读剪贴板
        ├─ 是文本且非空？ → 翻译
        └─ 否 → 打开空态悬浮窗，等用户输入
```

**划词只有热键这一条入口**：选中文本后不探测、不计时、不在选区旁画任何浮标或按钮。取词只在热键按下的那一刻发生（3.1），平时不监听选区变化。代价是用户必须记住快捷键；换来的是选中文本时屏幕上永远不会多出东西，也省掉了一条常驻的选区监听链路。

**悬浮窗在上次关闭的位置打开**，不跟随鼠标，规则见 1.4.1。

**翻译链路**：语言检测（3.2）→ 定目标语言（检测到中文译英文，反之亦然）→ 按服务列表顺序渲染卡片 → 只对默认展开的前 N 张发请求，其余等展开。重试与文案按 4.4 的错误分类执行；失败态是卡片内一行错误文字 + 重试按钮，卡片不变形不跳动。

发送前统一按各服务的 4.7.1 限制分片，不能让输入／OCR／语音绕过 MyMemory 的 500 UTF-8 字节上限；卡片展示合并结果与分段状态，不把分片变成多张卡片。

**词典形态触发**：只对**单词形式**原文且已展开的对应 provider 卡片查询词典；有词条就呈现词典，合法空词条才回退普通翻译，不隐藏并行请求。单词形式的判定，原文 trim 后：

| 文字系统 | 判定 |
|---|---|
| 拉丁文字 | 只含字母、连字符、撇号，无空白，≤ 32 字符 |
| 中文 | 只含汉字，≤ 4 字 |

含数字、标点、空白，或两种文字混排，一律不触发。没有已配置的 `dictionary` 服务时不触发（1.2）。

### 6.2 OCR 识别翻译

```
① 按下热键        全屏遮罩出现，光标变十字
② 按住鼠标左键    在起点按下
③ 拖动            选区随鼠标变化，四角短线 + 右上角尺寸标注，选区上没有任何按钮
④ 松开左键        截图即完成，遮罩立即消失——没有确认、没有工具条、没有第二次点击
⑤ 结果窗弹出      在上次关闭的位置（1.4.1），弹出即开始识别
⑥ 识别            位图留在宿主，给 OCR 插件一个图片句柄
⑦ 结果进原文卡片  可直接修改
⑧ 按设置里的"识别完成后自动翻译"开关决定是否立即翻译
```

- **整个截图只有一个手势**：按住左键 → 拖 → 松开。松开即完成，不需要再点任何按钮。
- **选区上没有工具条**：原先贴在选区下沿的四个按钮全部取消。"识别并翻译" / "仅识别文字" 的二选一改由设置里的"识别完成后自动翻译"开关代替；"重选"改成结果窗里的「重新截图」按钮；"取消"就是框选期间按 **Esc**（遮罩消失，不打开结果窗）。
- 框选期间只有一行 10.5px 提示：「按住左键拖动框选 · 松开即完成 · Esc 取消」。
- **没有拖出选区就松开**（点一下即松、或选区小于 8×8 px）视作取消，不打开结果窗。
- 截图按设置保留 N 天（默认 7 天）后清理。

### 6.3 语音翻译 / 音频翻译

两条录入路径，转写完成后一律回到同一套卡片列表。

**不做流式转写**：录音期间界面只显示电平与时长，录音结束后才一次性转写，再翻译。

- **麦克风**：用户控制开始／暂停／结束；WASAPI 采集 → 16 kHz 单声道 PCM 写入 `cache\` → 静音达阈值（默认 2 s）或用户点结束时停录 → 宿主按 ASR 插件声明的形状（4.7）编码，交一个音频句柄 → 拿到文本进原文卡片 → 翻译。
- **系统音频**：先倒计时 3 s 再开录，只由用户结束；采集换成 WASAPI loopback，其余同上。
- **时长上限**：单次录音 10 分钟，到点自动结束并提示。按所选 ASR 模型的格式、`maxSeconds`、编码后大小和完整请求体限制切片（4.7.2 / 6.4）；请求 `output: text`，宿主按片序合并，不为普通语音伪造时间码。

最终文本进原文卡片，**可修改后重译**。

### 6.4 视频转写

```
① 选文件      拖拽或选择，≤ 2 GB，MP4/MKV/MOV/MP3/WAV/M4A；显示任务设置（见下）
② 提取音频    Media Foundation IMFSourceReader 解码，输出媒体类型直接设为
              16 kHz / 单声道 / 16-bit PCM，重采样由 MF 自动插入
③ 切片        先由模型限制确定最大片长（≤5 分钟），尽量在末尾 10 秒内找静音点；
              没有静音点则按上限硬切，不承诺永不切断句子。首版不重叠，保留全部
              静音与媒体时间轴，每片记录绝对偏移；编码后重新验证文件和请求体大小。
              16 kHz 单声道 PCM 约 32 KB/s，5 分钟片约 9.6 MB，但不是所有模型的通用上限
④ 转写        逐片向支持 segments 的已选模型请求，校验片内时间码后加绝对偏移；
              创建稳定 segmentId，无语音片没有字幕，不能压缩后续时间轴
⑤ 翻译        单引擎（默认取翻译引擎列表第一个已启用且配置齐全项）；按 4.7.1
              的字节/条数/总量限制合批，并发最多 2。保留 segmentId，格式错配降为
              逐条一次；其余错误按 4.4 分类处理，某条失败不阻止其他字幕完成
⑥ 导出        Ctrl+E：双语 SRT / 原文 SRT / 纯文本 / 双语 Markdown 四选
```

- **暂停** = 停止投递新批次，不中断已发出的请求。
- **任务设置**：选择文件后，在原文件区域显示普通下拉「转写服务／模型」「翻译引擎」和「开始转写」按钮，不直接开始付费上传。视频 ASR 只列 `segments` 模型，默认 OpenAI/whisper-1；切换普通录音 ASR 不修改该选择。显示媒体时长、预计音频片数／转写用量和所选翻译引擎限额；识别前字符总量未知，明确写“翻译字符量待识别”，不伪造总价或共享 IP 余额。
- **MyMemory 长任务**：任务设置中明确显示“单次 500 UTF-8 字节；日额度按 IP 共享，长内容可能中途用完”，开始按钮即确认使用所选服务。转写后按实际原文累计已知字符和请求数，遇 `quota` 停止向该引擎投递，保留已识别原文/时间码及已完成译文；允许用户更换引擎后只翻译未完成条目，不重做 ASR。无跨进程重启续转承诺。
- **首版不做断点续转**：任务状态只在内存，临时音频片在 `cache\`，退出时清理。
- **未配置支持时间码的转写服务时**：按 1.2 的规则，托盘菜单项置灰、热键无动作，不打开结果窗；纯文本 ASR 仍可用于语音／音频翻译。

### 6.5 发音

无界面。用户选中文本按下热键 → 3.1 取词 → 用默认发音服务读出，同时在选中文本旁横向展开发音浮条（各服务 26×26 方块，排第一的就是默认服务）。浮条定位优先用 UIA 的 `BoundingRectangle`，拿不到回落鼠标位置。

设置项"单词优先用词典音频"：当前已经取得的词条有真人音频（`DictionaryResult.phonetics[].audioUrl`，宿主授权下载）时优先；未查询的词典不暗中触发，直接用 TTS。单播放器/缓存规则见 ARCHITECTURE 7/8.4。

### 6.6 生词收藏

点卡片收藏 → 事务写本地词条及每目标 outbox，`vocab_queue` 为查询投影。两种出口可同时开；文件导出/不同同步目标的状态独立。去重、取消收藏、远端未知结果与重启恢复见 ARCHITECTURE 8.3：

- **导出文件**（宿主功能，`Susu.Jobs` 的 VocabJob 直接生成，不经插件）：欧路词典 `.txt`、Anki `.apkg`、通用 CSV。三种格式走同一条流程：同一个"导出内容"勾选（释义／音标／例句）、同一个文件对话框、同一套进度与错误提示、导出后写入独立导出记录并更新队列投影；格式之间只差生成器。以后新增导出格式也只加生成器，不另开流程。
- **API 同步**（`vocab` 插件）：
  - **AnkiConnect** —— 本地 `http://127.0.0.1:8765`，无需授权，但要求 Anki 桌面版正在运行且装了插件；连不上时明确提示这两个前提。
  - **欧路词典** —— 授权 token，可选目标生词本。

---

## 7. 语言

**界面语言与翻译语言是两件事，互不影响。**

### 7.1 界面语言

- 简体中文 + English，两选一，跟不跟系统由用户定。
- 全部文案抽成资源表（`zh-Hans` / `en` 两份），代码里不出现字面量。4.4 的错误分类文案也在这里。
- 插件 manifest 的 `name` / `description` / 配置项标签支持按语言给值，缺失时回落 `en`。
- **应用名永远只写 `Su-Su`**，不随显示语言变化，不本地化、不音译、不加副标题（`DESIGN.md` 第 1 节）。
- 切换界面语言不影响任何翻译设置。

### 7.2 翻译语言

- 只有 简体中文 ↔ 英文。自动检测判出是哪一边，就译成另一边。
- 用户可关掉自动检测，手动固定源语言与目标语言。
- **代码不许写死成两种**：语言表、插件的 `languages` 映射、检测返回值全部按可扩展写。加语言时应当只改数据，不改代码。

---

## 8. 更新与分发

| 项 | 值 |
|---|---|
| 安装器 | NSIS，per-user，装到 `%LOCALAPPDATA%\Programs\Su-Su` |
| 签名 | 首版不做代码签名 |
| 开机自启 | `HKCU\...\Run`（per-user 安装天然匹配，不需要任务计划，不需要提权） |
| 更新通道 | 稳定版（预留 beta） |
| 更新方式 | 签名清单 → 完整包 hash 校验 → 用户触发安装 → 事务替换/健康检查/失败回滚（ARCHITECTURE 10） |
| WebView2 运行时 | 启动时检测；缺失或被策略禁用时引导安装 Evergreen Bootstrapper（约 2 MB），不得直接崩溃 |

不做代码签名带来的四条实现要求：

1. **更新清单必须签名**：宿主内置可信 Ed25519 根公钥，私钥离线保管；对发布的原始 UTF-8 YAML 字节先验签再解析。签名失败明确报校验错误，不显示“没有更新”。内置插件使用同一信任根、不同签名域；轮换由当前可信 key 签发（ARCHITECTURE 10）。
2. 发布页公示安装包的 **SHA-256**。
3. 更新清单走 HTTPS，更新包下载后**强制校验哈希才执行**。
4. 首次运行会弹 SmartScreen，安装引导里要提前说明；per-user 安装让更新不需要 UAC 提权。

安装/更新必须暂存、验证、事务激活并保留旧版；首次健康检查失败时二进制和迁移前 DB 成对回退。自动仅检查，用户触发安装；断电恢复、插件权限延续和卸载数据选项见 ARCHITECTURE 10，不用直接覆盖代替恢复协议。

自动检查在启动完成 15 s 后、距上次尝试≥24 h 时至多触发一次；应用/插件共用调度、低优先级串行，用户可关闭或手动检查。不用闲置高频轮询，失败留到下次符合条件的启动/手动重试。

---

## 9. 体积预算

| 组成 | 体积 |
|---|---|
| 宿主（裸 Win32 + WebView2 + NativeAOT，含签名器与 YAML 解析） | ~12–20 MB |
| JS 引擎（QuickJS-NG 原生库，或 Jint 编入 AOT 镜像） | ~1–3 MB |
| WebView2 运行时 | 0（Win11 预装） |
| Media Foundation（音视频解码） | 0（系统组件） |
| SAPI 本地 TTS | 0（系统组件） |
| ELS 语言检测 | 0（系统组件） |
| 界面资源（按第 17 版浅色画板实现的共享 Vue 组件/CSS/JS + SVG，非打包全部画板） | ~0.5–2 MB |
| 音频采集模块 | ~0.2–1 MB |
| 内置 JS 插件（翻译 7 + AI 5 + OCR 2 + TTS 3 + ASR 2 + 生词本 2，共 21 个；SAPI TTS 与 ELS 检测是宿主原生 Provider，文件导出是宿主功能，都不计） | < 300 KB |
| **合计** | **~15–25 MB，安装包 8–15 MB** |

---

## 10. 里程碑

以下保留产品质量关卡；**实际开发任务按 DEV-PLAN F00–F19 执行**，硬依赖和独立验收以其为准。架构设计完成不代表 M0 通过。内存数字统一为 MiB、延迟按 ARCHITECTURE 11 的 P95 口径；M1 API 冻结同时包含 options/vocab 和各媒体探针。

| | 内容 | 出口条件 |
|---|---|---|
| **M0 原型** | Win32 壳 + 插件宿主子进程（AppContainer + 引擎绑定 + IPC）+ 界面 WebView 保温 + 热键 + 取词三级策略 | 按 2.4 口径实测：闲置态 ≤ 25 MiB、保温态 ≤ 60 MiB、闲置 CPU < 0.1%、1.1 的延迟全部达标；QuickJS-NG 与 Jint 的内存与延迟对照有数据并选定引擎；WebView2 承载插件的退路有同口径对照数据；2.1.1 的容器/IPC 访问矩阵通过；NativeAOT 下 UIA、WebView2 的 COM 互操作与引擎绑定跑通；取词在 10 个常用程序（须含 Firefox 与一个旧 Chromium 内核的 Electron 应用）上的成功率与延迟有数据；3.1 的剪贴板格式/超限拒绝及竞争恢复测试通过 |
| **M1 主链路** | 插件运行时（4.3–4.7 契约）+ 输入翻译 + MyMemory／DeepL／OpenAI／腾讯翻译君四个插件 + 设置（通用／引擎／AI／网络）+ `susu-plugin` 工具 + 代表性媒体契约探针 | 装完能翻第一句话；DeepL/TC3/代理/SSE 跑通；四插件通过 `susu-plugin test`；JSON/Base64 图片请求、原始和 JSON/Base64 TTS 响应、multipart ASR 与 Gemini 内联文本转写的短样本真实调用通过；MyMemory 字节边界与字幕 ID 对齐通过；凭据字面串、重定向、越权句柄/账户/IPC、输入页清空及不回传旧凭据测试通过，之后才冻结 API v1 |
| **M2 取词链路** | 划词 + 剪贴板 + 悬浮窗 + 发音浮条 + 词典形态（有道）+ 语言检测（ELS） | 划词/剪贴板共享仲裁跑通（发音独立）；借用剪贴板的开关两态都验过，失败提示文案到位；词典形态触发规则完成测试 |
| **M3 图像与音频** | OCR（截图 + 腾讯 + LaTeX）+ 语音／音频翻译（WASAPI + 批量 ASR） | 录音结束到译文出现的耗时有数据；10 分钟上限与静音自动结束两条路都验过 |
| **M4 长内容** | 视频转写全管线 + 导出 | 42 分钟视频端到端跑通；时间码结构及 4.7.2 精度目标达标；连续无静音/静音片/片边界/中英字幕及 ID 异常样本通过；MyMemory 分片、额度暂停与换引擎后保留 ASR 结果通过；API 调用次数可解释；MF 音轨解码样本集通过或有明确提示 |
| **M5 收尾** | 生词收藏 + 英文界面 + 设置导出导入 + 签名更新 + 安装器 + 全部 21 个内置插件 | 可发布；21 个插件全部通过 `susu-plugin test` |

M0 排在最前：它的实测结果若不达标，插件运行时路线与取词三级策略都要回炉。

---

## 11. 画板待改清单

已移至 [画板修订记录 § 第三版](../design/ARTBOARD-REVISIONS.md#第三版线上第-16-版)。两轮画板修订都已完成；当前交互以 [DESIGN](../design/DESIGN.md) 为准。

---

## 12. 未结清项

O-01 至 O-12 的历史处置保留。R01–R06 已修订方案并同步画板；A1 为 R07–R13 补齐实施规格和模块任务。下列事项在设计层已处理；设计落点不等于评审风险已由运行证据关闭，各模块的验收结果见 [PROGRESS](../evidence/PROGRESS.md)：

| 编号 | 关联评审 | 设计落点与待执行模块 |
|---|---|---|
| O-13 | R07 | ARCHITECTURE 3–6；F01/F04/F06，J 用例 |
| O-14 | R08 | ARCHITECTURE 11；F00/F19，PER 用例 |
| O-15 | R09 | ARCHITECTURE 4.1；F00/F08，C/SEL 用例 |
| O-16 | R10 | ARCHITECTURE 1–3/6/9；F01/F03/F07，CFG/UI 用例 |
| O-17 | R11 | ARCHITECTURE 8；F02/F15/F17，DATA 用例 |
| O-18 | R12 | ARCHITECTURE 10；F16/F18，UPD 用例 |
| O-19 | R13 | ARCHITECTURE 9；F03/F19，UI 用例 |
| ~~O-20~~ | R01–R06 | **已结清**（2026-09-19）：画板按 [ARTBOARD-REVISIONS.md](../design/ARTBOARD-REVISIONS.md) 同步完毕，线上第 17 版，浅/深色源文件已导出回 `design/`。执行时的两处取舍见 `RECORD.md` D-58 |

### 移交开发测试计划的实测项

原始六项及本次架构验收已列入 [`TEST-PLAN.md`](../development/TEST-PLAN.md)，执行结果见各模块交付记录；下表保留原有实测项索引，执行任务对应 DEV-PLAN：

| 来源 | 实测项 | 阶段 |
|---|---|---|
| 2.4 / 10 | 两态常驻内存（2.4 口径）、闲置 CPU、1.1 的全部延迟 | M0 |
| 2.1 / 2.4 | 子进程拉起与 IPC 往返耗时；QuickJS-NG 与 Jint 对照；WebView2 承载插件的退路同口径对照 | M0 |
| 2.1.1 / 4.5.4 | AppContainer 允许/禁止资源矩阵、IPC 身份、宿主代理越权拒绝与失陷边界 | M0–M1 |
| 2.2 | NativeAOT 下 UIA、WebView2 的 COM 互操作与引擎绑定 | M0 |
| 3.1 | Chromium 首次 UIA 查询延迟；取词在 10 个常用程序上的成功率与延迟 | M0 |
| 3.1 | Firefox 原生 UIA 现状：是否默认启用、`TextPattern.GetSelection()` 是否可用；结果决定第二级 IAccessible2 的实现时机 | M0 |
| 3.3 | Media Foundation 音轨解码覆盖：MKV / WebM 里的 Opus、Vorbis 在各 Win11 版本（含 N 版）的支持情况；装上 Web Media Extensions 后 Vorbis 是否即可解 | M4 |
| 6.1 | 词典形态触发规则：两种文字系统、单词／短语／整句、带标点与不带标点 | M2 |

### 移交设计阶段的事项

第四版画板同步已于 2026-09-19 完成（O-20，线上第 17 版，43 张）。A1 的合并排序入口、路径级离线、设备中断等实施交互补充在 DESIGN 第 13 节；不修改画板存档，F03/F07 及相应功能模块落实并做视觉/行为验收，不将这些新增细节谎称为已画入第 17 版。
