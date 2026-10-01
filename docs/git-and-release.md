# Git 初始化与发布操作清单

> 本文档是给仓库所有者的**操作清单**：从零初始化本地仓库、按真实交付顺序构造提交历史、推送 GitHub、打标签、创建 Release。
> 上传由所有者自行执行；本文不代执行任何 git 命令。

## 0. 前置确认

- [ ] 已在 GitHub 创建空仓库（**不要**勾选自动生成 README/.gitignore/License，避免与本地冲突）；下文以 `<YOUR_REPO_URL>` 指代仓库地址，`<YOUR_GITHUB_USERNAME>` 指代用户名。
- [ ] 根目录 `.gitignore` 已生效（`release/`、`qa-tmp/`、`docs/phase-records/`、`probes/`、`*.log` 不入库）。
- [ ] 全局配置：`git config --global user.name` / `user.email` 已设置。

## 1. 初始化与首次提交序列

在项目根执行 `git init` 后，按**真实交付顺序**分批 add + commit（这样 `git log` 就是项目的时间线：探针→方案→UI→实现→测试→发布→开源）。建议的提交序列（Conventional Commits，按实际交付排列，可按需增删）：

```bash
git init

# ── 阶段 0–1：可行性与探针（结论以文档形式入库）──────────────────
git add docs/feasibility-report.md
git commit -m "docs: 可行性分析与技术事实核查（阶段 0）"

git add design/ui-preview/index.html
git commit -m "docs(ui): 12 候选界面预览页（阶段 0.5）"

git add docs/ui-selection.md
git commit -m "docs(ui): 固化 A1 选型规范（320x230 布局 + 四态色板，含 v1.1 行为增补）"

# ── 阶段 2–3：方案与巡查设计 ────────────────────────────────────
git add docs/final-technical-plan.md
git commit -m "docs: 最终技术方案与降级策略（D-01~D-20 决策清单）"

git add docs/scheduler-design.md
git commit -m "docs(scheduling): 定时巡查机制设计（伺服式心跳，探针 E 实测校准）"

# ── 阶段 4：正式实现（可按模块拆分提交）─────────────────────────
git add src/DeepSeekBalanceWidget.sln src/DeepSeekBalanceWidget/DeepSeekBalanceWidget.csproj src/DeepSeekBalanceWidget/app.manifest
git commit -m "build: .NET 8 WPF 工程骨架（H.NotifyIcon.Wpf 2.3.2 锁定）"

git add src/DeepSeekBalanceWidget/Infrastructure/
git commit -m "feat(infra): 时钟抽象/北京时间/日志脱敏/单实例 Mutex/主题跟随"

git add src/DeepSeekBalanceWidget/Credentials/ src/DeepSeekBalanceWidget/Balance/
git commit -m "feat(balance): 余额客户端与嵌套解析（四类异常精确分类，Key 仅存凭据管理器）"

git add src/DeepSeekBalanceWidget/Peak/ src/DeepSeekBalanceWidget/Scheduling/
git commit -m "feat(peak): 峰谷引擎 + 节假日双源缓存退避 + 1s 伺服心跳调度器"

git add src/DeepSeekBalanceWidget/Tray/
git commit -m "feat(tray): add tray icon with dynamic peak/idle icons（运行时渲染手写 ICO，翻转沿 NIM 重建）"

git add src/DeepSeekBalanceWidget/Panel/ src/DeepSeekBalanceWidget/Settings/ src/DeepSeekBalanceWidget/App.xaml src/DeepSeekBalanceWidget/App.xaml.cs src/DeepSeekBalanceWidget/Assets/
git commit -m "feat(panel): A1 无边框面板（双向防抖/失焦隐藏）与设置窗口"

# v1.1 行为变更
git add src/DeepSeekBalanceWidget/Panel/PanelPlacement.cs
git commit -m "feat(panel): implement edge-docked draggable panel (v1.1)（4px 阈值 + 28px 右缘吸附 + 位移补偿）"

git add src/DeepSeekBalanceWidget/Panel/PanelWindow.xaml* src/DeepSeekBalanceWidget/Tray/TrayController.cs src/DeepSeekBalanceWidget/Settings/
git commit -m "feat(panel): add pin toggle with tray-menu sync (v1.1)（钉住不隐藏 + 钉住时置顶可配 + 位置记忆）"

# FB-1 修复（2026-10-01，阶段 6 截图拍摄期发现）
git add src/DeepSeekBalanceWidget/Scheduling/Scheduler.cs src/DeepSeekBalanceWidget/App.xaml.cs
git commit -m "fix(scheduling): 节假日工作日启动时托盘/胶囊强调色停留高峰色（FB-1：启动首帧已发布状态作为翻转沿基准，数据到达后补发恰一次状态变更）"

# ── 阶段 5：测试与发布 ──────────────────────────────────────────
git add tests/
git commit -m "test: 集成测试 43 用例（真实组件 + 假时钟：翻转沿/ICO 像素/退避序列/停靠吸附/FB-1 回归）"

git add docs/test-report.md docs/release-notes.md docs/deployment.md
git commit -m "docs: 测试报告/发布说明/部署文档（阶段 5 三件套 + v1.1 增补）"

# ── 阶段 6：开源整理 ────────────────────────────────────────────
git add README.md LICENSE CONTRIBUTING.md SECURITY.md .gitignore .gitattributes .editorconfig
git commit -m "docs: 仓库门面（README/许可/贡献指南/安全策略/仓库配置）"

git add docs/architecture.md docs/adr/ docs/probe-results.md docs/deepseek-api.md docs/showcase/ design/
git commit -m "docs: 架构文档、6 篇 ADR、探针结论汇总、API 说明与展示材料"

git add .github/
git commit -m "ci: GitHub Actions 构建测试工作流与 Issue/PR 模板"

git add assets/
git commit -m "docs: README 截图（assets）"

git add docs/git-and-release.md
git commit -m "docs: git 初始化与发布操作清单"
```

> 提交信息正文（第二行起）可补充动机与实测数字；本清单以单行为主。`git status` 确认无遗漏（`probes/`、`qa-tmp/`、`release/`、`docs/phase-records/` 应显示为被忽略）。

## 2. 分支策略

- **main**：唯一长期分支，始终可构建可测试；一切改动经 PR 进入（含所有者本人，便于 CI 把关）。
- 功能分支命名：`feat/panel-pin`、`fix/tray-tooltip`、`docs/adr-*` 等，合并后删除。
- 不使用 develop 分支（个人项目规模，PR 到 main 即可）。

```bash
git branch -M main
```

## 3. 标签（版本对应关系）

| 标签 | 对应交付 | 打点建议 |
| --- | --- | --- |
| `v0.1.0-probe` | 五探针全部通过（可行性实证） | 无独立代码产物，仅为流程存证（可选） |
| `v0.5.0-mvp` | 阶段 5 完成时的 MVP（12 项验收通过，SHA-256 `0A2A2FAF…99E359` 产物） | 历史存证（产物已被 v1.1 重发布覆盖，建议不附附件） |
| `v1.0.0` | 当前版本（MVP + v1.1 右缘停靠/拖动/钉住 + FB-1 修复；SHA-256 `07E14926EB5302664AE1A7729A83ED69DAFEF0A8877CC2C52AB48316F7343312`，以 [release-notes.md §4](release-notes.md) 为准） | **首个公开 Release**，附 exe 与 SHA-256 |

```bash
git tag v0.1.0-probe  <探针结论提交的 hash>      # 可选
git tag v0.5.0-mvp   <阶段 5 三件套提交的 hash>   # 可选
git tag -a v1.0.0 -m "v1.0.0：MVP + v1.1 右缘停靠/拖动/钉住"
```

## 4. 关联远程并推送

```bash
git remote add origin https://github.com/tars774/deepseek-balance-widget.git
git push -u origin main
git push origin --tags
```

## 5. 创建 GitHub Release（v1.0.0）

1. GitHub 仓库页 → **Releases** → **Draft a new release**。
2. **Choose a tag**：输入 `v1.0.0`（已推送的标签）。
3. **Release title**：`v1.0.0 · DeepSeek 余额峰谷监控小组件（含 v1.1 面板停靠/拖动/钉住）`。
4. **Describe this release**：以 [docs/release-notes.md](release-notes.md) 为底稿（该文档已按"探针=v0.1.0-probe / MVP=v0.5.0-mvp / 当前=v1.0.0 候选"组织），要点：
   - 功能清单（含 v1.1 三条增补）；
   - 已知限制（未签名 SmartScreen / Win10 口径 / 溢出区左键行为）；
   - 校验值表（SHA-256，见 release-notes §4）；
   - 最小安装说明（3 步）+ 完全卸载指引。
5. **Attach binaries**：上传 `release/publish/DeepSeekBalanceWidget.exe`（本地自建产物；**发布前自行重算并核对 SHA-256**，方法见 release-notes §4——若你重新构建，产物哈希与文档值不同是正常的，请同步更新 Release 正文）。
6. **Publish release**。

## 6. 首推后核对清单

- [ ] Actions 页 [build.yml](../.github/workflows/build.yml) 首次运行结果——**重点关注集成测试 job**（CI 无交互桌面环境的首推结果待验证，见工作流内注释）。
- [ ] 仓库根目录文件树与 README「目录结构」一致（无 `probes/`、`qa-tmp/`、`release/`、`docs/phase-records/`）。
- [ ] README 中的 `assets/` 截图正常显示。
- [ ] LICENSE 版权行中的 `<YOUR_GITHUB_USERNAME>`、SECURITY.md 中的联系方式占位符已替换为真实值。
- [ ] Release 附件 SHA-256 与 Release 正文一致。
