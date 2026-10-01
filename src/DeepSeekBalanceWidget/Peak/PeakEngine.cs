using DeepSeekBalanceWidget.Infrastructure;

namespace DeepSeekBalanceWidget.Peak;

/// <summary>
/// 峰谷快照引擎（探针 C PeakStatusEngine / 探针 E PeakEngine 同步版移植）。
/// ComputeSnapshot 为纯内存同步路径：节假日数据取自 HolidayService 内存缓存（未命中时异步补拉、
/// 本 tick 按 null 处理 + Degraded 标志），tick 内绝无网络等待。
/// </summary>
public sealed class PeakEngine
{
    private readonly HolidayService _holidays;
    private readonly ITimeProvider _time;

    public PeakEngine(HolidayService holidays, ITimeProvider time)
    {
        _holidays = holidays;
        _time = time;
    }

    public WidgetSnapshot ComputeSnapshot()
    {
        var wall = BeijingClock.ToBeijing(_time.UtcNow);
        var today = DateOnly.FromDateTime(wall);
        var data = _holidays.GetDayData(today);                       // 命中缓存或异步补拉（null=降级处理）
        bool allIdle = StatusCalculator.IsAllDayIdle(today, data);    // D-11：本地星期优先
        var next = NextSwitch(wall, today, allIdle);                  // D-12：前瞻 ≤400 天
        var period = PeakPeriods.GetPeriod(wall.TimeOfDay, allIdle);
        TimeSpan? remaining = next is null ? null : next.Value - wall; // D-08：精确差值
        bool degraded = data is null && _holidays.IsDegradedNow;

        return new WidgetSnapshot(wall, today, allIdle, period.State, period.Name,
            Degraded: degraded, DataSource: data?.SourceName, NextSwitch: next,
            CountdownText: remaining is null ? "--:--:--" : Format.Countdown(remaining.Value),
            ProgressPercent: ProgressCalculator.ProgressPercent(wall, allIdle));
    }

    /// <summary>
    /// 下一切换点（D-08/D-12）：工作日内取 9/12/14/18；全天空闲日与工作日晚间 → 逐日向前搜索下一个
    /// 工作日 09:00（本地周末短路不请求；上限 400 天；未来日期拉取失败按周一至五处理）。
    /// </summary>
    private DateTime? NextSwitch(DateTime wall, DateOnly date, bool todayAllDayIdle)
    {
        var tod = wall.TimeOfDay;
        if (!todayAllDayIdle)
        {
            if (tod < TimeSpan.FromHours(9)) return date.ToDateTime(new TimeOnly(9, 0));
            if (tod < TimeSpan.FromHours(12)) return date.ToDateTime(new TimeOnly(12, 0));
            if (tod < TimeSpan.FromHours(14)) return date.ToDateTime(new TimeOnly(14, 0));
            if (tod < TimeSpan.FromHours(18)) return date.ToDateTime(new TimeOnly(18, 0));
        }
        for (int i = 1; i <= 400; i++)
        {
            var d = date.AddDays(i);
            if (StatusCalculator.IsWeekend(d)) continue;          // 本地周末短路，不消耗请求
            var fd = _holidays.GetDayData(d);
            if (!StatusCalculator.IsAllDayIdle(d, fd)) return d.ToDateTime(new TimeOnly(9, 0));
        }
        return null;
    }
}
