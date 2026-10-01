# 安全策略（Security）

## 支持版本

| 版本 | 状态 | 安全修复 |
| --- | --- | --- |
| 1.0.0（当前） | ✅ 支持 | ✅ |
| < 1.0.0 | ❌ 不支持 | ❌ |

## 报告漏洞

**请勿以公开 Issue 形式报告安全漏洞。**

请通过以下方式私下联系：

- 邮箱：tars774@163.com
- 或通过 GitHub 私信联系仓库所有者（[@tars774](https://github.com/tars774)）

请在报告中包含：受影响版本、复现步骤（越具体越好）、影响评估、（如可能）修复建议。我们会在 **72 小时内**确认收到，并在修复发布前与你协调披露时间线。

## 安全设计说明

| 设计点 | 说明 |
| --- | --- |
| API Key 存储 | 仅存 **Windows 凭据管理器**（Generic，`CRED_PERSIST_LOCAL_MACHINE`，仅本机可读）；不落文件、不进配置（`settings.json` 无 Key 字段） |
| 日志脱敏 | 日志写入层统一过 `SecretMasker`：完整 Key、`sk-` 前缀、`Authorization: Bearer`、高熵赋值片段"出现即打码"（`***MASKED***`）；日志不输出认证头 |
| 无遥测 | 应用不做任何遥测/统计上报；仅有的对外请求是 DeepSeek 余额接口与两个节假日公共 API |
| 数据边界 | 全部用户数据在当前用户域（`%APPDATA%\DeepSeekBalanceWidget\` + 凭据管理器 + 可选 HKCU Run 自启项）；无服务、无驱动、无机器级注册表写入 |
| 分发完整性 | Release 附件提供 SHA-256 校验值；exe 当前未签名（SmartScreen 首启提示，见 [docs/deployment.md §4](docs/deployment.md)），代码签名在路线图 |
| 单实例 | 命名 Mutex 防多实例竞争（`Local\DeepSeekBalanceWidget.SingleInstance`） |

## 给贡献者与报告者的提醒

- **Issue / PR / 截图中切勿包含 API Key、Token 或个人数据**。
- 怀疑自己曾泄漏 Key：立即到 DeepSeek 控制台**作废该 Key** 并生成新 Key（本组件设置窗口重新保存即可）。
- 本地调试产生的日志（`%APPDATA%\DeepSeekBalanceWidget\logs\`）分享前请自查。

## 已知安全边界（如实声明）

- 凭据管理器存储受当前 Windows 用户账户边界保护；同用户下的其他本机进程理论上可读取同持久化级别的凭据（Windows 机制本身如此，非本项目特有）。
- exe 未签名：下载分发场景存在 SmartScreen 提示与被替换风险，请始终核对 Release 附件的 SHA-256。
- "余额查询接口不产生费用"为低风险假设（官方定位账户查询接口），详见 [docs/deepseek-api.md](docs/deepseek-api.md)。
