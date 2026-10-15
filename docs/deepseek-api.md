# DeepSeek 余额接口说明

本文档化本组件对 DeepSeek 官方余额接口的使用方式：端点、认证、响应结构（含嵌套解析要点）、0 余额行为、错误分类与计费假设。全部结论来自探针 B 的真实环境实测（`probes/probe-b-balance-cred/`，结论见 [probe-results.md](probe-results.md)）。

> ⚠️ 本文档中的 Key 一律为占位符示例，不包含任何真实凭据。

## 端点与认证

```text
GET https://api.deepseek.com/user/balance
Authorization: Bearer <YOUR_API_KEY>
```

- 认证方式：Bearer Token（即 DeepSeek 控制台生成的 `sk-` 开头 API Key）。
- 客户端超时：8 秒（与节假日客户端一致；探针期 3s 仅为探针口径）。
- 调用方：本组件默认 30 分钟一次（可配置 5–1440 分钟），另有"打开面板即刷（30s 去抖）"与手动按钮两个触发源；三触发源统一入口 + 单飞重入保护（在飞时跳过，不并发）。

## 响应结构（实测）

HTTP 200 响应体实测样例（0 余额账号，无敏感信息）：

```json
{
  "is_available": false,
  "balance_infos": [
    {
      "currency": "CNY",
      "total_balance": "0.00",
      "granted_balance": "0.00",
      "topped_up_balance": "0.00"
    }
  ]
}
```

解析要点（`BalanceResponseParser` 的实现依据，逐条来自实测）：

| # | 要点 | 说明 |
| --- | --- | --- |
| 1 | **余额不在顶层，嵌套在 `balance_infos[]` 内** | 顶层只有 `is_available`；需求早期表述"`total_balance` 在顶层"不成立，解析代码必须按嵌套实现 |
| 2 | **金额为字符串类型** | `"0.00"`；容错 Number 形态；转换用 `decimal.TryParse` + **InvariantCulture**（避免区域小数符差异） |
| 3 | **优先取 CNY 元素** | 遍历数组取 `currency=="CNY"` 的元素；取不到取第一个（防御性支持多币种；单币种账号实测仅返回 CNY 一个元素） |
| 4 | **`is_available` 含义 = "余额是否可用于 API 调用"** | 0 余额时为 `false`——仅表示该 Key 当前不能成功调用模型接口，**不影响**余额查询本身的 200 响应；组件将其作为展示态（"余额不可调用"提示）而非错误 |
| 5 | **未知字段忽略** | 新增字段（如 granted/topped_up）不影响解析健壮性；`is_available` 缺失即抛 `JsonException` → `MalformedResponse`（不做静默默认） |
| 6 | 根必须为 JSON Object | `JsonDocument.Parse` 后校验根类型；数组/标量一律 `MalformedResponse` |

## 0 余额行为

**明确结论：余额接口与余额数额无关地正常工作。** 0 余额账号真实调用返回 HTTP 200 与完整 JSON 结构，解析无任何特殊分支需求。组件把 0 余额当**普通成功态**处理：面板显示 `¥ 0.00` 并附加"余额不可调用"提示（`is_available=false`）。

## 错误分类表（映射 UI）

| 分类 | 触发条件（实测异常形态） | UI 表现 | 重试策略 |
| --- | --- | --- | --- |
| `Success` | HTTP 200 + 解析成功（含 0 余额） | `¥ 数字`（0.00 附"余额不可调用"） | 成功 → 退避计数清零，立即刷新快照 |
| `NotConfigured` | 凭据不存在或读取失败（Win32 `ERROR_NOT_FOUND(1168)` = 正常未配置） | "请先在设置中配置 API Key"，整块可点击打开设置；**不发请求** | 定时到期时未配置 Key 直接跳过（不空转） |
| `Unauthorized` | HTTP 401 | "API Key 无效，请检查设置"，整块可点击打开设置 | 计入失败退避 1/5/15 分钟 |
| `NetworkFailure` | DNS 失败（`HttpRequestException←SocketException(HostNotFound)`）/ 超时（`TaskCanceledException←TimeoutException`）/ 其他非 200 | "余额获取失败"；**不显示过期缓存余额**，峰谷/倒计时/进度照常 | 计入失败退避 1/5/15 分钟 |
| `MalformedResponse` | 畸形 JSON（`JsonReaderException` / `JsonException`）或字段类型非法 | 同网络失败态 | 计入失败退避 1/5/15 分钟 |

补充语义：

- **失败一律不回退显示缓存余额**（A1 规范约定：错误时余额区只显示提示）；401 的错误体**不得**送入余额解析器（探针 B 首轮曾因此误报解析异常）。
- DeepSeek 服务端 401 错误信息会**自动把 Key 打码**为 `****xxxx` 回显；组件日志层另有"出现即打码"兜底（`sk-` 前缀 / `Authorization: Bearer` / 高熵赋值片段命中即替换 `***MASKED***`）。
- 退避为**全局门**：失败后 1→5→15 分钟（封顶 15 分钟循环）内，定时触发静默不消耗请求；**手动/开面板触发始终放行**（用户显式意图是天然恢复路径），其结果同样计入退避统计。

## 计费说明与假设

- **"余额查询接口不消耗 token / 不产生费用"为低风险既定假设**：官方文档将其定位为账户查询接口；探针 B 多次真实调用无任何异常计费证据。该假设未被严格证实（无法从外部观测计费系统），因此：
  - 默认轮询间隔取保守的 30 分钟；
  - 设置窗口允许把间隔调大至 1440 分钟（一天一次）；
  - 未配置 Key 时定时到期完全不发请求。
  - 若官方未来对该接口引入计费，调整间隔即可将影响降到最低。

## 凭据存储

- API Key 仅存 **Windows 凭据管理器**（Generic 凭据，TargetName `DeepSeekBalanceWidget_ApiKey`，`CRED_PERSIST_LOCAL_MACHINE`，仅本机可读）；控制面板 → 凭据管理器 → Windows 凭据 可见。
- **不落文件、不进配置、不进日志**：`settings.json` 中没有任何 Key 字段；日志经 SecretMasker 脱敏，测试期对 `%APPDATA%` 全文件做 UTF-8+UTF-16 双编码机扫 0 命中。
- 完全清除：设置窗口"清除 Key"，或 `cmdkey /delete:DeepSeekBalanceWidget_ApiKey`。
