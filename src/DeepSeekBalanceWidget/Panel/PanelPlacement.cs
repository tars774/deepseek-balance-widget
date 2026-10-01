namespace DeepSeekBalanceWidget.Panel;

/// <summary>
/// v1.1 面板定位/拖动/吸附纯函数（无 UI 依赖，可单测；需求变更 2026-09-30）。
/// 坐标一律为 WPF DIP（与 Window.Left/Top、SystemParameters.WorkArea 同一坐标系）。
/// 多显示器假设：以主显示器工作区为准（SystemParameters.WorkArea 即主屏工作区）。
/// </summary>
public static class PanelPlacement
{
    /// <summary>
    /// 阴影透明边距：面板窗口比可见卡片四周大 14px（PanelWindow.xaml 根 Grid Margin="14"）。
    /// "贴右缘"指可见卡片贴边 → 窗口右缘需超出工作区右缘 gutter 像素。
    /// </summary>
    public const double ShadowGutter = 14;

    /// <summary>默认停靠垂直位：窗口底边距主屏工作区底 12px（任务栏上方）。</summary>
    public const double DockBottomGap = 12;

    /// <summary>
    /// 拖动进入阈值：按下后位移 &gt;4px 才进入拖动（防误触设计——单击/双击空白处不产生位移，
    /// 不触发 DragMove；超过阈值视为明确的拖动意图）。
    /// </summary>
    public const double DragThresholdPx = 4;

    /// <summary>右缘吸附阈值：拖动释放后可见卡片右缘距工作区右缘 ≤28px → 水平吸附贴边（垂直保持）。</summary>
    public const double SnapThresholdPx = 28;

    /// <summary>
    /// 默认停靠位（v1.1 需求 1）：可见卡片贴工作区右缘、窗口底边距工作区底 12px。
    /// 即窗口 X = workRight + gutter − windowWidth（窗口右缘超出屏幕 14px），
    /// 窗口 Y = workBottom − DockBottomGap − windowHeight。
    /// </summary>
    public static (double X, double Y) ComputeDefaultDockedPosition(
        double workRight, double workBottom, double windowWidth, double windowHeight,
        double gutter = ShadowGutter, double bottomGap = DockBottomGap)
    {
        return (workRight + gutter - windowWidth, workBottom - bottomGap - windowHeight);
    }

    /// <summary>
    /// 拖动阈值判定（v1.1 需求 2）：位移欧氏距离 &gt; threshold 才算拖动（> 而非 ≥，恰好 4px 不触发）。
    /// </summary>
    public static bool IsDragThresholdExceeded(double dx, double dy, double threshold = DragThresholdPx)
        => Math.Sqrt(dx * dx + dy * dy) > threshold;

    /// <summary>
    /// 右缘吸附计算（v1.1 需求 3）：可见卡片右缘（windowX + windowWidth − gutter）距 workRight
    /// ≤ threshold（含越出右缘的负距离）→ 返回吸附后窗口 X（卡片贴右缘），否则原样返回。
    /// 仅水平吸附，垂直由调用方保持不变。
    /// </summary>
    public static double SnapX(
        double windowX, double workRight, double windowWidth,
        double gutter = ShadowGutter, double threshold = SnapThresholdPx)
    {
        double cardRight = windowX + windowWidth - gutter;
        double distance = workRight - cardRight;   // >0: 未贴边距离；≤0: 已越出右缘
        return distance <= threshold
            ? workRight + gutter - windowWidth
            : windowX;
    }
}

/// <summary>v1.1 面板钉住/持久化决策纯函数（无 UI 依赖，可单测）。</summary>
public static class PanelBehavior
{
    /// <summary>
    /// Deactivated 是否应隐藏面板（v1.1 需求 6）：钉住时不隐藏（需求 6）；
    /// 未钉住时保持既有显示侧防抖——距显示 &lt; showDebounceMs 忽略（防"弹出即隐藏"竞态，D-04 不变）。
    /// </summary>
    public static bool ShouldHideOnDeactivate(bool pinned, double sinceShowMs, double showDebounceMs)
        => !pinned && sinceShowMs >= showDebounceMs;

    /// <summary>
    /// 位置持久化守卫：窗口从未显示过时 Left/Top 为 NaN（启动即恢复钉住态的场景），
    /// 此时仅持久化钉住状态、跳过位置写入，避免把 NaN 写进 settings.json。
    /// </summary>
    public static bool ShouldPersistPosition(double x, double y)
        => double.IsFinite(x) && double.IsFinite(y);
}
