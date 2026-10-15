# 常见技术问答（FAQ）

## 1. 为什么不用 Windows 11 官方小组件板？

接入需要 MSIX 打包 + WinRT/COM 互操作、无 XAML 设计器，与"单文件 exe 免安装侧载"的交付目标直接冲突；且小组件板无法提供"托盘状态灯变色 + 点击弹出 + 右缘停靠拖动钉住"这类常驻轻量交互。详见 [ADR-001](../adr/ADR-001-windows-widgets-board.md)。

## 2. 为什么用 WPF 而不是 Electron / WebView2？

- 目标形态是常驻托盘小工具：Electron/WebView2 方案的体积（百 MB 级）与内存代价与"63 MiB 单文件、~134MB 工作集、CPU 1–2%·核"的目标差距悬殊。
- 本项目 UI 面积极小（一块 320×230 面板 + 一个设置窗口），WPF 资源字典即可实现完整深浅主题，无需浏览器引擎。
- WPF 的 `AllowsTransparency + CornerRadius` 是纯托管路线，零 Win11-only API，Win10/11 表现一致。

## 3. 心跳为什么不漂移？不用多媒体计时器吗？

两层保证：① **倒计时永远是绝对差值**（下一切换点 − 当前时刻，每 tick 重算，永不累加）——即使某 tick 晚到 100ms 甚至跳拍，下一 tick 显示立即回正；② **伺服式绝对对齐**消除心跳本身的累积漂移（每 tick 用单调时钟测误差并重排 Interval）。实测 5.5 分钟累计漂移 +2.5ms（要求 <1s）。系统计时器 ~15.6ms 量化无法消除也无害——它远小于 1s 的显示粒度。`timeBeginPeriod` 提升整机功耗换不来秒级 UI 可感知的收益，故不用。详见 [ADR-003](../adr/ADR-003-dispatchertimer-servo-heartbeat.md)。

## 4. API Key 存在哪里？安全吗？

只存 **Windows 凭据管理器**（Generic 凭据，仅本机可读），不落文件、不进配置、不进日志。日志写入层统一"出现即打码"（`sk-`/`Bearer`/高熵片段命中即替换）。验证手段：真实 Key 全树 UTF-8+UTF-16 双编码字节级扫描 0 命中；假 Key 三形态注入日志 0 明文。完全清除 = 设置窗口"清除 Key" 或 `cmdkey /delete:DeepSeekBalanceWidget_ApiKey`。详见 [ADR-002](../adr/ADR-002-credential-manager.md)。

## 5. 为什么安装包有 63MB？能不能小一点？

WPF 不支持裁剪（PublishTrimmed）与 NativeAOT，自包含发布必须内嵌整个 .NET 8 运行时 + WPF 原生库；压缩后 63.13 MiB。换来的是：免安装、目标机**不需要装 .NET**、单文件 0 伴随。若体积敏感（如内网分发），可按 [deployment.md §2](../deployment.md) 用 FDD 参数自行发布出 **2.11 MiB** 单文件——代价是目标机需安装 .NET 8 Desktop Runtime。详见 [ADR-005](../adr/ADR-005-scd-single-file.md)。

## 6. 支持 Windows 10 吗？

**Win11 全量实测通过；Win10 未实测（如实声明）**。技术兼容依据：源码零 Win11-only API（圆角为纯 WPF 自绘路线）、SCD 自包含不依赖系统 .NET。政策口径：.NET 8 官方支持矩阵中 Win10 仅 LTSC/Enterprise（消费版 22H2 已于 2025-10 停服）。分发前建议在 Win10 真机执行 [deployment.md §6 的 7 步清单（约 5 分钟）](../deployment.md)。

## 7. 会消耗我的 token / 产生费用吗？

余额查询接口官方定位为账户查询接口，探针期多轮真实调用无异常计费证据——**"不耗 token"是低风险既定假设，未被严格证实**。保守设计：默认 30 分钟轮询（可调至最高 1440 分钟 = 一天一次）；未配置 Key 时定时到期**完全不发请求**；失败进入 1/5/15 分钟退避不消耗请求。详见 [deepseek-api.md](../deepseek-api.md)。

## 8. 托盘图标找不到 / 左键行为不对？

- **找不到图标**：新装通常在任务栏溢出弹层（点任务栏"^"展开）；建议右键任务栏将其固定到可见区。
- **溢出区内左键表现为"总是显示、失焦隐藏"**：这是 Windows Shell 对溢出弹层的焦点语义（打开弹层的动作本身让面板失焦），非 bug；完整"点击切换"体验请把图标固定到任务栏可见区。详见 [probe-results.md](../probe-results.md) 探针 A 一节。

## 9. 如何完全卸载？

三必做 + 两条件：① 托盘右键退出；② 删除 `%APPDATA%\DeepSeekBalanceWidget\`；③ 删除凭据 `cmdkey /delete:DeepSeekBalanceWidget_ApiKey`；④（若开过自启）关闭设置窗口自启开关或删 Run 键；⑤ 删除 exe 本体（可选清理 `%TEMP%\.net\DeepSeekBalanceWidget\` 解压缓存）。无服务、无驱动、无机器级注册表写入。完整步骤见 [release-notes.md §6](../release-notes.md)。

## 10. 为什么先做 12 个候选 UI 再写代码？

UI 风格分歧若发生在代码完成后返工成本最高。项目把审美决策前置为静态 HTML 预览（4 风格 × 3 候选 × 4 状态 = 48 面板），经两轮验收修正渲染缺陷（首轮 12 个空闲面板背景透明失效会误导选型）后由用户选定 A1，其 CSS 参数被逐值提取为绑定规范——阶段 5 验收按规范值逐像素核对全部命中。详见 [ADR-004](../adr/ADR-004-ui-preview-first.md) 与 [design/ui-preview/index.html](../../design/ui-preview/index.html)。

## 11. 节假日 API 挂了怎么办？

双源（主 xiaoai + 备 dreace）自动切换；按北京日期缓存 + 前瞻 400 天，双源皆挂时进入 1/5/15 分钟退避并**降级为"周一至五工作日、周末空闲"的本地逻辑**（周末/普通日判定不受影响，仅"法定节假日"识别失效），面板底行显示"数据离线"小图标，网络恢复自动拉回并立即刷新快照。详见 [ADR-006](../adr/ADR-006-local-dayofweek-holiday.md)。

## 12. 集成测试怎么做到不依赖网络、不真等 21 分钟退避？

测试工程（`tests/DeepSeekBalanceWidget.IntegrationTests/`）自建 STA + Dispatcher 线程承载**真实** WPF 组件（Scheduler/PeakEngine/TrayController/PanelViewModel），时间经 `ITimeProvider` 注入 `FakeTimeProvider`（手动步进 + 加速倍率），节假日与余额用计数替身——零网络、退避序列秒级验证。43 用例（含 FB-1 回归 2 例）Debug/Release 双跑全绿。详见 [technical-highlights.md §5](technical-highlights.md)。
