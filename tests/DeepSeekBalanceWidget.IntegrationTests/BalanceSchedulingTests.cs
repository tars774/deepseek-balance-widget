using System.Windows.Threading;
using DeepSeekBalanceWidget.Balance;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Scheduling;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// 余额定时调度集成（阶段 5 测试清单第 9 项的组件级证据）：
/// 未配置 Key → 定时到期静默（§4.5，不发请求）；配置 Key → 启动即刷 + 到期触发 + 完成回调。
/// </summary>
public sealed class BalanceSchedulingTests
{
    // 本类测试共用一个 STA/Dispatcher 线程（DisableTestParallelization 下无并发竞争）
    private static readonly UiThreadFixture Ui = new();

    private static (Scheduler Scheduler, HolidayService Holidays) Build(
        FakeTimeProvider time, FakeBalanceService balance, bool keyConfigured)
    {
        var holidayData = new HolidayData(false, false, false, "fake-primary", "工作日");
        var holidays = new HolidayService(new FakeHolidaySource("fake-primary", holidayData),
            new FakeHolidaySource("fake-backup", holidayData), time);
        var peak = new PeakEngine(holidays, time);
        var options = new SchedulerOptions
        {
            Heartbeat = TimeSpan.FromMilliseconds(250),
            TickStatsEveryN = 0,
        };
        var scheduler = Ui.Invoke(() =>
        {
            UiThread.Capture();
            return new Scheduler(time, peak, balance, holidays, options, Ui.Dispatcher,
                isKeyConfigured: () => keyConfigured);
        });
        return (scheduler, holidays);
    }

    [Fact]
    public void Timer_Due_Silent_When_Key_Not_Configured()
    {
        var time = new FakeTimeProvider();
        time.SetBeijing(new DateTime(2026, 9, 30, 10, 0, 0));
        var balance = new FakeBalanceService();
        var (scheduler, holidays) = Build(time, balance, keyConfigured: false);
        Ui.Invoke(scheduler.Start);
        try
        {
            // tick#1（10:00，到期=启动即刷，未配置→跳过）；随后跨 3 个到期点
            WaitForTicks(scheduler, 1);
            for (int i = 0; i < 3; i++)
            {
                time.Advance(TimeSpan.FromMinutes(31));
                WaitForTicks(scheduler, i + 2);
            }
            Assert.Equal(0, balance.CallCount);                          // 全程静默
            Assert.Equal(0, scheduler.Counters.BalanceRefreshedCount);
            Assert.Equal(0, scheduler.Counters.BalanceRequested.GetValueOrDefault(BalanceRefreshSource.Timer));
        }
        finally
        {
            Ui.Invoke(scheduler.Dispose);
        }
        GC.KeepAlive(holidays);
    }

    [Fact]
    public void Timer_Due_Triggers_With_Key_And_Completes()
    {
        var time = new FakeTimeProvider();
        time.SetBeijing(new DateTime(2026, 9, 30, 10, 0, 0));
        var balance = new FakeBalanceService();
        var refreshed = new ManualResetEventSlim(false);
        var (scheduler, holidays) = Build(time, balance, keyConfigured: true);
        scheduler.BalanceRefreshed += (_, _) => refreshed.Set();
        Ui.Invoke(scheduler.Start);
        try
        {
            WaitForTicks(scheduler, 1);                                  // tick#1：启动即刷（source=Timer）
            Assert.True(refreshed.Wait(TimeSpan.FromSeconds(5)), "余额完成回调未到达");
            Assert.Equal(1, balance.CallCount);
            Assert.Equal(1, scheduler.Counters.BalanceRefreshedCount);

            time.Advance(TimeSpan.FromMinutes(31));                      // 越过 30min 到期点
            WaitForTicks(scheduler, 2);
            Assert.True(refreshed.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, balance.CallCount);                          // 第二周期触发

            time.Advance(TimeSpan.FromMinutes(25));                      // 10:56 < 下一到期点 11:01
            WaitForTicks(scheduler, 3);
            Assert.Equal(2, balance.CallCount);                          // 未到到期点 → 不触发

            time.Advance(TimeSpan.FromMinutes(10));                      // 11:06 ≥ 11:01
            WaitForTicks(scheduler, 4);
            Assert.True(refreshed.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(3, balance.CallCount);                          // 第三周期触发
        }
        finally
        {
            Ui.Invoke(scheduler.Dispose);
        }
        GC.KeepAlive(holidays);
    }

    private static void WaitForTicks(Scheduler scheduler, int n, int timeoutMs = 8000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (scheduler.TickCount < n && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
        Assert.True(scheduler.TickCount >= n, $"tick#{n} 未在 {timeoutMs}ms 内到达");
    }
}
