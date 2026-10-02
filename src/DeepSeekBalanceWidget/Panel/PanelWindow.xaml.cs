using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Scheduling;
using DeepSeekBalanceWidget.Settings;

namespace DeepSeekBalanceWidget.Panel;

/// <summary>
/// A1 面板（探针 A 已验证做法正式化）：
/// - 320×230 无边框圆角窗口（外框 348×258 留 14px 阴影余量）；ShowInTaskbar=false；
/// - ShowActivated=false + Show 后延迟一拍 Activate()（Background 优先级，D-05）；
/// - WS_EX_TOOLWINDOW（经 WindowInteropHelper/SetWindowLong，不进 Alt-Tab）；
/// - 失焦隐藏（Deactivated→Hide）+ 显示侧 300ms / 隐藏侧 250ms 双向防抖（D-04）；
/// - ISnapshotSink：倒计时/进度每 tick 直刷，面板不可见时跳过（降 CPU）。
/// v1.1 增补（需求变更 2026-09-30）：
/// - 默认弹出位 = 主屏工作区右缘停靠（可见卡片贴右缘、窗口底边距工作区底 12px；含 14px gutter 计算，
///   多显示器以主屏为准的假设见 PanelPlacement）——取代原"光标附近"弹出定位；
/// - 空白处按住拖动（4px 阈值防误触；按钮/余额点击块不发起拖动），释放后右缘 ≤28px 自动吸附；
/// - 图钉开关（面板顶行圆钮 + 托盘菜单勾选，双向实时同步）：钉住时 Deactivated 不隐藏；
/// - 位置 + 钉住状态持久化（拖动/吸附结束、钉住切换时回调 persistPanelState 写 settings.json）；
/// - 钉住置顶可配置（PinTopmost，默认开）：未钉住弹出始终 Topmost；钉住且非置顶时，
///   显示瞬间临时置顶抬升 z 序、延迟 Activate 后还原普通层级（保证托盘左键能带到前台）。
/// 需求变更 2026-10-02：
/// - 面板隐藏路径新增 panelHidden 回调（App → Scheduler.NotifyPanelVisibility(false)，余额显示期
///   快轮询的显隐信号）；ShowPanel 既有 panelShown 回调语义不变；
/// - 绿条流光扫过动画（ApplyFlowSettings）：淡白高光带经 Border.Clip 裁剪在绿条圆角矩形内，
///   FlowTranslate.BeginAnimation 直驱 TranslateTransform.X（合成层动画，不占 UI 线程逐帧）；
///   开关关闭停掉并释放动画、复位位移不留残影；档位（节奏/强度）与持久化见 Settings/AppSettings +
///   SettingsWindow。
/// </summary>
public partial class PanelWindow : Window, ISnapshotSink
{
    /// <summary>显示防抖：Show 后 300ms 内的 Deactivated 一律忽略（防"弹出即隐藏"竞态）。</summary>
    private const double ShowDebounceMs = 300;

    /// <summary>隐藏防抖：Deactivated 先隐藏后，紧随的托盘点击判为同一次点击忽略（防"点了关不掉"）。</summary>
    private const double HideDebounceMs = 250;

    private DateTimeOffset _shownAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastHiddenAtUtc = DateTimeOffset.MinValue;

    public PanelViewModel ViewModel { get; }

    private readonly Action _openSettings;
    private readonly Action _manualRefreshRequested;
    private readonly Action<double, double, bool> _persistPanelState;
    private readonly Action? _panelShown;
    private readonly Action? _panelHidden;   // 需求变更 2026-10-02：面板隐藏信号（余额快轮询显隐切换）

    // ---------------- v1.1：钉住 / 位置记忆 / 拖动状态 ----------------

    private bool _pinned;
    private bool _pinTopmost = true;
    private bool _hasRememberedPosition;
    private double _rememberedX, _rememberedY;

    private bool _dragThresholdArmed;
    private Point _dragStartPoint;
    private bool _restoreTopmostAfterActivate;

    /// <summary>钉住状态变化（面板图钉钮 / 托盘菜单 / 启动恢复）后广播；App 用于同步托盘菜单勾选。</summary>
    public event Action<bool>? PinnedChanged;

    public bool IsPinned => _pinned;

    public PanelWindow(PanelViewModel viewModel, Action openSettings, Action manualRefreshRequested,
        Action<double, double, bool> persistPanelState, Action? panelShown = null, Action? panelHidden = null)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        _openSettings = openSettings;
        _manualRefreshRequested = manualRefreshRequested;
        _persistPanelState = persistPanelState;
        _panelShown = panelShown;
        _panelHidden = panelHidden;
    }

    /// <summary>注入 settings.json 中的位置记忆（null/非法值 = 无记忆 → 走默认右缘停靠）。</summary>
    public void SetRememberedPosition(double? x, double? y)
    {
        if (x is double xv && y is double yv && double.IsFinite(xv) && double.IsFinite(yv))
        {
            _rememberedX = xv;
            _rememberedY = yv;
            _hasRememberedPosition = true;
            AppLog.Info($"面板位置记忆已载入 ({xv:F0},{yv:F0})");
        }
    }

    // ---------------- WS_EX_TOOLWINDOW：排除 Alt-Tab（D-05，探针 A exStyle=0x80088 实证） ----------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        AppLog.Info($"WS_EX_TOOLWINDOW 已设置 (exStyle=0x{GetWindowLong(hwnd, GWL_EXSTYLE):X})");
    }

    // ---------------- 失焦自动隐藏 + 显示侧防抖（D-04；v1.1：钉住时不隐藏） ----------------

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        var sinceShow = (DateTimeOffset.UtcNow - _shownAtUtc).TotalMilliseconds;
        // 决策抽为纯函数 PanelBehavior.ShouldHideOnDeactivate（可单测）：
        // 钉住 → 保持显示（v1.1 需求 6）；未钉住 → 显示侧防抖逻辑原样（D-04 不动）
        if (!PanelBehavior.ShouldHideOnDeactivate(_pinned, sinceShow, ShowDebounceMs))
        {
            if (_pinned)
                AppLog.Info("Deactivated 触发；面板已钉住 → 保持显示（v1.1）");
            else
                AppLog.Info($"Deactivated 触发；距显示 {sinceShow:F0}ms < {ShowDebounceMs:0}ms → 防抖期内忽略（防弹出即隐藏）");
            return;
        }
        HidePanel("deactivated");
    }

    // ---------------- 显示 / 隐藏（含隐藏侧防抖） ----------------

    /// <summary>托盘左键切换（探针 A TogglePanelViaTray 路径；钉住状态不改变切换语义——v1.1 需求 6）。</summary>
    public void TogglePanelViaTray()
    {
        if (Visibility == Visibility.Visible)
        {
            AppLog.Info("托盘左键：面板当前可见 → 隐藏");
            HidePanel("tray-toggle");
        }
        else
        {
            var sinceHide = DateTimeOffset.UtcNow - _lastHiddenAtUtc;
            if (sinceHide.TotalMilliseconds < HideDebounceMs)
            {
                // Deactivated 先把面板隐藏了，同一次托盘点击的 TrayLeftMouseUp 后到——
                // 若此处再 Show，用户视角就是"点了托盘面板却没关掉"。视为同一次点击，忽略。
                AppLog.Info($"托盘左键：距 Deactivated 隐藏仅 {sinceHide.TotalMilliseconds:F0}ms（< {HideDebounceMs:0}ms），判定为同一次点击，忽略");
                return;
            }
            AppLog.Info("托盘左键：面板当前隐藏 → 显示");
            ShowPanel();
        }
    }

    public void ShowPanel()
    {
        _shownAtUtc = DateTimeOffset.UtcNow;      // 防抖时间戳必须先于 Show 设置（探针 A §2.1）
        PositionForShow();
        // v1.1 需求 7：未钉住弹出始终 Topmost（现状）；钉住时按 PinTopmost 配置
        Topmost = !_pinned || _pinTopmost;
        // 钉住且非置顶：ShowActivated=false 直接 Show 抬不到前台——先临时置顶抬 z 序，
        // 延迟 Activate 后还原普通层级（保证"托盘左键重现面板能带到前台"）
        _restoreTopmostAfterActivate = _pinned && !_pinTopmost;
        Show();
        _panelShown?.Invoke();                    // §4.3 打开面板即刷（Scheduler 内部 30s 去抖）
        // 延迟一拍激活：让面板可被 Deactivated 监视，同时避开显示瞬间的焦点竞态（D-05）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Activate();
                if (_restoreTopmostAfterActivate)
                {
                    Topmost = _pinTopmost;        // 钉住非置顶：抬前台后还原（=ApplyTopmost 同式）
                    _restoreTopmostAfterActivate = false;
                    AppLog.Info($"钉住非置顶：已临时置顶抬升前台并还原 Topmost={Topmost}");
                }
            }
            catch (Exception ex) { AppLog.Warn($"面板延迟激活失败：{ex.Message}"); }
        }), DispatcherPriority.Background);
        AppLog.Info($"面板显示 位置=({Left:F0},{Top:F0}) 尺寸=320×230 钉住={_pinned} Topmost={Topmost}");
    }

    public void HidePanel(string reason)
    {
        if (Visibility != Visibility.Visible) return;
        Hide();
        _lastHiddenAtUtc = DateTimeOffset.UtcNow;
        _panelHidden?.Invoke();                   // 需求变更 2026-10-02：隐藏信号（App → Scheduler 快轮询切换）
        AppLog.Info($"面板隐藏 reason={reason}");
    }

    /// <summary>每 tick 直刷出口：面板不可见时跳过（降 CPU，探针 E §4.3 建议）。</summary>
    public void OnTickSnapshot(WidgetSnapshot snapshot)
    {
        if (!IsVisible || !IsLoaded) return;
        ViewModel.UpdateSnapshot(snapshot);
    }

    /// <summary>常驻托盘应用：面板关闭一律转为隐藏（窗口对象复用，托盘可再次弹出）。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!e.Cancel && Visibility == Visibility.Visible)
        {
            e.Cancel = true;
            HidePanel("closing");
        }
        base.OnClosing(e);
    }

    // ---------------- 显示定位（v1.1：记忆位优先，否则主屏右缘停靠默认位） ----------------

    /// <summary>
    /// 每次显示前定位：有位置记忆 → 记忆位（多显示器场景不做越界校正，以主屏为准的假设不变）；
    /// 无记忆 → 主屏工作区右缘停靠（可见卡片贴右缘 + 窗口底边距工作区底 12px，gutter 计算见 PanelPlacement）。
    /// </summary>
    private void PositionForShow()
    {
        // 首次显示前 ActualWidth/ActualHeight 为 0，必须用显式 Width/Height（348×258，含阴影余量）
        double w = Width, h = Height;
        if (_hasRememberedPosition)
        {
            Left = _rememberedX;
            Top = _rememberedY;
            return;
        }
        // 主显示器工作区（SystemParameters.WorkArea 即主屏；多显示器假设以主屏为准，v1.1 需求 1）
        var work = SystemParameters.WorkArea;
        (Left, Top) = PanelPlacement.ComputeDefaultDockedPosition(work.Right, work.Bottom, w, h);
    }

    // ---------------- v1.1：拖动 + 右缘吸附 ----------------

    private void PanelRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 可交互元素（齿轮/刷新/图钉三钮、余额点击块=「未配置 API Key」提示）不发起拖动，点击照常
        if (!IsDragAllowedSource(e.OriginalSource as DependencyObject)) return;
        _dragStartPoint = e.GetPosition(this);
        _dragThresholdArmed = true;
        PanelRoot.CaptureMouse();                 // 保证阈值判定期间 MouseMove 不丢
    }

    private void PanelRoot_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragThresholdArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        // 防误触设计（v1.1 需求 2）：按下后位移 >4px 才进入拖动——单击/双击无位移不触发 DragMove，
        // 超过阈值视为明确拖动意图（阈值常量与判定见 PanelPlacement.IsDragThresholdExceeded）
        if (!PanelPlacement.IsDragThresholdExceeded(p.X - _dragStartPoint.X, p.Y - _dragStartPoint.Y)) return;
        _dragThresholdArmed = false;
        PanelRoot.ReleaseMouseCapture();
        CompensatePreDragCursorDelta();            // 补上阈值判定期间光标已移动的位移（见方法注释）
        try
        {
            DragMove();                            // 系统级拖动，阻塞至松开鼠标（消息泵继续）
            OnDragFinished();
        }
        catch (InvalidOperationException ex)
        {
            AppLog.Warn($"拖动未生效（DragMove 前置条件不满足）：{ex.Message}");
        }
    }

    /// <summary>
    /// 阈值判定期间（按下 → 超过 4px → 进入 DragMove）光标已移动了一段位移，而 DragMove 从
    /// 窗口原位开始跟随光标——若不补偿，这段位移会丢失（冒烟 c 步实测：窗口落后光标 10–30px）。
    /// 进入 DragMove 前先把窗口平移到"抓取点对准光标当前位置"，保证拖动全程窗口贴合光标，
    /// 同时保留 >4px 才拖的防误触语义。
    /// </summary>
    private void CompensatePreDragCursorDelta()
    {
        if (!GetCursorPos(out var cursor)) return;
        var startScreen = PointToScreen(_dragStartPoint);          // 物理像素
        double pxPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Left += (cursor.X - startScreen.X) / pxPerDip;
        Top += (cursor.Y - startScreen.Y) / pxPerDip;
    }

    private void PanelRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 未超过阈值的按放 = 普通点击：解除捕获，不留拖动状态
        if (_dragThresholdArmed)
        {
            _dragThresholdArmed = false;
            PanelRoot.ReleaseMouseCapture();
        }
    }

    /// <summary>拖动释放：右缘吸附（≤28px 水平吸贴、垂直保持）→ 持久化位置 + 钉住状态。</summary>
    private void OnDragFinished()
    {
        var work = SystemParameters.WorkArea;      // 主屏工作区（多显示器假设以主屏为准）
        double snappedX = PanelPlacement.SnapX(Left, work.Right, Width);
        if (snappedX != Left)
        {
            AppLog.Info($"右缘吸附：卡片右缘距工作区右缘 ≤{PanelPlacement.SnapThresholdPx:0}px → ({Left:F0} → {snappedX:F0})");
            Left = snappedX;
        }
        PersistPanelState("拖动结束");
    }

    // ---------------- v1.1：钉住开关 + 置顶配置 ----------------

    /// <summary>
    /// 钉住切换统一入口（面板图钉钮 / 托盘菜单 / 启动恢复）。Topmost 即时重算、
    /// ViewModel 同步（图钉钮视觉）、广播 PinnedChanged（App 同步托盘菜单）、持久化。
    /// </summary>
    public void SetPinned(bool pinned, string source)
    {
        bool changed = _pinned != pinned;
        _pinned = pinned;
        ViewModel.SetPinned(pinned);
        ApplyTopmost();
        if (changed)
        {
            AppLog.Info($"面板钉住状态={pinned}（source={source}）Topmost={Topmost}");
            PinnedChanged?.Invoke(pinned);
            PersistPanelState("钉住切换");         // 未显示过时仅持久化钉住态（NaN 守卫，见 PanelBehavior）
        }
        else
        {
            AppLog.Info($"面板钉住状态无变化（={pinned}，source={source}）");
        }
    }

    /// <summary>钉住时置顶配置（设置窗口切换；Topmost 即时重算）。</summary>
    public void SetPinTopmost(bool pinTopmost, string source)
    {
        if (_pinTopmost == pinTopmost) return;
        _pinTopmost = pinTopmost;
        ApplyTopmost();
        AppLog.Info($"钉住时置顶={_pinTopmost}（source={source}）Topmost={Topmost}");
    }

    private void ApplyTopmost() => Topmost = !_pinned || _pinTopmost;

    private void PersistPanelState(string reason)
    {
        // NaN 守卫：窗口从未显示时 Left/Top 为 NaN，仅钉住切换场景跳过位置写入（状态已在文件中）
        if (!PanelBehavior.ShouldPersistPosition(Left, Top))
        {
            AppLog.Warn($"面板位置持久化跳过（窗口未显示过，位置无效）reason={reason} 钉住={_pinned}");
            return;
        }
        _persistPanelState(Left, Top, _pinned);
        // 同步内存中的位置记忆：settings.json 落盘的同时更新 _rememberedX/Y，
        // 否则本次会话内后续 ShowPanel 仍走旧记忆/默认停靠（冒烟 b 步实测教训）
        _rememberedX = Left;
        _rememberedY = Top;
        _hasRememberedPosition = true;
        AppLog.Info($"面板状态已持久化 reason={reason} 位置=({Left:F0},{Top:F0}) 钉住={_pinned}");
    }

    // ---------------- 流光效果（需求变更 2026-10-02：绿条流光扫过动画） ----------------

    /// <summary>高光带宽度（DIP）：一道淡白高光带，窄于进度条轨道。</summary>
    private const double FlowBandWidth = 56;

    /// <summary>
    /// 扫过行程终点 = 面板内容宽（面板本体 320 − 左右内边距 2×16）；行程起点 = −FlowBandWidth
    /// （带体完全在裁剪外）。绿条窄于行程时高光在裁剪外自然不可见（需求 B-1，无需特殊处理）。
    /// </summary>
    private const double FlowTrackWidth = 288;

    /// <summary>强度档位：极淡（默认，叠加 Brush.Acc 仅隐约可见）/ 可见（一眼可辨但不刺眼）。</summary>
    private const double FlowOpacityFaint = 0.22;
    private const double FlowOpacityVisible = 0.45;

    /// <summary>
    /// 应用流光设置（App 启动装配 + 设置窗口即时切换回调）：先停掉并释放旧动画，开关关闭即止
    /// （绿条纯色静止、不留残影，需求 B-5/B-6）；开启时按「节奏 × 强度」档位在 FlowTranslate 上
    /// 直接 BeginAnimation 驱动（合成层动画，不占 UI 线程逐帧计算，需求 B-5）。
    /// 【修复 2026-10-02 第二关退回】原 Storyboard.SetTarget(anim, FlowTranslate) +
    /// Begin(FlowHighlight, isControllable:true) 组合静默不生效：Storyboard 的目标解析按
    /// TargetName（containingObject 的 NameScope）通道设计，TranslateTransform 是 Freezable
    /// 而非 FrameworkElement/FrameworkContentElement，直引 SetTarget 不在该解析路径内——
    /// 时钟创建成功但属性挂接从未发生，带体恒停在裁剪外 X=-56（与验收实拍证据一致）。
    /// 改为对 Freezable 实例直接 BeginAnimation(TranslateTransform.XProperty, anim)：
    /// 动画时钟经 ApplyAnimationClock 直接注册到该实例的依赖属性动画缓存，无任何 namescope /
    /// 目标解析中间环节，必然驱动（XAML 内联声明的 Transform 带 InheritanceContext 未冻结，
    /// 是 BeginAnimation 的规范用法；SnapshotAndReplace 语义支持换档替换与 null 摘除）。
    /// </summary>
    public void ApplyFlowSettings(bool enabled, FlowSpeed speed, FlowIntensity intensity)
    {
        StopFlowAnimation();
        if (!enabled)
        {
            AppLog.Info("流光效果：已关闭（绿条纯色静止）");
            return;
        }
        FlowHighlight.Opacity = intensity == FlowIntensity.Visible ? FlowOpacityVisible : FlowOpacityFaint;
        FlowHighlight.Visibility = Visibility.Visible;
        if (speed == FlowSpeed.Visible)
        {
            // 明显：约 2s 一次、无停顿、无缝循环（带体出右缘即回左缘，两态均不可见 → 衔接无静止保持期）
            var sweep = new DoubleAnimation(-FlowBandWidth, FlowTrackWidth, TimeSpan.FromSeconds(2))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            FlowTranslate.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
        else
        {
            // 克制（默认）：一次扫过约 3.5s + 停顿约 1.5s（终点保持期带体在裁剪外），循环约 5s
            var sweep = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            sweep.KeyFrames.Add(new LinearDoubleKeyFrame(-FlowBandWidth, TimeSpan.Zero));
            sweep.KeyFrames.Add(new LinearDoubleKeyFrame(FlowTrackWidth, TimeSpan.FromSeconds(3.5)));
            sweep.KeyFrames.Add(new LinearDoubleKeyFrame(FlowTrackWidth, TimeSpan.FromSeconds(5)));
            FlowTranslate.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
        AppLog.Info($"流光效果：已开启 节奏={speed} 强度={intensity}（扫过行程 {FlowTrackWidth:0}px + 带宽 {FlowBandWidth:0}px）");
    }

    /// <summary>
    /// 停掉并释放流光动画：BeginAnimation(null) 摘除动画时钟（与驱动方式配套——SnapshotAndReplace
    /// 下摘除后属性即回 XAML 基值 X=-56，此处再显式复位双保险），隐藏带体，不留残影（需求 B-5）。
    /// </summary>
    private void StopFlowAnimation()
    {
        FlowTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        FlowTranslate.X = -FlowBandWidth;
        FlowHighlight.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 绿 Border 尺寸变化（星宽列每 tick 随剩余占比变化）→ 更新圆角矩形裁剪：
    /// 高光带只在绿条圆角矩形内可见，不溢出灰条、圆角外或文字（需求 B-1）。
    /// </summary>
    private void ProgressFill_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ProgressFill.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radiusX: 2, radiusY: 2);
    }

    // ---------------- 面板内部交互 ----------------

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => _manualRefreshRequested();

    private void PinButton_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned, "panel-pin-button");

    private void BalanceBlock_Click(object sender, MouseButtonEventArgs e)
    {
        // A1 约定 1：未配置 / Key 无效 → 整块可点击打开设置
        if (ViewModel.IsBalanceClickable) _openSettings();
    }

    /// <summary>
    /// 拖动发起范围判定：从命中元素向上走到 PanelRoot，途中遇到按钮（齿轮/刷新/图钉）
    /// 或余额点击块（未配置 Key 提示，点击开设置）则不发起拖动，保证这些元素点击照常。
    /// </summary>
    private bool IsDragAllowedSource(DependencyObject? source)
    {
        DependencyObject? d = source;
        while (d != null && !ReferenceEquals(d, PanelRoot))
        {
            if (d is System.Windows.Controls.Button) return false;
            if (ReferenceEquals(d, BalanceBlock)) return false;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return true;
    }

    // ---------- Win32 ----------

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
