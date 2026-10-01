namespace DeepSeekBalanceWidget.Peak;

public enum PeakState { Idle, Peak }

public sealed record WorkPeriod(TimeSpan Start, TimeSpan End, PeakState State, string Name);

/// <summary>
/// 时段定义（北京时间，探针 C 口径 / D-09 进度区间）：
/// 工作日: 00:00-09:00 清晨空闲 | 09:00-12:00 上午高峰 | 12:00-14:00 午休空闲 | 14:00-18:00 下午高峰 | 18:00-24:00 晚间空闲
/// 全天空闲日(周六/周日/法定节假日): 00:00-24:00 全天空闲，进度 = 当天已过比例。
/// </summary>
public static class PeakPeriods
{
    public static readonly WorkPeriod AllDayIdle = new(TimeSpan.Zero, TimeSpan.FromHours(24), PeakState.Idle, "全天空闲");

    public static readonly WorkPeriod[] WorkdayPeriods =
    {
        new(TimeSpan.Zero,          TimeSpan.FromHours(9),  PeakState.Idle, "清晨空闲"),
        new(TimeSpan.FromHours(9),  TimeSpan.FromHours(12), PeakState.Peak, "上午高峰"),
        new(TimeSpan.FromHours(12), TimeSpan.FromHours(14), PeakState.Idle, "午休空闲"),
        new(TimeSpan.FromHours(14), TimeSpan.FromHours(18), PeakState.Peak, "下午高峰"),
        new(TimeSpan.FromHours(18), TimeSpan.FromHours(24), PeakState.Idle, "晚间空闲"),
    };

    public static WorkPeriod GetPeriod(TimeSpan timeOfDay, bool allDayIdle)
    {
        if (allDayIdle) return AllDayIdle;
        foreach (var p in WorkdayPeriods)
            if (timeOfDay >= p.Start && timeOfDay < p.End) return p;
        return WorkdayPeriods[^1];
    }
}

/// <summary>规则 2（D-11）：本地 DayOfWeek 优先——周六/周日一律全天空闲；周一至五且 API 报法定节假日 → 全天空闲。</summary>
public static class StatusCalculator
{
    public static bool IsWeekend(DateOnly d) => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    public static bool IsAllDayIdle(DateOnly date, HolidayData? data)
        => IsWeekend(date) || (data?.IsReportedHoliday ?? false);
}

/// <summary>倒计时 = 精确差值（D-08），HH 允许 >24，剩余秒向上取整，负值钳为 0。</summary>
public static class Format
{
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var s = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        return $"{(int)s.TotalHours:D2}:{s.Minutes:D2}:{s.Seconds:D2}";
    }
}

/// <summary>
/// 进度区间（D-09）：当前时段已过比例 0-100%。全天空闲日 = 00:00-24:00 当天已过比例；
/// 工作日清晨/午休/晚间空闲按其实际区间计（晚间空闲不跨天累计）。
/// </summary>
public static class ProgressCalculator
{
    public static double ProgressPercent(DateTime beijingWall, bool allDayIdle)
    {
        var p = PeakPeriods.GetPeriod(beijingWall.TimeOfDay, allDayIdle);
        var date = DateOnly.FromDateTime(beijingWall);
        var start = date.ToDateTime(TimeOnly.MinValue) + p.Start;
        var end = date.ToDateTime(TimeOnly.MinValue) + p.End;
        var frac = (beijingWall - start).TotalMinutes / (end - start).TotalMinutes;
        return Math.Clamp(frac, 0, 1) * 100.0;
    }
}

/// <summary>心跳直刷的峰谷快照（每 tick 重算；倒计时/进度为绝对差值，永不累加）。</summary>
public sealed record WidgetSnapshot(
    DateTime BeijingWall, DateOnly BeijingDate, bool AllDayIdle, PeakState State, string PeriodName,
    bool Degraded, string? DataSource, DateTime? NextSwitch, string CountdownText, double ProgressPercent);

/// <summary>节假日数据（探针 C 归一化层两源统一形态；RawJson 不进正式版）。</summary>
public sealed record HolidayData(
    bool IsReportedHoliday,    // 法定节假日（主源 daytype=1；备源 isHoliday=true 且当天周一至五）
    bool IsMakeupWorkday,      // 调休补班（主源 daytype=3；备源 isHoliday=false 且当天周末）
    bool IsWeekendReported,    // 普通周末（主源 daytype=2；备源 isHoliday=true 且当天周末）
    string SourceName,
    string? Note);
