namespace DeepSeekBalanceWidget.Scheduling;

/// <summary>
/// 全部间隔参数可注入（设计 §7.2）。默认值即正式版取值：
/// 心跳 1s；余额间隔 30min（可配置 5–1440min）；面板去抖 30s；退避 1/5/15min；统计摘要每 60 tick。
/// 心跳重排一律采用伺服式绝对对齐（§2.3，2026-09-29 探针 E 实测修订；无朴素模式开关）。
/// </summary>
public sealed class SchedulerOptions
{
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan BalanceInterval { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan PanelDebounce { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan[] BackoffSequence { get; set; } = RetryPolicy.DefaultSequence;
    public int TickStatsEveryN { get; set; } = 60;

    /// <summary>余额间隔有效范围钳制：5–1440 分钟（下限防高频轮询，上限 = 一天一次）。</summary>
    public static TimeSpan ClampBalanceInterval(TimeSpan t)
        => TimeSpan.FromMinutes(Math.Clamp(t.TotalMinutes, 5, 1440));
}
