# ADR-002：API Key 用 Windows Credential Manager 存储（自写 P/Invoke）

- 状态：已接受
- 日期：2026-09-28（探针 B 实证）
- 关联：[deepseek-api.md](../deepseek-api.md) · [SECURITY.md](../../SECURITY.md)

## 背景

组件需要持久保存用户的 DeepSeek API Key（`sk-` 开头）。安全要求：不落明文文件、不进日志。候选方案：明文配置文件、DPAPI 加密文件、Windows Credential Manager、第三方凭据库（CredentialManagement 等）。

## 决策

**API Key 只存 Windows 凭据管理器（Credential Manager，Generic 凭据）**，通过自写的 `CredWriteW` / `CredReadW` / `CredDeleteW` P/Invoke 访问；不使用第三方凭据库。非敏感配置（主题/间隔/自启/面板位置）才存本地 JSON（`settings.json`，无 Key 字段）。

## 理由

1. **系统级安全边界**：凭据管理器是 Windows 为"本机秘密"设计的标准机制——按用户上下文加密（`CRED_PERSIST_LOCAL_MACHINE` 限定本机可读），用户可通过控制面板查看/删除，无需我们自造加密轮子。
2. **自写 P/Invoke 成本极低**：三个 Win32 函数、约百行封装；实测全链（写→读回→覆盖→删除→`ERROR_NOT_FOUND(1168)` 正常处理）一次通过（探针 B 7/7）。第三方库（CredentialManagement）陈旧且引入维护风险，收益为零。
3. **与日志脱敏互补**：Key 从物理上不落盘（双编码字节级机扫 0 命中），日志层另有 `SecretMasker` 兜底（`sk-`/`Bearer`/高熵片段出现即打码），纵深防御。
4. **可控的失败语义**：读不到凭据 = `NotConfigured`（正常未配置路径），引导用户去设置窗口；凭据损坏等异常 Win32 错误码同样映射到"请重新保存 Key"。

## 后果

- 正面：Key 无明文暴露面；卸载简单（`cmdkey /delete` 一条命令）；无第三方依赖。
- 负面：凭据管理器 UI 对普通用户较隐蔽（文档需指路：控制面板 → 凭据管理器 → Windows 凭据）；`CRED_PERSIST_LOCAL_MACHINE` 意味着 Key 不随用户漫游配置迁移（对本工具可接受）。
- 中性：测试通过 `ICredentialStore` 抽象注入替身，不触碰真实凭据。

## 替代方案

| 方案 | 结论 |
| --- | --- |
| 明文存 settings.json | 否决（违背安全基线） |
| DPAPI 加密后落盘 | 未采用（与凭据管理器同级安全但多一道密文管理；且"删 Key"语义不如系统凭据直观） |
| 第三方 CredentialManagement 库 | 否决（陈旧、零收益） |
| 不存 Key、每次启动手输 | 否决（体验不可接受） |
