# 部署文档 — DeepSeek 余额峰谷监控小组件（v0.5.0-mvp）

本文档面向：① 从源码复现单文件发布的构建/发布者；② 在 Windows 机器上侧载运行与卸载的最终用户。
（git 初始化、提交序列与 GitHub Release 创建步骤见 [git-and-release.md](git-and-release.md)。）

---

## 1. 构建环境要求

| 项 | 要求 |
| --- | --- |
| OS | Windows 10/11 x64（本发布实测：Windows 11 build 26200） |
| .NET SDK | **.NET 8 SDK**（本发布实测 8.0.425）；WPF 工作负载随 SDK 自带 |
| 网络 | NuGet 包还原（仅直接依赖 `H.NotifyIcon.Wpf` **2.3.2**，版本锁定禁 2.4.x） |
| 源码 | `src/DeepSeekBalanceWidget/`（net8.0-windows + WPF；`TreatWarningsAsErrors=true`，发布要求 0 警告 0 错误） |

构建验证（可选，发布前建议）：

```text
dotnet build src/DeepSeekBalanceWidget/DeepSeekBalanceWidget.csproj -c Release
→ 0 警告 0 错误
dotnet test tests/DeepSeekBalanceWidget.IntegrationTests/DeepSeekBalanceWidget.IntegrationTests.csproj -c Release
→ 43/43 通过（FB-1 修复后，2026-10-01；v1.1 基线 41/41、阶段 5 基线 9/9）
```

## 2. 单文件发布（完整步骤与参数解释）

在仓库根目录执行（PowerShell 或 cmd，` 反引号续行为 PowerShell 语法）：

```powershell
dotnet publish src/DeepSeekBalanceWidget/DeepSeekBalanceWidget.csproj `
  -c Release `
  -r win-x64 `
  -p:PublishSingleFile=true `
  -p:SelfContained=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o release/publish
```

| 参数 | 作用 | 不加的后果 |
| --- | --- | --- |
| `-c Release` | 发布配置 | Debug 体积大、含调试代码 |
| `-r win-x64` | RID 特定发布 | 单文件的强制要求（无 RID 不能 PublishSingleFile） |
| `-p:PublishSingleFile=true` | 打包为单个 exe | 输出散装 DLL/deps.json 等几十个文件 |
| `-p:SelfContained=true` | 内嵌 .NET 8 运行时（免装 .NET） | 目标机必须安装 .NET 8 Desktop Runtime（FDD 形态） |
| `-p:IncludeNativeLibrariesForSelfExtract=true` | WPF 原生库（wpfgfx 等）一并嵌入，首启解压到 `%TEMP%\.net` | 原生 DLL 散落在 exe 旁，破坏严格单文件 |
| `-p:EnableCompressionInSingleFile=true` | 压缩内嵌内容 | 体积 154MB → 63MB（无启动差异，实测必加） |
| `-p:DebugType=None -p:DebugSymbols=false` | 不生成 .pdb | 目录里会多出 `DeepSeekBalanceWidget.pdb` 伴随文件 |

**WPF 约束**：工程已固定 `<PublishTrimmed>false` `<PublishAot>false`（WPF 不支持裁剪/AOT，不要开启）。发布耗时约 1 分钟（压缩占大头）。

### 2.1 产物核验

1. **严格单文件**：`release/publish/` 内有且仅有 `DeepSeekBalanceWidget.exe`（无 pdb/deps.json/runtimeconfig.json/原生 DLL）。
2. **体积**：66,202,308 B（63.13 MiB）。参照系：探针 D 同参数发布探针 A 工程为 71,910,187 B（68.58 MiB）——差异来自被测负载不同（正式版代码精简），属合理偏差；**正式版口径以 66,202,308 B 为准**，未来版本变化应解释原因。（v1.1 重发布：新增面板停靠/拖动/钉住功能后体积与 v0.5.0-mvp 持平；FB-1 修复版（2026-10-01）体积亦持平，仍为 66,202,308 B。）
3. **SHA-256**：

```powershell
Get-FileHash .\release\publish\DeepSeekBalanceWidget.exe -Algorithm SHA256
# 07E14926EB5302664AE1A7729A83ED69DAFEF0A8877CC2C52AB48316F7343312（FB-1 修复版，2026-10-01 重发布）
# 历史值 5DDF2EBE3651E5E111C4B4FAB08913BA0BA3E51099842AB4E5444440FE628B10（v1.1，2026-09-30）已被取代
# 历史值 0A2A2FAFBCB97ED94E4AF6F75DC411340825C6D7DED56FFD7ED62C5A8B99E359（v0.5.0-mvp）已作废
```

4. **签名与 MotW**：`Get-AuthenticodeSignature` = NotSigned；本机构建无 Zone.Identifier 流。对外分发（下载场景）将触发 SmartScreen（见 §4）。

### 2.2 免 .NET 环境冒烟（发布者自检）

1. 把 exe **单独**复制到一个空目录；
2. 以最小环境启动（PowerShell）：

```powershell
$env:DOTNET_ROOT = $null; $env:PATH = 'C:\Windows\System32;C:\Windows'
Start-Process .\DeepSeekBalanceWidget.exe
```

3. 预期：首启自解压 5 个 WPF 原生库（~7.8MB）到 `%TEMP%\.net\DeepSeekBalanceWidget\`；约 **1 秒**内托盘出现状态圆点（本发布实测冷启 976ms）；右键图标可退出，退出后 exe 目录不新增任何文件（日志在 `%APPDATA%`）。

## 3. 侧载运行（最终用户）

1. 将 `DeepSeekBalanceWidget.exe` 放到任意目录（如桌面/工具目录），双击运行。
2. 新装图标通常在任务栏**溢出弹层**（点任务栏"^"展开）；建议右键任务栏将其固定到可见区。
3. 配置 API Key（可选）：右键图标 → 设置 → 粘贴 `sk-` 开头的 Key → "保存 Key"。Key 只进 Windows 凭据管理器（本机），不落文件、不进日志。
4. 退出：托盘右键 → 退出。

## 4. SmartScreen（未签名侧载）

- exe 未签名。**本地直接构建/复制**的文件无 Mark-of-the-Web，运行无提示（本发布实测）。
- **经网络分发**（浏览器/聊天工具保存）的文件带 MotW，首次运行会弹"Windows 已保护你的电脑"：

```text
┌─────────────────────────────────────────────┐
│  Windows 已保护你的电脑                      │
│  Microsoft Defender SmartScreen 阻止了……    │
│                                             │
│  [ 不运行 ]                    [ 更多信息 ▼ ] │
└─────────────────────────────────────────────┘
```

处理：点 **"更多信息"** → 出现 **"仍要运行"** 按钮 → 点击即可（仅首次）。企业环境可由管理员解除哈希拦截。根治方案为代码签名（发布策略项，不在 MVP 范围）。

## 5. 数据位置与完全卸载

### 5.1 应用数据一览（均在当前用户域，无系统级写入）

| 位置 | 内容 | 用途/处置 |
| --- | --- | --- |
| `%APPDATA%\DeepSeekBalanceWidget\logs\app-YYYYMMDD.log` | 运行日志（UTF-8，按日滚动，全量脱敏） | 故障排查入口；可随时删除 |
| `%APPDATA%\DeepSeekBalanceWidget\settings.json` | 主题/刷新间隔/手动余额设置 | 删除即恢复默认（跟随系统主题、30 分钟间隔） |
| `%APPDATA%\DeepSeekBalanceWidget\icons\` | 运行时托盘图标（4 枚 ICO） | 运行中生成、退出自动删除；无需手动处理 |
| Windows 凭据管理器 `DeepSeekBalanceWidget_ApiKey` | API Key（Generic，仅本机可读） | 见 5.2 删除 |
| `%TEMP%\.net\DeepSeekBalanceWidget\` | 单文件首启解压的原生库（~7.8MB） | .NET 标准缓存，删除无副作用 |
| `HKCU\...\CurrentVersion\Run`（仅勾选自启后） | `DeepSeekBalanceWidget` 自启项 | 见 5.2 移除 |

### 5.2 完全卸载步骤

```powershell
# 1) 退出应用（托盘右键 → 退出；确认无进程）
Get-Process DeepSeekBalanceWidget -ErrorAction SilentlyContinue   # 应无输出

# 2) 删除凭据（若配置过 Key）
cmdkey /delete:DeepSeekBalanceWidget_ApiKey

# 3) 移除自启（若开启过）
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
  -Name 'DeepSeekBalanceWidget' -ErrorAction SilentlyContinue

# 4) 删除数据目录
Remove-Item "$env:APPDATA\DeepSeekBalanceWidget" -Recurse -Force

# 5) 删除 exe 本体与（可选）解压缓存
Remove-Item <exe 路径>
Remove-Item "$env:TEMP\.net\DeepSeekBalanceWidget" -Recurse -Force -ErrorAction SilentlyContinue
```

无服务、无驱动、无机器级注册表写入——以上即完全卸载。

## 6. Windows 10 人工验证清单（约 5 分钟，发布前必做）

> 依据：本机仅 Win11 实测（全量通过）；Win10 未实测、未假装。源码零 Win11-only API + 自包含运行时 ⇒ 技术兼容；.NET 8 官方支持矩阵 Win10 仅 **LTSC/Enterprise** 口径（消费版 22H2 已于 2025-10-14 停止支持——技术上无不运行依据，但属"能跑≠受支持"）。
> 准备：把 `DeepSeekBalanceWidget.exe` **单独**复制到桌面（不带任何伴随文件）。

1. （30s）双击运行：**不应出现** "You must install .NET Desktop Runtime" 对话框；任务栏通知区出现状态圆点图标（若被折叠，点任务栏"^"展开）。颜色应与当时时段一致（工作日 9–12/14–18 为橙，其余绿）。
2. （30s）悬停图标：tooltip 显示"DeepSeek 余额小组件 · 高峰/空闲时段"。
3. （60s）左键单击图标：光标左上方弹出 A1 面板（圆角正常、状态胶囊、倒计时、进度条）；点击面板以外任意处：面板隐藏。
4. （60s）右键图标：菜单出现三项（打开面板/设置/退出）；点"设置"：设置窗口打开、主题/间隔/Key 各项可操作，Esc 或"关闭"退出。
5. （30s）再次右键 → 点"退出"：图标消失；任务管理器确认无 `DeepSeekBalanceWidget.exe` 进程。
6. （30s）再次双击运行（可选）：应明显快于首次（`%TEMP%\.net` 解压缓存命中）；随后再次"退出"清理。
7. （30s，可选）同机若装有 .NET 8 Desktop Runtime，可另测 FDD 形态；未装则 FDD 弹"需要安装 .NET"——这正是 SCD 版存在的意义。

任何一步失败：记录第几步与现象（截图更佳），连同 `%APPDATA%\DeepSeekBalanceWidget\logs\` 当日日志作为定位输入。

## 7. 故障排查

**日志位置**：`%APPDATA%\DeepSeekBalanceWidget\logs\app-YYYYMMDD.log`（UTF-8；行格式 `时间 [级别] [sess:tag] 消息`；Key/Authorization 恒为脱敏形态）。会话 tag 区分多次启动。

| 现象 | 日志特征 | 处置 |
| --- | --- | --- |
| 双击无反应/托盘不出 | 无新会话行，或"单实例检测：已有实例在运行" | 已有实例在跑（任务栏/溢出区找图标）；或按杀毒软件拦截排查 |
| 图标不变色 | 无"状态翻转沿"行 | 核对系统时间/时区；日志"节假日"行确认数据源 |
| 余额区"API Key 无效，请检查设置" | `HTTP 401（Key 无效）` | Key 失效/错误 → 设置中重存或清除；1/5/15 分钟自动退避，成功即恢复 |
| 余额区"余额获取失败" | `HttpRequestException←SocketException(...)` / `超时` | 断网/DNS/代理问题；恢复后手动刷新即可（C-3 立即重算） |
| 余额区长期显示"请先在设置中配置 API Key" | `未配置 Key，跳过本次余额请求` | 正常未配置态；峰谷功能不受影响 |
| 底行出现 ⚠ 小图标 | `[holiday] …双源失败 → 进入退避` | 节假日双源不可用（降级为周一至五判断）；网络恢复自动拉回 |
| 首启较慢 | — | 首启解压 ~1s 内（实测 976ms）；二次启动命中缓存更快 |
| 日志中出现 `sk-***MASKED***` | — | 脱敏层正常工作的痕迹，非泄漏 |

错误分类对照（BalanceStatus → UI 文案）：

| 分类 | 触发 | UI 显示 |
| --- | --- | --- |
| Success | HTTP 200 + 解析成功（含 0 余额） | `¥ 数字`（0.00 附"余额不可调用"） |
| NotConfigured | 凭据不存在/读取失败 | "请先在设置中配置 API Key"（整块可点击） |
| Unauthorized | HTTP 401 | "API Key 无效，请检查设置"（整块可点击） |
| NetworkFailure | DNS 失败/超时/其他非 200 | "余额获取失败" |
| MalformedResponse | 畸形 JSON/字段格式非法 | "余额获取失败" |

---

*发布命令、参数与核验数值均为 2026-09-30 实测实录；证据截图与日志摘录见 `release/evidence/`、`docs/test-report.md` §3。*
