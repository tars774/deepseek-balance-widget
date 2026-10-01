using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Scheduling;

namespace DeepSeekBalanceWidget.Peak;

/// <summary>
/// 节假日数据服务（D-12 / D-13 + C-2 / C-3 必改项，探针 C HolidayService + 探针 E 同步管线移植）：
/// - 按北京日期缓存，同一日期只拉一次（含 in-flight 单飞去重）；跨天后对新日期自然重拉，
///   TrimCache 有界化保留 [今天-1, 今天+400]；
/// - 主/备双源 failover（探针 C：主源冷启动 8s 超时风险由备源兜底）；
/// - 失败进入 1/5/15 分钟退避全局门（D-13）：窗口内 GetDayData 直接返回 null（Degraded），
///   不消耗请求；成功清零；
/// - 【C-2 必改】缓存/退避/pending 全部状态加锁线程安全；
/// - 拉取为异步 fire-observe（tick 不等待网络）；成功（Degraded 恢复）→ FetchCompleted 立即刷新快照
///   【C-3 必改】。
/// </summary>
public sealed class HolidayService
{
    private readonly IHolidaySource _primary;
    private readonly IHolidaySource _backup;
    private readonly ITimeProvider _time;
    private readonly RetryPolicy _retry = new();

    private readonly object _gate = new();                       // C-2：全部可变状态共用一把锁
    private readonly Dictionary<DateOnly, HolidayData> _cache = new();
    private readonly HashSet<DateOnly> _pending = new();
    private DateTimeOffset _nextAttemptAllowedUtc = DateTimeOffset.MinValue;

    public HolidayService(IHolidaySource primary, IHolidaySource backup, ITimeProvider time)
    {
        _primary = primary;
        _backup = backup;
        _time = time;
    }

    public RetryPolicy Retry => _retry;

    private int _fetchRounds;
    /// <summary>累计拉取轮次（回归断言口径：每日期恰一次）。</summary>
    public int FetchRounds => _fetchRounds;

    /// <summary>拉取成功（Degraded 恢复）后立即刷新快照的钩子（C-3）。发起方为 UI 线程，await 续体回 UI 线程。</summary>
    public event Action? FetchCompleted;

    /// <summary>Degraded = 双源失败后的退避窗口内（窗口内静默，不消耗请求）。</summary>
    public bool IsDegradedNow
    {
        get { lock (_gate) return _time.UtcNow < _nextAttemptAllowedUtc; }
    }

    /// <summary>
    /// tick 同步路径：命中缓存即返；未命中且可发起时调度异步拉取（本调用不等待网络）——
    /// 真实 HTTP 不会同步完成，返回 null 由下一 tick / FetchCompleted 重算修正。
    /// </summary>
    public HolidayData? GetDayData(DateOnly date)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(date, out var cached)) return cached;

            var now = _time.UtcNow;
            if (now < _nextAttemptAllowedUtc) return null;   // 退避窗口内：静默（D-13），不发请求
            if (!_pending.Add(date)) return null;            // 已在拉取中：单飞（同日期唯一在飞）
        }
        _ = FetchAndCacheAsync(date);   // 异步外发，fire-observe（异常均在内部消化并记日志）
        return null;
    }

    private async Task FetchAndCacheAsync(DateOnly date)
    {
        Interlocked.Increment(ref _fetchRounds);
        HolidayData? data = null;
        string? error = null;
        try
        {
            var rp = await _primary.FetchAsync(date);
            if (rp.Success && rp.Data != null)
            {
                data = rp.Data;
            }
            else
            {
                AppLog.Warn($"[holiday] {_primary.Name} {date:yyyy-MM-dd} 拉取失败({rp.Error}) → 切换备源");
                var rb = await _backup.FetchAsync(date);
                if (rb.Success && rb.Data != null)
                {
                    data = rb.Data;
                }
                else
                {
                    error = rb.Error;
                    AppLog.Warn($"[holiday] {_backup.Name} {date:yyyy-MM-dd} 拉取失败({rb.Error}) → 降级本地逻辑");
                }
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        bool recovered = false;
        lock (_gate)
        {
            _pending.Remove(date);
            if (data != null)
            {
                _cache[date] = data;
                _retry.OnSuccess();                              // 成功清零（D-13）
                _nextAttemptAllowedUtc = DateTimeOffset.MinValue;
                recovered = true;
                AppLog.Info($"[holiday] {date:yyyy-MM-dd} <- {data.SourceName} holiday={data.IsReportedHoliday} " +
                            $"makeup={data.IsMakeupWorkday} note={data.Note}");
            }
            else
            {
                _retry.OnFailure();
                _nextAttemptAllowedUtc = _time.UtcNow + _retry.CurrentBackoff;
                AppLog.Warn($"[holiday] {date:yyyy-MM-dd} 双源失败({error}) → 进入退避 {RetryPolicy.FormatBackoff(_retry.CurrentBackoff)}，" +
                            $"下次允许 {_nextAttemptAllowedUtc.ToLocalTime():HH:mm:ss}，连续失败 {_retry.ConsecutiveFailures}");
            }
        }

        if (recovered)
        {
            // C-3：Degraded 恢复 → 立即刷新快照（Scheduler.RecomputeNow；UI 线程触发）
            FetchCompleted?.Invoke();
        }
    }

    /// <summary>跨天时清理缓存窗口外旧键（保留 [今天-1, 今天+400]，D-12 有界化）。</summary>
    public void TrimCache(DateOnly today)
    {
        lock (_gate)
        {
            var lo = today.AddDays(-1);
            var hi = today.AddDays(400);
            var stale = _cache.Keys.Where(k => k < lo || k > hi).ToList();
            foreach (var k in stale) _cache.Remove(k);
            if (stale.Count > 0)
                AppLog.Info($"[holiday] 缓存窗口清理：移除 {stale.Count} 个过期键");
        }
    }
}
