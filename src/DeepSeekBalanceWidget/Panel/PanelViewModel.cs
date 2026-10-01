using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using DeepSeekBalanceWidget.Balance;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;

namespace DeepSeekBalanceWidget.Panel;

/// <summary>
/// 面板绑定源（final-technical-plan §3.2）：状态/余额/倒计时/进度/上次刷新/降级标志。
/// 倒计时与进度由 Scheduler 每 tick 直写（§3.2：不走事件，1Hz 直刷）；状态胶囊文案由
/// 翻转沿（StateChanged）与首帧快照驱动；余额区四态渲染按 D-07 + A1 §5 五条补充约定。
/// </summary>
public sealed class PanelViewModel : INotifyPropertyChanged
{
    private PeakState? _appliedState;
    private BalanceStatus _balanceStatus = BalanceStatus.NotConfigured;
    private bool _degraded;
    private bool _isRefreshing;
    private DateTimeOffset? _lastSuccessBeijing;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));

    private void RaiseAll(params string[] names)
    {
        foreach (var n in names) Raise(n);
    }

    // ---------------- 顶行：状态胶囊 ----------------

    public string PillText => _appliedState == PeakState.Peak ? "高峰时段" : "空闲时段";

    // ---------------- 倒计时区（每 tick 直刷） ----------------

    public string CountdownPrefix => _appliedState == PeakState.Peak ? "距空闲时段还有 " : "距高峰时段还有 ";

    public string CountdownText { get; private set; } = "--:--:--";

    /// <summary>A1 §2 进度条：4px 细横条，填充 = 当前时段已过比例（星号比例两列实现）。</summary>
    public GridLength ProgressFilled { get; private set; } = new(0, GridUnitType.Star);
    public GridLength ProgressRemaining { get; private set; } = new(1, GridUnitType.Star);

    // ---------------- 余额区（D-07 四态 + A1 约定 1/2） ----------------

    public string BalanceText { get; private set; } = "¥ --";
    public bool BalanceNumberVisible { get; private set; }
    public bool CaptionVisible { get; private set; }
    public string BalanceHint { get; private set; } = "请先在设置中配置 API Key";
    public bool BalanceHintVisible { get; private set; } = true;

    /// <summary>is_available=false 的展示态提示（非错误，探针 B §三）。</summary>
    public bool UnavailableVisible { get; private set; }

    /// <summary>未配置 / Key 无效 → 整块可点击打开设置（A1 约定 1）。</summary>
    public bool IsBalanceClickable => _balanceStatus is BalanceStatus.NotConfigured or BalanceStatus.Unauthorized;

    /// <summary>余额区鼠标指针：可点击态显示手型。</summary>
    public System.Windows.Input.Cursor BalanceCursor =>
        IsBalanceClickable ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set { if (_isRefreshing != value) { _isRefreshing = value; Raise(); } }   // A1 约定 4
    }

    // ---------------- v1.1：钉住状态（顶行图钉钮 DataTrigger 绑定源） ----------------

    private bool _isPinned;

    /// <summary>钉住状态：驱动图钉钮「未钉=描边常态色 / 已钉=填充强调色」与 tooltip 切换。</summary>
    public bool IsPinned
    {
        get => _isPinned;
        private set { if (_isPinned != value) { _isPinned = value; Raise(); } }
    }

    /// <summary>钉住切换入口（PanelWindow.SetPinned 同步调用）。</summary>
    public void SetPinned(bool pinned) => IsPinned = pinned;

    // ---------------- 底部行 ----------------

    /// <summary>「上次刷新 今天 HH:mm」：取值语义 = 最近一次成功刷新时间（失败不回退，§6 补充）。</summary>
    public string LastRefreshLine { get; private set; } = "上次刷新 尚未成功";

    /// <summary>节假日降级小图标（A1 约定 3：底行右侧 10–12px --muted，悬停文案）。</summary>
    public bool DegradedVisible => _degraded;

    // ---------------- 更新入口 ----------------

    /// <summary>状态翻转沿 / 首 tick 初始化（A1 胶囊文案 + 倒计时前缀）。</summary>
    public void ApplyState(PeakState state)
    {
        if (_appliedState == state) return;
        _appliedState = state;
        Raise(nameof(PillText));
        Raise(nameof(CountdownPrefix));
    }

    /// <summary>每 tick 直刷：倒计时 + 进度（含首帧状态初始化，无事件路径）。</summary>
    public void UpdateSnapshot(WidgetSnapshot snap)
    {
        ApplyState(snap.State);
        if (CountdownText != snap.CountdownText)
        {
            CountdownText = snap.CountdownText;
            Raise(nameof(CountdownText));
        }
        double frac = Math.Clamp(snap.ProgressPercent / 100.0, 0, 1);
        ProgressFilled = new GridLength(frac, GridUnitType.Star);
        ProgressRemaining = new GridLength(1.0 - frac, GridUnitType.Star);
        Raise(nameof(ProgressFilled));
        Raise(nameof(ProgressRemaining));
        SetDegraded(snap.Degraded);
    }

    public void SetDegraded(bool degraded)
    {
        if (_degraded == degraded) return;
        _degraded = degraded;
        Raise(nameof(DegradedVisible));
    }

    public void SetRefreshing(bool refreshing) => IsRefreshing = refreshing;

    /// <summary>
    /// 余额区四态渲染（D-07 + A1 §5-1/2；D-19 手动余额模式优先）。
    /// 失败态一律不显示缓存余额（约定 2）；is_available=false 仅加「余额不可调用」提示（§6-5）。
    /// </summary>
    public void UpdateBalance(BalanceResult result, bool manualBalanceEnabled, string manualBalanceText, DateTimeOffset atUtc)
    {
        _balanceStatus = result.Status;
        UnavailableVisible = false;
        BalanceHintVisible = false;

        if (manualBalanceEnabled &&
            decimal.TryParse(manualBalanceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var manual))
        {
            // D-19：P2 可选降级——手动余额仅本地展示
            BalanceText = $"¥ {manual.ToString("N2", CultureInfo.InvariantCulture)}";
            BalanceNumberVisible = true;
            CaptionVisible = true;
        }
        else
        {
            switch (result.Status)
            {
                case BalanceStatus.Success:
                    BalanceText = $"¥ {(result.Balance ?? 0).ToString("N2", CultureInfo.InvariantCulture)}";
                    BalanceNumberVisible = true;
                    CaptionVisible = true;
                    UnavailableVisible = !result.IsAvailable;   // 0 余额普通成功；仅附加提示
                    _lastSuccessBeijing = BeijingClock.ToBeijing(atUtc);
                    break;

                case BalanceStatus.NotConfigured:
                    BalanceHint = "请先在设置中配置 API Key";
                    BalanceNumberVisible = false;
                    CaptionVisible = false;
                    BalanceHintVisible = true;
                    break;

                case BalanceStatus.Unauthorized:
                    BalanceHint = "API Key 无效，请检查设置";
                    BalanceNumberVisible = false;
                    CaptionVisible = false;
                    BalanceHintVisible = true;
                    break;

                case BalanceStatus.NetworkFailure:
                case BalanceStatus.MalformedResponse:
                    BalanceHint = "余额获取失败";
                    BalanceNumberVisible = false;
                    CaptionVisible = false;
                    BalanceHintVisible = true;
                    break;
            }
        }

        if (result.Status == BalanceStatus.Success)
            LastRefreshLine = BuildRefreshLine(atUtc);

        RaiseAll(nameof(BalanceText), nameof(BalanceNumberVisible), nameof(CaptionVisible),
                 nameof(BalanceHint), nameof(BalanceHintVisible), nameof(UnavailableVisible),
                 nameof(IsBalanceClickable), nameof(BalanceCursor), nameof(LastRefreshLine));
    }

    private string BuildRefreshLine(DateTimeOffset atUtc)
    {
        var beijing = BeijingClock.ToBeijing(atUtc);
        var nowBeijing = BeijingClock.ToBeijing(DateTimeOffset.UtcNow);
        return beijing.Date == nowBeijing.Date
            ? $"上次刷新 今天 {beijing:HH:mm}"
            : $"上次刷新 {beijing:MM-dd HH:mm}";
    }
}
