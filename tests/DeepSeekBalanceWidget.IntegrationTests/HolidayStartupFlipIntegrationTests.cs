using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Threading;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Scheduling;
using DeepSeekBalanceWidget.Tray;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// FB-1 回归（2026-10-01 发现，阶段 6 修复）：
/// 法定节假日的工作日启动时，App 按 snap0.State（节假日数据未到达 → 本地工作日逻辑）先行初始化
/// 托盘图标与胶囊强调色；节假日数据晚于首帧到达（真实场景：异步拉取 0.2–8s），节假日恢复（C-3）
/// 触发 RecomputeNow 的 Peak→Idle 重算发生在首 tick 前后——修复前被"首评估即初始化、不发事件"
/// 语义吞掉，托盘/强调色停留高峰橙直到下一次真实翻转沿（节假日全天无翻转）。
/// 修复：Scheduler.InitialPublishedState 注入"已对外发布的初始状态"，首次评估与之不同则补发恰一次
/// StateChanged（App 已装配）。本组用例以"延迟返回的 fake 节假日源"复现该时序并断言：
/// 托盘图标变体翻转为空闲（CurrentState + tooltip）、翻转沿恰一次、随后无重复事件；
/// 并以"fake 源返回工作日"对照断言普通工作日启动不误翻（不回退既有语义）。
/// </summary>
public sealed class HolidayStartupFlipIntegrationTests : IDisposable
{
    private readonly UiThreadFixture _ui = new();
    private readonly string _iconDir = Path.Combine(Path.GetTempPath(), "dsw-test-icons-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _ui.Dispose();
        try { if (Directory.Exists(_iconDir)) Directory.Delete(_iconDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(20);
        return condition();
    }

    /// <summary>延迟返回的节假日源替身：可按日期给出不同数据（模拟"网络晚于首帧"，零真实网络）。</summary>
    private sealed class DelayedHolidaySource : IHolidaySource
    {
        private readonly Func<DateOnly, HolidayData> _dataFor;
        private readonly TimeSpan _delay;
        public string Name { get; }

        public DelayedHolidaySource(string name, Func<DateOnly, HolidayData> dataFor, TimeSpan delay)
        {
            Name = name;
            _dataFor = dataFor;
            _delay = delay;
        }

        public async Task<ApiResult> FetchAsync(DateOnly date, CancellationToken ct = default)
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct);
            return ApiResult.Ok(_dataFor(date));
        }
    }

    private static HolidayData Holiday(string note = "国庆节")
        => new(IsReportedHoliday: true, IsMakeupWorkday: false, IsWeekendReported: false, SourceName: "fake-primary", Note: note);

    private static HolidayData Workday(string note = "工作日")
        => new(IsReportedHoliday: false, IsMakeupWorkday: false, IsWeekendReported: false, SourceName: "fake-primary", Note: note);

    [Fact]
    public void Holiday_Weekday_Startup_Data_Arrives_After_First_Frame_Tray_Flip_To_Idle_Once()
    {
        var time = new FakeTimeProvider();
        // 2026-10-01 = 周四（工作日口径）10:30：无节假日数据时按本地工作日逻辑 = 上午高峰（= snap0）
        time.SetBeijing(new DateTime(2026, 10, 1, 10, 30, 0));

        // 仅 10-01 报法定节假日；后续日期（10-02 周五起）报工作日——避免前瞻拉取放大测试面
        var delayed = new DelayedHolidaySource("fake-primary",
            date => date == new DateOnly(2026, 10, 1) ? Holiday() : Workday(),
            delay: TimeSpan.FromMilliseconds(400));
        var holidays = new HolidayService(delayed, new DelayedHolidaySource("fake-backup", _ => Workday(), TimeSpan.Zero), time);
        var peak = new PeakEngine(holidays, time);
        var balance = new FakeBalanceService();
        var options = new SchedulerOptions { Heartbeat = TimeSpan.FromMilliseconds(250), TickStatsEveryN = 0 };

        var vm = new Panel.PanelViewModel();
        var sink = new RecordingSink(vm);
        var stateEvents = new List<StateChangedArgs>();
        var eventsLock = new object();
        Scheduler scheduler = null!;
        TrayController tray = null!;

        _ui.Invoke(() =>
        {
            // 复刻 App.OnStartup 首帧时序：snap0 先于节假日数据计算，并按它初始化托盘/胶囊
            var snap0 = peak.ComputeSnapshot();
            Assert.Equal(PeakState.Peak, snap0.State);            // 前置：首帧 = 工作日高峰（缺陷触发前提）
            Assert.False(snap0.Degraded);                          // 周四 10:30 本地逻辑即可判定，未降级

            tray = new TrayController(_iconDir, isDarkTheme: () => false);
            tray.Initialize(snap0.State);
            vm.ApplyState(snap0.State);
            vm.UpdateSnapshot(snap0);

            scheduler = new Scheduler(time, peak, balance, holidays, options, _ui.Dispatcher,
                isKeyConfigured: () => false);
            scheduler.InitialPublishedState = snap0.State;         // FB-1 修复装配（App 同款）
            scheduler.StateChanged += (_, e) =>
            {
                lock (eventsLock) stateEvents.Add(e);
                vm.ApplyState(e.New);                              // 与 App 装配相同的翻转沿路径
                tray.SetState(e.New);
            };
            // 复刻 App 的 C-3 装配：节假日恢复 → UI 线程封送 → RecomputeNow
            holidays.FetchCompleted += () =>
            {
                if (!UiThread.IsCurrent)
                    _ = _ui.Dispatcher.BeginInvoke(() => scheduler.RecomputeNow("节假日数据恢复(C-3)"));
                else
                    scheduler.RecomputeNow("节假日数据恢复(C-3)");
            };
            scheduler.Sink = sink;
            scheduler.Start();
        });

        try
        {
            // 节假日数据延迟 ~400ms 到达 → 恢复重算 → 必须补发翻转沿（修复前：0 事件、托盘停留 Peak）
            Assert.True(WaitFor(() => scheduler.Counters.StateChangedCount >= 1),
                "节假日数据到达后未发生 StateChanged（FB-1 未修复的表现）");
            Thread.Sleep(1200);                                    // 再跑数个 tick，验证无重复翻转沿

            lock (eventsLock)
            {
                Assert.Single(stateEvents);                        // 恰一次，无重复/抖动
                Assert.Equal(PeakState.Peak, stateEvents[0].Old);  // Old = 已对外发布的初始状态（snap0）
                Assert.Equal(PeakState.Idle, stateEvents[0].New);
                Assert.False(stateEvents[0].Degraded);
            }

            // 托盘（真实组件）：图标变体应为空闲（idle）而非高峰 + tooltip 重建为"空闲时段"
            _ui.Invoke(() =>
            {
                Assert.Equal(PeakState.Idle, tray.CurrentState);
                var field = typeof(TrayController).GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance);
                var taskbar = Assert.IsAssignableFrom<H.NotifyIcon.TaskbarIcon>(field!.GetValue(tray));
                Assert.Contains("空闲时段", taskbar.ToolTipText);
                Assert.DoesNotContain("高峰时段", taskbar.ToolTipText);
            });

            // 面板胶囊同步翻转；倒计时前缀改指下一个高峰
            Assert.Equal("空闲时段", vm.PillText);
            Assert.Equal("距高峰时段还有 ", vm.CountdownPrefix);

            // 快照（缓存命中路径）此后稳定为 Idle
            Assert.Equal(PeakState.Idle, scheduler.LastSnapshot!.State);
        }
        finally
        {
            _ui.Invoke(() => scheduler.Dispose());
            _ui.Invoke(() => tray.Dispose());
        }
    }

    [Fact]
    public void Workday_Startup_No_Spurious_Flip_Stays_Peak()
    {
        var time = new FakeTimeProvider();
        time.SetBeijing(new DateTime(2026, 10, 1, 10, 30, 0));   // 周四 10:30（工作日口径）

        // 全日期均报工作日：数据到达前后快照都应为高峰——不发生翻转、不误发事件
        var delayed = new DelayedHolidaySource("fake-primary", _ => Workday(), TimeSpan.FromMilliseconds(400));
        var holidays = new HolidayService(delayed, new DelayedHolidaySource("fake-backup", _ => Workday(), TimeSpan.Zero), time);
        var peak = new PeakEngine(holidays, time);
        var balance = new FakeBalanceService();
        var options = new SchedulerOptions { Heartbeat = TimeSpan.FromMilliseconds(250), TickStatsEveryN = 0 };

        var vm = new Panel.PanelViewModel();
        var sink = new RecordingSink(vm);
        var stateEvents = new List<StateChangedArgs>();
        var eventsLock = new object();
        Scheduler scheduler = null!;
        TrayController tray = null!;

        _ui.Invoke(() =>
        {
            var snap0 = peak.ComputeSnapshot();
            Assert.Equal(PeakState.Peak, snap0.State);

            tray = new TrayController(_iconDir, isDarkTheme: () => false);
            tray.Initialize(snap0.State);
            vm.ApplyState(snap0.State);
            vm.UpdateSnapshot(snap0);

            scheduler = new Scheduler(time, peak, balance, holidays, options, _ui.Dispatcher,
                isKeyConfigured: () => false);
            scheduler.InitialPublishedState = snap0.State;
            scheduler.StateChanged += (_, e) =>
            {
                lock (eventsLock) stateEvents.Add(e);
                vm.ApplyState(e.New);
                tray.SetState(e.New);
            };
            holidays.FetchCompleted += () =>
            {
                if (!UiThread.IsCurrent)
                    _ = _ui.Dispatcher.BeginInvoke(() => scheduler.RecomputeNow("节假日数据恢复(C-3)"));
                else
                    scheduler.RecomputeNow("节假日数据恢复(C-3)");
            };
            scheduler.Sink = sink;
            scheduler.Start();
        });

        try
        {
            // 覆盖"数据到达前后 + 多个 tick"窗口：状态恒为高峰，无翻转沿
            Thread.Sleep(2500);

            lock (eventsLock) Assert.Empty(stateEvents);
            _ui.Invoke(() =>
            {
                Assert.Equal(PeakState.Peak, tray.CurrentState);
                var field = typeof(TrayController).GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance);
                var taskbar = Assert.IsAssignableFrom<H.NotifyIcon.TaskbarIcon>(field!.GetValue(tray));
                Assert.Contains("高峰时段", taskbar.ToolTipText);
            });
            Assert.Equal("高峰时段", vm.PillText);
            Assert.Equal(PeakState.Peak, scheduler.LastSnapshot!.State);
        }
        finally
        {
            _ui.Invoke(() => scheduler.Dispose());
            _ui.Invoke(() => tray.Dispose());
        }
    }
}
