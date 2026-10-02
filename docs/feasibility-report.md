# 《DeepSeek 余额峰谷监控小组件》可行性分析报告

| 项目 | 内容 |
| --- | --- |
| 文档阶段 | 阶段 0（技术事实核查与可行性分析） |
| 报告日期 | 2026-09-28 |
| 撰写者 | 阶段 0 执行子 Agent（基于 WebSearch / WebFetch / 实际 HTTP 探测核实，未做任何实现） |
| 状态 | 待用户评审。评审通过后进入阶段 0.5（界面预览与用户选型） |
| 适用读者 | 项目所有者及任何不熟悉本项目背景的开发者 |

> 本文所有密钥类信息一律使用占位符（如 `<YOUR_API_KEY>`），不包含任何真实凭据。

---

## 1. 项目概述

**一句话目标**：开发一个 Windows 桌面小组件（C# + WPF，.NET 8），常驻系统托盘，实时监控 DeepSeek API 账户总余额、当前处于"高峰时段"还是"空闲时段"（按北京时间 UTC+8 判定），并显示距下一次状态切换的倒计时；最终以单文件 exe 侧载方式交付，并开源到 GitHub。

**关键约束摘要**：

- 平台：Windows 10 / Windows 11；技术栈锁定 C# + WPF + .NET 8。
- 托盘库锁定 H.NotifyIcon.Wpf（NuGet）；HTTP 用 HttpClient；JSON 用 System.Text.Json。
- API Key 只存 Windows Credential Manager（CredRead/CredWrite P/Invoke 或 CredentialManagement 库），不落盘明文。
- 交付形态：单文件 exe 侧载运行；不需要 MSIX 打包；不使用 Win11 官方小组件板。
- 峰谷状态与倒计时完全离线计算；节假日数据由两个节假日 API 提供（主 + 备）并缓存，双 API 失败时降级为"周一至周五为工作日"的本地逻辑。
- 流程约束：巡查（心跳）模块须在核心功能之前先独立实现；正式实现前必须先做 12 候选界面预览并由用户选定（阶段 0.5 硬门控）。

---

## 2. 完整需求复述

以下内容为项目需求的忠实复述，是后续所有阶段的需求基线。

### 2.1 十六条功能需求

1. **常驻系统托盘**：程序常驻系统托盘，右键菜单包含"打开面板""设置""退出"三项。
2. **图标随峰谷状态动态变化**：高峰时段显示橙色图标，空闲时段显示绿色图标。
3. **左键单击托盘图标**：弹出/隐藏面板（切换行为）。
4. **面板形态**：无边框圆角窗口，宽 320px，高约 220–260px，失去焦点自动隐藏。
5. **面板顶部**：左侧为当前状态标签（高峰/空闲），右侧为设置齿轮按钮。
6. **面板余额区**：大号加粗字体显示总余额，下方小字"总余额"。
7. **面板倒计时区**：文字如"距空闲时段还有 01:23:45"，下方一条细进度条，显示当前时段已过比例。**（2026-10-02 需求变更：进度条口径已改为「剩余 ÷ 总长」并与倒计时逐秒严格同步——本文为阶段 0 原始需求原文，当前实现口径以 [release-notes.md](release-notes.md) §1 与 [ui-selection.md](ui-selection.md) §8 为准。）**
8. **面板底部**：极小灰色字体显示上次刷新时间。
9. **设置窗口**：包含 API Key、后台刷新间隔、开机自启、主题四项设置。
10. **API Key 存储**：只保存到 Windows Credential Manager，不落盘明文文件。
11. **刷新策略**：后台余额刷新间隔默认 30 分钟，可配置；打开面板时立即刷新一次；余额查询接口不消耗 token。
12. **开机自启**：写入注册表 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`。
13. **主题**：支持跟随系统 / 深色 / 浅色三种模式。
14. **异常处理**：覆盖 API Key 无效、网络失败、节假日 API 失败等情形。
15. **定时巡查机制**：秒级心跳（1 秒）、状态重算、余额刷新调度、跨天检测、异常退避重试（如 1 分钟、5 分钟、15 分钟，成功后重置；验收基线取确定值 1/5/15 分钟）、巡查日志（日志中不得包含 API Key）。巡查模块须在核心功能之前先独立实现，作为全应用的时间基准与状态驱动源。
16. **界面预览先行**：正式实现前必须先做界面预览：4 种风格 × 每种 3 个候选项 = 12 个候选项，由用户选定后才能进入正式实现。

### 2.2 数据源规则（DeepSeek API）

- 获取余额：`GET https://api.deepseek.com/user/balance`，请求头 `Authorization: Bearer <YOUR_API_KEY>`，解析字段：`total_balance`。
- 余额查询接口不消耗 token、不产生费用。
- API Key 由用户在设置窗口输入，保存到 Windows Credential Manager。
- API Key 无效或未配置：面板显示"请先在设置中配置 API Key"，点击可跳转设置窗口。
- 网络请求失败：面板只显示错误提示，不显示上次缓存的余额。
- 峰谷状态和倒计时完全离线计算，不依赖网络；余额拉取失败也正常显示峰谷状态。

### 2.3 峰谷状态判断与节假日规则（北京时间 UTC+8）

- 判断当天类型：调用节假日 API，确定当天是工作日、节假日、双休日还是调休日。
- **空闲时段直接判定**：法定节假日全天空闲；周六、周日全天空闲；**调休上班的周末全天空闲（按用户原始规则，周末一律空闲）**。
- 工作日时段划分：高峰 9:00–12:00、14:00–18:00；空闲 12:00–14:00、18:00–次日 9:00。
- 倒计时始终指向下一个状态切换点，格式 `HH:mm:ss`。
- 进度条显示当前时段已过比例。**（2026-10-02 需求变更：改为「剩余 ÷ 总长」，与倒计时逐秒同步——见 [ui-selection.md](ui-selection.md) §8。）**
- 节假日 API：主 API `https://publicapi.xiaoai.me/holiday/day?date=YYYY-MM-DD`；备用 API `https://holiday.dreace.top?date=YYYY-MM-DD`。两个都失败时，降级为仅按"周一至周五为工作日"的本地逻辑判断，并在面板显示小提示图标。
- 每日首次启动拉取当天节假日数据并缓存；跨天时重新拉取。

### 2.4 安全要求

- API Key 只存 Windows Credential Manager。
- 日志不输出 API Key 或认证头（Authorization header）。
- `.gitignore` 排除本地配置、构建产物、用户数据、日志。
- 敏感信息一律用占位符（如 `<YOUR_API_KEY>`）。

### 2.5 已知决策（用户已拍板）

- **放弃 Windows 11 官方小组件板**：门槛高（需 MSIX 打包、COM 互操作、无 XAML 设计器），改用系统托盘 + 点击弹出面板方案。
- **面板尺寸固定**：320 × 220–260，无边框、圆角、失焦自动隐藏。
- **余额刷新策略**：打开面板即时刷新 + 后台定时（默认 30 分钟）静默刷新。

---

## 3. 逐项可行性判定

判定等级：**可行** / **需替代方案**（可以做到，但实现方式与需求字面描述有差异或需附加约束）/ **不可行**。风险等级：低 / 中 / 高。

| 编号 | 需求组 | 判定 | 理由与关键技术要点 | 风险 |
| --- | --- | --- | --- | --- |
| R1 | 托盘常驻 + 右键菜单（打开面板/设置/退出） | 可行 | H.NotifyIcon.Wpf 提供 TaskbarIcon 控件与上下文菜单绑定；存在且活跃维护，需为 .NET 8 锁定 2.3.2 版本（详见 §4-b） | 低 |
| R2 | 托盘图标随峰谷状态换色（橙/绿） | 可行 | 准备两套 .ico 资源或用库内动态图标能力运行时生成；切换仅是 `IconSource`/`Icon` 赋值。需注意多尺寸（16/20/24/32）与 DPI | 低 |
| R3 | 左键单击托盘图标弹出/隐藏面板 | 可行 | H.NotifyIcon 支持 TrayLeftMouseDown/Up 事件做切换；存在"托盘点击→失焦隐藏"竞态，需防抖（详见 §4-e、§5-7） | 中 |
| R4 | 无边框圆角面板，320×220–260，失焦自动隐藏 | 可行 | `WindowStyle=None` + `WindowChrome`（或 Win11 DWM 圆角）；失焦隐藏用 `Window.Deactivated`；Alt-Tab 排除需 `WS_EX_TOOLWINDOW`。存在平台坑，均有成熟解法（详见 §4-e、§5-7） | 中 |
| R5 | 面板顶部：状态标签 + 设置齿轮 | 可行 | 纯 WPF 布局，无技术风险 | 低 |
| R6 | 余额区大号加粗显示总余额 | 可行 | 纯 UI；注意余额为字符串需转 decimal 格式化显示 | 低 |
| R7 | 倒计时文字 + 时段进度条 | 可行 | 全部离线计算；由巡查模块秒级驱动；切换点边界（23:59:59→00:00、跨天）需边界测试 | 低 |
| R8 | 底部极小灰字显示上次刷新时间 | 可行 | 纯 UI + 状态字段 | 低 |
| R9 | 设置窗口（API Key / 刷新间隔 / 自启 / 主题） | 可行 | 常规 WPF 窗口 + 数据绑定 | 低 |
| R10 | API Key 只存 Windows Credential Manager | 可行 | Win32 `CredReadW/CredWriteW` P/Invoke（`CRED_TYPE_GENERIC`），约百行代码；或第三方 CredentialManagement 库（较陈旧，建议自写）。字符集与持久化标志有细节要求（详见 §4、§5-8） | 中 |
| R11 | 刷新策略（默认 30 分钟后台 + 开面板即刷） | 可行 | `HttpClient` + 定时调度挂在巡查模块；无技术风险 | 低 |
| R12 | 开机自启写 `HKCU\...\Run` | 可行 | `Microsoft.Win32.Registry.CurrentUser` 读写标准做法；需处理杀软对 Run 键写入的偶发告警提示 | 低 |
| R13 | 主题：跟随系统 / 深色 / 浅色 | 可行 | 深浅色用 WPF 资源字典动态切换；"跟随系统"读注册表 `AppsUseLightTheme` 并监听变更（WM_SETTINGCHANGE 或轮询）。自绘资源字典即可，无需引入重型主题库 | 中 |
| R14 | 异常处理（Key 无效 / 网络失败 / 节假日 API 失败） | 可行 | 对 401/402/429/5xx、超时、畸形 JSON 分别建模；节假日 API 有双源+降级设计（§4-d、§4-f） | 低 |
| R15 | 巡查模块先行（1 秒心跳、状态重算、退避重试、巡查日志） | 可行 | 用单一 DispatcherTimer（1s）或专用心跳线程作为时间基准；退避序列 1/5/15 分钟成功后重置是纯调度逻辑。先行的开发顺序由流程保证 | 低 |
| R16 | 12 候选界面预览（HTML）由用户选定 | 可行 | 纯静态 HTML+CSS 预览页，浏览器打开即看；无技术风险，仅是流程硬门控 | 低 |
| D1 | DeepSeek 余额接口接入 | 可行（**带关键解析修正**） | 接口真实存在、Bearer 认证；**`total_balance` 不在响应顶层，而是嵌套在 `balance_infos` 数组元素内**，且为字符串类型、可能多币种——解析代码必须按此实现（详见 §4-a，此为对需求原文表述的重要修正） | 中 |
| D2 | 节假日 API 双源 + 缓存 + 降级 | 可行 | 两源均实测可达并拿到结构化 JSON；主源实测出现过 1 次连接超时，双源+缓存+降级策略确有必要（详见 §4-d） | 中 |
| D3 | 峰谷/倒计时离线计算（UTC+8） | 可行 | `TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")` + `ConvertTimeFromUtc(UtcNow)`；不得假设本地时区即北京时间（详见 §5-5） | 低 |
| T1 | 单文件 exe 侧载交付 | 可行（**带约束**） | WPF 支持 `PublishSingleFile`；但 WPF 不支持裁剪与 NativeAOT，自包含单文件体积大，且 WPF 原生 DLL 默认留在 exe 旁，需 `IncludeNativeLibrariesForSelfExtract=true`（详见 §4-c、§5-2/3） | 中 |
| T2 | 放弃 Win11 官方小组件板 → 托盘+弹出面板 | 决策合理 | 官方小组件板需 MSIX+COM 互操作且无 XAML 设计器；替代方案技术栈完全兼容既有选型 | 低 |
| S1 | 安全要求（凭据存储 / 日志脱敏 / .gitignore / 占位符） | 可行 | 均为常规工程实践；日志脱敏需在日志写入层统一过滤 | 低 |

**总结**：全部需求组均为"可行"或"可行（带约束）"，无"不可行"项。需要特别注意的两个"带修正"点：D1 的 `total_balance` 嵌套结构（直接影响解析代码正确性）与 T1 的单文件发布约束（体积 + 原生 DLL）。

---

## 4. 关键技术事实核查结果

以下事实均于报告日期（2026-09-28）通过 WebSearch / WebFetch 或直接 HTTP 请求核实；无法核实的项明确标注"待阶段 1 探针验证"。

### a) DeepSeek 余额接口 —— 已核实，发现关键结构差异

- **接口存在**：`GET https://api.deepseek.com/user/balance` 为 DeepSeek 官方文档公开端点（"Get User Balance"，查询账户余额，支持 CNY/USD）。来源：[api-docs.deepseek.com/api/get-user-balance](https://api-docs.deepseek.com/api/get-user-balance)。
- **认证方式**：DeepSeek API 统一使用 `Authorization: Bearer <YOUR_API_KEY>` 请求头。来源：[api-docs.deepseek.com](https://api-docs.deepseek.com/)（Quick Start 页明确示例）。
- **响应结构（200，application/json）**：

  ```json
  {
    "is_available": true,
    "balance_infos": [
      {
        "currency": "CNY",
        "total_balance": "110.00",
        "granted_balance": "10.00",
        "topped_up_balance": "100.00"
      }
    ]
  }
  ```

- **关键结论 1（必须修正需求中的隐含理解）**：`total_balance` **不在响应顶层**，而是**嵌套在 `balance_infos` 数组元素内**。解析代码若在顶层找 `total_balance` 会直接失败。
- **关键结论 2**：`balance_infos` 是**数组**，可能同时含 CNY 与 USD 两个元素； monetary 字段（total_balance/granted_balance/topped_up_balance）均为**字符串**而非数字。程序应：优先取 `currency == "CNY"` 的元素（可让用户在设置中选择币种，默认 CNY），取不到则取第一个元素；字符串需 `decimal.TryParse` 转换。
- **关键结论 3**：顶层 `is_available`（bool）表示余额是否可用于 API 调用，可作为面板附加提示。
- **计费情况**：官方文档页面未显式声明该端点计费情况。需求断言"余额查询不消耗 token、不产生费用"作为既定输入接受（社区示例均直接调用该端点，风险低）；**严格证实待阶段 1 探针 B 验证**。
- 社区使用示例（Python 取 CNY total_balance 的写法）与上述结构一致；第三方兼容网关可能返回包裹在 `data` 字段下的变体结构——本项目直连官方域名 `api.deepseek.com`，不做网关兼容。社区来源：GitHub 公开代码片段（搜索结果，无固定 URL，仅佐证）。

### b) H.NotifyIcon.Wpf NuGet 包 —— 已核实，存在且活跃维护，但需锁定版本

- **存在且活跃**：作者 HavenDV（havendv），MIT 许可，总下载约 444K（日均约 1.2K），2026 年仍持续发布 dev/beta 预发布版；是 hardcodet/wpf-notifyicon 停更后的延续项目；GitHub 仓库 [github.com/HavenDV/H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) 活跃（含示例与 Discord 渠道）。来源：[nuget.org/packages/H.NotifyIcon.Wpf](https://www.nuget.org/packages/H.NotifyIcon.Wpf)。
- **版本与 .NET 8 兼容性（关键细节）**：
  - 最新稳定版 **2.4.1**（发布于 2025-12-01）目标框架为 `net10.0-windows7.0` + `net462`，**没有 net8.0 专属目标**。
  - **2.3.2**（更新于 2025-10-23）目标框架为 `net8.0-windows7.0` + `net462`，是**支持 .NET 8 的最新稳定版**。来源：[nuget.org/packages/H.NotifyIcon.Wpf/2.3.2](https://www.nuget.org/packages/H.NotifyIcon.Wpf/2.3.2)。
- **结论**：包可用、维护健康；.NET 8 项目应**在 csproj 中锁定 `H.NotifyIcon.Wpf` 版本 2.3.2**（`Version="2.3.2"`），避免 NuGet 自动升级到 2.4.x 后目标框架失配。若未来升级到 .NET 10 可顺势升级包版本。包自身宣称支持 trimming/NativeAOT（指库本身注解兼容，与宿主 WPF 应用不支持裁剪/AOT 并不矛盾，见 c 项）。
- 功能覆盖确认：支持通知、上下文菜单、ICommand 绑定、Efficiency Mode、动态图标生成——满足 R1/R2/R3 所需能力。

### c) .NET 8 WPF 单文件发布 / 裁剪 / AOT —— 已核实，支持单文件，不支持裁剪与 AOT

- **单文件发布**：`PublishSingleFile=true` 同时支持框架依赖（framework-dependent, FDD）与自包含（self-contained, SCD）两种模式；单文件应用必须按 OS/架构分别发布（win-x64 等）。来源：[learn.microsoft.com/dotnet/core/deploying/single-file/overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)。
- **两种模式差异**：
  - **FDD 单文件**：体积小（量级估计约 1–5 MB），但目标机器必须安装 **.NET Desktop Runtime 8**（WPF 桌面应用需要 Desktop Runtime，不是基础 .NET Runtime）。来源：[learn.microsoft.com/dotnet/core/install/windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。
  - **SCD 单文件**：内嵌运行时，无需目标机安装 .NET，但"体积较大，因为包含运行时与框架库"；可用 `EnableCompressionInSingleFile=true` 压缩内嵌程序集（启动时有解压开销）。体积量级估计：未压缩约 60–80 MB、压缩后约 30–40 MB——**该数字为经验量级估计，官方文档仅定性"较大"，精确数值待阶段 1 探针 D 实测**。
- **WPF 原生 DLL 不默认打包（重要坑）**：WPF 的原生库 `wpfgfx_cor3.dll`、`PresentationNative_cor3.dll`、`D3DCompiler_47_cor3.dll`、`vcruntime140_cor3.dll` 默认**不会**打进单文件 exe，而是留在 exe 旁边；官方文档明确"原生二进制是独立文件"，需设 `IncludeNativeLibrariesForSelfExtract=true` 才嵌入（运行时先解压到 `%TEMP%\.net`）。来源：同上 single-file overview；佐证：[nietras.com 单文件原生库文章](https://nietras.com/2022/01/03/bendingdotnet-move-native-libraries)、[dotnet/runtime#61279](https://github.com/dotnet/runtime/issues/61279)、[VS Developer Community 反馈](https://developercommunity.visualstudio.com/t/deploy-with-vs2022-single-exe-file-for-net-6-wpf-a/1582681)。
- **裁剪（PublishTrimmed）**：官方文档明确——"**WPF 框架大量使用反射并严重依赖运行时代码检查，裁剪分析无法保留全部必需代码；几乎没有 WPF 应用在裁剪后还能运行，因此 .NET SDK 已禁用对 WPF 的裁剪支持**"。来源：[learn.microsoft.com/dotnet/core/deploying/trimming/incompatibilities](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities)（引用 issue [dotnet/wpf#3811](https://github.com/dotnet/wpf/issues/3811)）。
- **NativeAOT**：官方 AOT 能力不支持 WPF。AOT 的限制列表包含"必须裁剪（Requires trimming）"与"无内置 COM（No built-in COM）"，而 WPF 依赖裁剪（已被禁用）且大量依赖 COM 互操作，因此 WPF 无法使用 NativeAOT（官方 AOT 支持目标表中也无 WPF）。来源：[learn.microsoft.com/dotnet/core/deploying/native-aot](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。
- **结论**：单文件发布可行，但**放弃裁剪与 AOT 幻想**；SCD 单文件体积将以数十 MB 计，需向用户明确预期（详见 §5-2）。
- **.NET 8 对 Windows 的支持矩阵**：Windows 11 全系（x64/x86/Arm64）与 Windows 10（21H2、1809、1607 LTSC/Enterprise，x64/x86/Arm64）受支持；注意官方文档已注明"Windows 10 支持限于 LTSC 和 Enterprise 版本"（Windows 10 主流支持已于 2025-10 结束的口径变化）。来源：[learn.microsoft.com/dotnet/core/install/windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)。

### d) 两个节假日 API —— 已实测可达，结构如下（含真实响应）

实测时间：2026-09-28（当天）。注：下文样本中 2025 年的日期为历史日期查询（用于取得真实的调休补班样本），非当天实测。注：下文中文在终端里曾显示乱码，为本地控制台 GBK 编码显示问题，HTTP 响应本身为合法 UTF-8 JSON（经转码校验内容正确）。

**主 API：`https://publicapi.xiaoai.me/holiday/day?date=YYYY-MM-DD`**（实测 HTTP 200）

```json
{
  "code": 0,
  "msg": "ok",
  "data": [
    {
      "daytype": 3,
      "holiday": "国庆节调休",
      "rest": 0,
      "date": "2025-10-11",
      "week": 6,
      "week_desc_en": "Saturday",
      "week_desc_cn": "星期六"
    }
  ]
}
```

实测样本归纳 `daytype` 枚举（建议程序按此映射并容错未知值）：

| daytype | holiday 样本 | rest | 含义 |
| --- | --- | --- | --- |
| 0 | "工作日" | 0 | 工作日（周一至周五） |
| 1 | "国庆节" | 1 | 法定节假日 |
| 2 | "双休日" | 1 | 正常周末 |
| 3 | "国庆节调休" | 0 | **调休补班日（发生在周末）** |

实测观察：多次请求中出现过 1 次连接超时（15 秒无响应）→ 主源稳定性一般，双源 + 本地缓存 + 降级策略确有必要。`data` 为数组（当日单元素），解析时取首元素。

**备用 API：`https://holiday.dreace.top?date=YYYY-MM-DD`**（实测 HTTP 200，响应更快）

```json
{"date":"2025-09-28","isHoliday":false,"note":"补班工作日","type":"工作日"}
{"date":"2026-10-17","isHoliday":true,"note":"周末","type":"假日"}
{"date":"2026-10-01","isHoliday":true,"note":"国庆节","type":"假日"}
{"date":"2026-09-28","isHoliday":false,"note":"普通工作日","type":"工作日"}
```

结构归纳：`isHoliday`（bool）+ `type`（"工作日" / "假日" 两值）+ `note`（"普通工作日" / "补班工作日" / "周末" / 节假日名称）。

**结论**：两源均真实可用、返回结构已确认；解析层应对两个不同 schema 分别建模，并容错字段缺失/未知枚举。计费方面两者均为公开免费服务，但无 SLA——以缓存与降级兜底。

### e) WPF 无边框面板失焦自动隐藏 —— 常用实现已确认，平台坑已识别

- **标准实现**：无边框面板窗口订阅自身 `Window.Deactivated` 事件，触发即 `Hide()`。这是 WPF 弹层类面板的通用做法，无根本性障碍。
- **已知坑与解法（均有公开资料佐证）**：
  1. **托盘切换竞态**：单击托盘图标本身会使即将弹出/刚弹出的面板 `Deactivated`（焦点被 Explorer/托盘区抢走），造成"弹出即隐藏"或"快速双击出现显示-隐藏闪跳"。解法：显示时记录时间戳，`Deactivated` 处理器内若"距显示 < 数百毫秒"则 `Dispatcher.BeginInvoke` 延迟判定；或结合鼠标是否悬停在托盘/面板上判断。佐证：[Rick Strahl《Window Activation Headaches in WPF》](https://weblog.west-wind.com/posts/2020/Oct/12/Window-Activation-Headaches-in-WPF)（托盘/间接触发的窗口激活难题）。
  2. **抢焦点与激活循环**：面板显示时默认会激活自身（抢焦点）。设 `ShowActivated=false` 可在显示时不抢焦点、不触发 Activated→Deactivated→隐藏 的循环；代价是用户需点击面板后它才获得键盘焦点（本面板以展示为主，可接受）。来源：[Window.ShowActivated Property（MS Learn）](https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.showactivated?view=windowsdesktop-10.0)。
  3. **Alt-Tab 污染**：仅 `ShowInTaskbar=false` 不足以把无边框窗口从 Alt-Tab 列表移除；需通过 `WindowInteropHelper` + `SetWindowLong` 加 `WS_EX_TOOLWINDOW` 扩展样式。佐证：[Stack Overflow: Hide a WPF form from Alt+Tab](https://stackoverflow.com/questions/56645242/hide-a-wpf-form-from-alttab)。
  4. **若改用 Popup 控件**：`StaysOpen=False` 的 Popup 会在外部鼠标按下时关闭，但**不会**在应用整体失活时关闭，且存在嵌套 Popup 首开即关的已知缺陷——因此**推荐用无边框 Window 而非 Popup** 承载面板（与需求"窗口面板"语义一致）。佐证：[dotnet/wpf#11824](https://github.com/dotnet/wpf/issues/11824)、[Microsoft Q&A 相关问题](https://learn.microsoft.com/en-us/answers/questions/5970089/in-wpf-with-vb-there-is-no-closing-of-a-popup-open)。
  5. **Topmost 归属**：面板置顶应通过 `Topmost=true` + 正确的 `Owner`（隐藏主窗口）实现，否则可能出现"宿主失焦后弹层仍悬在其他应用之上"或 z 序错乱。佐证：[DevExpress 支持案例](https://supportcenter.devexpress.com/ticket/details/t751544/)。
- **结论**：可行，坑全部有成熟解法；**具体组合（托盘点击切换 + ShowActivated + 防抖窗口期）的实际手感待阶段 1 探针 A 验证**。

### f) "调休上班的周末全天空闲"规则与节假日 API 类型映射 —— 无矛盾，映射规则明确

结合 d 项实测数据，两个 API 对"调休补班周末"的表达：

| 日期情形 | xiaoai（主源） | dreace（备用源） |
| --- | --- | --- |
| 正常周末 | `daytype=2`，holiday="双休日"，rest=1 | `isHoliday=true`，type="假日"，note="周末" |
| 调休补班周末（如 2025-10-11 周六、2025-09-28 周日） | `daytype=3`，holiday="国庆节调休"，rest=0 | `isHoliday=false`，type="工作日"，note="补班工作日" |
| 法定节假日（如 2026-10-01 周四） | `daytype=1`，rest=1 | `isHoliday=true`，type="假日"，note="国庆节" |
| 普通工作日 | `daytype=0`，rest=0 | `isHoliday=false`，type="工作日"，note="普通工作日" |

**映射结论**：

1. **无矛盾，但存在"规则覆盖 API"的显式决策点**：用户原始规则"周末一律空闲（含调休补班周末）"意味着周末判定应以**本地 `DayOfWeek` 为准**（周六/周日 → 空闲），API 结果不覆盖周末；此时 dreace 会把补班周末报成"工作日"（`isHoliday=false`），若程序盲信 `isHoliday` 就会在补班周六跑工作日时段——**必须以 DayOfWeek 优先**。
2. 节假日 API 在本规则下的实际职责收敛为：判断"周一至周五中哪些是法定节假日"（→ 空闲）。xiaoai 的 `daytype=3` 与 dreace 的 note="补班工作日" 仍可用于 UI 提示（如显示"今日调休补班"），并预留将来若用户想改为"调休周末按工作日时段"的平滑开关（数据已具备，只改判定函数；仅记录可能性，默认不实现——当前规则仍为"周末一律空闲"）。
3. 降级路径一致性：双 API 都失败时按"周一至周五为工作日"判断——该降级逻辑与"周末一律空闲"规则天然兼容（周末本来就不依赖 API）。
4. 边界提醒：法定节假日若恰逢周末，两种规则结果一致（均空闲），无冲突。

---

## 5. 已知平台限制与替代方案

1. **放弃 Win11 官方小组件板（决策已定）**：官方小组件板需要 MSIX 打包、COM 互操作、且无 XAML 设计器，开发成本高、可控性差。替代方案（系统托盘 + 点击弹出无边框面板）与既定技术栈完全兼容，是正确决策。面板定位可读取托盘区屏幕坐标将面板弹出在托盘附近。
2. **WPF 不支持裁剪/AOT → 自包含单文件体积较大**：如 §4-c 所述，WPF 裁剪被 SDK 禁用、NativeAOT 不可用。缓解：使用 `EnableCompressionInSingleFile=true` 压缩（可接受启动开销）；体积预期写入 README；如需极小体积可另发 FDD 版本并引导安装 .NET Desktop Runtime 8。探针 D 实测精确体积后回填文档。
3. **未签名 exe 的 SmartScreen / Defender 提示与侧载说明**：无数字签名、无信誉积累的 exe，首次运行时 Windows Defender SmartScreen 会弹"Windows 已保护你的电脑"蓝色警告（用户需点击"更多信息"→"仍要运行"）；通过浏览器下载的 exe 还可能被标记 Mark-of-the-Web。缓解：README 提供"右键 exe → 属性 → 解除锁定 / 或警告框选择仍要运行"的图文说明；发布时提供 SHA-256 校验值；远期可选开源签名证书（OV/个体证书对开源项目成本较高，暂不做）。来源：[Microsoft Defender SmartScreen overview（MS Learn）](https://learn.microsoft.com/en-us/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/)。
4. **单实例运行需互斥锁**：托盘应用重复启动会产生多个图标。标准做法：启动时尝试获取命名 `Mutex`（如 `Local\DeepSeekBalanceWidget` 之类固定名），获取失败则激活已有实例（可用命名 Event/管道通知其弹出面板）后退出。
5. **北京时间必须显式取时区，不得假设本地时区**：峰谷计算一律基于 `TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")` + `TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz)`（Windows 时区 ID；若日后跨平台需用 `TryFindSystemTimeZoneById` 容错并回退 IANA "Asia/Shanghai" 或固定 +08:00 偏移）。绝不可用 `DateTime.Now` 直接当北京时间。跨天检测（节假日缓存失效、日志滚动）基于该北京时间日期。
6. **托盘图标需准备/生成橙绿两色资源**：至少准备 16/20/24/32 px 多尺寸 .ico（Explorer 托盘在高 DPI 下会取更大尺寸）；橙=高峰、绿=空闲，另建议备一套灰色（节假日 API 降级状态）与错误状态图标。可用 H.NotifyIcon 的动态图标生成能力（GeneratedIcon）在运行时画，或预置静态资源；探针 A 决定采用哪种。
7. **无边框面板失焦隐藏的实现方式与坑**：见 §4-e——`Window.Deactivated` + `ShowActivated=false` + `WS_EX_TOOLWINDOW`（Alt-Tab 排除）+ 托盘切换防抖 + 正确 Owner/Topmost；另注意：点击桌面其他区域触发 Deactivated 是预期行为（自动隐藏），但"点击面板自身内部控件"不得触发（控件在窗口内部不会使窗口失活，天然安全）。圆角实现：Win11 可用 DWM `DWMWA_WINDOW_CORNER_PREFERENCE`，Win10 需自绘（WindowChrome + 边框模板或 AllowTransparency，后者有渲染性能代价）——探针 A 验证两种渲染效果。
8. **Credential Manager P/Invoke 的字符集与持久化标志**：必须调用 **W 变体**（`CredReadW`/`CredWriteW`），C# 声明用 `CharSet = CharSet.Unicode`（wincred.h 的 CredWrite 宏按 UNICODE 宏二选一，混用会出错）；结构体 `CREDENTIAL` 用 `CRED_TYPE_GENERIC(=1)`，密钥写入 `CredentialBlob`（按字节计大小，上限 5×512 字节，API Key 完全够用）；`Persist` 取 `CRED_PERSIST_LOCAL_MACHINE(=2)`（本机所有会话可见，推荐）或 `CRED_PERSIST_ENTERPRISE(=3)`（随漫游配置文件同步；无漫游域环境时效果同本机，家用机推荐 LOCAL_MACHINE）；`TargetName` 建议形如 `DeepSeekBalanceWidget_ApiKey`（微软建议服务方前缀命名约定）。第三方 CredentialManagement 库多年未更新，建议直接自写约百行 P/Invoke。来源：[CredWriteA function（MS Learn）](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritea)、[CREDENTIAL structure（MS Learn）](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentiala)。
9. **.NET 8 运行时依赖矩阵**：框架依赖单文件要求目标机装有 .NET Desktop Runtime 8；自包含则免安装。Windows 10 支持口径已收窄至 LTSC/Enterprise（主流支持 2025-10 结束），自包含发布在普通 Win10/11 家庭版机器上仍可运行，但 README 应写明"建议 Windows 10 22H2 及以上 / Windows 11"。

---

## 6. 界面预览与用户选型流程（阶段 0.5 规则复述）

- **候选规模**：4 种风格 × 每种 3 个候选项 = **12 个候选项**。
- **每个候选项必须覆盖**：
  - 两种状态：高峰（橙色）/ 空闲（绿色）；
  - 两套主题：浅色 / 深色；
  - 全部面板元素：状态标签、总余额（大号加粗）、倒计时文字、进度条、上次刷新时间（极小灰字）、设置齿轮、刷新按钮。
- **形式**：HTML 预览页（纯静态 HTML+CSS，浏览器直接打开，无需运行 WPF），按风格分组呈现 12 个候选，并展示同一候选在"高峰/空闲 × 浅色/深色"四种组合下的效果。
- **硬门控**：用户从 12 个候选中明确选定风格与具体候选之前，**不得进入正式实现**（不创建正式 WPF 项目结构）。
- **产出物约定**：预览页放置于 `docs/ui-preview/` 下（本阶段不创建，仅约定；开源整理时已归位至 `design/ui-preview/`）；用户选型结果记录后作为正式实现的 UI 基线。

---

## 7. 风险清单与缓解措施（按风险等级排序）

**中等风险（无高风险项）**

| 编号 | 风险 | 影响 | 缓解措施 |
| --- | --- | --- | --- |
| RM-1 | 余额解析按需求字面"顶层 total_balance"实现会失败（实际嵌套于 `balance_infos[]`，字符串、多币种） | 面板永远显示错误/空余额 | 以本报告 §4-a 结构为准实现解析：遍历 `balance_infos`，优先 CNY，字符串转 decimal；探针 B 用真实 `<YOUR_API_KEY>` 回归验证 |
| RM-2 | WPF 单文件发布体积大且原生 DLL 默认不打包 | 交付物并非"严格单文件"、体积超预期 | `IncludeNativeLibrariesForSelfExtract=true` + `EnableCompressionInSingleFile=true`；探针 D 实测体积并回填；README 说明解压目录 `%TEMP%\.net` 行为 |
| RM-3 | 主节假日 API（xiaoai）实测出现过超时，双源无 SLA | 峰谷判定降级频繁 | 主备双源 + 当日缓存 + 指数退避（1/5/15 分钟）；缓存当天成功结果，避免重复请求；降级时面板显示提示图标 |
| RM-4 | 托盘左键弹出面板与 Deactivated 隐藏的竞态（弹出即隐藏/快速双击闪跳） | 核心交互手感差 | 显示时间戳 + 延迟防抖判定 + `ShowActivated=false`；探针 A 专门验证交互手感 |
| RM-5 | H.NotifyIcon.Wpf 版本错配（2.4.x 无 net8.0 目标） | NuGet 升级后编译告警或运行期行为异常 | csproj 显式锁定 2.3.2；升级 .NET 10 时再评估升级包版本 |
| RM-6 | 主题"跟随系统"需读取注册表并响应系统主题切换事件 | 切换主题后小组件不跟随 | 读 `AppsUseLightTheme` + 监听 WM_SETTINGCHANGE（或巡查心跳低频轮询）；深浅色资源字典统一切换入口 |

**低风险**

| 编号 | 风险 | 缓解措施 |
| --- | --- | --- |
| RL-1 | Credential Manager P/Invoke 字符集/结构体 Marshal 错误导致读写失败 | 按 §5-8 使用 W 变体 + Unicode；探针 B 做写入-读取-更新-删除往返测试 |
| RL-2 | 未签名 exe 触发 SmartScreen"Windows 已保护你的电脑" | README 图文说明侧载步骤；发布 SHA-256 校验值 |
| RL-3 | HKCU Run 自启被杀软提示/拦截 | 文档说明；提供设置窗口开关由用户主动写入 |
| RL-4 | 个别机器时区数据库异常导致找不到 "China Standard Time" | `TryFindSystemTimeZoneById` 失败时回退固定 UTC+8 偏移并记日志 |
| RL-5 | 节假日 API 未来变更响应结构 | 解析层容错（未知字段忽略、枚举未知值走降级）；探针 C 保留真实样本作回归 |
| RL-6 | 单文件 API 不兼容点（如 `Assembly.Location` 返回空串）影响资源加载 | 资源一律嵌入（ResourceDictionary/嵌入图标），路径用 `AppContext.BaseDirectory`；探针 D 覆盖 |
| RL-7 | 余额字符串解析（"110.00"）区域设置差异 | 统一 `InvariantCulture` 解析与格式化 |

---

## 8. 探针计划概览（阶段 1 前置探针）

> 探针均为一次性最小可运行验证工程，验证通过即销毁或归档，不进入正式代码库。

### 探针 A：托盘 + 弹出面板骨架

- **验证内容**：H.NotifyIcon.Wpf 2.3.2 在 .NET 8 下创建托盘图标（右键菜单：打开面板/设置/退出）；左键单击弹出/隐藏切换；320×230 无边框圆角面板；`Deactivated` 失焦自动隐藏；`ShowActivated=false` 不抢焦点；`WS_EX_TOOLWINDOW` 排除 Alt-Tab；托盘切换防抖手感；橙/绿图标切换；单实例 Mutex。
- **验收标准**：连续 20 次左键切换无"弹出即隐藏"；面板不出现在 Alt-Tab；点击桌面其他区域面板正确隐藏；双屏/不同 DPI 下图标清晰。
- **失败降级（P1）**：托盘弹出手感无法达标 → 降级为"仅托盘右键菜单打开/关闭面板 + 托盘图标换色保留"，去掉左键切换。

### 探针 B：余额接口 + Credential Manager

- **验证内容**：用占位符密钥占位（真实密钥由用户在探针运行时注入，**不得写入代码或日志**）调用 `GET https://api.deepseek.com/user/balance`（Bearer）；验证 `balance_infos[]` 嵌套解析（优先 CNY）、字符串转 decimal、`is_available` 读取；401（无效 Key）/断网/超时/畸形 JSON 四类异常路径；CredWriteW/CredReadW 往返（写入→读取→覆盖→删除），`CRED_PERSIST_LOCAL_MACHINE` 持久性；日志全量检查无 Key 无 Authorization 头。
- **验收标准**：四类异常均被捕获且面板文案正确；凭据在"Windows 凭据管理器"UI 中可见且重启后仍可读；日志 0 泄漏。
- **失败降级（P2）**：接口或凭据链路不可用 → 降级为设置窗口手动输入余额（仅本地展示，不涉及密钥），峰谷功能不受影响。

### 探针 C：峰谷判断 + 节假日 API

- **验证内容**：双 API 对拍多日期样本（实测样本：2026-10-01 法定假、2026-10-17 正常周末、2025-10-11 与 2025-09-28 调休补班周末、2026-09-28 普通工作日）；`daytype`/`type` 枚举映射与未知值容错；"周末一律空闲"覆盖逻辑（补班周末不跑工作日时段）；双 API 失败降级为周一至周五逻辑 + 面板提示图标；倒计时与进度条边界（切换瞬间、23:59:59、跨天重取节假日数据、`China Standard Time` 时区转换）；退避重试 1/5/15 分钟序列与成功重置。
- **验收标准**：给定任意北京时间时刻，状态与倒计时与手算一致；断网状态下峰谷照常显示；降级图标出现时机正确。
- **失败降级（P3）**：双源均不可用 → 长期运行于"周一至周五工作日 + 周末空闲"本地逻辑，面板常驻提示图标。

### 探针 D：单文件发布 + Win10/11 兼容

- **验证内容**：发布命令实测（示意，非最终脚本）：

  ```powershell
  # 示意：自包含单文件 + 内嵌原生库 + 压缩（具体参数以探针 D 实测为准）
  dotnet publish -c Release -r win-x64 `
    -p:PublishSingleFile=true -p:SelfContained=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true
  ```

  验证产物文件清单（是否严格单文件）、体积（FDD vs SCD vs SCD+压缩）、无 .NET 运行时的干净环境运行、Windows 10 与 Windows 11 各一台实测（托盘/面板/圆角渲染差异）、开机自启注册表写入与移除、卸载残留（凭据、注册表项）清理说明。
- **验收标准**：干净 Win10/11 无运行时环境双击即可运行；体积数据回填本报告与 README。
- **失败降级（P4）**：单文件有不可解决问题 → 降级为普通文件夹发布（含 exe + 原生 DLL）+ README 安装说明，或提供 FDD 版 + 运行时安装引导。

---

## 9. 总结论

**项目整体可行性结论：可行。** 全部 16 条功能需求、数据源规则、峰谷节假日规则、安全要求与既定交付形态均无"不可行"项；技术栈选型（C# + WPF + .NET 8 + H.NotifyIcon.Wpf + HttpClient + System.Text.Json + Credential Manager）组合成立。两项需要在实现中"带修正执行"的关键点：

1. **余额解析必须按嵌套结构实现**（`total_balance` 在 `balance_infos[]` 元素内、字符串类型、多币种），不能按需求原文"顶层 total_balance"的字面理解写解析代码。
2. **单文件 exe 是"自包含压缩单文件 + 可能的内嵌解压"形态**：WPF 无裁剪/AOT，SCD 单文件体积为数十 MB 量级（探针 D 实测），且需 `IncludeNativeLibrariesForSelfExtract` 才能消灭 exe 旁的原生 DLL；用户需接受该体积预期。

**进入阶段 0.5 的前置条件**：

1. 用户评审并确认本报告（尤其确认 §4-a 的解析结构修正与 §5-2 的体积预期）；
2. 用户知晓探针计划（§8）及四条降级路径（P1–P4）并认可；
3. 之后立即进入阶段 0.5：产出 12 候选 HTML 预览页（4 风格 × 3 候选，覆盖高峰/空闲 × 深浅色与全部面板元素）；
4. **硬门控**：用户从 12 个候选中选定前，不得开始任何正式实现（含创建正式项目结构与 git init）。
5. 确认"余额查询接口不消耗 token"为待探针 B 验证的假设。

---

### 附：本报告引用来源汇总

- DeepSeek 余额接口文档：https://api-docs.deepseek.com/api/get-user-balance
- DeepSeek 认证方式：https://api-docs.deepseek.com/
- H.NotifyIcon.Wpf 包页：https://www.nuget.org/packages/H.NotifyIcon.Wpf
- H.NotifyIcon.Wpf 2.3.2 页：https://www.nuget.org/packages/H.NotifyIcon.Wpf/2.3.2
- H.NotifyIcon 仓库：https://github.com/HavenDV/H.NotifyIcon
- 单文件部署概览：https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview
- 已知裁剪不兼容（WPF 禁用裁剪）：https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities
- Native AOT 概览（限制列表）：https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/
- WPF 裁剪兼容性跟踪 issue：https://github.com/dotnet/wpf/issues/3811
- .NET Windows 安装与支持矩阵（含 Desktop Runtime 说明）：https://learn.microsoft.com/en-us/dotnet/core/install/windows
- CredWriteA 函数：https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritea
- CREDENTIAL 结构（Persist 标志）：https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentiala
- Microsoft Defender SmartScreen overview：https://learn.microsoft.com/en-us/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/
- Window.ShowActivated 属性：https://learn.microsoft.com/en-us/dotnet/api/system.windows.window.showactivated?view=windowsdesktop-10.0
- Rick Strahl：Window Activation Headaches in WPF：https://weblog.west-wind.com/posts/2020/Oct/12/Window-Activation-Headaches-in-WPF
- Stack Overflow：Hide a WPF form from Alt+Tab：https://stackoverflow.com/questions/56645242/hide-a-wpf-form-from-alttab
- dotnet/wpf#11824（Popup 自动关闭缺陷）：https://github.com/dotnet/wpf/issues/11824
- dotnet/runtime#61279（单文件 WPF 多文件残留）：https://github.com/dotnet/runtime/issues/61279
- nietras.com：Move Native Libraries（WPF 原生库与单文件）：https://nietras.com/2022/01/03/bendingdotnet-move-native-libraries
- 节假日 API（主）：https://publicapi.xiaoai.me/holiday/day?date=YYYY-MM-DD（2026-09-28 实测）
- 节假日 API（备）：https://holiday.dreace.top?date=YYYY-MM-DD（2026-09-28 实测）
