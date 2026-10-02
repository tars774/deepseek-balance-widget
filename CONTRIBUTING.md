# 参与贡献（Contributing）

感谢关注本项目！欢迎 Issue 报告问题与 PR 贡献代码。

## 环境要求

| 项 | 要求 |
| --- | --- |
| OS | Windows 10/11 x64（WPF 工程，只能在 Windows 上构建/运行） |
| .NET SDK | **.NET 8 SDK**（WPF 工作负载随 SDK 自带） |
| IDE（可选） | Visual Studio 2022 / VS Code + C# 扩展 / Rider |
| 网络 | NuGet 包还原（直接依赖仅 `H.NotifyIcon.Wpf` **2.3.2**，版本锁定禁 2.4.x——2.4.x 无 net8.0 目标） |

## 构建与测试

```powershell
# 还原 + 构建（项目 TreatWarningsAsErrors=true，要求 0 警告 0 错误）
dotnet build src/DeepSeekBalanceWidget/DeepSeekBalanceWidget.csproj -c Release

# 集成测试（56 用例，真实 WPF 组件 + 假时钟）
dotnet test tests/DeepSeekBalanceWidget.IntegrationTests/DeepSeekBalanceWidget.IntegrationTests.csproj -c Release
```

注意：集成测试会创建真实 WPF 窗口/托盘组件（STA + Dispatcher 自建线程），请在**交互桌面会话**中运行（远程桌面/CI 环境如无桌面会话可能失败——CI 首推结果待验证，见 [.github/workflows/build.yml](.github/workflows/build.yml) 注释）。测试不访问网络、不触碰真实凭据。

发布单文件 exe 的完整命令与参数解释见 [docs/deployment.md](docs/deployment.md)。

## 提交规范（Conventional Commits）

```text
feat: 支持面板右缘吸附
fix: 修复拖动起点位移丢失
docs: 补充 ADR-003 心跳选型
test: 新增面板吸附边界用例
refactor: 抽取 PanelPlacement 纯函数
chore: 调整 CI 触发分支
```

- 类型：`feat` / `fix` / `docs` / `test` / `refactor` / `perf` / `build` / `ci` / `chore`
- 范围（可选）：`feat(tray): ...`、`fix(panel): ...`
- 一次提交一件事；行为变更请同步更新相关文档（`docs/ui-selection.md` 为 UI 绑定规范，改 UI 先改规范）。

## PR 流程

1. Fork（或从 main 拉功能分支）→ 改动 → 本地构建 + 全量测试通过。
2. 按 [PULL_REQUEST_TEMPLATE](.github/PULL_REQUEST_TEMPLATE.md) 填写改动说明与测试证明，勾选自检清单。
3. CI（[build.yml](.github/workflows/build.yml)）通过。
4. 维护者 review 后合并（main 分支 + PR 合并，不直接 push main）。

## 行为变更 / 新功能建议

建议先开 Issue 讨论（尤其是涉及 [docs/ui-selection.md](docs/ui-selection.md) 绑定规范或 [docs/adr/](docs/adr/) 已记录决策的变更），达成一致后再动手，避免无效返工。

## 安全提醒

**提交 Issue / PR / 截图时切勿包含 API Key、Token 或个人数据**；本地方便调试产生的日志、配置样例请先脱敏。漏洞类问题请勿公开 Issue，按 [SECURITY.md](SECURITY.md) 私下报告。
