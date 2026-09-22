# Su-Su · 方案记录

这里保存**决策的来龙去脉**：为什么这么定、否决过什么、调研与实测拿到的数据。

- **产品执行依据是 [`PLAN.md`](PLAN.md)**；技术实施见 [`ARCHITECTURE.md`](ARCHITECTURE.md)，模块开发顺序见 [`DEV-PLAN.md`](DEV-PLAN.md)。本文件只保存决策依据。
- 视觉与交互基准是 [`DESIGN.md`](DESIGN.md)。
- 本文件不用于指导开发，只在**有人想推翻某条决策时**查阅——先看这里为什么当初否掉它。

**文档沿革**：项目最初有 `PRD.md`（做什么）与 `TECH.md`（用什么做）两份文档，2026-09-17 合并进 `PLAN.md`，2026-09-18 删除原文件；同日把方案理由从 `PLAN.md` 剥离到本文件。2026-09-18 晚对 `PLAN.md` 做了一次评审（D-28 至 D-30、D-33 至 D-35），随后决定去掉 Pot / Bob 插件兼容并重写 `PLAN.md` 第 4 章（D-24 至 D-27）。同日深夜第三次评审，采纳插件运行时改为嵌入式引擎子进程、界面 WebView 关窗保温（D-36、D-37，调研 3.6），并补上签名器缺口与几处文档矛盾（D-38 至 D-43，调研 3.7）。

**`PLAN.md` 版本对照**：第一版 = 2026-09-17 的合并本（D-01 至 D-23）；第二版 = 2026-09-18 去掉 Pot / Bob 兼容后的重写（D-24 至 D-35）；第三版 = 2026-09-18 深夜第三次评审后（D-36 至 D-43，后续交互修订至 D-51）；第四版 = 修复 R01–R06（D-52 至 D-57）；D-58 为第 17 版画板同步；A1 实施设计与模块计划 = 2026-09-19（D-59 至 D-65）。R07–R13 已补设计与开发落点，不代表 Windows/API 实测通过；全部验收见 `TEST-PLAN.md`。

---

## 1. 决策记录

一条决策一行。**编号即时间序**：D-01 至 D-23 为 2026-09-17，D-24 至 D-57 为 2026-09-18，D-58 至 D-65 为 2026-09-19。

| 编号 | 决策 | 理由 / 代价 | 落在 |
|---|---|---|---|
| D-01 | 常驻一个无 UI 的插件宿主 WebView，托盘态内存指标从 < 20 MB 放宽到 ≤ 60 MB | 插件跑在 V8 里，若随窗口释放 WebView2，则每次热键都要重建 V8 + 重载插件才发得出第一个请求，且剪贴板监听这类后台链路无处执行。延迟比内存值钱 | PLAN 2.1 / 2.4 |
| D-02 | 划词取词以无损方式为主：UIA → Legacy MSAA → 受保护的模拟复制，三级回落 | 调研结论见 3.1。严格"零污染"只在前两级成立 | PLAN 3.1 |
| D-03 | 内置服务补入有道翻译与 Claude | 画板已在用；有道提供词典形态，不可替代 | PLAN 1.3 |
| D-04 | 弃用 Windows 自带语音识别，默认转写服务改为复用 AI 平台凭据的云端 ASR | WSR 正被弃用；离线识别要按语言单独装可选组件，不能假定用户有；Win+H 那套其实还是走微软的在线服务。当可选插件可以，当默认不行 | PLAN 1.3 |
| D-05 | 插件只做 JS 一档，内置服务用同一套 JS 插件实现，宿主零特例 | 两档 = 两套加载／配置／错误路径，且声明式很快会被某家的签名算法逼成 JS。代价：启动时多解析十几个 JS。**可行性已核**：插件只能发 HTTP + 用宿主注入的 API，对着服务清单逐项核过——翻译引擎、AI 平台、腾讯 OCR、Simple LaTeX、生词本 API 全是 HTTP 服务，一个都不受影响；真正需要本地能力的只有系统 TTS 和音频采集，这两个本来就是宿主内置 | PLAN 4.1 |
| D-06 | 一律要求填 key，不提供任何免密钥引擎 | 非官方免费接口有合规与稳定性风险。代价：首次启动全是空态。**后经 D-22、D-23 修正**，见第 2 节 | PLAN 1.3 |
| D-07 | 视频转写先提取音频再转写，不把视频送给 ASR | ASR 服务按音频计费与限长（OpenAI 单次 25 MB），直接送视频既贵又超限。16 kHz 单声道 PCM 是 32 KB/s，5 分钟片约 9.6 MB，安全 | PLAN 6.4 |
| D-08 | 生词收藏首版做 AnkiConnect + Anki `.apkg` + 欧路词典 | 墨墨、扇贝无公开 API，不做 | PLAN 6.6 |
| D-09 | 凭据加密存储（DPAPI CurrentUser），设置中增加导出／导入设置 | DPAPI 绑定当前用户 + 当前机器，重装系统或换机后必然失效，所以导出是必需功能而非锦上添花 | PLAN 5.4 |
| D-10 | 首版不做便携版 | 与 D-09 的 DPAPI 互斥 | PLAN 1.5 |
| D-11 | 首版不签名 | 省证书钱。代价：SmartScreen 拦截、部分杀软误报。缓解见 PLAN 8 | PLAN 8 |
| D-12 | 首版不做翻译历史 | 需求里没有，画板也没有界面 | PLAN 5.3 |
| D-13 | 不做离线 OCR | Manggo 的价码是 +44 MB（见 5.1），而 OCR 清单全是云服务 | PLAN 1.5 |
| D-14 | 界面语言首版简体中文 + 英文，文案从第一天就全部抽成资源表 | 事后补 i18n 会返工 | PLAN 7.1 |
| D-15 | 自动语言检测用 Windows ELS，不带 CLD3 | 调研结论见 3.2。ELS 零体积做到同一件事，CLD3 要 2–3 MB 加一份 protobuf 依赖 | PLAN 3.2 |
| D-16 | 词典形态触发规则：原文 trim 后无空白且 ≤ 32 字符（无空格文字系统另算） | 中文没有词间空格，"无空白"会把整句误判成单词，所以中文改用 ≤ 4 字的字符数阈值。先按此实现，后续需专门测试。**后经 D-45 收紧为只对单词形式触发** | PLAN 6.1 |
| D-17 | 开源，许可证在首版开发完成、发布前补 | 在补上之前不要公开仓库——没有 LICENSE = 保留全部权利，他人不能合法 fork 或贡献。**后经 D-44 定为 MIT** | PLAN 1.1 |
| D-18 | 首版翻译语言只做 简体中文 ↔ 英文 互译，其余语言暂时取消 | 大幅缩小语言表、映射表与测试面。代价：ELS 与语言映射两块要按"以后会加语言"的形状写，不能写死成两项 | PLAN 7.2 |
| D-19 | 界面语言与翻译语言彻底拆分，互不影响 | 二者不再共用"主语言"这一个概念，原 `SetGeneral` 的设计作废 | PLAN 7 |
| D-20 | 「借用剪贴板取词」是独立开关，默认关；关闭时无损取词失败必须明确提示 | 默认不动用户剪贴板是更安全的默认值。代价：默认状态下在 UIA 拿不到的程序里划词直接失败，提示文案要给出口 | PLAN 3.1 |
| D-21 | 语言检测默认用 Windows 内置（ELS）；用户配置了声明 `detect` 能力的服务后可在设置里更换 | 检测从"一段内置逻辑"升格为"一类可替换的服务"，与翻译／OCR／TTS 同构 | PLAN 3.2 / 4.7 |
| D-22 | 内置 MyMemory 作为唯一免配置翻译引擎 | 调研结论见 3.3。它是唯一无需注册、官方文档允许、有明确免费额度的翻译 API，用来让用户装完就能翻第一句话 | PLAN 1.3 |
| D-23 | GLM 默认模型设为 `GLM-4-Flash`；翻译引擎补入腾讯翻译君；AI 平台补入 Ollama | Bob 清单对照的结论（3.3）。`GLM-4-Flash` 无限免费、零成本拉起首启体验；腾讯翻译君每月 500 万字符是专业引擎里额度最大的，且与腾讯 OCR 共用凭据；Ollama 我们这边体积为 0。Groq／硅基流动／火山／阿里／百度／小牛不进首版——没有同等协同点，只会让清单变长 | PLAN 1.3 |
| D-24 | 去掉 Pot / Bob 插件兼容：只有 `.susuext` 一种包格式、一条加载路径、没有适配壳 | 调研 3.4。两家契约与 PLAN 4.2 的取消、权限、大二进制三条要求互斥；Pot 的 `run` 与文件读取必须砍掉，砍掉后依赖它们的插件也不能用；兼容的实际收益只剩 translate 类插件。代价：失去现成社区插件，首版生态就是 24 个内置插件 | PLAN 1.5 / 4.1 / 4.8 |
| D-25 | 密钥永不进插件：请求里写 `{{secret.x}}` 占位符，宿主替换并用内置签名器（`tencent-tc3` / `aws-sigv4` / `bearer`）签名 | D-24 之后插件不再需要自己算签名，`$secret.get` 失去存在理由。插件被攻破也拿不到 key，只能在白名单域名内发请求 | PLAN 4.1 / 4.5 / 5.4 |
| D-26 | 插件契约改为 ES Module + `susu-plugin.d.ts` + `apiVersion`；能力函数全部 async 且必收 `AbortSignal`；跑在 module Worker 里 | 只有一套契约才能强制取消与超时、发布类型定义、做一致性测试（`susu-plugin test`）。Bob 的回调式全局函数与 Pot 的 `options.utils` 都做不到 | PLAN 4.3 / 4.4 / 4.11 |
| D-27 | 错误协议定为七类枚举（auth / quota / rate_limited / network / timeout / unsupported_language / bad_response），用户文案全部由宿主资源表提供 | PLAN 1.3 要求区分「额度用完」与「服务故障」、6.1 要求失败自动重试一次，只有错误分类通用了才做得到。Bob 的 serviceError 类型有限，Pot 只返回字符串 | PLAN 4.4 |
| D-28 | 插件隔离定义为：沙箱 iframe（源）+ module Worker（线程，可强杀）；不承诺插件之间的进程隔离 | 评审指出原文「进程隔离」与拓扑矛盾。调研 3.5：WebView2 132 起沙箱 iframe 与父页面分进程，但所有插件共用那一个进程，所以线程隔离仍然必要 | PLAN 2.1 |
| D-29 | 托盘态内存按整棵进程树的 private working set 之和度量；超预算的退路是轻量 JS 引擎承载插件宿主 | 原预算漏掉 WebView2 进程组的固定成本，原退路「砍 iframe 隔离」救不了固定成本 | PLAN 2.4 |
| D-30 | 增加三个延迟目标（热键→窗口 150 ms、取词→首请求 50 ms、界面 WebView 冷建→首帧 300 ms） | D-01 用内存换延迟，没有延迟数字就无法被 M0 证实或证伪。三个阈值是起点，M0 实测后可调 | PLAN 1.1 / 10 |
| D-31 | 语音翻译取消流式，录完一次性转写；`asr` 能力只有批量一种形状 | 用户决定。流式与批量是两套调用形状；OpenAI 的流式转写无免费额度、Gemini Live 免费层有会话限制，M3 原出口条件「实时可见中间结果」在免费路径上不成立。批量一种形状让 6.3 与 6.4 共用一条路 | PLAN 4.7 / 6.3 |
| D-32 | 配置文件与插件 manifest 用 YAML，限定 1.2 核心子集 | 用户决定。限定核心子集（不用锚点、别名、合并键、自定义标签、多文档）是为了避开解析器之间的差异与别名展开的体积放大。代价：.NET 没有 AOT 友好的原生 YAML 序列化器，要验证 YamlDotNet 的静态上下文模式；IPC 协议与更新清单以外的运行时数据仍用 JSON | PLAN 5.2 / 4.6 |
| D-33 | 域名白名单可引用配置字段（`$config.x`），保存配置时提示一次；没有运行时授权弹窗 | Ollama、OpenAI 兼容 base URL 是用户自填的，静态白名单挡住的正是它们。去掉外来插件后不存在「不知道要访问谁」的插件，弹窗没有场景 | PLAN 4.6 |
| D-34 | 更新清单用宿主内置 Ed25519 公钥签名；同一把公钥签内置插件包 | 不做代码签名时，SHA-256 与安装包同源，攻破域名即可同时换掉两者；签名是唯一不同源的校验，成本几乎为零 | PLAN 8 / 4.8 |
| D-35 | 取词第二级改为 IAccessible2 | UIA 的 LegacyIAccessiblePattern 只透出 IAccessible，`accValue` 是整段文本，没有选区；Firefox 与旧 Chromium 内核的 Electron 只实现 IA2 | PLAN 3.1 |
| D-36 | 插件运行时改为嵌入式 JS 引擎（基线 QuickJS-NG，Jint 为 M0 对照），跑在 `susu.exe --plugin-host` 的 AppContainer 常驻子进程里，每插件一个独立运行时、启用即加载、常驻不释放；WebView2 承载插件降为 M0 退路 | 调研 3.6。原方案的隔离与安全建立在 Chromium 行为上（沙箱 iframe 分进程要求运行时 ≥ 132、要用带 RequestSourceKinds 的拦截器、后台节流靠启动参数关），Evergreen 运行时随时可变、应用控制不了；「网络只经宿主」靠删 `fetch` 加拦截器两层代码保证。改后由 AppContainer 无网络能力强制，引擎被攻破也拿不到密钥与网络；WebView2 进程组固定成本从闲置态里消失；Worker 的空闲释放与沙箱 iframe 编排整套不要了：一个运行时几百 KB，生命周期管理比它省下的内存贵。子进程随应用启动拉起而不是按需拉起，是因为取词完成到首个请求发出的 50 ms 里塞不下一次进程拉起。引擎绑定自写而不用现有 NuGet 包，是因为那些包没按 NativeAOT 设计。代价：没有 DevTools 调插件（首版靠 `$log` 与插件日志面板）；Web API 要宿主补齐并写死清单；`susu-plugin test` 改为驱动真实子进程；多一层 IPC。第 4 节否决的「进程外插件」是每插件一个进程、理由是热插拔，与此不是同一件事 | PLAN 2.1 / 2.2 / 2.4 / 4.3 / 4.9 / 4.11 |
| D-37 | 界面 WebView 关窗后隐藏保温，全部窗口关闭并闲置 10 分钟（设置项）后释放整个 WebView2 环境；托盘态预算拆成闲置态 ≤ 25 MB 与保温态 ≤ 60 MB；保温态热键到窗口可见 ≤ 50 ms | D-36 之后常驻 WebView2 的唯一理由是界面热，那就让界面而不是插件宿主享受这份常驻，闲置期整个放掉。旧方案花同样的固定成本买到的是「插件热」，每次热键仍要冷建界面。代价：保温期内内存与旧方案相当；多一个保温时长设置项 | PLAN 1.1 / 2.1 / 2.4 |
| D-38 | 宿主签名器增加 `digest` 与 `hmac` 两个原语级方案；命名方案仍只有 tencent-tc3 / aws-sigv4 / bearer | 调研 3.7 逐项核首版清单：有道要求 `sha256(appKey+input+salt+curtime+appSecret)`，密钥拼在被哈希串里，三个命名方案都表达不了，按 D-25 有道插件写不出来，而它是词典形态唯一来源。原语只做碰密钥的那一步，待签串由插件拼，密钥仍不出宿主；D-05 担心的「声明式被签名算法逼成 JS」针对的是完整签名流程，不适用于哈希原语。代价：多两个方案的实现与测试 | PLAN 1.3 / 4.5 |
| D-39 | `Provider` 只有两种实现：插件、宿主原生（SAPI TTS、ELS 检测）；内置插件按 22 个计，不再写 24；不允许第三种实现 | 原文「全部内置服务都是插件、宿主零特例」与「宿主自带 ELS 实现」「Windows 内置 TTS」互相矛盾，且插件运行时里没有系统能力，这两项本来就做不成插件；体积表把它们算进 24 个插件是错的。写明两条实现路径，实现时才不会给宿主原生项开一个「内置插件」的假分支 | PLAN 1.3 / 2.3 / 4.1 / 9 / 10 |
| D-40 | 首版 `permissions` 只有 `hosts` 一项，删掉 `clipboard` | 服务插件不取词、不读剪贴板，功能插件首版不开放，这个权限位没有消费者，留着就是一个没人校验的字段。功能插件开放时再加 | PLAN 4.2 / 4.6 |
| D-41 | `settings.yaml` 由宿主生成写回：键顺序固定、注释来自资源表随写回再生成；用户改的值保留、用户加的注释不保留，文件头与设置页写明 | D-32 原要求「保留用户注释」，YamlDotNet 做不到往返保留注释，为此自写解析器不值得。把规则定死比留一个「尽量」强 | PLAN 5.2 |
| D-42 | Media Foundation 解不开 Vorbis / Theora / OGG 时引导安装商店免费的「Web Media Extensions」，N 版引导「Media Feature Pack」；不自带解码器 | 缺的是系统可选组件，不是能力上限；引导安装零体积、零许可风险，与「不打包 ffmpeg」一致 | PLAN 3.3 / 12 |
| D-43 | 内置插件在程序目录、随应用更新、不可卸载只能禁用；用户插件在 `%APPDATA%`；同 id 用户包版本严格更高才覆盖内置并标注，否则忽略并标注；第三方包用内置 id 且非宿主公钥签名的拒绝安装 | 4.8 原来没写内置插件放哪、同 id 谁生效，字段级设计一定撞上。「严格更高才覆盖」让用户能试新版又不会被降级；拒绝冒用内置 id 的未签名包，堵住用第三方包替换内置插件这条路 | PLAN 4.8 / 5.1 |
| D-44 | 开源许可证暂选 MIT，发布前确认；`LICENSE` 加入前仓库不公开 | 用户决定，O-01 结清。MIT 与「不要求插件开源」一致，对插件作者最宽松 | PLAN 1.1 / 11 · B1 |
| D-45 | 词典形态只对「单词形式」触发：拉丁文字只含字母、连字符、撇号，无空白，≤ 32 字符；中文只含汉字，≤ 4 字；含数字、标点、空白或混排一律不触发 | 用户决定，O-03 结清。触发条件收紧成可枚举的形式，原来「短语也可能命中」的灰区消失，专项测试降为普通用例 | PLAN 6.1 |
| D-46 | 不做首次启动引导；功能依赖的服务类别里没有可用服务时，入口不展示或置灰（托盘项置灰、热键无动作、卡片不出对应形态、设置页热键行标注），不做空态引导 | 用户决定，O-05 结清。代价：新用户要自己找到设置页配服务；D-23 提到的两条免费路只出现在设置页与 README，不再有专门引导界面；热键无动作对不知情的用户可能像坏了，靠设置页标注兜底 | PLAN 1.2 / 1.5 / 6.4 / 11 |
| D-47 | 词典形态必须由用户配置 `dictionary` 服务后才可用，未配置时不显示；不做免费词典来源 | 用户决定，O-09 结清。代价：不花钱看不到音标、词性、释义 | PLAN 1.5 / 4.7 / 6.1 |
| D-48 | 欧路 `.txt`、Anki `.apkg`、CSV 三种文件导出统一归为宿主功能，走同一条导出流程，格式之间只差生成器；`vocab` 插件只做 API 同步；内置插件按 21 个计 | 用户决定，O-12 结清。文件导出类功能行为一致；插件运行时本来就写不了文件 | PLAN 1.3 / 4.7 / 6.6 / 9 / 10 |
| D-49 | 其余未结清项处置：错误与离线的全局提示、深色主题设计移入设计阶段（PLAN 11 新增画板）；M0 实测项、MF 解码覆盖、Firefox UIA 现状移入开发测试计划（独立文档，尚未编写） | 用户决定，O-06 / O-07 / O-08 / O-10 / O-11 结清。PLAN 第 12 章不再有未结清项 | PLAN 11 / 12 |
| D-50 | 画板按 PLAN 第 11 章改完时的取舍：`Main` 把 MyMemory 画成第一张卡片；「通用」页拆成上下两张画板（内容超过一屏）；`SetEngines` 删掉「调用策略」分组（默认展开数只留在通用页，失败重试由 4.4 的错误协议决定，不再是用户配置）；`SetHotkeys` 删掉「显示 / 隐藏主窗口」行（1.2 没有这个功能）；`SetPrompt` 的「应用到」补了 Claude 与 Ollama；全局错误提示定为悬浮条 / 托盘通知 / 离线状态栏三种承载；深色 token 表见 DESIGN 第 2 节，深色画板由脚本从浅色映射生成 | 设计阶段决定。删「调用策略」是因为它与通用页重复且重试逻辑已被协议接管；删「显示 / 隐藏主窗口」是因为功能清单里没有这项。代价：深色版是映射而非逐张手调，个别深色画板的细节（如截图遮罩里的示意线）只保证 token 正确 | PLAN 11 · DESIGN 2 / 8 / 9 / 12 |
| D-51 | 四条交互收敛：① 划词只有热键一个入口，取消选中后的触发浮标；② 所有窗口不跟随鼠标，关闭时记位置、下次原位打开，位置存 SQLite `window_state`；③ OCR 选区取消工具条，松开鼠标即完成并立即弹结果窗识别；④ 设置窗口每次打开都居中，不记位置。截图手势确定为：按住左键 → 拖 → 松开即完成，无确认无第二次点击；点一下即松或选区小于 8×8 px 视作取消。拿不到上次位置的两种情况（从未开过、坐标已不在任何工作区内）一律居中 | 用户决定。①省掉一条常驻的选区监听链路，选中文本时屏幕上不再多出东西，代价是必须记快捷键；②窗口位置可预期，不会因鼠标在屏幕边缘而被挤到角落，代价是多一张表与一次工作区校验（位置失效要回落居中）；③把「识别并翻译 / 仅识别文字」的二选一让给设置项，少一次点击，代价是改主意只能靠结果窗里的「重新截图」；④设置是低频、长驻的窗口，居中比原位更好找。位置不放 `settings.yaml` 是因为每次关窗都要重写 YAML 并按 D-41 重新生成注释，既费事又打断用户手改 | PLAN 1.2 / 1.4.1 / 1.5 / 5.3 / 6.1 / 6.2 · DESIGN 9 / 12 |
| D-52 | 模拟复制改为尽力恢复；物化有限格式快照，不预写历史排除标记；无法安全快照则不发 Ctrl+C，新复制优先，不承诺历史/云同步不受影响 | R01。排除格式属于当前内容，外部复制可清空它；OleGetClipboard 返回数据对象而非独立快照。代价：遇私有格式、虚拟文件、大剪贴板时会拒绝借用；可能留下复制结果。修正 D-02 / D-20 及调研 3.1 的保证范围 | PLAN 3.1 · DESIGN 7 · TEST-PLAN C |
| D-53 | 宿主补 JSON/Base64 文件注入和字段提取、HTTP 状态/有界错误体、句柄调用租约与资源上限；签名基于最终发送字节 | R02。腾讯 OCR、Google/腾讯 TTS 不能只靠 multipart 或原始文件响应覆盖。代价：宿主增加流式编码与有界 JSON 提取；M1 增加媒体契约探针，API v1 通过后才冻结 | PLAN 4.4 / 4.5.1 / 10 · TEST-PLAN B |
| D-54 | 翻译按服务声明的字节/标量/条数限制分片；MyMemory 上限 500 UTF-8 字节；字幕用 ID 对齐，批量错配降为逐条；长任务显示限制、额度耗尽保留 ASR 并允许换引擎 | R03。约 1500 字符不是通用单次上限，单字符串不能保字幕对应。代价：MyMemory 长任务请求数增多，视频开始前需选服务并显示额度说明；保持单服务单卡片 | PLAN 1.3 / 4.7.1 / 6.1 / 6.4 · DESIGN 9 · TEST-PLAN T |
| D-55 | ASR 按模型声明 text/segments 和媒体格式交集；基线 OpenAI/whisper-1 支持两种用途、Gemini/gemini-2.5-flash 首版仅文本；普通录音和视频服务独立选择；无静音允许硬切，保留时间轴 | R04。账户复用不等于模型/时间码能力复用，WAV 不能因限大小就自动改 AAC。模型是待实测基线，不是已验证承诺。代价：Gemini 首版不可选为视频 ASR，M1/M4 增加返回协议与时间精度验收 | PLAN 1.2 / 4.7.2 / 6.3 / 6.4 · DESIGN 7 / 9 · TEST-PLAN A |
| D-56 | 取消字符串 secret 占位符，改为独立控制字段的 literal/secret 引用；按账户、插件身份、origin、写入位置/签名器绑定，设置页只短暂持有新输入、旧凭据不回传；统一网络/IPC/UI 桥授权 | R05。原文可含占位符；远端可回显密钥；HTML 密码框与“从不进入 WebView”冲突。保留 HTML 设置页并收窄明文承诺，不新增原生密码窗口。代价：密钥接收方与输入页属于信任边界，不能保证任意远端编码回显被阻止；schema 和设置授权增加明确字段。修正 D-25 / D-33 / D-38 | PLAN 4.5.2–4.5.5 / 4.6 / 5.4 · DESIGN 7 · TEST-PLAN S |
| D-57 | AppContainer 限定为直接联网受限、程序/用户敏感目录不可访问或写入、容器私有资源受控；明确原生引擎失陷时整个插件子进程同一失陷域，补 profile/ACL/IPC/Job 验收 | R06。AppContainer 自有目录/注册表可写，多 JSRuntime 不提供原生漏洞后的插件间隔离。保留单子进程，修正 D-36 的绝对保证；宿主代理仍需独立授权，沙箱启动失败不无保护回落 | PLAN 2.1 / 4.5.4 / 10 · TEST-PLAN X |
| D-58 | 按 `design/REVISIONS.md` 同步画板（线上第 17 版）时的两处取舍：① 「发音与语音」页内容超过一屏，拆成 `SetSpeech`（发音服务 · 发音）与 `SetSpeechB`（转写服务），左导航仍是一项，与「通用」页的拆法一致；② 删掉 `SetAI`「调用」组里的「温度」行 | ① 转写按用途拆成语音／音频与视频两组后，加上关联账户与可选模型标注，单屏装不下；拆画板比画一张溢出的图诚实。② PLAN 全文没有「温度」这一项，它是早期画板自造的；按 4.6，模型参数属于插件 manifest 声明的 config，应出现在该服务的展开区而不是全局「调用」组。腾讯云账户的选择器放在 `SetOcr`（同一套密钥），`SetEngines` 的腾讯翻译君行只标注关联，避免两页重复同一个控件 | PLAN 11 · DESIGN 7 / 8 / 12 · design/REVISIONS.md |

**选型阶段已结清、后续未改动的决策**：宿主语言 C# / NativeAOT；跨端首版不做；功能插件第一版不开放；热键由宿主统一注册；热键注册失败必须提示；快捷键冲突只查应用内（系统级冲突查不了，但注册失败这个布尔值拿得到，必须据此提示用户换一个）。插件安装／卸载只重启插件宿主、不重启应用，按 PLAN 4.9 执行。

---

### A1 技术架构与模块开发（2026-09-19）

| 编号 | 决策 | 理由 / 代价 | 落在 |
|---|---|---|---|
| D-59 | 每 Job 串行状态、generation/attempt/seq、单点重试和有界调度；每包 runtime 任一能力启用即保留；关闭与最小化区分 | R07。防止旧响应污染新任务、流式重复、重试倍增和禁用一个能力杀掉其他能力。代价：明确投影/任务所有权，强制重建 runtime 时全部在途调用都要失败 | ARCHITECTURE 3–6；DEV-PLAN F01/F04/F06；TEST-PLAN J |
| D-60 | 整树 PWS 用 MiB；默认与全 21 插件/全部窗口测冷暖与回落，至少 30 次，延迟按 P95 | R08。预算不能只算一个渲染进程；区分原生等待壳可见和内容可交互、本地检测与远端 RTT。目标仍待 M0 验证，未达标必须记录路线/预算决策 | ARCHITECTURE 11；F00/F19；PER |
| D-61 | 新增临时 selection-host，MTA UIA/IA2、独立 STA 快照，父进程硬 deadline 回收；取词前不激活窗口 | R09。COM 外部调用可能无法靠 CancellationToken 解除，不能拖住主消息泵或泄漏线程。代价：每次取词有启动成本，M0 实测；不是每插件独立进程，闲置不常驻 | ARCHITECTURE 4.1；F00/F08；SEL/C |
| D-62 | .NET 10 LTS/NativeAOT、Win11 x64、Vue3/TS/Vite 预编译；Contracts/Domain/Windows 边界；账户/实例/能力身份、options 与合并排序 | R10。画板缺运行时不能直接交付产品；源生成 COM 有边界，IA2 需显式 ABI；动态模型/牌组不能绑死宿主。代价：增加构建工具与 schema 生成，F00 验证实际版本，F07 增加合并排序入口 | ARCHITECTURE 1–3/6/9；F00–F07；CFG/UI |
| D-63 | 运行 DB/缓存移 LocalAppData，settings/secrets 有 journal；收藏拆本地条目/分目标 outbox/导出记录，写入结果不确定先核对；导入替换且重新授权 | R11。单 vocab_queue 无法表达部分成功；无远端事务不能保证恰好一次。代价：恢复与不确定状态更复杂，不支持可靠 lookup 的供应商需要人工核对；不新增独立收藏管理窗 | ARCHITECTURE 8；F02/F15/F17；DATA |
| D-64 | 包安全解压、签名身份延续、权限差异确认、暂存/激活/回滚；应用更新原始字节验签、可信根轮换、二进制与 DB 成对恢复 | R12。校验后直接覆盖无法处理断电和迁移失败；签名失败不等于无更新。代价：保留旧版本/一致备份和健康检查，自动检查不自动安装 | ARCHITECTURE 10；F16/F18；UPD |
| D-65 | DIP/PMv2、整体工作区约束；默认大小与最大化分离；离线按路径保留结果和本地服务；明确键盘/IME 行为 | R13。原固定尺寸与最大化按钮矛盾，全部折叠丢失有效反馈；D-51 划词仅热键，清理托盘旧矛盾。A1 新交互写 DESIGN 13，未伪称已同步第 17 版画板 | ARCHITECTURE 9；DESIGN 13；F03/F19；UI |

以上细化对应 20 个模块 F00–F19、21 个服务适配器工作项，不表示这些项目已创建或完成。首个可试用闭环为 F06，其他功能依赖明确后逐个交付；M0–M5 继续充当质量关卡。

## 2. 方案修正链

被后续决策改写过的条目，按时间排。**当前有效的永远是最后一条。**

| 原决策 | 修正 | 现状 |
|---|---|---|
| 托盘态内存 < 20 MB（选型期） | D-01 | ≤ 60 MB |
| 插件随窗口释放 WebView2（选型期） | D-01 | 插件宿主 WebView 常驻，只有界面 WebView 随窗释放 |
| 划词取词纯 UIA、绝不碰剪贴板（选型期） | D-02 → D-20 | 三级回落，第三级是默认关闭的开关 |
| 最初需求写的"先执行复制当前选中文本操作" | D-02 | 改为无损取词优先，结果一致但不动剪贴板 |
| 插件分声明式 + JS 两档（选型期） | D-05 | 只有 JS 一档 |
| D-06 一律要求填 key | D-22 | MyMemory 例外 |
| D-06 + D-22 | D-23 | Ollama 也例外；GLM 默认走免费模型 |
| 「主语言」同时表达界面语言与默认目标语言 | D-19 | 拆成两个独立概念 |
| 转写服务含「Windows 内置」 | D-04 | 删除，改为复用 AI 平台凭据 |
| 语言检测是宿主内置的一段逻辑 | D-21 | 升格为可替换的服务 |
| 插件包格式含 `.potext` / `.bobplugin`，写适配壳（D-05 时期） | D-24 | 只有 `.susuext`，没有适配壳 |
| `$secret.get` 把明文密钥传入插件，签名在插件里算 | D-25 | 占位符 + 宿主签名器，插件不可读 |
| 插件是 `main.js` 全局函数 + 注入 `require` / `__dirname` | D-26 | ES Module + Worker + 类型定义 |
| 「沙箱、进程隔离、版本管理三样必须有」 | D-28 | 源隔离 + 线程隔离 + 可强杀 |
| 托盘态 ≤ 60 MB 只计插件宿主 WebView 一项 | D-29 | 整棵进程树之和，固定成本单列 |
| 语音翻译流式转写、partial 结果实时回填 | D-31 | 录完一次性转写 |
| `settings.json` / `manifest.json` | D-32 | YAML |
| 取词第二级 Legacy MSAA | D-35 | IAccessible2 |
| 更新只校验 SHA-256 | D-34 | 清单 Ed25519 签名 + SHA-256 |
| D-01 插件宿主 WebView 常驻 | D-36 | 插件宿主是 AppContainer 子进程里的嵌入式引擎，不再有插件宿主 WebView |
| D-01 界面 WebView 随窗释放 | D-37 | 关窗保温，闲置后整体释放 |
| D-28 沙箱 iframe（源）+ module Worker（线程） | D-36 | AppContainer 子进程（进程）+ 每插件独立引擎运行时 |
| D-29 退路是轻量 JS 引擎、预算 ≤ 60 MB 一个数 | D-36 → D-37 | 主次对调：引擎是基线，WebView2 承载插件是退路；预算拆成闲置态 ≤ 25 MB、保温态 ≤ 60 MB |
| 插件按展开卡片按需加载、Worker 空闲 60 s 释放 | D-36 | 启用即加载，常驻不释放 |
| D-25 签名器只有 tencent-tc3 / aws-sigv4 / bearer | D-38 | 加 `digest` / `hmac` 两个原语 |
| D-05「全部内置服务都是插件、宿主零特例」、内置插件 24 个 | D-39 | 插件 22 个 + 宿主原生 Provider 2 个，两条实现路径 |
| `permissions` 含 `clipboard` | D-40 | 首版只有 `hosts` |
| D-32「写回时保留用户注释」 | D-41 | 值保留、用户注释不保留，注释由宿主再生成 |
| 解不开的音轨「给明确提示」 | D-42 | 提示指向商店的 Web Media Extensions / Media Feature Pack |
| D-16 触发规则「无空白且 ≤ 32 字符」 | D-45 | 只对单词形式触发，字符集可枚举 |
| D-22 / D-23 的「首启引导摆出两条免费路」、6.4「无可用服务时显示引导」 | D-46 | 不做首启引导与空态引导，入口置灰 |
| D-39 内置插件 22 个（生词本 3） | D-48 | 21 个（生词本 2），文件导出是宿主功能 |
| 模拟复制前写排除格式、OleGetClipboard 全格式快照 | D-52 | 有限格式物化、来源/序列号核对、尽力恢复；历史/云同步无法保证排除 |
| 二进制只用原始 body/multipart 与 file 响应 | D-53 | 增加宿主 JSON/Base64 变换及句柄生命周期 |
| 视频统一按约 1500 字符/20 条拼接翻译 | D-54 | 服务限制优先，字幕 ID 映射，single/items 两种适配 |
| ASR 平台统一返回时间码，有大小限制就 AAC | D-55 | 模型级 text/segments 与完整格式协商，WAV 基线，超限切片 |
| 密钥字符串占位符，任何 WebView 都不见明文 | D-56 | 显式控制字段、账户/origin 授权；可信设置页只暂存新输入，远端回显属信任边界 |
| D-36 无可写文件系统，引擎失陷后仍按插件隔离 | D-57 | 容器私有资源可写，同子进程为一个原生失陷域，宿主代理独立验证 |

---

## 3. 调研记录

### 3.1 能否在不污染剪贴板的前提下取词（2026-09-17）

> 历史调研保留。下文“排除格式配合全格式快照”的保证已被 D-52 修正：该标记不能约束外部程序的下一次复制，`OleGetClipboard` 不会自动物化快照。当前执行规则只看 PLAN 3.1。

**结论：大部分场景可以，严格意义上的全场景零污染做不到。**

- **UIA `TextPattern`** 覆盖面比预期广：Win32 edit/richedit（靠 MSAA→UIA 桥）、WPF / WinUI / UWP、Office、Windows Terminal、Qt 5.12+、Chromium 系（含 Chrome、Edge、Electron）全都支持 `GetSelection()`。
- **Chromium 懒启用无障碍树**：检测到 UIA 客户端才构建，对某个进程的首次查询会触发建树，有一次性延迟，建成后该进程常驻一份 a11y 开销。历史上还有过 `--force-renderer-accessibility` 相关的回归（Chrome 117）——所以不依赖命令行开关，只依赖"检测到客户端即启用"这条默认路径。
- **模拟复制并非无解**：`ExcludeClipboardContentFromMonitorProcessing` 这个剪贴板格式能让一次复制既不进 Win+V 历史也不同步到云剪贴板（密码管理器就是这么用的），配合 `OleGetClipboard` 全格式快照 + 还原，可以把污染压到用户基本不可见。
- **但压不到零**：延迟渲染（delayed rendering）的格式无法完美还原，第三方剪贴板管理器仍可能抓到那一瞬。**这就是 D-20 让这个开关默认关闭的理由**——"几乎不污染"终究不是"不污染"，要不要接受由用户决定。
- **UIPI 是硬边界**：未提权进程读不到提权窗口的内容，这一条开关打开也救不了，模拟 Ctrl+C 同样发不进提权窗口。

### 3.2 有没有免费的语言检测服务（2026-09-17）

**结论：有，而且在系统里**——Windows ELS（Extended Linguistic Services）的 Microsoft Language Detection，Windows 7 起自带，0 体积、0 费用、纯本地、离线可用。技术参数写在 PLAN 3.2。

否掉 CLD3 的理由：它要 2–3 MB 加一份 protobuf 依赖，而 ELS 做同一件事且零成本。云端检测也一概不用：既要 key 又要往返延迟。

### 3.3 免费翻译服务（2026-09-17）

**问题**：D-06 决定一律要填 key 之后，用户装完打开是一列全灰卡片。

**结论**：只有 **MyMemory** 一个真正零配置的选项。接口与额度写在 PLAN 1.3。

**为什么不选其他的**：

| 候选 | 不选的理由 |
|---|---|
| Microsoft Translator F0（每月 200 万字符） | 额度最大，但要注册 Azure 账号建资源拿 key——解决不了"装完就能用" |
| DeepL Free（每月 50 万字符） | 要 key，部分地区注册还要验证信用卡 |
| Google Cloud Translation（首 50 万字符免费） | 要 key + 结算账号 |
| Gemini／GLM-4-Flash 等 LLM 免费层 | 要 key；它们已经在 AI 平台清单里，不重复一份 |
| Google／Bing 的非官方免费接口 | 合规与稳定性风险，D-06 已否决 |
| LibreTranslate 自建 | 要用户自己起服务，不是开箱即用 |
| Argos／OPUS-MT 本地模型 | 单个中英模型就约 100 MB，与体积目标冲突 |

#### 对照：Bob 的 27 个文本翻译服务（2026-09-17 实查）

**里面没有第二个"不用 key"的选项**——唯一两个不用 key 的，一个是 macOS 独有、一个是非官方接口，Windows 都拿不到。

① **完全免费、无限，但要注册拿 key**

| 服务 | 免费额度 | 对我们 |
|---|---|---|
| 智谱 GLM `GLM-4-Flash` | 无限免费 | 已在清单，D-23 将其设为默认模型 |
| Gemini | 完全免费 | 已在清单 |
| Groq | 完全免费 | 候选，首版不进 |
| 硅基流动 `Qwen2.5-7B` 等 | 无限免费 | 候选，首版不进 |
| 混元 `hunyuan-lite`、文心一言部分模型 | 免费 | 不进 |

② **完全免费、本地、零 key**：Ollama —— 跑在用户自己机器上，插件只是往 `127.0.0.1:11434` 发 HTTP，我们这边体积为 0；代价是用户自己装 Ollama 和几 GB 的模型。D-23 采纳。

③ **大额度免费，要 key（专业翻译引擎）**

| 服务 | 免费额度 | 超出后 |
|---|---|---|
| 腾讯翻译君 | 每月 500 万字符 | 58 元/100 万字符 |
| 小牛翻译 | 每日 20 万字符 | 500 元/1000 万字符 |
| 火山翻译 | 每月 200 万字符 | 49 元/100 万字符 |
| Microsoft 翻译 | 每月 200 万字符 | 10 美元/100 万字符 |
| Amazon 翻译 | 每月 200 万字符（AWS 免费套餐，仅前 12 个月） | 15 美元/100 万字符 |
| 阿里翻译 | 每月 100 万字符 | 50 元/100 万字符 |
| 百度翻译 | 每月 100 万字符 | 49 元/100 万字符 |
| Google 翻译 | 每月 50 万字符 | 20 美元/100 万字符 |
| DeepL 翻译 | 每月 50 万字符 | 4.99 欧元/月 + 20 欧元/100 万字符 |

④ **无免费额度**：有道翻译、彩云小译、OpenAI、Azure OpenAI、DeepSeek、通义千问、豆包、零一万物、Kimi。

⑤ **我们拿不到的两个**

| 服务 | 为什么拿不到 |
|---|---|
| 系统翻译 | macOS 12.3.1+ 独有。Windows 没有对等物，没有公开的本地翻译 API。这是 Bob 结构性地比我们多一个免费项的原因 |
| 金山词霸 | "无限免费"但 Bob 自己注明"稳定性不保证"= 非官方接口，D-06 已否决这一类 |

另有 **简明英汉词典**（无限免费、可离线）——本地词典数据，不是翻译服务，但对词典形态有价值，可作为按需下载的本地词典备选。

**三点启示**：MyMemory 仍是唯一零配置选择但撑不起日常使用；真正的免费解法是"免费但要注册"这一档（国内 `GLM-4-Flash`、海外 Gemini），质量远好于 MyMemory；腾讯翻译君额度最大且与腾讯 OCR 共用凭据。后两条落成 D-23。

### 3.4 Pot 与 Bob 的插件契约（2026-09-18 实查）

**问题**：PLAN 4.4 原本要兼容 `.potext` 与 `.bobplugin`。兼容层到底要迁就什么？

**Bob**（`info.json` + `main.js` + `icon.png`）

- 入口是全局函数：`translate(query, completion)`、`supportLanguages()`、可选 `pluginValidate(completion)`、`pluginTimeoutInterval()`（30–300 s）。回调式；1.8 起 `query` 里才有 `cancelSignal` 与 `onStream`，老插件没有。
- `$http.request / get / post / streamRequest`：自家的请求形状（`header`、`handler`、`timeout` 秒、`files` 上传），二进制用 `$data` 类型。
- OCR 的 `query.image` 是 `$data`，TTS 返回 url 或 base64——二进制直接进 JS。
- `info.json` 的 `options` 只有 `text` 与 `menu` 两种控件；没有权限声明；`appcast` 是自家更新源。
- 错误是 `serviceError { type, message, troubleshootingLink }`，`type` 只有少数几种。

**Pot**（`info.json` + `main.js`）

- 入口是全局 `async translate(text, from, to, options)`；OCR 是 `recognize(base64, lang, options)`，图片以 base64 字符串传入。
- `options.utils` 带 `tauriFetch` / `http`、`readBinaryFile`、`readTextFile`、`cacheDir`、`pluginDir`、`osType`、`run`（执行本机命令）、`CryptoJS`、`Database`。
- 配置声明 `needs` 只有 `input` 与 `select`；没有权限声明；返回值是字符串，流式用 `setResult()`。

**结论**：

1. 两家都要求明文密钥进插件（自己拼 Authorization、自己算签名），与「密钥永不进插件」互斥。
2. 两家的 OCR / TTS 都把二进制送进 JS，与 PLAN 4.2 第 6 条互斥。
3. Pot 的 `run` 与文件读取是宿主必须砍掉的能力，砍掉后依赖它们的插件也不能用。
4. 两家都没有权限声明，域名白名单只能靠适配壳猜或运行时弹窗。
5. 老 Bob 插件没有取消，PLAN 4.2 第 1 条对它们无法强制。

兼容的实际收益只剩 translate 类插件，代价是三条加载路径、两套配置与语言码转换、回调桥、CommonJS 垫片。D-24 据此去掉兼容。

### 3.5 WebView2 里沙箱 iframe 的进程隔离（2026-09-18）

- Chromium 127 起，桌面平台把 `sandbox` 且无 `allow-same-origin` 的 iframe 移入与父页面分离的进程；WebView2 运行时 132 起默认启用（`IsolateSandboxedIframes`）。
- 分离的是「与父页面」，不是「彼此之间」：同站点的沙箱 iframe 共用一个沙箱进程。所以插件之间仍需 Worker 做线程隔离（D-28）。
- 连带影响：拦截进程外 iframe 的请求必须用带 `RequestSourceKinds` 的 `AddWebResourceRequestedFilter` 变体，否则拦不到；内存预算要多算一个渲染进程（PLAN 2.4）。
- 若要每插件一个进程，可用 `SetVirtualHostNameToFolderMapping` 给每个插件独立主机名，让站点隔离拆进程；代价每进程约 10–20 MB，与 60 MB 预算冲突，首版不做（PLAN 1.5）。
- **D-36 之后这一节只对退路方案有效**。

### 3.6 嵌入式引擎与 WebView2 承载插件的对比（2026-09-18 第三次评审）

**问题**：PLAN 2.4 原本把嵌入式引擎当作超预算时的退路。评审发现主次应当对调。

**WebView2 承载插件的三个结构性问题**：

1. **隔离与安全建立在 Chromium 行为上**。沙箱 iframe 与父页面分进程要求 WebView2 运行时 ≥ 132；进程外 iframe 的出站请求只有带 `RequestSourceKinds` 的 `AddWebResourceRequestedFilter` 拦得到；隐藏页面的定时器节流要靠 `--disable-background-timer-throttling` 这类启动参数关。这些都是 Evergreen 运行时随时可能改的行为，应用无法控制，企业固定版本的环境还可能落后。
2. **「网络只经宿主」是代码约定而非结构保证**：删掉 `fetch` / `XMLHttpRequest` / `WebSocket` 加宿主侧整体拦截，两层缺一不可，任何一层出纰漏就是插件直连网络。
3. **常驻成本买错了东西**。WebView2 进程组固定成本 25–35 MB 是为「插件热」付的，但每次热键仍要冷建界面 WebView，首帧只能定 300 ms。

**嵌入式引擎方案**：

- 引擎本身没有 I/O 原语，插件运行时里网络、文件、进程这些能力从一开始就不存在。
- 跑在 AppContainer 子进程里，不授予网络能力、插件目录只读、无可写文件系统：即使引擎有内存安全漏洞被利用，攻击者拿到的也是一个没有网络、没有密钥的进程。这比 V8 沙箱渲染进程弱一些（没有 Chromium 那层沙箱逃逸难度），但对本项目的威胁模型足够，且不依赖 Evergreen 行为。
- 不做进程内引擎：进程内引擎一旦被攻破就是主进程，DPAPI 解出的密钥就在同一片内存里。子进程多出的是一层 IPC，宿主 API 本来就是消息形状。
- 子进程用 `susu.exe --plugin-host` 同一个二进制的第二种模式，不多发一个文件。
- 一个运行时几百 KB，启用即加载、常驻不释放，去掉了 Worker 的空闲释放与按展开卡片按需创建两套生命周期逻辑。

**引擎候选**：

| | QuickJS-NG | Jint |
|---|---|---|
| 形态 | C 库，约 1 MB，经 P/Invoke 接入，绑定自写 | 纯 C#，编入 AOT 镜像，约 2–3 MB |
| 语言支持 | ES2023，ES Module、async generator 齐 | ES2023 大部分，async / generator 有 |
| 资源控制 | `JS_SetMemoryLimit`、`JS_SetInterruptHandler`、每 `JSRuntime` 独立堆 | `LimitMemory`、`TimeoutInterval`、`MaxStatements` |
| AOT | 只是 P/Invoke，天然兼容 | 官方称支持裁剪，NativeAOT 需 M0 核 |
| 性能 | 解释器，够用 | 更慢，够用与否 M0 看数据 |
| 风险 | 原生依赖、绑定要自己维护 | 少一个原生依赖，但 AOT 与语言覆盖面待核 |

基线 QuickJS-NG，Jint 作 M0 对照。

**代价如实记**：

- 没有 DevTools 调插件。首版生态是 21 个内置插件，`$log` 加设置页里的插件日志面板够用；第三方作者的调试体验以后补。
- `AbortSignal`、`TextEncoder`、`URL` 这类 Web API 引擎没有，宿主补齐并在 `susu-plugin.d.ts` 里写死清单（PLAN 4.3）。
- `susu-plugin test` 不能再用 Node 模拟运行时，要驱动真实子进程。这一条其实是改进：旧方案 Node 与 V8 Worker 之间同样有差异。

**对既有结论的修正**：5.1 与 5.2 里「WebView2 预装 = V8 白拿」的论断，只在不算常驻内存时成立；算上每天常驻 30 MB 的固定成本，1–3 MB 的引擎反而更便宜。第 4 节否决「进程外插件」针对的是每插件一个进程与热插拔，与 D-36 的单个子进程不是同一件事。

**内存估算**（2.4 口径，M0 实测为准）：

| 方案 | 闲置态 | 保温态 / 活跃期 |
|---|---|---|
| WebView2 承载插件（旧） | 50–60 MB | 55–70 MB |
| 引擎子进程 + 界面保温（D-36 / D-37） | 15–22 MB | 45–60 MB |

### 3.7 首版服务清单的鉴权方式逐项核对（2026-09-18 第三次评审）

**问题**：D-25 规定密钥不进插件、签名由宿主做，签名器只有三个命名方案。首版清单里的每一项能不能用这三个方案加占位符写出来？

| 类别 | 服务 | 鉴权方式 | 需要的宿主机制 |
|---|---|---|---|
| 翻译 | MyMemory | 无；`de` 参数填邮箱涨额度 | 无 |
| 翻译 | 腾讯翻译君 | TC3-HMAC-SHA256 | `tencent-tc3` |
| 翻译 | Google Cloud Translation | query `key=` | 占位符 |
| 翻译 | Microsoft Translator | 头 `Ocp-Apim-Subscription-Key` + `Ocp-Apim-Subscription-Region` | 占位符 |
| 翻译 | DeepL | 头 `Authorization: DeepL-Auth-Key <key>` | 占位符 |
| 翻译 | Amazon Translate | SigV4 | `aws-sigv4` |
| 翻译 | **有道** | `sign = sha256(appKey + input + salt + curtime + appSecret)`，`input` 是 q 的截断规则 | **三个命名方案都不行**，需要 `digest` |
| AI | OpenAI | `Authorization: Bearer` | `bearer` |
| AI | GLM | `Authorization: Bearer` | `bearer` |
| AI | Gemini | query `key=` 或头 `x-goog-api-key` | 占位符 |
| AI | Claude | 头 `x-api-key` | 占位符 |
| AI | Ollama | 无 | 无 |
| OCR | 腾讯 OCR | TC3 | `tencent-tc3` |
| OCR | Simple LaTeX | 头 `token`（用户级 UAT） | 占位符 |
| TTS | Microsoft（Azure Speech） | 头 `Ocp-Apim-Subscription-Key` | 占位符 |
| TTS | Google TTS | query `key=` | 占位符 |
| TTS | 腾讯 TTS | TC3 | `tencent-tc3` |
| TTS | Windows 内置 | 宿主原生 Provider | 不经插件 |
| ASR | OpenAI | `bearer`，复用 AI 平台凭据 | `bearer` |
| ASR | Gemini | query `key=`，复用 AI 平台凭据 | 占位符 |
| 生词本 | AnkiConnect | 无（可选 `key` 字段，占位符） | 占位符 |
| 生词本 | 欧路词典 | 头 `Authorization` token | 占位符 |
| 生词本 | Anki `.apkg` | 本地文件导出，无网络 | 宿主功能，不是插件（D-48） |
| 检测 | Windows 内置（ELS） | 宿主原生 Provider | 不经插件 |

**结论**：唯一的缺口是有道。加 `digest`（密钥拼进被哈希串）与 `hmac`（密钥作 HMAC key）两个原语后，首版清单全部覆盖，后续候选里的百度（`md5(appid+q+salt+key)`）、阿里（HMAC-SHA1）、火山（HMAC-SHA256 派生签名）也都落在这两个原语加插件侧拼串之内。落成 D-38。

顺带发现的两处矛盾一并处理：SAPI TTS 与 ELS 检测在插件运行时里做不了，却被算进「24 个内置插件」（D-39）；`permissions.clipboard` 在首版没有任何消费者（D-40）。

---

## 4. 已否决的方案

保留结论，不保留推演过程。

| 方案 | 否决理由 |
|---|---|
| **Electron + TypeScript** | 剪贴板没有变更事件只能轮询（闲置 CPU 不为 0）；划词取词只能模拟 `Ctrl+C`，污染用户剪贴板。包体 150 MB+、内存基线 200 MB 起。它唯一的硬优势（UI 与画板复用）被"WebView2 直接加载 HTML"完全抹平 |
| **.NET + WPF + WebView2** | 磁盘 75–150 MB 且瘦不下来（见 4.1）。插件要写 C#，第三方门槛高于 JS。进程内插件没有崩溃隔离 |
| **WinUI 3** | `DESIGN.md` 是纯白扁平、不用 Mica，WinUI 3 的原生观感优势用不上；未打包场景坑多、多窗口管理不如 WPF 稳 |
| **Tauri 2 + Rust** | Tauri 的 "plugin" 是编译期 Rust crate，用户装不了——插件层必须另造一套，Tauri 只贡献窗口壳；WASM 沙箱里做不了热键、录音、截图。Rust 写 COM/UIAutomation/WASAPI 的人力成本显著高于 C# |
| **每插件一个进程** | "插件不需要热插拔"作废了它的头号卖点"杀进程 = 100% 干净卸载"，且每进程 10–20 MB 与预算冲突。**D-36 采纳的是另一件事**：全部插件共一个 AppContainer 子进程，理由是内存与 OS 级隔离，不是热插拔 |
| **WebView2 常驻承载插件（沙箱 iframe + module Worker）** | 未否决，降为退路。见 D-36 与 3.6：隔离建立在 Evergreen 行为上、网络封锁是代码约定、常驻成本买到的是插件热而非界面热 |
| **进程内嵌入式引擎** | 引擎被攻破即主进程，DPAPI 解出的密钥在同一片内存里。子进程只多一层 IPC，见 3.6 |
| **声明式 + JS 两档插件** | 两档等于两套加载／配置／错误路径，而声明式很快会被某家的签名算法逼成 JS（D-05） |
| **CLD3 本地语言检测** | ELS 零体积做到同一件事（D-15） |
| **本地 ASR（whisper.cpp + 模型 ≈ 190 MB）** | 换不来离线可用性——翻译引擎全是云服务，离线时识别出文字也翻译不了，只是把失败点从第一步挪到第二步。留作可选插件，不进初装包 |
| **兼容 Pot / Bob 插件** | 见 3.4 与 D-24。实际收益只剩 translate 类插件，代价是三条加载路径与一整套垫片，且与密钥、二进制、权限三条原则互斥 |
| **流式语音转写** | D-31。流式与批量两套形状，默认免费路径上流式不可用；录完一次性转写让语音翻译与视频转写共用一条路 |
| **运行时域名授权弹窗** | 与「权限按 manifest 授予」矛盾；去掉外来插件后没有需要它的场景（D-33） |
| **UIA LegacyIAccessiblePattern 做取词第二级** | 拿不到选区文本，见 D-35 |
| **打包 ffmpeg** | Media Foundation 是系统组件，0 体积、0 许可风险。理由是"平台已自带"，不是"ffmpeg 太大"——Qt 裁剪过的 LGPL shared 构建只有 17.9 MB。此条在跨端时会重新变成议题 |

### 4.1 「要 C# DLL 插件」与「体积瘦身」互斥

这不是可调优的参数，记录在此以免被重新提起：

1. WPF 至今不能安全裁剪（`PublishTrimmed` 会抛未处理异常，XAML 的反射让 trimmer 无法静态分析）。
2. 更根本：**运行时加载插件的应用本身就不该裁剪**——trimmer 看不见插件的依赖，按主程序调用图裁掉的框架代码，插件一碰就炸，而且是发布之后、装了某个第三方插件才炸。
3. NativeAOT 不支持 `AssemblyLoadContext` / 运行时加载程序集。

本方案走的是这条互斥线的另一端：**不要 C# DLL 插件，因此可以 NativeAOT**。同期项目 DeskBox（.NET 10 + WinUI 3 + `PublishAot` + `PublishTrimmed`）能出货，正是因为它同样不做运行时加载 C# 插件——它印证这条互斥，不是反例。

### 4.2 Tauri 的流行不构成重新评估的理由

2025 年中之后新建的热门桌面项目里 Tauri 2 已是事实默认值，但它火的是"造壳快"（官方插件矩阵包办托盘/单实例/更新/剪贴板），解决的不是苏苏的问题。抽样验证：这批项目中**没有一个提供用户可自由装卸的第三方功能插件生态**——dbx 的"90+ 数据库"是编译进去的 driver，不是用户装的插件。**用户级插件生态必须自建，框架不会送。**

反倒是 vicinae（C++23 + Qt6 原生宿主 + React/TS 扩展 + 直接兼容 Raycast 商店）证明了本方案的架构成立：原生宿主 + 脚本插件 + 兼容既有生态，在非 macOS、脚本引擎非预装的环境下同样跑得通。

### 4.3 若未来跨端，先读这四条

**跨端一旦从"以后可能"变成"确定要做"，PLAN 第 2 节的架构即作废，要改选 C++/Qt 或 Rust 原生 UI。**

1. **C# 是唯一会因跨端从最优掉到最差的选择**：macOS 绑定要手调 `objc_msgSend`（无官方绑定，与 NativeAOT 组合有坑），Linux 要手写 GTK/X11 P/Invoke。对照：Rust 有 windows-rs / objc2 / gtk-rs 三套，C++ 有 Qt 一套全包。
2. **macOS 没有 WASAPI loopback 的对等物**。可行路径是 ScreenCaptureKit（13+）或 CoreAudio process taps（14.4+），API 形状完全不同——纯新写，不是移植。
3. **Linux 的问题不是体积，是 Wayland**：全局热键、截图选区、划词取词三项要么走 portal 要么做不了；Flatpak 沙箱直接挡住热键和取词。
4. **HTML 跨端是幻觉**：Windows 的 WebView2 是 Chromium，mac/Linux 是 WebKit，1px 亚像素取整三端对不齐；`Segoe UI Variable` / 微软雅黑在另两端一个都没有，而 `DESIGN.md` 的字阶是照 Segoe UI 调出来的。Qt Widgets 自绘三端像素级一致，代价是画板重写成 C++ 组件——**那是一次性的，WebView 路线是永远在修渲染差异**。

保证金：系统能力全部走抽象接口、插件契约不泄露 Windows 概念、设计 token 化。这三条即使永不跨端本身也是好设计，所以现在就做。

---

## 5. 实测数据

### 5.1 Manggo 1.0.1（Windows，2026-09 解包实测）

同品类最接近的竞品，作者 Pylogmon 即 Pot 的作者。闭源商业产品。**磁盘 121 MB / 安装包 55 MB。**

| 层 | 技术 | | 组成 | 体积 |
|---|---|---|---|---|
| 语言 | C++（MSVC / C++-WinRT） | | Qt 6.11.1 框架（7 dll + 14 插件） | 34.5 MB |
| GUI | Qt 6.11.1 Qt Widgets（非 QML） | | 主程序（含静态 OpenCV） | 23.2 MB |
| 本地 OCR | ONNX Runtime + PaddleOCR PP-OCRv6_small | | ONNX Runtime + 模型 | 44.4 MB |
| 音视频 | FFmpeg 7.x | | FFmpeg | 17.9 MB |
| 插件运行时 | Bun，首次使用时按需下载 | | | |

三条结论：

1. **Qt 路线的地板是 ~58 MB 磁盘**（去掉本地 OCR 和 FFmpeg）——我们目标的两三倍。那 34.5 MB 省不掉：闭源商业产品走 LGPL 合规必须动态链接。
2. **Bun 方案在我们的约束下更差**：它按需下载运行时看着省体积，实际是为跨平台付的税（mac/Linux 没有 WebView2，只能自带统一运行时）。Win11-only 下 WebView2 预装 = V8 预装，零字节、零下载、零离线失败。**此判断只在单平台下成立。** **D-36 修正**：「白拿」只在不算常驻内存时成立，见 3.6；界面仍然白拿 WebView2，插件不再。
3. **原生控件路线可行，但要付整套设计系统的钱**：Manggo 手写了 `AppButton` / `AppCard` / `AppSwitch` / `AppNavbar` / `AppTitleBar` + `ThemeManager`。`DESIGN.md` 的发丝线、三级灰阶、四档圆角要在 C++ 里全部重实现，19 张画板从可交付资产降级成参考图。

**它的插件机制**（曾打算参照它的多格式加载，D-24 后不再）：宿主 C++ 通过 stdin/stdout JSON-lines IPC 驱动 Bun 子进程；Bob 兼容层往 `globalThis` 注入 `$http`、`Body`、`require`、`__dirname` 并 `process.chdir(pluginDir)`，用完还原；`loadPotPlugin` / `loadBobPlugin` / `loadNativePlugin` 三条加载路径并存，文件对话框收 `*.mplugin *.potext *.bobplugin *.zip`。

**复现方式**：`7z x Manggo-1.0.1-Windows-AMD64.exe`（NSIS 包），再对 `bin/Manggo.exe` 跑 `strings` 看 RTTI 与导入表。全部数字来自本地解包，非官方公布。

### 5.2 Bob（macOS）

Bob 1.20.0：12.7 MB 下载 / ~25–35 MB 磁盘。**别拿它的体积当硬指标**——它小是因为 AppKit 和 Swift runtime 都在系统里，且它没有视频转写、没有系统音频录制，我们要带的这两项它一分没付。

另一个成本参照：Pot 的宿主侧插件实现约 120 行。

值得抄的是插件机制：`.bobplugin` 压缩包里就三个文件（`main.js` + `info.json` + `icon.png`），跑在宿主提供的 JS 环境里，注入 `$http` 之类的受限 API。社区靠这个机制长出了 OpenAI、DeepL、Gemini、Ollama 等一大批第三方插件——**它零成本拿到脚本引擎，因为 macOS 预装 JavaScriptCore**。Windows 上的对等物本是 WebView2 预装 = V8 预装，但常驻内存的价格让它不再划算，D-36 改为自带 1–3 MB 的嵌入式引擎（3.6）。

### 5.3 ASR 引擎与模型体积

供日后评估可选的本地 ASR 插件。

| 形态 | 体积 | | 模型 | 体积 |
|---|---|---|---|---|
| 云端 ASR（纯 HTTP） | < 50 KB | | `tiny-q5_1` | 31 MB |
| whisper.cpp CPU 版 | 8.2 MB | | `base-q5_1`（中文勉强可用） | 57 MB |
| whisper.cpp + BLAS | 20.4 MB | | `small-q5_1`（中文实用下限） | 181 MB |
| whisper.cpp + CUDA | 260–643 MB（绝不打包） | | `large-v3-turbo-q5_0` | 547 MB |

本地转写插件的现实配置：引擎 8 MB + `small-q5_1` 181 MB ≈ 190 MB。模型应在插件内再做一层按需下载（装插件时让用户选档位），这样"插件"本身还是 10 MB 级别。

### 5.4 体积目标的现实性对照

同期两个 Tauri 项目（同为原生壳 + 系统 WebView 的架构）：dbx 用 20 MB 装下 90+ 数据库驱动，terax 用 7 MB 装下终端 + AI 工作区。PLAN 第 9 节的 15–25 MB 不是乐观估计。

---

## 6. 参考来源

**调研（2026-09-17）**

- [MappingRecognizeText 函数](https://learn.microsoft.com/en-us/windows/win32/api/elscore/nf-elscore-mappingrecognizetext) · [Microsoft Language Detection（含服务 GUID）](https://learn.microsoft.com/en-us/windows/win32/intl/microsoft-language-detection) · [About Extended Linguistic Services](https://learn.microsoft.com/en-us/windows/win32/intl/about-extended-linguistic-services)
- [TextPattern.GetSelection](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.textpattern.getselection) · [UI Automation TextPattern 概览](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview) · [Chromium 无障碍概览（懒启用机制）](https://chromium.googlesource.com/chromium/src/+/main/docs/accessibility/overview.md) · [Chrome 117 的 --force-renderer-accessibility 回归](https://issues.chromium.org/issues/40072866)
- [Clipboard Formats（剪贴板历史与云剪贴板排除格式）](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-formats)
- [MyMemory API 技术规格](https://mymemory.translated.net/doc/spec.php) · [MyMemory 用量限制](https://mymemory.translated.net/doc/usagelimits.php)
- [Bob 服务与免费额度清单](https://bobtranslate.com/guide/advance/service.html)（3.3 的对照表实查自此）

**插件机制**
[Bob 插件文档](https://bobtranslate.com/guide/advance/plugin.html) · [Bob $http API](https://bobtranslate.com/plugin/api/http.html) · [Bob info.json](https://bobtranslate.com/plugin/quickstart/info.html) · [Bob 翻译插件契约](https://bobtranslate.com/plugin/quickstart/translate.html) · [Bob OCR 插件契约](https://bobtranslate.com/plugin/quickstart/ocr.html) · [pot-app 翻译插件模板](https://github.com/pot-app/pot-app-translate-plugin-template) · [pot-app OCR 插件模板](https://github.com/pot-app/pot-app-recognize-plugin-template) · [bobplugin GitHub topic](https://github.com/topics/bobplugin) · [pot-app 插件列表](https://github.com/pot-app/pot-app-plugin-list) · [vicinae](https://github.com/vicinaehq/vicinae)

**.NET 与体积约束**
[.NET assembly unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability) · [WPF 不能安全裁剪 dotnet/wpf#4216](https://github.com/dotnet/wpf/issues/4216)

**WebView2 进程模型**
[WebView2 公告：默认启用 IsolateSandboxedIframes](https://github.com/MicrosoftEdge/WebView2Announcements/issues/99) · [Chromium 进程模型与站点隔离](https://chromium.googlesource.com/chromium/src/+/main/docs/process_model_and_site_isolation.md) · [WebView2 进程模型](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-model) · [SetVirtualHostNameToFolderMapping](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping)

**Windows 平台能力**
[Win32 Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording) · [WebView2 Evergreen 预装于 Windows 11](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/evergreen-vs-fixed-version) · [Windows 11 语音识别需按语言装组件](https://learn.microsoft.com/en-us/answers/questions/5785855/speech-recognition-app-missing-on-windows-11) · [Windows Speech Recognition 弃用](https://en.wikipedia.org/wiki/Windows_Speech_Recognition)

**体积实测来源**
[ripperhe/Bob releases](https://github.com/ripperhe/Bob/releases) · [whisper.cpp releases](https://github.com/ggml-org/whisper.cpp/releases) · [whisper.cpp 模型仓库](https://huggingface.co/ggerganov/whisper.cpp) · [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds/releases)

**第四版六项修订的官方依据（文档核查，不是服务实测）**

- D-52：[EmptyClipboard 会清空当前内容](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-emptyclipboard)、[OleGetClipboard 返回 IDataObject](https://learn.microsoft.com/en-us/windows/win32/api/ole2/nf-ole2-olegetclipboard)。结合上述历史排除格式文档，不能由预写标记推导外部复制受保护。
- D-53：[Google TTS 的 JSON/Base64 响应](https://docs.cloud.google.com/text-to-speech/docs/create-audio)、[腾讯 TTS Audio 字段](https://cloud.tencent.cn/document/product/1073/37995)、[腾讯 OCR 的 ImageBase64 输入](https://cloud.tencent.com/document/product/866/116115)。
- D-54：[MyMemory q 最多 500 UTF-8 字节](https://mymemory.translated.net/doc/spec.php)。
- D-55：[OpenAI 文件转写的模型、格式与时间码](https://developers.openai.com/api/docs/guides/speech-to-text)、[Gemini 音频输入及整包大小限制](https://ai.google.dev/gemini-api/docs/audio)、[Gemini 2.5 Flash 官方模型卡](https://modelcards.withgoogle.com/assets/documents/gemini-2.5-flash.pdf)、[Gemini 模型退役表](https://ai.google.dev/gemini-api/docs/deprecations)。模型基线在开发和发布前须真实复核；Gemini 仅文本是首版保守产品边界，并非宣称平台无法生成时间标记。
- D-56：[WebView2 origin、消息参数与导航安全要求](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security)。显式引用和凭据绑定是本项目的设计选择。
- D-57：[AppContainer 的应用私有数据/注册表与未打包应用生命周期](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-for-legacy-applications-)。同进程原生失陷边界是本项目对现有进程拓扑的约束分析。
