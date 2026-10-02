# ADR-005：发布形态 = SCD 自包含·压缩·单文件

- 状态：已接受（探针 D 实测定夺）
- 日期：2026-09-28（探针）/ 2026-09-30（正式版口径）
- 关联：[deployment.md §2](../deployment.md) · [probe-results.md](../probe-results.md)

## 背景

交付目标是"单文件 exe 免安装侧载"。.NET 8 WPF 的发布形态候选：FDD 单文件（依赖目标机运行时）、SCD 自包含单文件（压缩 / 不压缩）、普通文件夹发布。WPF 约束：不支持裁剪（PublishTrimmed）与 NativeAOT，且原生 DLL（wpfgfx 等）默认散落 exe 旁。

## 决策

**SCD 自包含 + 压缩 + 单文件**，发布参数：

```text
dotnet publish -c Release -r win-x64
  -p:PublishSingleFile=true -p:SelfContained=true
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
  -p:DebugType=None -p:DebugSymbols=false
```

`release/` 目录不入库（见 [.gitignore](../../.gitignore)）；用户从 GitHub Release 附件获取 exe，或按上述命令自建。

## 理由（探针 D 实测）

| 形态 | 体积（探针 A 工程 / 正式版） | 干净环境运行 | 结论 |
| --- | --- | --- | --- |
| FDD 单文件 | 2.11 MiB / 同量级 | 依赖目标机 .NET 8 Desktop Runtime | 备选（体积敏感场景） |
| **SCD 压缩单文件** | **68.58 MiB / 63.13 MiB（66,202,308 B）**〔探针期与阶段 5 实测值；v1.2 起正式版为 63.14 MiB / 66,206,404 B，见 [deployment.md §2](../deployment.md)〕 | **成功**（最小 PATH、无 DOTNET_ROOT） | **采用** |
| SCD 不压缩单文件 | 154.55 MiB | 成功 | 否决（无启动收益） |

1. **严格单文件达成**：三形态均 1 个 exe、0 伴随文件；5 个 WPF 原生库内嵌，首启解压 ~7.8MiB 到 `%TEMP%\.net` 仅 **+36ms**（NVMe 实测）——"自包含 + 单文件"的矛盾由 `IncludeNativeLibrariesForSelfExtract` 化解。
2. **免环境依赖**：目标机无需安装 .NET（这正是 FDD 版弹"需要安装 .NET"时 SCD 的存在意义）；干净目录真跑冒烟全链通过。
3. **启动代价可忽略**：正式版冷启托盘就绪 976ms（含解压），热启 469–976ms。
4. **可复现**：验收方独立重发布产物与执行者产物 SHA-256 逐字节一致；发布命令/参数/预期体积写入 [deployment.md](../deployment.md) 可照抄。

## 后果

- 正面：用户体验 = 下载一个文件双击；分发物可哈希校验防篡改。
- 负面：63 MiB 体积（对照 FDD 的 2 MiB）；未签名 exe 经网络分发触发 SmartScreen（分发策略项，见 [deployment.md §4](../deployment.md)，代码签名在路线图）。
- 中性：`%TEMP%\.net` 解压缓存为 .NET 标准机制，删除无副作用。

## 替代方案

| 方案 | 结论 |
| --- | --- |
| FDD 单文件 | 备选保留（2 MiB，但要求用户装运行时——支持成本转嫁） |
| SCD 不压缩 | 否决（体积翻倍、启动无差） |
| 普通文件夹发布 | 否决（几十个散落文件，违背免安装单文件目标） |
| MSIX 安装包 | 否决（与 [ADR-001](ADR-001-windows-widgets-board.md) 同因：安装形态 + 签名门槛） |
| NativeAOT / 裁剪 | 不可行（WPF 不支持） |
