# 阶段 5 测试报告（v0.5.0-mvp）

| 项目 | 内容 |
| --- | --- |
| 阶段 | 阶段 5：测试、打包与部署 |
| 测试日期 | 2026-09-30（北京时间 08:46–09:25 实机测试窗口） |
| 被测对象 | `src/DeepSeekBalanceWidget`（阶段 4 验收通过版，Release 构建） |
| 测试环境 | Windows 11 Pro（build 26200）x64，3200×2000 @200% DPI，桌面可交互；.NET 8 SDK 8.0.425（私有目录） |
| API Key 状态 | **无真实 Key（用户已作废，未请求、未编造）**——错误分支用假密钥 `sk-probe-fake-0000` 走真实 401 端到端，测试后已删除 |
| 证据目录 | `release/evidence/`（截图 38 张，文件名前缀 t01–t11 对应下表各项） |
| 结论速览 | **12/12 项通过**（其中第 4 项额外捕获到 09:00:00 真实峰谷切换；第 5 项为 401 代测说明；第 7 项 Win10 不可测如实标注） |

---

## 1. 测试清单逐项结论

### 项 1：启动 ≤5 秒托盘出现且颜色正确 —— **通过**

| 次数 | 场景 | 进程创建 → 日志"托盘就绪" |
| --- | --- | --- |
| #1 | 冷启动（当日首启） | 08:46:27.751 → 08:46:28.896 = **1145 ms** |
| #2 | 退出后重启（热） | 09:19:30.283 → 09:19:30.752 = **469 ms** |
| #3 | SCD 单文件·干净目录冷启动（含首启解压） | 09:21:21.727 → 09:21:22.703 = **976 ms** |

- 三次均远小于 5s 上限；SCD 形态含 5 个 WPF 原生库解压仍仅 976 ms（对照探针 D 基线 909–1338 ms，一致）。
- 颜色与当时真实状态一致：08:46 启动处于清晨空闲（00:00–09:00）→ 深色主题空闲变体 `idle-dark.ico`（填充 #46D27F）；09:19 重启处于上午高峰 → `peak-dark.ico`（填充 #FF9C4A）。tooltip 同步"空闲时段/高峰时段"。
- 证据：日志行（sess:23aeed49/120c01a5/71533780 "托盘就绪 IsCreated=True tooltip=…"）；溢出弹层截图 `t01-tray-flyout-idle-dark*.png` 像素采样 **#46D27F 填充 + #17994E 圆环 = A1 深色空闲精确值**。

### 项 2：左键点击托盘 → 面板 ≤300ms 弹出 —— **通过**

- 方法：真实鼠标点击溢出弹层内托盘图标，Win32 轮询（≤5–20ms 间隔）面板窗口 `IsWindowVisible`。
- 数据（6 次）：冷启首样 **141 ms**；随后 5 次 min/mean/max = **47 / 56.4 / 62 ms**。
- 无 Key 面板余额区显示"请先在设置中配置 API Key"——按 A1 §5-1 约定 1 这本身就是正确显示（余额数字位置不显示缓存值），如实记录（`t03-panel-countdown-A-idle.png`、`t08-panel-peak-dark.png`）。

### 项 3：倒计时每秒更新、误差 <1 秒 —— **通过**

- 精确 60.0s 间隔双截图（E2 08:56:29.932 / F2 08:57:30.008）：
  - 倒计时差值 **03:31 → 02:31 = 恰 60s**（与截图间隔 60.076s 差 0.076s，<1s）；
  - 每帧"截图时刻 + 倒计时"= 09:00:00.9 / 09:00:01.0 ≈ 下一切换点 09:00:00（±1s 内）；
  - 另有 56s 间隔对（C/D）与 20s 级面板实拍交叉印证逐秒递减。
- 证据：`t03-countdown-E2/F2-crop-idle.png`（倒计时清晰可读）。

### 项 4：峰谷状态切换同步更新 —— **通过（真实切换 + Fake 时钟集成双证据）**

计划中"真实切换点不可达"，实际当天 **09:00:00 真实切换可达并被完整捕获**（超出计划的 bonus）：

- **真实切换（生产环境）**：日志 `09:00:00.023 状态翻转沿：Idle → Peak（Degraded=False, 时段=上午高峰）tick#811`，18ms 内 `托盘图标已重建 → 橙(高峰)`；面板胶囊"空闲时段"(绿)→"高峰时段"(橙)，倒计时目标 09:00→12:00（`02:58:49`），进度条由 ~99% 重置为 ~0%。截图 `t04-flip-before/after-crop.png`、托盘橙图标 `t04-tray-flyout-peak*.png`（像素 **#FF9C4A 填充 + #E4720B 环，绿色像素 0 命中**）。
- **Fake 时钟集成测试**（`tests/DeepSeekBalanceWidget.IntegrationTests`，xUnit net8.0-windows，加入 sln）：FakeTimeProvider + 真实 Scheduler + 真实 PeakEngine + 真实 TrayController + PanelViewModel，跨北京 12:00:00 每 tick +1s 步进，断言：StateChanged 恰 **1** 次（Peak→Idle，无抖动）；托盘 CurrentState 峰→谷、tooltip 重建为"空闲时段"；4 枚 ICO 变体中心像素 = A1 精确色值（#E4720B/#FF9C4A/#17994E/#46D27F）；胶囊翻转 + 倒计时前缀改指高峰；快照 NextSwitch 12:00→**14:00**；进度条填充 99.9x%→0.0x%（区间重置）。**该工程 9/9 用例全绿（Debug 与 Release 双跑），可作阶段 6 CI 种子。**

### 项 5：错误分支端到端（代测"断网显示错误提示、不显示过期余额"） —— **通过（401 代测）**

- 流程实录（全程 UI 自动化 + 真实服务端）：设置窗口写入假密钥 → `设置：API Key 已保存（长度 54，凭据 Win32=0）`（长度 54 = 重试累计输入，纯假密钥无敏感性）→ 手动刷新 → `余额刷新：HTTP 401（Key 无效）`（真实 api.deepseek.com 服务端返回，242ms）→ `status=Unauthorized` → **面板显示"API Key 无效，请检查设置"，不显示任何余额数字**（截图 `t05-panel-401-crop.png`：胶囊/倒计时/进度条照常，余额区为错误提示）→ 退避序列真实触发 **1min→5min→15min→15min**（日志）→ 设置窗口"清除 Key" → `设置：API Key 已清除` → 面板恢复"请先在设置中配置 API Key"（`t05-panel-after-clear-crop.png`）。
- **代测说明（如实）**：本项原口径为"断网显示错误提示、不显示过期余额"。纯 DNS/超时分类已由探针 B 7/7 背书（HttpRequestException←SocketException(HostNotFound)、TaskCanceledException←TimeoutException），本轮以 401 代测**同一 UI 错误分支**（错误提示替换余额数字、不回退显示过期余额）。文案映射差异如实记录：401→"API Key 无效，请检查设置"；网络失败→"余额获取失败"（该分支的 UI 渲染由集成测试 `PanelViewModelBalanceStateTests` 覆盖：四态断言 + 失败态不显示数字 + 不回退缓存值）。
- 凭据清理：`cmdkey /list` 确认 `LegacyGeneric:target=DeepSeekBalanceWidget_ApiKey` 先出现后消失，**无残留**。

### 项 6：API Key 不出现在日志/配置文件 —— **通过**

- 机扫 `%APPDATA%\DeepSeekBalanceWidget\` 全部 7 个文件（settings.json + 4 ICO + 2 日志），UTF-8 与 UTF-16 双编码、四种模式（`sk-`、`Authorization`、`Bearer`、假密钥全串）：**命中 0**。
- 日志中假密钥仅以"长度 54"出现（B-1 脱敏层工作正常，含 `sk-` 形态兜底）。

### 项 7：Win10/11 —— **Win11 通过；Win10 不可测，如实标注**

- **Win11（本机 build 26200）**：上述全部测试即直接证据。
- **Win10：本机无法实测，未做任何假装验证。** 技术兼容性依据：① 零 Win11-only API（圆角 = `AllowsTransparency + CornerRadius` 纯 WPF 路线，探针 D §6.3 源码核查口径不变）；② SCD 自包含不依赖系统 .NET（本轮 976 ms 干净环境启动实证）；③ .NET 8 官方支持矩阵 Win10 仅 LTSC/Enterprise 口径、消费版 22H2 已于 2025-10 停服（政策性保留意见）。
- **处置**：`docs/deployment.md` 内联 probe D §6.4 的 7 步 Win10 人工验证清单（约 5 分钟），测试报告与部署文档均指向该清单，待用户在 Win10 真机执行。

### 项 8：界面与 A1 选定方案一致性 —— **通过**

| A1 规范值（docs/ui-selection.md） | 实现 | 取证 |
| --- | --- | --- |
| 面板 320×230 / 圆角 14 / 阴影 0 10px 26px rgba(15,23,42,.16) | 视口 348×258（四周 14px 阴影余量）+ `CornerRadius=14` + DropShadow(26/10/0.16/#0F172A) | XAML 逐值 + 日志 `面板显示 尺寸=320×230` |
| 胶囊 12px/600/全圆角/4px 13px | FontSize 12 SemiBold CornerRadius 999 Padding 13,4 | `t04-flip-after-crop.png` |
| 总余额 30px/800/色 --fg | FontSize 30 ExtraBold Brush.Fg | XAML（余额数字需成功刷新后显示，本轮无真实 Key，字号版式与 hint 态并存验证） |
| 倒计时 12px、数字 700/--fg/tabular-nums、格式 HH:mm:ss | Run FontWeight Bold Typography.NumeralAlignment=Tabular | `t03-countdown-*-crop.png`（HH>24 未触发——当日 12:00 目标仅 2h；口径由探针 C 94/94 背书） |
| 进度条 4px 高 / 全圆角 / 轨道 --line / 填充 --acc | Grid Height=4，Border CornerRadius=2 | 像素实测：轨道 **#303947 = A1 --line 精确值**、填充 **#FF9C4A = A1 --acc 精确值**、条高 8 物理px = 4 DIP |
| 上次刷新 10px / --muted | FontSize 10 Brush.Muted | 截图底部行"上次刷新 尚未成功" |
| 四态色板 15 项 | ThemeManager 精确表 + 深浅主题字典 | 像素取证：--bg 深 **#1B212C**、--sub 深 **#A9B2BE**、--acc 深(峰) **#FF9C4A**、--acc 深(谷) **#46D27F**、浅色 bg **#FFFFFF**；托盘 4 变体 ICO 中心像素 4/4 精确（含浅色峰 #E4720B） |
| 按钮 26×26 圆形、图标 14×14 线性 SVG | CircleIconButton 样式 + Feather 线性 Path | 截图 `t05-panel-401-crop.png` 右上双圆钮 |
| 落选方案未混入 | 单列布局、无额外语素 | 全部截图 |

- **高峰两态代偿说明（如实）**：高峰浅色/高峰深色面板自然实拍已取得（09:00 后为高峰：`t04-flip-after`、`t08-panel-peak-dark/light`）；**空闲浅色**面板实拍未能取得（空闲时段 12:00–14:00 未到达测试窗口），以托盘 ICO 空闲浅色变体像素（#17994E 精确）+ 集成测试 4 变体断言 + 深色空闲面板实拍（`t03-countdown-E2/F2-idle`）代偿——空闲态与高峰态共用同一布局/中性色板，仅语义色切换，风险极低。
- 对照基准：`design/ui-preview/index.html`（原 docs/ui-preview，已归位 design/）A1 区块（未改动该文件，逐值人工比对）。

### 项 9：定时巡查按预期触发 —— **通过**

- **心跳**（sess:23aeed49，单次连续运行 33 分钟 1970 tick）：统计摘要 min/mean/max = 954.5 / **1000.0** / 1049.1 ms；漂移在 ±15.3ms 内振荡（无累积）；maxLateness 58.6ms（≈4 个 15.6ms 量化档，无追赶拍）；跳槽位累计 0；单 tick 处理耗时 max 19.38ms（首 tick JIT）→ 稳态 µs 级。**对照探针 E 基线（mean 1000.0、drift +2.5~+15.7ms、maxLateness 28.9ms）：一致。**
- **余额调度**：未配置 Key 时定时到期静默（`余额定时到期：未配置 Key → 跳过（不发起请求）`，8:46:30 首到期即静默）；面板打开即刷（source=Panel ×7，30s 去抖抑制 ×5）；假密钥期间 401 → 退避 **1→5→15→15 分钟**（与 RetryPolicy 序列一致）；SCD 实例 46 tick mean 1000.3ms。
- 0 ERROR 行；0 未处理异常。

### 项 10：浅色/深色主题正常 —— **通过**

- 设置窗口切浅色：日志 `主题已切换为 Light（即时生效）→ 主题已应用 → 浅色 → 托盘图标已重建（主题切换）主题=浅`——**设置窗/面板/托盘三处同步换色**（设置窗白底截图 `t10-theme-light-full*.png`，浅色面板 `t08-panel-peak-light*.png`，托盘像素 **#E4720B 填充 + #C25E06 环 = A1 浅色峰精确值**，深色填充 #FF9C4A 命中 0）；settings.json 实时持久化 `"Theme": "Light"`。
- 切回深色：日志对应 `主题=深`，托盘回到 #FF9C4A 填充，settings.json `"Theme": "Dark"`。
- 跟随系统路径冒烟：启动日志 `系统主题监听就绪：AppsUseLightTheme=True（WM_SETTINGCHANGE + 30s 轮询兜底）`；本机注册表 `AppsUseLightTheme` 值不存在（按设计读缺失视为浅色）——**只读取、未做任何注册表写入**，原值即"无值"，无需还原。

### 项 11：退出与清理 —— **通过（两次托盘退出链 + 重启抢占）**

- 两次"右键托盘 → 退出"完整链（日志）：`退出开始 reason=托盘菜单·退出 → Scheduler Stop(终值统计) → 托盘图标已释放 → 图标临时目录已清理 → 退出清理完成（Mutex 已释放）→ === 进程退出 ===`；进程消失（tasklist 复核）、`icons/` 目录删除、SCD 实例退出后干净目录仍只有 exe 本体（无日志/图标落 exe 旁）。
- Mutex 释放实证：退出后重启 `单实例 Mutex 抢占成功`；运行中二次启动 `单实例检测：已有实例在运行（Mutex 抢占失败），二次启动自动退出`（exitCode=0）。
- 右键菜单为生产版三项：打开面板/设置/退出（`t11-context-menu-crop.png`，无探针后门项）。

### 项 12：回归 —— **通过**

- `dotnet clean` + 删除 bin/obj 后 `dotnet build -c Release`：**0 警告 0 错误**（TreatWarningsAsErrors=true 下）。
- `dotnet test`（Release）：**9/9 通过**（Debug 亦 9/9）。测试工程同标 0 警告。

---

## 2. 集成测试工程摘要

| 项 | 内容 |
| --- | --- |
| 位置 | `tests/DeepSeekBalanceWidget.IntegrationTests/`（xUnit 2.9.2 + runner 2.8.2 + Test SDK 17.11.1，net8.0-windows，UseWPF，已加入 `src/DeepSeekBalanceWidget.sln`） |
| 用例（9） | ① `Cross_120000_StateChanged_Once_And_All_Components_Sync`（项 4 核心，全管线联动）② `Four_Variants_Match_A1_Palette_And_Flip_Rebuilds`（托盘 4 变体 + NIM 重建 + 同态短路 + 目录随用随清）③ `Timer_Due_Silent_When_Key_Not_Configured` ④ `Timer_Due_Triggers_With_Key_And_Completes`（启动即刷 + 到期触发 + 未到期不触发）⑤ `Failure_States_Show_Hint_Without_Balance_Number`（4 态 Theory：NotConfigured/Unauthorized/NetworkFailure/MalformedResponse → 各自文案 + 不显示数字 + 不回退缓存）⑥ `Success_With_Zero_Balance_Is_Normal_Success_With_Unavailable_Hint` |
| 方法要点 | 自建 STA+Dispatcher 测试线程（WPF 组件约束）；FakeTimeProvider 每 tick +1s 与 250ms 真实节拍对齐；FakeHolidaySource/FakeBalanceService 替身（零网络）；禁用测试并行化（Application/UiThread 静态约束） |
| 结果 | **通过 9 / 失败 0**（Debug、Release 双配置复验） |

## 3. SCD 单文件发布实测

| 项 | 结果 |
| --- | --- |
| 命令 | `dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false` → `release/publish/` |
| 产物 | **严格单文件**：目录仅 `DeepSeekBalanceWidget.exe` 1 个文件（无 pdb/deps.json/runtimeconfig.json/原生 DLL 伴随） |
| 体积 | **66,202,308 B（63.13 MiB）**——对照探针 D 的 71,910,187 B（68.58 MiB，被测对象为探针 A 工程）差 −5.7MB：合理偏差，两者代码负载不同（探针 A 含 sim/real 双运行器与诊断代码，正式应用代码精简），运行时与压缩机制相同；探针 D 的体积口径随被测物变化，正式版以本次 66,202,308 B 为准 |
| SHA-256 | `A8BABFBE25E49323A4640CC1ACBBA4F85DA1FE246E6042E5B35E6E229FD3D2E6`（历史记录；当前产物 SHA 见 docs/release-notes.md §4） |
| 内嵌核验 | wpfgfx_cor3 / PresentationNative_cor3 / D3DCompiler_47_cor3 / vcruntime140_cor3 / PenImc_cor3 + H.NotifyIcon.Wpf.dll + 主程序集全部内嵌（coreclr/hostfxr/hostpolicy 在压缩包内，首启解压实证：`%TEMP%\.net\DeepSeekBalanceWidget\` 出现 5 个原生库共 ~7.8MB） |
| 签名/MotW | NotSigned；无 Zone.Identifier（本机构建）。分发场景 SmartScreen 提示见 release-notes/deployment |
| 真跑冒烟 | 单独复制到空临时目录（仅 exe）+ `PATH=C:\Windows\System32;C:\Windows` + 清除 DOTNET_ROOT → 冷启动 **976ms** 托盘就绪；溢出弹层橙色图标（#FF9C4A+#E4720B 像素命中）；面板完整渲染（`t02-scd-panel-peak*.png`）；日志落 `%APPDATA%`（exe 旁零新增文件）；托盘菜单退出全链清理 |

## 4. 遗留与建议

1. **Win10 真机人工验证未执行**（不可测）——7 步清单已内联至 `docs/deployment.md` §6，建议分发前在 Win10 LTSC/Enterprise 真机跑一遍。
2. **空闲浅色面板自然实拍缺失**（时段未达）——已用 ICO 像素 + 集成测试代偿（见项 8 说明）；如需完全实拍可在 12:00–14:00（工作日）或周末补拍一张。
3. **对外分发前建议代码签名**（当前 NotSigned，SmartScreen 首启提示"仍要运行"）——分发策略项，不阻塞 MVP。
4. **余额 0.00 显示口径**：探针 B 实测 0 余额账号为普通成功（显示 ¥ 0.00 + "余额不可调用"提示）；本轮无真实 Key 未再触发该实拍，由集成测试用例 ⑥ 覆盖。
5. 面板弹出延迟测得的 47–62ms 为托盘点击→窗口可见（含 60ms 定位前摇），UI 渲染体感与 A1 原型无差异；未单独测"托盘可见区内"（本机图标在溢出区，A-5 行为边界见 release-notes 已知限制）。
6. 测试期 UI 自动化脚本与临时截图存于 `%TEMP%\phase5\`（项目外），不影响仓库。

## 5. 结论

**阶段 5 测试通过。** 12 项验收标准全数满足（1 项代测说明、1 项不可测如实标注并给出人工清单），集成测试 9/9、回归 0 警告 0 错误、SCD 单文件发布实测含真跑冒烟全部达标，测试期间设置/凭据/注册表/进程已全部还原。

## 6. v1.1 增补验证（2026-09-30，需求变更：面板右缘停靠 + 拖动 + 钉住）

### 6.1 自动化测试（9 → 41 用例，全绿）

| 项 | 内容 |
| --- | --- |
| 结果 | **通过 41 / 失败 0**（Release 与 Debug 双配置）；`dotnet build -c Release` **0 警告 0 错误**（TreatWarningsAsErrors=true，主工程与测试工程同标） |
| 新增 `PanelPlacementTests`（25 断言用例） | 默认停靠位计算（1920×1040 工作区：窗口 X=1586 → 卡片右缘=1920 贴边；窗口底=1028=1040−12，14px gutter 换算）；拖动阈值判定 Theory 9 例（恰好 4px 不触发=防误触口径、√18≈4.24 触发、方向无关）；右缘吸附 Theory 6 例（20px/恰 28px/28.1px/86px/已越出右缘/已贴边）；`PanelBehavior.ShouldHideOnDeactivate` Theory 7 例（钉住任何时刻不隐藏 ×3、未钉防抖期内忽略 ×2、防抖期满隐藏 ×2）；位置持久化 NaN 守卫 |
| 新增 `PanelStatePersistenceTests`（5 例） | settings.json 读写往返（PanelX=1586/PanelY=770/Pinned=true/PinTopmost=false，注入临时目录、不触碰真实 %APPDATA%）；缺文件回退默认（无位置记忆/未钉/PinTopmost 默认开）；v1.0 旧格式文件（无新字段）兼容加载；部分字段文件缺省回退；PanelViewModel.SetPinned 触发 IsPinned PropertyChanged |
| 新增 `TrayPinMenuTests`（2 例） | 真实 TrayController：菜单含「钉住面板」IsCheckable 且初始未勾选；SetPanelPinned 程序同步不回环（事件未触发）；状态翻转沿 NIM 重建菜单后勾选态镜像保持；用户勾/取消勾（Click 路由事件）发出带新状态的 PanelPinToggleRequested |
| 工程改动 | `LocalSettingsStore` 目录可注入（缺省 = 真实 %APPDATA%，App 侧不变）；`TrayController.TrayContextMenu` 只读暴露（测试断言菜单勾选态） |

### 6.2 真机冒烟（Win11 26200，3200×2000 @ scale 2.0，截图存 `%TEMP%\v11-smoke\`）

| 步骤 | 操作与证据 | 结果 |
| --- | --- | --- |
| a | 删 settings.json → `--show-panel-on-start` 启动：日志 `面板显示 位置=(1266,682)`；窗口右缘 3228px=屏幕 3200+14×2（gutter 超出 14 DIP），卡片右缘=1600 DIP=工作区右缘；窗口底 940 DIP=952−12（`a-docked.png`）；首次显示**不写** settings.json（仅拖动/钉住时持久化） | **通过** |
| b | PowerShell mouse_event 真实拖动到屏幕中部：窗口 DIP=(626,330) ↔ settings.json `PanelX:626 PanelY:330`；点桌面隐藏 → 托盘左键重现于 (626,330)（`b-dragged.png`/`b-reshow.png`） | **通过** |
| c | 拖至卡片右缘距工作区右缘 15px 释放 → 自动吸附：窗口 1251→1266 DIP（卡片右缘=1600 贴边、垂直保持 330）；日志 `右缘吸附：卡片右缘距工作区右缘 ≤28px → (1251 → 1266)`（`c-snap.png`） | **通过** |
| d | 点顶行图钉：日志 `面板钉住状态=True（source=panel-pin-button）`+`托盘菜单「钉住面板」勾选同步为 True`，图钉变橙色填充（`d1-pinned-crop.png`）；点桌面 → `Deactivated 触发；面板已钉住 → 保持显示（v1.1）`（`d2-pinned-desktop.png`）；再点图钉取消 → 点桌面 → `面板隐藏 reason=deactivated`（`d3-unpinned-hidden.png`） | **通过** |
| e | 托盘菜单勾选「钉住面板」：日志 `用户勾选 → True`→`source=tray-menu`，托盘菜单截图勾选态与面板图钉态一致（`e3-tray-menu-checked.png`）；设置窗口「钉住时置顶」关→开：`Topmost=False/True`，Win32 `GetWindowLong(GWL_EXSTYLE)` WS_EX_TOPMOST 位实测 False/True 随动（`e4-settings-pin-topmost-off.png`） | **通过** |
| f | 托盘退出：日志 `退出开始 reason=托盘菜单·退出`→`=== 进程退出 ===`，进程消失；重启（无参数）：面板不自动弹出（`f1-restart-no-panel.png`），日志 `startup-restore` 恢复钉住（NaN 守卫跳过位置回写）+ 托盘菜单勾选同步；点托盘 → 显示于记忆位置 (1266,330) 钉住=True（`f2-reshow-remembered.png`） | **通过** |

### 6.3 冒烟期发现并修复的两个缺陷（均已回归验证）

1. **位置记忆仅落盘、未同步内存**：拖动后 settings.json 已更新，但同会话内再次弹出仍走旧记忆/默认停靠（冒烟 b 首轮失败暴露）→ 修复：`PanelWindow.PersistPanelState` 落盘同时更新 `_rememberedX/Y` 内存记忆。
2. **拖动起点位移丢失**：按下 → 超过 4px 阈值 → 进入 `DragMove()` 之间光标已移动 10–30px，而 DragMove 从窗口原位开始跟随，窗口落后光标（冒烟 c 首轮实测差 15 DIP）→ 修复：进入 DragMove 前按 `GetCursorPos`−`PointToScreen(拖动起点)` 先行补偿窗口位置（防误触 4px 阈值语义保留）。修复后拖动全程窗口贴合光标，吸附判定精确。

### 6.4 收尾状态

- 集成测试 41/41（Release+Debug）；主工程 Release 0 警告 0 错误。
- 重发布：`release/publish/DeepSeekBalanceWidget.exe` SHA-256 `5DDF2EBE3651E5E111C4B4FAB08913BA0BA3E51099842AB4E5444440FE628B10`，66,202,308 B（与 v0.5.0-mvp 同体积）；严格单文件（目录仅 1 个文件）。
- 收尾无残留进程（托盘退出链两次实证）；settings.json 最终态 `Pinned=false`（未钉）、`PinTopmost=true`（默认开），PanelX/PanelY 留有冒烟测试位置 (1266,330)（不影响使用，可拖动覆盖或删除文件重置）。

## 7. FB-1 修复验证（2026-10-01，阶段 6）

### 7.1 缺陷与根因

- **现象**：法定节假日的工作日（如 2026-10-01 国庆节，周四）启动应用时，托盘图标与胶囊强调色停留在"高峰橙"，而胶囊文字与倒计时正确显示"空闲时段"；日志 0 条"状态翻转沿"。若无修复，该错色将持续到下一次真实翻转沿（节假日全天无翻转，最长可错数天）。
- **根因**：启动首帧快照 snap0 在节假日数据到达前计算（按本地工作日逻辑 → 高峰），App 以 snap0.State 同步初始化托盘（`TrayController.Initialize`）与强调色（`ApplyAccent`）；节假日数据到达触发节假日恢复（C-3）→ `Scheduler.RecomputeNow`，其 Peak→Idle 重算发生在首个 tick 之前（或首 tick），被 `PublishFlipIfNeeded` 的"首评估即初始化、不发事件"语义（`_lastState==null`）静默吸收，托盘 `SetState`/`ApplyAccent` 永不执行。
- **证据（修复前实录，sess:1baecdf2 @ 2026-09-30 同机）**：`托盘就绪 … icon=…\peak-light.ico` → `[holiday] … holiday=True` → `立即刷新快照 reason=节假日数据恢复(C-3) state=Idle`，全日志无"状态翻转沿"。

### 7.2 修复方案

- `Scheduler` 新增 `InitialPublishedState`（启动首帧已对外发布的状态，App 装配时注入 `snap0.State`）：`PublishFlipIfNeeded` 首次评估（`_lastState==null`）以它为翻转沿基准，与快照不同则**补发恰一次** `StateChanged`（Old=已发布状态），随后 `_lastState` 落为新状态——不产生重复事件、不写 `_lastDate`（跨天 prevDate 基准语义不变）、未注入时保持原语义（既有用例零回退）。代码位置：`src/DeepSeekBalanceWidget/Scheduling/Scheduler.cs`（`PublishFlipIfNeeded` + 属性）、`App.xaml.cs`（装配注释）。

### 7.3 自动化验证（41 → 43 用例，全绿）

| 项 | 内容 |
| --- | --- |
| 结果 | **通过 43 / 失败 0**（Release）；`dotnet build -c Release` **0 警告 0 错误** |
| 新增 `HolidayStartupFlipIntegrationTests.Holiday_Weekday_Startup_Data_Arrives_After_First_Frame_Tray_Flip_To_Idle_Once` | Fake 时钟 2026-10-01（周四）10:30 + **延迟 400ms 的 fake 节假日源**（复现"数据晚于首帧"）：断言首帧快照=高峰 → 数据到达后 StateChanged 恰 1 次（Old=Peak/New=Idle/非 Degraded）、真实 TrayController `CurrentState` 翻转为**空闲**、tooltip 重建含"空闲时段"（无"高峰时段"）、胶囊"空闲时段"、倒计时前缀改指高峰；随后多个 tick 无重复事件 |
| 新增 `…Workday_Startup_No_Spurious_Flip_Stays_Peak` | 同时序、fake 源报工作日：数据到达前后状态恒为高峰、0 次翻转沿（普通工作日不误翻不回退） |
| 既有用例 | 41 例零回退（含跨 12:00:00 翻转、ICO 像素、退避序列、停靠吸附） |

### 7.4 真机冒烟（修复版 SCD exe，SHA-256 `07E14926…43312`）

- **节假日同态路径（12:13–12:16，午休窗口，snap0=Idle）**：三轮启动日志一致——4 变体 ICO 生成像素 = A1 精确值（#E4720B/#FF9C4A/#17994E/#46D27F）；`托盘就绪 tooltip=…空闲时段 icon=…idle-dark.ico / idle-light.ico`（变体随主题正确）；节假日数据到达后 `立即刷新快照 state=Idle`，无多余翻转沿（snap0 与终态同态，无翻转需求）——与胶囊"空闲时段"一致。
- **FB-1 场景路径（14:00 后高峰窗口，snap0=Peak）**：见 §7.5（节假日数据到达后日志出现 `状态翻转沿：Peak → Idle`，托盘重建为空闲变体）。

### 7.5 FB-1 场景真机实录（2026-10-01 14:04，修复版 SCD exe）

法定节假日（国庆节）的工作日 14:04 启动（删 settings.json → 默认右缘停靠，`--show-panel-on-start`），完整复现缺陷触发时序并验证修复：

```text
14:04:44.817 图标已生成 peak-light.ico 中心像素=#E4720B        ← 4 变体精确色值
14:04:44.941 托盘就绪 tooltip=…高峰时段 icon=…\peak-dark.ico   ← snap0（节假日数据未到）=工作日高峰橙
14:04:45.349 状态翻转沿：Peak → Idle（Degraded=False, 时段=全天空闲）tick#0   ← ★修复生效：翻转沿补发（修复前 0 条）
14:04:45.366 托盘图标已重建（状态切换 Peak→Idle）→ 绿(空闲) tooltip=…空闲时段 主题=深
14:04:45.367 立即刷新快照 reason=节假日数据恢复(C-3) state=Idle countdown=18:55:15
14:04:46.379 立即刷新快照 … state=Idle countdown=162:55:14     ← 前瞻收敛：下一高峰 = 10-08（周四）09:00
```

- 面板实拍（翻转后 PrintWindow）：深色底 + **绿**色胶囊"空闲时段" + 绿进度条，倒计时 162:53:09——胶囊文字与强调色一致（修复前强调色停留橙）。
- 时序说明：本次节假日数据在首 tick 前到达（tick#0 标签 = RecomputeNow 路径），恰为修复前被吞的同一路径；翻转后连续重算仅此一次 StateChanged，无重复事件。

### 7.6 遗留

- 高峰态自然实拍仍待补（工作日 9–12/14–18；节假日全天空闲无法自然出现）。
- Win10 人工清单、双屏/DPI、8 小时内存趋势等观察项沿用 §4/§6 口径。
