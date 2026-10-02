namespace DeepSeekBalanceWidget.Scheduling;

/// <summary>
/// 全部间隔参数可注入（设计 §7.2）。默认值即正式版取值：
/// 心跳 1s；余额间隔 30min（可配置 5–1440min）；面板去抖 30s；退避 1/5/15min；统计摘要每 60 tick；
/// 显示期快轮询 5s + 失败退避 5/10/20/40/60s（需求变更 2026-10-02，快轮询为固定策略不设设置项）。
/// 心跳重排一律采用伺服式绝对对齐（§2.3，2026-09-29 探针 E 实测修订；无朴素模式开关）。
/// </summary>
public sealed class SchedulerOptions
{
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan BalanceInterval { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan PanelDebounce { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan[] BackoffSequence { get; set; } = RetryPolicy.DefaultSequence;
    public int TickStatsEveryN { get; set; } = 60;

    /// <summary>
    /// 面板显示期余额快轮询间隔（需求变更 2026-10-02）：固定 5s，非设置项；
    /// 面板隐藏后回到 <see cref="BalanceInterval"/> 设定周期。
    /// </summary>
    public TimeSpan FastPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 显示期快轮询失败退避序列（需求变更 2026-10-02）：5→10→20→40→60s（60s 封顶循环）；
    /// 成功恢复 5s。与后台 1/5/15 分钟序列（<see cref="BackoffSequence"/>）分实例，互不放大。
    /// </summary>
    public TimeSpan[] FastPollBackoffSequence { get; set; } =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60),
    };

    /// <summary>余额间隔有效范围钳制：5–1440 分钟（下限防高频轮询，上限 = 一天一次）。</summary>
    public static TimeSpan ClampBalanceInterval(TimeSpan t)
        => TimeSpan.FromMinutes(Math.Clamp(t.TotalMinutes, 5, 1440));
}
