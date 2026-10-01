using System.Diagnostics;

namespace DeepSeekBalanceWidget.Infrastructure;

/// <summary>
/// 可注入时钟（探针 C 做法移植 + 探针 E 扩展；scheduler-design §7.1）。
/// 生产用 SystemTimeProvider；单元测试注入 FakeTimeProvider（随产品源码走，供 tests/ 工程引用）。
/// 全部时间读取必须经本接口——绝不用 DateTime.Now 当北京时间（D-14）。
/// </summary>
public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemTimeProvider : ITimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// 测试假时钟（探针 E 实装能力移植）：
/// ① 手动步进：SetUtc / SetBeijing / Advance——确定性单步；
/// ② 加速倍率：StartFlow(multiplier) 后虚拟时间按真实流逝 × 倍率流动（把 30 分钟压缩到秒级的流式验证）。
/// 步进语义约定：先评估后推进——tick n 的评估时刻 = 锚点 + (n−1)×步长（由调用方保证）。
/// </summary>
public sealed class FakeTimeProvider : ITimeProvider
{
    private DateTimeOffset _current;
    private double _multiplier;          // 0 = 离散手动模式
    private Stopwatch? _flowSw;
    private DateTimeOffset _flowAnchor;

    public FakeTimeProvider()
        => _current = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow
    {
        get
        {
            if (_multiplier > 0 && _flowSw != null)
                return _flowAnchor + TimeSpan.FromMilliseconds(_flowSw.Elapsed.TotalMilliseconds * _multiplier);
            return _current;
        }
    }

    public void SetUtc(DateTimeOffset utc) { StopFlow(); _current = utc; }

    /// <summary>直接给北京墙上时间（Kind 任意，按 Unspecified 处理）。</summary>
    public void SetBeijing(DateTime beijingWall)
        => SetUtc(new DateTimeOffset(DateTime.SpecifyKind(beijingWall, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).ToUniversalTime());

    /// <summary>从当前虚拟时刻前进 delta（flow 模式下先冻结再步进）。</summary>
    public void Advance(TimeSpan delta) { var now = UtcNow; StopFlow(); _current = now + delta; }

    /// <summary>开启加速流动：虚拟时刻 = 锚点 + 真实流逝 × multiplier。</summary>
    public void StartFlow(double multiplier)
    {
        _current = UtcNow;
        _flowAnchor = _current;
        _flowSw = Stopwatch.StartNew();
        _multiplier = multiplier;
    }

    public void StopFlow()
    {
        if (_multiplier <= 0) return;
        _current = UtcNow;               // 冻结在当前虚拟时刻
        _multiplier = 0;
        _flowSw = null;
    }

    public bool IsFlowing => _multiplier > 0;
    public double Multiplier => _multiplier;
}
