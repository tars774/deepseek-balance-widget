namespace DeepSeekBalanceWidget.Scheduling;

/// <summary>
/// 退避策略（探针 C RetryPolicy 原样移植）：连续失败按 1min → 5min → 15min 重试，
/// 之后保持 15min 封顶循环；任一成功即重置（下次失败从 1min 起）。
/// 同一机制两份实例：HolidayService（节假日全局门）与 Scheduler（余额退避）各持一份（设计 §6.1）。
/// </summary>
public sealed class RetryPolicy
{
    public static readonly TimeSpan[] DefaultSequence =
    { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15) };

    private readonly TimeSpan[] _sequence;
    public TimeSpan[] Sequence => _sequence;

    public int ConsecutiveFailures { get; private set; }

    public RetryPolicy(TimeSpan[]? sequence = null) => _sequence = sequence ?? DefaultSequence;

    /// <summary>当前应等待的退避时长 = Sequence[min(连续失败次数-1, 末位)]。</summary>
    public TimeSpan CurrentBackoff => ConsecutiveFailures <= 0
        ? TimeSpan.Zero
        : _sequence[Math.Min(ConsecutiveFailures - 1, _sequence.Length - 1)];

    public void OnSuccess() => ConsecutiveFailures = 0;
    public void OnFailure() => ConsecutiveFailures++;

    /// <summary>退避时长的日志友好形式。</summary>
    public static string FormatBackoff(TimeSpan t) => t == TimeSpan.Zero ? "0" : $"{(int)t.TotalMinutes}min";
}
