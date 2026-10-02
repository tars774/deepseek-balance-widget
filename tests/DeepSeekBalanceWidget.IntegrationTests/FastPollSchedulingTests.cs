using System.Windows.Threading;
using DeepSeekBalanceWidget.Balance;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Scheduling;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// 余额显示期快轮询集成（需求变更 2026-10-02，scheduler-design §4.6）：
/// ① 面板显示期间固定每 5s 触发一次（FastPoll 源，走现有 Balance 管线 + 单飞保护）；
/// ② 面板隐藏后快轮询完全停止，回到设定周期（锚点 = 最近成功时刻 + BalanceInterval）；
/// ③ 重新显示立即先刷一次（Panel 源），30s 去抖窗内的重显不放大请求量，且 5s 节奏经到期点钳制接管；
/// ④ 失败按 5→10→20→40→60s 指数退避（60s 封顶循环），成功后恢复 5s 且序列复位。
/// 全程 FakeTimeProvider + 计数替身，零网络；触发挂在 1s tick 记账逻辑（心跳管线零改动）。
/// 同步约定：TickCount 在 tick 起点自增而余额触发/完成在 tick 内稍后执行——断言计数前必须
/// WaitRefreshed（等完成回调记账，同 BalanceSchedulingTests 的事件等待模式），避免假负竞争。
/// </summary>
public sealed class FastPollSchedulingTests : IDisposable
{
    private readonly UiThreadFixture _ui = new();

    public void Dispose() => _ui.Dispose();

    private Scheduler BuildScheduler(FakeTimeProvider time, HolidayService holidays, FakeBalanceService balance)
    {
        var peak = new PeakEngine(holidays, time);
        var options = new SchedulerOptions
        {
            Heartbeat = TimeSpan.FromMilliseconds(250),
            TickStatsEveryN = 0,
        };
        return _ui.Invoke(() => new Scheduler(time, peak, balance, holidays, options,
            _ui.Dispatcher, isKeyConfigured: () => true));
    }

    private static HolidayService BuildHolidays(FakeTimeProvider time)
    {
        var holidayData = new HolidayData(false, false, false, "fake-primary", "工作日");
        return new HolidayService(new FakeHolidaySource("fake-primary", holidayData),
            new FakeHolidaySource("fake-backup", holidayData), time);
    }

    private static BalanceResult Failure() =>
        new(BalanceStatus.NetworkFailure, null, null, IsAvailable: true, Note: null);

    [Fact]
    public void Panel_Visible_FastPolls_Every_5s_Hidden_Resumes_Configured_Period()
    {
        var time = new FakeTimeProvider();
        time.SetBeijing(new DateTime(2026, 9, 30, 10, 0, 0));
        var balance = new FakeBalanceService();
        var holidays = BuildHolidays(time);
        var scheduler = BuildScheduler(time, holidays, balance);
        var requested = new List<BalanceRequestedArgs>();
        scheduler.BalanceRefreshRequested += (_, e) => requested.Add(e);

        _ui.Invoke(scheduler.Start);
        try
        {
            // tick#1：启动即刷（Timer 源，隐藏态）→ 成功
            WaitForTicks(scheduler, 1);
            WaitRefreshed(scheduler, 1);
            Assert.Equal(1, balance.CallCount);
            Assert.Equal(1, scheduler.Counters.BalanceRequested.GetValueOrDefault(BalanceRefreshSource.Timer));

            // 越过 30s 去抖/新鲜度窗后显示 → 立即先刷一次（Panel 源；完成回调同步内联于 Ui.Invoke）
            time.Advance(TimeSpan.FromSeconds(31));
            _ui.Invoke(() => scheduler.NotifyPanelVisibility(true));
            WaitRefreshed(scheduler, 2);
            Assert.Equal(2, balance.CallCount);
            Assert.Equal(1, scheduler.Counters.BalanceRequested.GetValueOrDefault(BalanceRefreshSource.Panel));

            // 显示期节奏：每 5s 恰一次 FastPoll（单飞空闲、无退避）
            for (int i = 0; i < 3; i++)
            {
                time.Advance(TimeSpan.FromSeconds(5));
                WaitForTicks(scheduler, 3 + i);
                WaitRefreshed(scheduler, 3 + i);
            }
            Assert.Equal(5, balance.CallCount);
            Assert.Equal(3, scheduler.Counters.BalanceRequested.GetValueOrDefault(BalanceRefreshSource.FastPoll));

            // 隐藏 → 快轮询完全停止；到期点 = 最近成功 + 设定周期（30min）
            var callsAtHide = balance.CallCount;
            _ui.Invoke(() => scheduler.NotifyPanelVisibility(false));
            time.Advance(TimeSpan.FromMinutes(25));
            WaitForTicks(scheduler, 6);
            Assert.Equal(callsAtHide, balance.CallCount);                    // 25min 内无任何请求（虚拟时间确定性强）
            Assert.Equal(3, scheduler.Counters.BalanceRequested.GetValueOrDefault(BalanceRefreshSource.FastPoll));

            time.Advance(TimeSpan.FromMinutes(6));                           // 越过 最近成功 + 30min
            WaitForTicks(scheduler, 7);
            WaitRefreshed(scheduler, 6);
            Assert.Equal(callsAtHide + 1, balance.CallCount);                // 恰一次后台周期刷新
            Assert.Equal(2, scheduler.Counters.BalanceRequested.GetValueOrDefault(BalanceRefreshSource.Timer));

            // 触发源序列：Timer（启动）→ Panel（显示首刷）→ FastPoll×3 → Timer（后台周期）
            Assert.Equal(BalanceRefreshSource.Timer, requested[0].Source);
            Assert.Equal(BalanceRefreshSource.Panel, requested[1].Source);
            Assert.All(requested.Skip(2).Take(3), r => Assert.Equal(BalanceRefreshSource.FastPoll, r.Source));
            Assert.Equal(BalanceRefreshSource.Timer, requested[^1].Source);
        }
        finally
        {
            _ui.Invoke(scheduler.Dispose);
        }
    }

    [Fact]
    public void Panel_ReShow_Within_Debounce_Suppressed_While_Fast_Rhythm_Clamped()
    {
        var time = new FakeTimeProvider();
        time.SetBeijing(new DateTime(2026, 9, 30, 10, 0, 0));
        var balance = new FakeBalanceService();
        var holidays = BuildHolidays(time);
        var scheduler = BuildScheduler(time, holidays, balance);

        _ui.Invoke(scheduler.Start);
        try
        {
            WaitForTicks(scheduler, 1);                                      // 启动即刷（Timer）
            WaitRefreshed(scheduler, 1);
            Assert.Equal(1, balance.CallCount);

            // 越过去抖窗后首次显示 → Panel 立即刷（完成回调同步内联于 Ui.Invoke）
            time.Advance(TimeSpan.FromSeconds(31));
            _ui.Invoke(() => scheduler.NotifyPanelVisibility(true));
            WaitRefreshed(scheduler, 2);
            Assert.Equal(2, balance.CallCount);
            Assert.Equal(1, scheduler.Counters.BalanceRequested.GetValueOrDefault(BalanceRefreshSource.Panel));

            // 显隐抖动：立即隐藏再显示（30s 去抖窗内）→ 不放大请求量（Panel 源被抑制）
            _ui.Invoke(() => scheduler.NotifyPanelVisibility(false));
            _ui.Invoke(() => scheduler.NotifyPanelVisibility(true));
            Assert.Equal(1, scheduler.Counters.PanelDebounceSuppressions);
            Assert.Equal(2, balance.CallCount);                              // 抑制期无新增请求

            // 但到期点已被钳入快轮询节奏 → 5s 内 FastPoll 接管（数据不因抑制而变陈旧）
            time.Advance(TimeSpan.FromSeconds(5));
            WaitForTicks(scheduler, 3);
            WaitRefreshed(scheduler, 3);
            Assert.Equal(3, balance.CallCount);
            Assert.Equal(1, scheduler.Counters.BalanceRequested.GetValueOrDefault(BalanceRefreshSource.FastPoll));
        }
        finally
        {
            _ui.Invoke(scheduler.Dispose);
        }
    }

    [Fact]
    public void FastPoll_Failures_Back_Off_Exponentially_Capped_At_60s_Success_Restores_5s()
    {
        var time = new FakeTimeProvider();
        time.SetBeijing(new DateTime(2026, 9, 30, 10, 0, 0));
        var balance = new FakeBalanceService { Provider = Failure };         // 启动即刷与显示期各次刷新一律失败
        var holidays = BuildHolidays(time);
        var scheduler = BuildScheduler(time, holidays, balance);
        var requested = new List<BalanceRequestedArgs>();
        scheduler.BalanceRefreshRequested += (_, e) => requested.Add(e);

        _ui.Invoke(scheduler.Start);
        try
        {
            WaitForTicks(scheduler, 1);                                      // 启动即刷失败（隐藏态 → 后台 1min 退避，不影响显示期序列）
            WaitRefreshed(scheduler, 1);
            Assert.Equal(1, balance.CallCount);

            time.Advance(TimeSpan.FromSeconds(31));
            _ui.Invoke(() => scheduler.NotifyPanelVisibility(true));
            WaitRefreshed(scheduler, 2);
            Assert.Equal(2, balance.CallCount);                              // 显示期 Panel 立即刷 → 失败 → 快轮询退避 5s 起

            // 快轮询退避序列：5→10→20→40→60→60→60（60s 封顶循环）
            var refreshed = 2;
            foreach (var step in new[] { 5, 10, 20, 40, 60, 60, 60 })
            {
                time.Advance(TimeSpan.FromSeconds(step));
                WaitForTicks(scheduler, (int)scheduler.TickCount + 1);
                WaitRefreshed(scheduler, ++refreshed);
            }
            Assert.Equal(9, balance.CallCount);                              // 1 Timer + 1 Panel + 7 FastPoll

            // 成功 → 恢复 5s；随后再次失败 → 序列从 5s 重新起算（成功复位）
            balance.Provider = null;
            time.Advance(TimeSpan.FromSeconds(60));
            WaitForTicks(scheduler, (int)scheduler.TickCount + 1);
            WaitRefreshed(scheduler, ++refreshed);                           // 第 8 次 FastPoll：成功
            Assert.Equal(10, balance.CallCount);
            time.Advance(TimeSpan.FromSeconds(5));
            WaitForTicks(scheduler, (int)scheduler.TickCount + 1);
            WaitRefreshed(scheduler, ++refreshed);                           // 第 9 次 FastPoll：成功（5s 节奏）
            Assert.Equal(11, balance.CallCount);
            balance.Provider = Failure;
            time.Advance(TimeSpan.FromSeconds(5));
            WaitForTicks(scheduler, (int)scheduler.TickCount + 1);
            WaitRefreshed(scheduler, ++refreshed);                           // 失败（复位后首个失败 → 退避 5s）
            time.Advance(TimeSpan.FromSeconds(5));
            WaitForTicks(scheduler, (int)scheduler.TickCount + 1);
            WaitRefreshed(scheduler, ++refreshed);                           // 5s 后即重试（非 10s）
            Assert.Equal(13, balance.CallCount);

            // 逐对校验显示期请求间隔：5,10,20,40,60,60,60,60 | 5,5 | 5,5
            // （requested[0]=Timer 启动即刷，requested[1]=Panel 显示期首刷，其后均为 FastPoll；
            //   60s 连续三段为封顶循环；最后一段 60s = 封顶等待后才注入成功结果，其后恢复 5s 节奏、
            //   再失败时序列从 5s 重新起算；AtUtc 取自 FakeTimeProvider 虚拟时刻，整秒精确无量化误差）
            Assert.Equal(BalanceRefreshSource.Timer, requested[0].Source);
            Assert.Equal(BalanceRefreshSource.Panel, requested[1].Source);
            Assert.All(requested.Skip(2), r => Assert.Equal(BalanceRefreshSource.FastPoll, r.Source));
            var deltas = new List<double>();
            for (int i = 2; i < requested.Count; i++)
                deltas.Add((requested[i].AtUtc - requested[i - 1].AtUtc).TotalSeconds);
            Assert.Equal(new double[] { 5, 10, 20, 40, 60, 60, 60, 60, 5, 5, 5 }, deltas);
        }
        finally
        {
            _ui.Invoke(scheduler.Dispose);
        }
    }

    private static void WaitForTicks(Scheduler scheduler, int n, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (scheduler.TickCount < n && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
        Assert.True(scheduler.TickCount >= n, $"tick#{n} 未在 {timeoutMs}ms 内到达（实际 {scheduler.TickCount}）");
    }

    /// <summary>等待余额完成回调记账到位（BalanceRefreshedCount 在 OnRefreshCompleted 内自增；
    /// 触发发生在 tick 内 TickCount 自增之后，直接断言 CallCount 存在竞争窗口）。</summary>
    private static void WaitRefreshed(Scheduler scheduler, int n, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (scheduler.Counters.BalanceRefreshedCount < n && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(5);
        Assert.True(scheduler.Counters.BalanceRefreshedCount >= n,
            $"余额完成回调#{n} 未在 {timeoutMs}ms 内到达（实际 {scheduler.Counters.BalanceRefreshedCount}）");
    }
}
