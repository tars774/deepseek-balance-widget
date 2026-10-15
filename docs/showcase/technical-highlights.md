# 技术亮点清单（Technical Highlights）

每条按"问题 → 方案 → 实测数字 → 代码位置"组织。所有数字均来自探针/验收阶段的真实环境实测（汇总见 [probe-results.md](../probe-results.md)）。

## 1. 伺服心跳：±ms 级精度的 1 秒心跳

- **问题**：Windows 系统计时器量化 ~15.6ms，朴素 `DispatcherTimer(1s)` 的漂移逐 tick 累积（实测 0.03–0.85 s/分钟）；而"直觉补偿公式"在 WPF 的平台语义下更糟（−2978.6ms，每 3–8 tick 多打 1 拍）——探针 E 首轮 real 浸泡 4 项全 FAIL 才暴露。
- **方案**：Stopwatch 单调时钟 + 每 tick 计算带符号误差，伺服式重排 `Interval = clamp(Heartbeat − e, 250ms, 2000ms)`；长停顿跳槽位补一次而非爆发；倒计时本身按"下一切换点 − now"绝对差值每 tick 重算，永不累加。
- **实测**：330 tick 浸泡 mean **1000.0ms**、5.5 分钟累计漂移 **+2.5ms**（验收方独立复测 +7.2ms）；生产日志 1970 tick 漂移 ±15ms 无累积、跳槽位 0。
- **代码位置**：`src/DeepSeekBalanceWidget/Scheduling/Scheduler.cs`（对齐与跳槽位）、`Scheduling/SchedulerOptions.cs`（间隔可注入）。

## 2. 双源节假日 + 本地星期优先：补班周末不误判

- **问题**：调休补班的周末，两个节假日 API 字面都报"工作日"（主源 `daytype=3 调休`、备源 `isHoliday=false 工作日`），按 API 判定会把周末误判成高峰日。
- **方案**：规则层本地 `DayOfWeek` 优先——周末一律全天空闲不看 API；API 只在"周一至五是否法定节假日"维度起作用；主源成功判定 = `code∈{0,200}` + 日期回显校验（无匹配判失败）；按北京日期缓存 + 前瞻 ≤400 天 + 1/5/15 分钟退避全局门。
- **实测**：五日期双源对拍一致；两个补班周末双源验证"仍全天空闲"；缓存每日期恰拉一次、跨天零重复（计数器）；一次真实主源冷启动 8s 超时被 failover 正确兜底。
- **代码位置**：`src/DeepSeekBalanceWidget/Peak/PeakEngine.cs`（规则层）、`Peak/HolidayService.cs`（缓存/退避/前瞻）、`Peak/HolidayClients.cs`（双源+归一化）。

## 3. 四类异常精确映射 UI

- **问题**：网络故障形态各异，笼统的"获取失败"无法指导用户行动（是 Key 错了还是断网了？）。
- **方案**：异常逐类捕获映射——401 →"API Key 无效，请检查设置"（可点击打开设置）；DNS（`HttpRequestException←SocketException(HostNotFound)`）/ 超时（`TaskCanceledException←TimeoutException`）/ 畸形 JSON（`JsonReaderException`/`JsonException`）→"余额获取失败"；`is_available=false` 是展示态非错误；失败一律不回退显示过期缓存余额。
- **实测**：四类异常真实服务端/注入复现全部精确分类；401 端到端（假密钥 → 真实 401 → UI 提示 → 退避 1/5/15min → 清除 Key 恢复）实录通过；`is_available` 缺失改为显式 `MalformedResponse`（不做静默默认）。
- **代码位置**：`src/DeepSeekBalanceWidget/Balance/BalanceClient.cs`、`Balance/BalanceResponseParser.cs`、`Balance/BalanceModels.cs`。

## 4. 凭据与日志：Key 全生命周期无明文

- **问题**：API Key 是用户最敏感的数据；日志是泄漏高发区。
- **方案**：Key 只进 Windows 凭据管理器（自写 CredWriteW/CredReadW/CredDeleteW P/Invoke，无第三方库）；日志写入层统一过 `SecretMasker`——完整 Key + `sk-` 前缀 + `Authorization: Bearer` + 高熵赋值片段，"出现即打码"；日志落 `%APPDATA%`（不落 exe 旁，探针 D 教训）。
- **实测**：真实 Key 全树（含日志/evidence）UTF-8+UTF-16 双编码字节级扫描 **0 命中**；假 Key 三形态注入日志 0 明文；阶段 5 对 `%APPDATA%` 全部 7 个文件机扫 0 命中（假密钥仅以"长度 54"出现）。
- **代码位置**：`src/DeepSeekBalanceWidget/Credentials/Win32CredentialStore.cs`、`Infrastructure/SecretMasker.cs`、`Infrastructure/Logger.cs`。

## 5. 43 集成测试：真组件 + 假时钟

- **问题**：WPF 组件的时间行为（翻转沿/跨天/退避 21 分钟序列）无法靠真实等待测试；纯 mock 测试又测不出组件联动问题。
- **方案**：自建 STA + Dispatcher 测试线程承载真实 WPF 组件；`FakeTimeProvider` 注入假时钟（手动步进/加速倍率）；节假日与余额用计数替身（零网络）；断言覆盖：跨 12:00:00 状态翻转全管线联动（StateChanged 恰 1 次、托盘换色、胶囊翻转、进度重置）、4 枚 ICO 变体逐像素 = A1 色板、退避序列 1/5/15/15 分钟（假时钟秒级验证）、面板余额四态、0 余额正常成功态、钉住菜单两处同步不回环、面板停靠/吸附/阈值 25 断言。
- **实测**：43/43 通过（Debug 与 Release 双跑；43 含 FB-1 回归 2 例，2026-10-01 起）；0 警告 0 错误（TreatWarningsAsErrors=true）。
- **代码位置**：`tests/DeepSeekBalanceWidget.IntegrationTests/`（PeakFlip / TrayIconVariant / BalanceScheduling / PanelPlacement / PanelStatePersistence / TrayPinMenu / PanelViewModelBalanceState）。

## 6. 63 MiB 单文件免安装

- **问题**：WPF 不支持裁剪/AOT，自包含发布天然庞大，且原生 DLL 默认散落 exe 旁破坏"单文件"。
- **方案**：SCD 压缩单文件（参数见 [ADR-005](../adr/ADR-005-scd-single-file.md)），原生库内嵌 + 首启解压。
- **实测**：严格 1 个 exe、0 伴随文件，**66,202,308 B（63.13 MiB）**；首启解压 5 原生库 ~7.8MiB 仅 +36ms；干净环境（最小 PATH、无 DOTNET_ROOT）冷启托盘就绪 **976ms**；退出后 exe 旁零新增文件。
- **代码位置**：发布参数在 [deployment.md](../deployment.md)；`DeepSeekBalanceWidget.csproj`（`PublishTrimmed=false`、`PublishAot=false`）。

## 7. 图标渲染管线：绕过 H.NotifyIcon 2.3.2 的转换限制

- **问题**：2.3.2 的 `IconSource→Icon` 转换链只接受 ICO 容器（`RenderTargetBitmap` 直转抛 `NotImplementedException`、裸 PNG 流抛 `ArgumentException`）。
- **方案**：`DrawingVisual` 画圆 → `CopyPixels` → 手写最小 ICO（ICONDIR + ICONDIRENTRY + 32bpp BGRA DIB + AND 掩码，R/B 通道顺序敏感）落盘 → `BitmapImage` 文件 URI；图标目录随用随清、退出清理。
- **实测**：4 枚变体（峰/谷 × 浅/深）中心像素 = A1 精确色值（#E4720B / #FF9C4A / #17994E / #46D27F），集成测试逐像素断言。
- **代码位置**：`src/DeepSeekBalanceWidget/Tray/DynamicIconRenderer.cs`。

## 8. 竞态防御：双向防抖 + 弹出避让

- **问题**：托盘点击与窗口失焦两条路径竞争同一语义——"弹出即隐藏"（显示瞬间焦点竞态）与"点了托盘关不掉"（失焦先隐藏、随后的托盘 Up 事件又把隐藏当切换）。
- **方案**：显示侧防抖 300ms（`OnDeactivated` 距显示过近一律忽略）+ 隐藏侧防抖 250ms（托盘点击距上次隐藏过近判为同一次点击）+ `ShowActivated=false` + 延迟一拍 `Activate()`；Topmost 面板弹出定位水平避让托盘图标列（第一版覆盖式定位实测把第 2~10 次图标点击全部吞掉）。
- **实测**：探针 A 18 次失焦 0 误杀、10 次连续切换 10/10；生产环境 09:00:00.023 真实翻转 18ms 内托盘重建 + 三要素联动。
- **代码位置**：`src/DeepSeekBalanceWidget/Panel/PanelWindow.xaml.cs`、`Tray/TrayController.cs`。

## 9. v1.1 面板位置系统：纯函数化 + 位移补偿

- **问题**：右缘停靠/拖动/吸附/记忆涉及 DPI 换算（14px 阴影 gutter）、防误触阈值与 DragMove 的平台行为（进入时窗口从原位跟随，光标已在别处）。
- **方案**：停靠计算/4px 阈值/28px 吸附抽为纯函数 `PanelPlacement`（25 断言用例）；进入 `DragMove` 前 `CompensatePreDragCursorDelta` 按光标实际位移先行补偿窗口位置；位置/钉住持久化含 NaN 守卫与 v1.0 旧格式兼容。
- **实测**：拖动全程窗口贴合光标（修复前实测差 15 DIP）；吸附边界（恰 28px 吸 / 28.1px 不吸）断言；重启后记忆位置重现。
- **代码位置**：`src/DeepSeekBalanceWidget/Panel/PanelPlacement.cs`、`Panel/PanelWindow.xaml.cs`。
