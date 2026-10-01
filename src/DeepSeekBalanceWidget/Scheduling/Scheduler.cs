using System.Diagnostics;
using System.Windows.Threading;
using DeepSeekBalanceWidget.Balance;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;

namespace DeepSeekBalanceWidget.Scheduling;

public enum BalanceRefreshSource
{
    /// <summary>后台定时（默认 30 分钟，可配置 5–1440）。</summary>
    Timer,

    /// <summary>面板打开即刷（30s 去抖）。</summary>
    Panel,

    /// <summary>手动刷新按钮（立即触发，不去抖）。</summary>
    Manual,
}

// ---------------- 事件契约（scheduler-design §3.2：全部在 UI 线程触发） ----------------

public sealed record StateChangedArgs(PeakState Old, PeakState New, bool Degraded, DateTimeOffset AtUtc);
public sealed record DayChangedArgs(DateOnly OldDate, DateOnly NewDate);
public sealed record BalanceRequestedArgs(BalanceRefreshSource Source, DateTimeOffset AtUtc);
public sealed record BalanceRefreshedArgs(BalanceResult Result, TimeSpan Duration, BalanceRefreshSource Source, DateTimeOffset AtUtc);

/// <summary>每 tick 直刷出口（面板）。面板不可见时实现方自行跳过（降 CPU，§2.2-④）。</summary>
public interface ISnapshotSink
{
    void OnTickSnapshot(WidgetSnapshot snapshot);
}

/// <summary>只读计数器（回归断言口径，设计 §7.3）。</summary>
public sealed class SchedulerCounters
{
    public long StateChangedCount { get; internal set; }
    public long DayChangedCount { get; internal set; }
    public long ReentrySkips { get; internal set; }
    public long PanelDebounceSuppressions { get; internal set; }
    public long SnapshotRecomputeCount { get; internal set; }
    public long BalanceRefreshedCount { get; internal set; }
    public Dictionary<BalanceRefreshSource, long> BalanceRequested { get; } = new();
    internal void Bump(BalanceRefreshSource s) => BalanceRequested[s] = BalanceRequested.GetValueOrDefault(s) + 1;
}

/// <summary>
/// 巡查心跳（scheduler-design §2/§3/§4/§5，探针 E 已验证实现正式化）：
/// 1s DispatcherTimer（UI 线程）→ 六步管线（读时钟/漂移记账/快照/直刷/翻转沿+跨天/余额调度）；
/// 伺服式绝对对齐 Interval = clamp(Heartbeat − e, 250, 2000) + 跳槽位追赶
/// （§2.3，2026-09-29 探针 E 实测修订：WPF 改 Interval 按"旧到期点"重排，伺服式在两种平台语义下均收敛）；
/// 倒计时/进度每 tick 直刷（绝对差值，永不累加）、托盘/胶囊仅在翻转沿更新；
/// 余额调度三触发源 + 单飞重入保护 + 1/5/15 分钟退避全局门 + 未配置 Key 定时静默（§4.5）。
/// 事件全部在 UI 线程触发，订阅者无需自行封送（§3.2）。
/// </summary>
public sealed class Scheduler : IDisposable
{
    private readonly ITimeProvider _time;
    private readonly PeakEngine _peak;
    private readonly IBalanceService _balance;
    private readonly SchedulerOptions _opt;
    private readonly HolidayService _holidays;
    private readonly RetryPolicy _balanceRetry;
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _isKeyConfigured;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    private DispatcherTimer? _timer;
    private TimeSpan _nextSlot;                    // 绝对对齐槽位（Stopwatch 单调时钟）
    private DateTimeOffset _nextTimerDue;
    private DateTimeOffset? _lastPanelTriggerUtc;
    private DateTimeOffset? _lastSuccessUtc;
    private bool _balanceInFlight;
    private PeakState? _lastState;
    private DateOnly? _lastDate;
    private long _skippedSlotsTotal;

    // 漂移记账（§8.1 统计摘要；增量统计，无逐 tick 集合，内存恒定）
    private bool _hasLastFire;
    private TimeSpan _lastFireSw;
    private TimeSpan _statsAnchorSw;
    private long _intervalCount;
    private double _intervalSumMs, _intervalMinMs = double.MaxValue, _intervalMaxMs;
    private double _maxLatenessMs, _procMaxMs;

    public long TickCount { get; private set; }
    public SchedulerCounters Counters { get; } = new();
    public ISnapshotSink? Sink { get; set; }
    public WidgetSnapshot? LastSnapshot { get; private set; }

    /// <summary>
    /// 启动首帧已对外发布的状态（App.OnStartup 按 snap0.State 初始化托盘图标/胶囊强调色，
    /// 该初始化早于节假日等异步数据到达）。设置后，首次翻转沿评估以它为基准——
    /// 若首次重算/首 tick 快照与它不同，补发一次 StateChanged（FB-1 根治）；
    /// 不设置（null，如部分测试场景）保持"首评估即初始化"原语义，行为不变。
    /// </summary>
    public PeakState? InitialPublishedState { get; set; }

    // ---- 4 类事件（全部 UI 线程触发，§3.2 契约） ----
    public event EventHandler<StateChangedArgs>? StateChanged;
    public event EventHandler<DayChangedArgs>? DayChanged;
    public event EventHandler<BalanceRequestedArgs>? BalanceRefreshRequested;
    public event EventHandler<BalanceRefreshedArgs>? BalanceRefreshed;

    public Scheduler(ITimeProvider time, PeakEngine peak, IBalanceService balance, HolidayService holidays,
        SchedulerOptions opt, Dispatcher dispatcher, Func<bool> isKeyConfigured)
    {
        _time = time;
        _peak = peak;
        _balance = balance;
        _holidays = holidays;
        _opt = opt;
        _dispatcher = dispatcher;
        _isKeyConfigured = isKeyConfigured;
        _balanceRetry = new RetryPolicy(opt.BackoffSequence);
        // §4.2：正式版取"启动即刷一次再计时"（首 tick 到期即触发，来源 Timer）
        _nextTimerDue = time.UtcNow;
        _statsAnchorSw = _sw.Elapsed;
        AppLog.Info($"Scheduler 构造：heartbeat={opt.Heartbeat.TotalSeconds:0.#}s " +
                    $"balanceInterval={opt.BalanceInterval.TotalMinutes:0}min " +
                    $"panelDebounce={opt.PanelDebounce.TotalSeconds:0}s 首次定时到期=启动即刷");
    }

    // ---------------- Start / Stop ----------------

    public void Start()
    {
        _nextSlot = _sw.Elapsed + _opt.Heartbeat;
        _timer = new DispatcherTimer { Interval = _opt.Heartbeat };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        AppLog.Info("Scheduler Start：DispatcherTimer(1s) 已启动（UI 线程，伺服式绝对对齐 §2.3）");
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
        AppLog.Info("Scheduler Stop：" + StatsSummary("终值"));
    }

    // ---------------- 心跳（§2.2 六步管线 + §2.3 伺服式绝对对齐） ----------------

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var fireAt = _sw.Elapsed;

        // §2.3 伺服式绝对对齐（2026-09-29 探针 E 实测修订）：
        // WPF 在 Tick 内修改 Interval 时按"旧到期点"重排——伺服式 Interval = clamp(Heartbeat − e, 250, 2000)
        // 在"按到期点重排"与"按触发时刻重排"两种平台语义下均收敛（e→0 或量化档内振荡），
        // 无累积漂移、无追赶拍；长停顿（e≥Heartbeat）走"跳槽位追赶不爆发"路径；
        // clamp 下界 250ms 防忙转，上界 2000ms 限制单步补偿幅度。
        double errMs = (fireAt - _nextSlot).TotalMilliseconds;   // 带符号误差（负=早于槽位触发）
        int skipped = 0;
        while (errMs >= _opt.Heartbeat.TotalMilliseconds)
        {
            _nextSlot += _opt.Heartbeat;
            errMs -= _opt.Heartbeat.TotalMilliseconds;
            skipped++;
        }
        if (skipped > 0)
        {
            _skippedSlotsTotal += skipped;
            AppLog.Warn($"心跳跳槽位 {skipped} 个（tick 处理/调度延迟过大）→ 追赶不爆发 tick#{TickCount + 1}");
        }
        _nextSlot += _opt.Heartbeat;                             // 每 tick 恰推进一个槽位
        _timer!.Interval = TimeSpan.FromMilliseconds(
            Math.Clamp(_opt.Heartbeat.TotalMilliseconds - errMs, 250, 2000));   // 伺服式重排（关键）
        double latenessMs = Math.Max(0, errMs);

        TickCore(latenessMs);
    }

    private void TickCore(double latenessMs)
    {
        TickCount++;
        var tickStart = _sw.Elapsed;
        var now = _time.UtcNow;                     // ① 读时钟（绝对时刻，永不累加）
        var prevDate = _lastDate;                   // 跨天检测基准取快照计算前的日期（探针 E 实测修正：
                                                    //  ③ 内同步完成的补拉会提前改写 _lastDate，否则 DayChanged 被吞）

        // ② 漂移记账（真实墙钟间隔 + 对齐槽位迟到量；越界按 §8 记日志）
        if (_hasLastFire)
        {
            var ivMs = (_sw.Elapsed - _lastFireSw).TotalMilliseconds;
            _intervalCount++;
            _intervalSumMs += ivMs;
            _intervalMinMs = Math.Min(_intervalMinMs, ivMs);
            _intervalMaxMs = Math.Max(_intervalMaxMs, ivMs);
            if (ivMs < 500 || ivMs > 1500)
                AppLog.Warn($"tick 间隔异常 {ivMs:F1}ms（越界 0.5–1.5s）tick#{TickCount}");
        }
        else
        {
            _statsAnchorSw = _sw.Elapsed;
        }
        _lastFireSw = _sw.Elapsed;
        _hasLastFire = true;
        _maxLatenessMs = Math.Max(_maxLatenessMs, latenessMs);

        // ③ 峰谷快照（纯内存；节假日未命中缓存时异步补拉，tick 不等待网络）
        var snap = _peak.ComputeSnapshot();

        // ④ 直刷 UI（倒计时/进度每 tick；面板不可见时 sink 自行跳过；不发事件）
        Sink?.OnTickSnapshot(snap);
        LastSnapshot = snap;

        // ⑤ 翻转沿检测 + 跨天检测（先记账，后余额调度）
        PublishFlipIfNeeded(snap, now);
        if (prevDate.HasValue && snap.BeijingDate != prevDate.Value)
        {
            Counters.DayChangedCount++;
            AppLog.Info($"跨天检测：{prevDate} → {snap.BeijingDate}（跨天点=UTC 16:00）tick#{TickCount}");
            _holidays.TrimCache(snap.BeijingDate);
            DayChanged?.Invoke(this, new DayChangedArgs(prevDate.Value, snap.BeijingDate));
            // 新日期数据已由本 tick ComputeSnapshot → GetDayData(新日期) 发起重拉（经退避全局门，§5）
        }
        _lastState = snap.State;
        _lastDate = snap.BeijingDate;

        // ⑥ 余额调度到期检查（到期 → 异步触发，绝不在 tick 内同步执行 HTTP）
        CheckBalanceDue(now);

        var procMs = (_sw.Elapsed - tickStart).TotalMilliseconds;
        _procMaxMs = Math.Max(_procMaxMs, procMs);
        if (_opt.TickStatsEveryN > 0 && TickCount % _opt.TickStatsEveryN == 0)
            AppLog.Info("心跳统计摘要：" + StatsSummary($"tick#{TickCount}"));
    }

    private void PublishFlipIfNeeded(WidgetSnapshot snap, DateTimeOffset now)
    {
        // 翻转沿基准 = _lastState；首次评估（_lastState==null）时的语义选择（FB-1 根治，2026-10-01）：
        // 根因——App 启动首帧按 snap0.State 同步初始化了托盘图标与胶囊强调色（snap0 计算早于节假日
        // 等异步数据到达），而本调度器此前的"首评估即初始化、不发事件"语义会把"首次重算/首 tick
        // 快照 != snap0"的真实翻转吞掉：法定节假日的工作日启动时（snap0 按工作日逻辑=Peak，节假日
        // 数据到达后重算=Idle），托盘/强调色停留在 Peak 色直到下一次真实翻转沿（节假日全天无翻转）。
        // 修复——外部把"已发布的初始状态"经 InitialPublishedState 注入，首次评估若与快照不同则
        // 补发恰一次 StateChanged（Old=已发布状态）；同态或未注入时行为与旧语义一致。
        // 不写 _lastDate、不产生重复事件（_lastState 随即被调用方落为 snap.State）。
        var baseline = _lastState ?? InitialPublishedState;
        if (baseline.HasValue && snap.State != baseline.Value)
        {
            Counters.StateChangedCount++;
            AppLog.Info($"状态翻转沿：{baseline} → {snap.State}（Degraded={snap.Degraded}, 时段={snap.PeriodName}）tick#{TickCount}");
            StateChanged?.Invoke(this, new StateChangedArgs(baseline.Value, snap.State, snap.Degraded, now));
        }
    }

    /// <summary>立即刷新快照（C-3 / D-13 成功即刷；不受心跳节拍限制）。</summary>
    public void RecomputeNow(string reason)
    {
        var snap = _peak.ComputeSnapshot();
        Counters.SnapshotRecomputeCount++;
        Sink?.OnTickSnapshot(snap);
        LastSnapshot = snap;
        PublishFlipIfNeeded(snap, _time.UtcNow);
        _lastState = snap.State;
        // 此处不写 _lastDate：跨天基准只在心跳管线 tick#⑤ 的快照步骤留档。
        // 若本回调（余额成功/节假日恢复 C-3）恰落在跨天后的第一个 tick 之前（约 1s 窗口），
        // 提前改写 _lastDate 会使下一 tick 的 prevDate 基准已是新日期，该次 DayChanged
        // （含 TrimCache 与事件）被吞——与探针 E 修正的"同 tick 吞事件"同族，故解耦。
        AppLog.Info($"立即刷新快照 reason={reason} state={snap.State} countdown={snap.CountdownText}");
    }

    // ---------------- 余额刷新调度（§4 三触发源 + §2.4 单飞） ----------------

    private void CheckBalanceDue(DateTimeOffset now)
    {
        if (now < _nextTimerDue) return;

        // §4.5：未配置 Key 时定时到期不触发请求（避免空转），仅推进到期点并记日志
        if (!_isKeyConfigured())
        {
            _nextTimerDue = now + _opt.BalanceInterval;
            AppLog.Info("余额定时到期：未配置 Key → 跳过（不发起请求）");
            return;
        }

        if (_balanceInFlight)
        {
            // §2.4 单飞：不排队、不并发；同一到期点只触发一次
            Counters.ReentrySkips++;
            _nextTimerDue = now + _opt.BalanceInterval;
            AppLog.Warn($"余额定时到期但上次刷新未完成 → 跳过本次触发（单飞重入保护）skips={Counters.ReentrySkips} tick#{TickCount}");
            return;
        }
        _nextTimerDue = now + _opt.BalanceInterval;
        RequestBalanceRefresh(BalanceRefreshSource.Timer);
    }

    /// <summary>面板打开即刷（30s 去抖，§4.3：距上次触发或距上次成功刷新 &lt;30s 一并抑制）。</summary>
    public void NotifyPanelOpened()
    {
        var now = _time.UtcNow;
        if (_lastPanelTriggerUtc.HasValue && now - _lastPanelTriggerUtc.Value < _opt.PanelDebounce)
        {
            Counters.PanelDebounceSuppressions++;
            AppLog.Info($"面板打开触发去抖：距上次触发 {(now - _lastPanelTriggerUtc.Value).TotalSeconds:F1}s " +
                        $"< {_opt.PanelDebounce.TotalSeconds:0}s → 抑制");
            return;
        }
        if (_lastSuccessUtc.HasValue && now - _lastSuccessUtc.Value < _opt.PanelDebounce)
        {
            Counters.PanelDebounceSuppressions++;
            AppLog.Info($"面板打开触发去抖：距上次成功刷新 {(now - _lastSuccessUtc.Value).TotalSeconds:F1}s → 数据新鲜，抑制");
            return;
        }
        _lastPanelTriggerUtc = now;
        RequestBalanceRefresh(BalanceRefreshSource.Panel);
    }

    /// <summary>统一入口（三触发源共用）：单飞保护 → 事件 → 异步执行（§4.1）。手动触发不去抖（§4.4）。</summary>
    public void RequestBalanceRefresh(BalanceRefreshSource source)
    {
        if (_balanceInFlight)
        {
            Counters.ReentrySkips++;
            AppLog.Warn($"余额刷新请求被单飞保护跳过 source={source} skips={Counters.ReentrySkips}");
            return;
        }
        _balanceInFlight = true;
        Counters.Bump(source);
        AppLog.Info($"余额刷新触发 source={source} at={_time.UtcNow.ToLocalTime():yyyy-MM-dd HH:mm:ss} tick#{TickCount}");
        BalanceRefreshRequested?.Invoke(this, new BalanceRequestedArgs(source, _time.UtcNow));
        _ = RunRefreshAsync(source);   // 异步外发：tick/调用方不等待 HTTP（fire-observe）
    }

    /// <summary>余额刷新间隔设置变更：即时生效（以下次刷新锚点重排到期点）。</summary>
    public void UpdateBalanceInterval(TimeSpan interval)
    {
        var clamped = SchedulerOptions.ClampBalanceInterval(interval);
        _opt.BalanceInterval = clamped;
        var anchor = _lastSuccessUtc ?? _time.UtcNow;
        _nextTimerDue = anchor + clamped;
        AppLog.Info($"余额刷新间隔已更新为 {clamped.TotalMinutes:0} 分钟（即时生效），下次定时到期 {_nextTimerDue.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    private async Task RunRefreshAsync(BalanceRefreshSource source)
    {
        var sw = Stopwatch.StartNew();
        BalanceResult result;
        try
        {
            result = await _balance.RefreshAsync();
        }
        catch (Exception ex)
        {
            // BalanceClient 正常不抛（内部全分类）；防御兜底归网络失败态（D-07）
            result = new BalanceResult(BalanceStatus.NetworkFailure, null, null, IsAvailable: true,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        sw.Stop();
        OnRefreshCompleted(source, result, sw.Elapsed);   // await 后回到 UI 线程（捕获的同步上下文）
    }

    private void OnRefreshCompleted(BalanceRefreshSource source, BalanceResult result, TimeSpan duration)
    {
        if (!UiThread.IsCurrent)
        {
            // 防御封送路径（正常情况下 await 已自动回 UI 线程，§3.2）
            AppLog.Warn("余额完成回调不在 UI 线程 → Dispatcher.BeginInvoke 封送");
            _ = _dispatcher.BeginInvoke(() => OnRefreshCompleted(source, result, duration));
            return;
        }
        _balanceInFlight = false;
        Counters.BalanceRefreshedCount++;
        var now = _time.UtcNow;
        AppLog.Info($"余额刷新完成 source={source} status={result.Status} 耗时={duration.TotalMilliseconds:F0}ms" +
                    (result.Note is null ? "" : $" note={result.Note}"));
        BalanceRefreshed?.Invoke(this, new BalanceRefreshedArgs(result, duration, source, now));

        if (result.Status == BalanceStatus.Success)
        {
            _balanceRetry.OnSuccess();                       // 成功清零（D-13）
            _lastSuccessUtc = now;
            _nextTimerDue = now + _opt.BalanceInterval;
            RecomputeNow("余额成功→立即刷新快照");            // C-3：恢复后立即刷新快照
        }
        else if (result.Status == BalanceStatus.NotConfigured)
        {
            // §4.5：未配置为普通完成——不进退避序列（人工通道与定时均不受影响）
            _nextTimerDue = now + _opt.BalanceInterval;
        }
        else
        {
            // §6.3：失败（401/DNS/超时/畸形）→ 下一次定时到期 = now + CurrentBackoff（1/5/15 序列），
            // 序列内定时静默；手动/开面板通道始终放行（人工通道是天然恢复路径）
            _balanceRetry.OnFailure();
            var backoff = _balanceRetry.CurrentBackoff;
            _nextTimerDue = now + backoff;
            AppLog.Warn($"余额刷新失败（{result.Status}）→ 进入退避 {RetryPolicy.FormatBackoff(backoff)}，" +
                        $"下次定时到期 {_nextTimerDue.ToLocalTime():yyyy-MM-dd HH:mm:ss}，" +
                        $"连续失败 {_balanceRetry.ConsecutiveFailures}");
        }
    }

    // ---------------- 统计摘要（§8.1） ----------------

    private string StatsSummary(string label)
    {
        double meanMs = _intervalCount > 0 ? _intervalSumMs / _intervalCount : 0;
        // 累计漂移 = (lastFire − anchor) − N×Heartbeat；伺服对齐后应 ≪1s（§9.3）
        double driftMs = _intervalCount > 0
            ? (_lastFireSw - _statsAnchorSw).TotalMilliseconds - _intervalCount * _opt.Heartbeat.TotalMilliseconds
            : 0;
        return $"{label}: ticks={TickCount} 间隔 min/mean/max={(_intervalCount > 0 ? _intervalMinMs : 0):F1}/{meanMs:F1}/{_intervalMaxMs:F1}ms " +
               $"maxLateness={_maxLatenessMs:F1}ms drift={driftMs:+0.0;-0.0}ms tickProcMax={_procMaxMs:F2}ms " +
               $"skips(重入)={Counters.ReentrySkips} 去抖抑制={Counters.PanelDebounceSuppressions} 跳槽位累计={_skippedSlotsTotal}";
    }

    public void Dispose() => Stop();
}
