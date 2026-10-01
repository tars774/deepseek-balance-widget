namespace DeepSeekBalanceWidget.Balance;

/// <summary>余额刷新结果分类（D-07：四类异常精确映射 + 0 余额为普通成功）。</summary>
public enum BalanceStatus
{
    /// <summary>HTTP 200 + 解析成功（is_available=false 的 0 余额亦属普通成功）。</summary>
    Success,

    /// <summary>未配置 Key（不发请求；凭据读取失败视同此态引导重存，§6-7）。</summary>
    NotConfigured,

    /// <summary>HTTP 401——Key 无效（服务端自动掩码回显，不泄漏）。</summary>
    Unauthorized,

    /// <summary>DNS 失败 / 超时 / 非 200 非 401 状态——网络失败态。</summary>
    NetworkFailure,

    /// <summary>畸形 JSON / 字段格式非法。</summary>
    MalformedResponse,
}

/// <summary>
/// 余额刷新结果（D-07）。
/// IsAvailable 语义 = 顶层 is_available「余额是否可用于 API 调用」：false 仅作展示态提示
/// （「余额不可调用」），非错误；0 余额账号查询本身为普通成功（探针 B §三）。
/// </summary>
public sealed record BalanceResult(
    BalanceStatus Status,
    decimal? Balance,
    string? Currency,
    bool IsAvailable,
    string? Note);

public interface IBalanceService
{
    /// <summary>执行一次余额刷新。未配置 Key 时直接返回 NotConfigured，不发请求。</summary>
    Task<BalanceResult> RefreshAsync();
}
