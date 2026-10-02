using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// 进度「剩余口径」单测（需求变更 2026-10-02）：
/// 绿条占比 = 剩余 ÷ 总长，钳制 [0,1]——终点 = NextSwitch（D-08 同源），起点 = 当前连续时段段起点
/// （上一状态翻转时刻；连续多日全天空闲取上一工作日 18:00）；剩余与倒计时同源同值，
/// 数字归零瞬间绿条恰好归零、翻入新时段立即回满；NextSwitch=null 满绿。
/// 覆盖：工作日高峰段内中点、跨天晚间空闲（18:00 → 次日 09:00）、工作日清晨空闲跨午夜回溯、
/// 连续多日节假日空闲段、切换瞬间归零/回满、NextSwitch=null 满绿、[0,1] 钳制边界。
/// 全部经 PeakEngine（FakeTimeProvider + 假节假日源）计算，零网络。
/// </summary>
public sealed class RemainingProgressTests
{
    private static HolidayData Workday() => new(false, false, false, "fake-primary", "工作日");
    private static HolidayData Holiday() => new(true, false, false, "fake-primary", "法定节假日");

    private static PeakEngine BuildEngine(FakeTimeProvider time, Func<DateOnly, HolidayData> dataFor)
    {
        var holidays = new HolidayService(
            new LambdaHolidaySource("fake-primary", dataFor),
            new LambdaHolidaySource("fake-backup", dataFor), time);
        return new PeakEngine(holidays, time);
    }

    [Fact]
    public void Workday_Morning_Peak_Midpoint_Is_Half()
    {
        var time = new FakeTimeProvider();
        var engine = BuildEngine(time, _ => Workday());
        time.SetBeijing(new DateTime(2026, 9, 30, 10, 30, 0));   // 周三，上午高峰（09:00-12:00）中点

        var snap = engine.ComputeSnapshot();

        Assert.Equal(PeakState.Peak, snap.State);
        Assert.Equal(new DateTime(2026, 9, 30, 12, 0, 0), snap.NextSwitch!.Value);
        // 段 = 09:00 → 12:00（3h），剩余 1.5h → 50%
        Assert.Equal(50.0, snap.ProgressPercent, 6);
    }

    [Fact]
    public void Workday_Evening_Idle_Spans_Midnight_To_Next_Workday_9AM()
    {
        var time = new FakeTimeProvider();
        var engine = BuildEngine(time, _ => Workday());
        time.SetBeijing(new DateTime(2026, 9, 30, 22, 0, 0));    // 周三 22:00（晚间空闲），周四为工作日

        var snap = engine.ComputeSnapshot();

        Assert.Equal(PeakState.Idle, snap.State);
        // 终点 = 次日（工作日）09:00；起点 = 当日 18:00（下午高峰结束）；总长 15h，剩余 11h
        Assert.Equal(new DateTime(2026, 10, 1, 9, 0, 0), snap.NextSwitch!.Value);
        Assert.Equal(11.0 / 15.0 * 100.0, snap.ProgressPercent, 6);
    }

    [Fact]
    public void Workday_Morning_Idle_Segment_Starts_At_Previous_Workday_Evening()
    {
        var time = new FakeTimeProvider();
        var engine = BuildEngine(time, _ => Workday());
        time.SetBeijing(new DateTime(2026, 9, 30, 8, 0, 0));     // 周三 08:00（清晨空闲），周二为工作日

        var snap = engine.ComputeSnapshot();

        Assert.Equal(PeakState.Idle, snap.State);
        // 段 = 周二 18:00 → 周三 09:00（跨午夜连续空闲，总长 15h），剩余 1h
        Assert.Equal(new DateTime(2026, 9, 30, 9, 0, 0), snap.NextSwitch!.Value);
        Assert.Equal(1.0 / 15.0 * 100.0, snap.ProgressPercent, 6);
    }

    [Fact]
    public void Consecutive_Holiday_Idle_Segment_Starts_At_Last_Workday_Evening()
    {
        // 2026-10-02（周五）工作日；10-03/10-04 周末（本地短路）；10-05（周一）法定节假日；
        // 10-06（周二）工作日——连续空闲段 = 周五 18:00 → 周二 09:00（87h）
        var time = new FakeTimeProvider();
        var engine = BuildEngine(time, d => d == new DateOnly(2026, 10, 5) ? Holiday() : Workday());

        // 同步替身约定：GetDayData 首调用发起拉取并返回 null（未知日期按工作日处理，与生产"下一 tick
        // 自然修正"同口径）——先在目标时刻跑一次预热快照让节假日数据入缓存，再取稳定态断言
        time.SetBeijing(new DateTime(2026, 10, 5, 20, 0, 0));    // 周一（节假日）20:00
        _ = engine.ComputeSnapshot();                            // 预热（首调返回 null，缓存已同步填充）
        var mondayNight = engine.ComputeSnapshot();
        Assert.True(mondayNight.AllDayIdle);
        Assert.Equal(new DateTime(2026, 10, 6, 9, 0, 0), mondayNight.NextSwitch!.Value);
        Assert.Equal(13.0 / 87.0 * 100.0, mondayNight.ProgressPercent, 6);   // 剩余 = 周二 09:00 − 周一 20:00 = 13h

        time.SetBeijing(new DateTime(2026, 10, 3, 12, 0, 0));    // 周六（周末）12:00
        var saturdayNoon = engine.ComputeSnapshot();
        Assert.True(saturdayNoon.AllDayIdle);
        Assert.Equal(new DateTime(2026, 10, 6, 9, 0, 0), saturdayNoon.NextSwitch!.Value);
        Assert.Equal(69.0 / 87.0 * 100.0, saturdayNoon.ProgressPercent, 6);  // 剩余 = 周二 09:00 − 周六 12:00 = 69h
    }

    [Fact]
    public void Switch_Instant_Progress_Near_Zero_Before_And_Full_After()
    {
        var time = new FakeTimeProvider();
        var engine = BuildEngine(time, _ => Workday());

        time.SetBeijing(new DateTime(2026, 9, 30, 11, 59, 59));  // 上午高峰末尾：数字 00:00:01，条恰好趋零
        var before = engine.ComputeSnapshot();
        Assert.Equal(PeakState.Peak, before.State);
        Assert.True(before.ProgressPercent < 1.0, $"翻转前进度 {before.ProgressPercent:F4}%");

        time.SetBeijing(new DateTime(2026, 9, 30, 12, 0, 0));    // 翻入午休空闲：剩余 = 总长 → 立即回满
        var after = engine.ComputeSnapshot();
        Assert.Equal(PeakState.Idle, after.State);
        Assert.True(after.ProgressPercent > 99.0, $"翻转后进度 {after.ProgressPercent:F4}%");
    }

    [Fact]
    public void NextSwitch_Null_Keeps_Full_Green_With_Placeholder_Countdown()
    {
        // 全部日期报法定节假日 → 400 天前瞻无工作日 → NextSwitch=null → 满绿静止 +「--:--:--」。
        // 同步替身约定：GetDayData 首调用发起拉取并返回 null（未知日期按工作日处理，与生产"下一 tick
        // 自然修正"同口径）——前瞻窗口逐 tick 入缓存，重算至搜索走到底（≤400 天，周末本地短路免拉取）
        var time = new FakeTimeProvider();
        var engine = BuildEngine(time, _ => Holiday());
        time.SetBeijing(new DateTime(2026, 9, 30, 10, 0, 0));

        var snap = engine.ComputeSnapshot();
        for (int i = 0; i < 410 && snap.NextSwitch is not null; i++)
            snap = engine.ComputeSnapshot();

        Assert.Null(snap.NextSwitch);
        Assert.Equal("--:--:--", snap.CountdownText);
        Assert.Equal(100.0, snap.ProgressPercent, 6);
    }

    [Fact]
    public void ProgressCalculator_Clamps_To_Unit_Range_And_Guards_Degenerate_Total()
    {
        var start = new DateTime(2026, 9, 30, 9, 0, 0);
        var end = new DateTime(2026, 9, 30, 12, 0, 0);

        Assert.Equal(100.0, ProgressCalculator.ProgressPercent(new DateTime(2026, 9, 30, 8, 0, 0), end, start), 6);  // 墙钟早于段起点 → 钳满
        Assert.Equal(0.0, ProgressCalculator.ProgressPercent(end, end, start), 6);                                    // 剩余 = 0 → 钳空
        Assert.Equal(0.0, ProgressCalculator.ProgressPercent(new DateTime(2026, 9, 30, 13, 0, 0), end, start), 6);    // 越过终点 → 钳空
        Assert.Equal(0.0, ProgressCalculator.ProgressPercent(end, end, end), 6);                                      // 退化 total≤0（已到终点）
        Assert.Equal(100.0, ProgressCalculator.ProgressPercent(new DateTime(2026, 9, 30, 8, 0, 0), end, end), 6);     // 退化 total≤0（未到终点）
        Assert.False(double.IsNaN(ProgressCalculator.ProgressPercent(end, end, end)));                                // 无 NaN
    }

    [Fact]
    public void PanelViewModel_Maps_Remaining_Fraction_To_Green_Column()
    {
        // 需求变更 2026-10-02 语义反转：绿条（Filled）= 剩余占比，灰条（Remaining）= 已过占比
        var vm = new Panel.PanelViewModel();
        var snap = new WidgetSnapshot(
            new DateTime(2026, 9, 30, 10, 30, 0), new DateOnly(2026, 9, 30), AllDayIdle: false,
            PeakState.Peak, "上午高峰", Degraded: false, DataSource: "fake-primary",
            NextSwitch: new DateTime(2026, 9, 30, 12, 0, 0), CountdownText: "01:30:00", ProgressPercent: 25.0);

        vm.UpdateSnapshot(snap);

        Assert.Equal(0.25, vm.ProgressFilled.Value, 6);
        Assert.Equal(0.75, vm.ProgressRemaining.Value, 6);
    }
}
