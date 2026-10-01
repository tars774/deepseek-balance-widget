namespace DeepSeekBalanceWidget.Infrastructure;

/// <summary>
/// UTC → 北京时间（UTC+8）（D-14）。优先系统时区 "China Standard Time"，找不到依次回退
/// "Asia/Shanghai"、固定 +08:00（并置 UsingFixedFallback 标志）。跨天发生在 UTC 16:00
/// （探针 C G 套件秒级验证）。探针 C BeijingTime.cs 原样移植并更名。
/// </summary>
public static class BeijingClock
{
    public static bool UsingFixedFallback { get; private set; }

    private static readonly TimeZoneInfo Tz = Resolve();

    private static TimeZoneInfo Resolve()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch (Exception) { /* 尝试下一个 */ }

        try { return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai"); }
        catch (Exception) { /* 回退 */ }

        UsingFixedFallback = true;
        return TimeZoneInfo.CreateCustomTimeZone("UTC+08:00(fallback)", TimeSpan.FromHours(8),
            "北京时间(固定+08:00)", "北京时间固定+08:00");
    }

    public static string TzId => Tz.Id;

    /// <summary>北京墙上时间（Kind=Unspecified，仅用于显示与日内计算）。</summary>
    public static DateTime ToBeijing(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Tz).DateTime;

    /// <summary>把北京墙上日期时间转为 UTC 时刻（设置/测试辅助）。</summary>
    public static DateTimeOffset FromBeijing(DateTime beijingWall)
        => new DateTimeOffset(DateTime.SpecifyKind(beijingWall, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUniversalTime();
}
