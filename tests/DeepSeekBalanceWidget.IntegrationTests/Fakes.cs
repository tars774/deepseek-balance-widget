using DeepSeekBalanceWidget.Balance;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Scheduling;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>节假日源替身：返回可控 HolidayData，不发起任何网络请求。</summary>
public sealed class FakeHolidaySource : IHolidaySource
{
    private readonly HolidayData _data;
    public string Name { get; }

    public FakeHolidaySource(string name, HolidayData? data = null)
    {
        Name = name;
        _data = data ?? new HolidayData(IsReportedHoliday: false, IsMakeupWorkday: false,
            IsWeekendReported: false, SourceName: name, Note: "工作日");
    }

    public Task<ApiResult> FetchAsync(DateOnly date, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok(_data));
}

/// <summary>按日期可编程的节假日源替身（需求变更 2026-10-02 进度口径用例：连续节假日/工作日混合日历）。</summary>
public sealed class LambdaHolidaySource : IHolidaySource
{
    private readonly Func<DateOnly, HolidayData> _dataFor;
    public string Name { get; }

    public LambdaHolidaySource(string name, Func<DateOnly, HolidayData> dataFor)
    {
        Name = name;
        _dataFor = dataFor;
    }

    public Task<ApiResult> FetchAsync(DateOnly date, CancellationToken ct = default)
        => Task.FromResult(ApiResult.Ok(_dataFor(date)));
}

/// <summary>余额服务替身：计数调用、可配置返回结果（默认成功 ¥110.00）。</summary>
public sealed class FakeBalanceService : IBalanceService
{
    private readonly BalanceResult _result;
    public int CallCount { get; private set; }

    /// <summary>
    /// 可编程结果提供者（需求变更 2026-10-02 快轮询退避用例：逐次给失败/成功序列）；
    /// null 时返回构造时给定的固定结果。
    /// </summary>
    public Func<BalanceResult>? Provider { get; set; }

    public FakeBalanceService(BalanceResult? result = null)
        => _result = result ?? new BalanceResult(BalanceStatus.Success, 110.00m, "CNY", IsAvailable: true, Note: null);

    public Task<BalanceResult> RefreshAsync()
    {
        CallCount++;
        return Task.FromResult(Provider?.Invoke() ?? _result);
    }
}

/// <summary>快照记录 sink：逐 tick 记录（含 PanelViewModel 同步直刷路径）。</summary>
public sealed class RecordingSink : ISnapshotSink
{
    public List<WidgetSnapshot> Snapshots { get; } = new();
    private readonly Panel.PanelViewModel? _vm;

    public RecordingSink(Panel.PanelViewModel? vm = null) => _vm = vm;

    public void OnTickSnapshot(WidgetSnapshot snapshot)
    {
        lock (Snapshots) Snapshots.Add(snapshot);
        _vm?.UpdateSnapshot(snapshot);
    }
}
