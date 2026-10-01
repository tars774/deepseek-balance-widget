using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using DeepSeekBalanceWidget.Balance;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Scheduling;
using DeepSeekBalanceWidget.Tray;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// 阶段 5 测试清单第 4 项（真实切换点不可达 → Fake 时钟集成测试）：
/// FakeTimeProvider + 真实 Scheduler + 真实 PeakEngine + 真实 TrayController + PanelViewModel，
/// 跨北京 12:00:00 逐秒步进——断言 StateChanged 恰 1 次、托盘图标变体峰→谷、
/// 胶囊翻转、倒计时目标 12:00→14:00、进度条区间重置。同时是阶段 6 CI 的种子。
/// </summary>
public sealed class PeakFlipIntegrationTests : IDisposable
{
    private readonly UiThreadFixture _ui = new();
    private readonly string _iconDir = Path.Combine(Path.GetTempPath(), "dsw-test-icons-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _ui.Dispose();
        try { if (Directory.Exists(_iconDir)) Directory.Delete(_iconDir, recursive: true); } catch { /* 清理失败不影响结论 */ }
    }

    private static void WaitForTicks(Scheduler scheduler, int n, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (scheduler.TickCount < n && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
        Assert.True(scheduler.TickCount >= n, $"tick#{n} 未在 {timeoutMs}ms 内到达（实际 {scheduler.TickCount}）");
    }

    [Fact]
    public void Cross_120000_StateChanged_Once_And_All_Components_Sync()
    {
        var time = new FakeTimeProvider();
        // 2026-09-30 = 周三（工作日）；11:59:57 处于上午高峰（09:00-12:00）
        time.SetBeijing(new DateTime(2026, 9, 30, 11, 59, 57));

        var holidayData = new HolidayData(false, false, false, "fake-primary", "工作日");
        var holidays = new HolidayService(new FakeHolidaySource("fake-primary", holidayData),
            new FakeHolidaySource("fake-backup", holidayData), time);
        var peak = new PeakEngine(holidays, time);
        var balance = new FakeBalanceService();
        var options = new SchedulerOptions
        {
            Heartbeat = TimeSpan.FromMilliseconds(250),   // 真实 tick 节拍 4Hz；虚拟时间每 tick +1s（FakeTimeProvider）
            TickStatsEveryN = 0,
        };

        var vm = new Panel.PanelViewModel();
        var sink = new RecordingSink(vm);
        var stateEvents = new List<StateChangedArgs>();
        var eventsLock = new object();
        bool keyConfigured = false;

        TrayController tray = null!;
        Scheduler scheduler = null!;
        _ui.Invoke(() =>
        {
            tray = new TrayController(_iconDir, isDarkTheme: () => false);
            tray.Initialize(PeakState.Peak);                 // 启动初态 = 高峰（与 11:59 快照一致）
            vm.ApplyState(PeakState.Peak);
            scheduler = new Scheduler(time, peak, balance, holidays, options, _ui.Dispatcher,
                isKeyConfigured: () => keyConfigured);
            scheduler.StateChanged += (_, e) =>
            {
                lock (eventsLock) stateEvents.Add(e);
                vm.ApplyState(e.New);                        // 与 App 装配相同的翻转沿路径
                tray.SetState(e.New);
            };
            scheduler.Sink = sink;
            scheduler.Start();
        });

        try
        {
            // tick#1 读到 11:59:57；随后每步 +1s 跨过 12:00:00 至 12:00:03
            WaitForTicks(scheduler, 1);
            for (int i = 0; i < 6; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                WaitForTicks(scheduler, i + 2);
            }

            var snaps = sink.Snapshots.ToList();
            Assert.True(snaps.Count >= 7, $"快照数不足：{snaps.Count}");

            // ---- 翻转沿：StateChanged 恰 1 次（Peak→Idle），且无重复/无抖动 ----
            lock (eventsLock)
            {
                Assert.Single(stateEvents);
                Assert.Equal(PeakState.Peak, stateEvents[0].Old);
                Assert.Equal(PeakState.Idle, stateEvents[0].New);
                Assert.False(stateEvents[0].Degraded);
            }

            // ---- 快照时间线：12:00:00 前全部 Peak（目标 12:00），从 12:00:00 起全部 Idle（目标 14:00）----
            var noon = snaps.FindIndex(s => s.BeijingWall.TimeOfDay == new TimeSpan(12, 0, 0));
            Assert.True(noon > 0, "未记录到 12:00:00 快照");
            foreach (var s in snaps.Take(noon))
            {
                Assert.Equal(PeakState.Peak, s.State);
                Assert.Equal(new TimeSpan(12, 0, 0), s.NextSwitch!.Value.TimeOfDay);
            }
            foreach (var s in snaps.Skip(noon))
            {
                Assert.Equal(PeakState.Idle, s.State);
                Assert.Equal(new TimeSpan(14, 0, 0), s.NextSwitch!.Value.TimeOfDay);   // 目标从 12:00 改指 14:00
            }

            // ---- 倒计时：跨点前 00:00:0x，12:00:00 恰为 02:00:00（=距 14:00），随后逐秒递减 ----
            Assert.Equal("02:00:00", snaps[noon].CountdownText);
            Assert.Equal("01:59:59", snaps[noon + 1].CountdownText);
            Assert.StartsWith("00:00:0", snaps[noon - 1].CountdownText);

            // ---- 进度条区间重置：翻转前 ≈100%（上午高峰末尾），翻转后 ≈0%（午休空闲开头）----
            Assert.True(snaps[noon - 1].ProgressPercent > 99.0, $"翻转前进度 {snaps[noon - 1].ProgressPercent:F2}%");
            Assert.True(snaps[noon].ProgressPercent < 1.0, $"翻转后进度 {snaps[noon].ProgressPercent:F2}%");

            // ---- 托盘（真实组件）：CurrentState 峰→谷，tooltip 重建为"空闲时段"（A-1 NIM 重建路径）----
            _ui.Invoke(() =>
            {
                Assert.Equal(PeakState.Idle, tray.CurrentState);
                var field = typeof(TrayController).GetField("_tray",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var taskbar = Assert.IsAssignableFrom<H.NotifyIcon.TaskbarIcon>(field!.GetValue(tray));
                Assert.Contains("空闲时段", taskbar.ToolTipText);
            });

            // ---- 面板 ViewModel：胶囊翻转 + 倒计时前缀改指高峰（14:00）+ 进度条区间重置 ----
            // （循环末 tick = 12:00:03，vm 呈现该 tick 直刷值：距 14:00 还剩 01:59:57）
            Assert.Equal("空闲时段", vm.PillText);
            Assert.Equal("距高峰时段还有 ", vm.CountdownPrefix);
            Assert.Equal("01:59:57", vm.CountdownText);
            Assert.True(vm.ProgressFilled.Value < 0.01, $"面板进度条填充 {vm.ProgressFilled.Value:F4}");
            Assert.True(vm.ProgressRemaining.Value > 0.99);
        }
        finally
        {
            _ui.Invoke(() => scheduler.Dispose());
            _ui.Invoke(() => tray.Dispose());
        }
    }
}
