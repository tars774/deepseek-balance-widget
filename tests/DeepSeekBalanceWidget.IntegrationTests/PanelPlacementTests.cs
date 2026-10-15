using DeepSeekBalanceWidget.Panel;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// v1.1 面板定位/拖动/吸附纯函数（PanelPlacement）+ 钉住/持久化决策纯函数（PanelBehavior）。
/// 坐标均为 DIP；典型 1920×1080 主屏任务栏 40px 时工作区 ≈ 1920×1040。
/// </summary>
public sealed class PanelPlacementTests
{
    private const double W = 348, H = 258;          // 窗口外框（含 14px 阴影余量）
    private const double Gutter = PanelPlacement.ShadowGutter;

    [Fact]
    public void Default_Docked_Position_Card_Flush_To_Right_Edge_And_Above_Taskbar()
    {
        // 主屏工作区 1920×1040（任务栏 40px）
        var (x, y) = PanelPlacement.ComputeDefaultDockedPosition(1920, 1040, W, H);

        // 窗口右缘超出屏幕 14px（gutter）→ 可见卡片右缘 = x + 348 - 14 = 1920（贴工作区右缘）
        Assert.Equal(1920.0 + Gutter - W, x);
        Assert.Equal(1920, x + W - Gutter);

        // 窗口底边距工作区底 12px（任务栏上方）
        Assert.Equal(1040.0 - PanelPlacement.DockBottomGap - H, y);
        Assert.Equal(1028, y + H);
    }

    [Fact]
    public void Default_Docked_Position_Honors_Custom_Gutter_And_Bottom_Gap()
    {
        var (x, y) = PanelPlacement.ComputeDefaultDockedPosition(1000, 800, 200, 100, gutter: 10, bottomGap: 5);
        Assert.Equal(1000 + 10 - 200, x);
        Assert.Equal(800 - 5 - 100, y);
    }

    [Theory]
    [InlineData(0, 0, false)]      // 无位移（单击）
    [InlineData(4, 0, false)]      // 恰好 4px：阈值是 > 而非 ≥，不触发
    [InlineData(0, 4, false)]
    [InlineData(2, 2, false)]      // √8 ≈ 2.83
    [InlineData(3, 3, true)]       // √18 ≈ 4.24 > 4
    [InlineData(4.1, 0, true)]
    [InlineData(0, 5, true)]
    [InlineData(-5, 0, true)]      // 方向无关
    [InlineData(0, -5, true)]
    public void Drag_Threshold_Exceeded_Only_Beyond_4px(double dx, double dy, bool expected)
    {
        Assert.Equal(expected, PanelPlacement.IsDragThresholdExceeded(dx, dy));
    }

    [Theory]
    [InlineData(1586, true)]       // 卡片右缘 1920：已贴边，保持吸附位
    [InlineData(1566, true)]       // 卡片右缘 1900：距 20px ≤ 28 → 吸附到 1586
    [InlineData(1558, true)]       // 卡片右缘 1892：距恰好 28px（含边界）→ 吸附
    [InlineData(1557.9, false)]    // 距 28.1px > 28 → 不吸
    [InlineData(1500, false)]      // 距 86px → 不吸
    [InlineData(1600, true)]       // 卡片右缘 1934 已越出右缘（负距离）→ 拉回贴边
    public void Snap_X_Only_Within_28px_Of_Work_Area_Right_Edge(double windowX, bool shouldSnap)
    {
        const double workRight = 1920;
        var snapped = PanelPlacement.SnapX(windowX, workRight, W);

        if (shouldSnap)
        {
            Assert.Equal(workRight + Gutter - W, snapped);     // 吸附位 = 默认停靠 X
            Assert.Equal(workRight, snapped + W - Gutter);     // 吸附后卡片右缘贴边
        }
        else
        {
            Assert.Equal(windowX, snapped);                    // 原样返回（垂直不受影响）
        }
    }

    // ---------------- PanelBehavior（钉住/防抖/持久化守卫决策） ----------------

    [Theory]
    [InlineData(true, 50, false)]      // 钉住：任何时刻失焦都不隐藏（v1.1 需求 6）
    [InlineData(true, 400, false)]
    [InlineData(true, 100000, false)]
    [InlineData(false, 50, false)]     // 未钉住 + 显示防抖期内（<300ms）→ 忽略（D-04 不变）
    [InlineData(false, 299.9, false)]
    [InlineData(false, 300, true)]     // 防抖期满 → 隐藏
    [InlineData(false, 5000, true)]
    public void Deactivate_Hides_Only_When_Unpinned_And_Past_Show_Debounce(
        bool pinned, double sinceShowMs, bool expectedHide)
    {
        Assert.Equal(expectedHide, PanelBehavior.ShouldHideOnDeactivate(pinned, sinceShowMs, showDebounceMs: 300));
    }

    [Fact]
    public void Persist_Position_Guard_Rejects_NaN_From_Never_Shown_Window()
    {
        Assert.False(PanelBehavior.ShouldPersistPosition(double.NaN, double.NaN));   // 启动恢复钉住态（窗口未显示）
        Assert.True(PanelBehavior.ShouldPersistPosition(1586, 770));                 // 正常位置
    }

    // ---------------- v1.2.1 记忆位越界回退（IsCardReachable，2026-10-07 修复） ----------------
    // 边界 = 虚拟桌面（所有显示器并集），判定对象 = 可见卡片（窗口内缩 ShadowGutter=14），双轴交集均需 ≥48px。

    private const double BoundsRight = 1600, BoundsBottom = 1000;   // 单屏 1600×1000 逻辑屏的虚拟桌面

    [Fact]
    public void Real_Machine_2026_10_07_Remembered_Position_Is_Unreachable()
    {
        // 2026-10-07 用户机实机值：2560×1440 逻辑屏的右缘停靠记忆位 (2226,1146)（hwnd 物理
        // (4452,2291)-(5148,2807) @200%），换到 1600×1000 逻辑屏后整窗出屏 → 必须判不可达并回退
        Assert.False(PanelPlacement.IsCardReachable(2226, 1146, W, H, 0, 0, BoundsRight, BoundsBottom));
    }

    [Fact]
    public void Default_Docked_Position_And_Right_Edge_Snap_Position_Are_Reachable()
    {
        // 1600×952 工作区（任务栏 48px）的默认停靠位 = (1600+14−348, 952−12−258) = (1266,682)
        var (x, y) = PanelPlacement.ComputeDefaultDockedPosition(1600, 952, W, H);
        Assert.Equal(1266, x);
        Assert.Equal(682, y);
        Assert.True(PanelPlacement.IsCardReachable(x, y, W, H, 0, 0, BoundsRight, BoundsBottom));

        // 右缘吸附位与默认停靠 X 同值（卡片贴工作区右缘，窗口右缘超出 14px）→ 同样可达
        var snapped = PanelPlacement.SnapX(1500, 1600, W);
        Assert.Equal(x, snapped);
        Assert.True(PanelPlacement.IsCardReachable(snapped, y, W, H, 0, 0, BoundsRight, BoundsBottom));
    }

    [Theory]
    [InlineData(1538, 682, true)]       // X 轴：可见宽 = 1600−(1538+14) 恰好 48 → 可达（阈值含边界）
    [InlineData(1538.1, 682, false)]    // X 轴：可见宽 47.9 → 不可达
    [InlineData(1266, 938, true)]       // Y 轴：可见高 = 1000−(938+14) 恰好 48 → 可达
    [InlineData(1266, 938.1, false)]    // Y 轴：可见高 47.9 → 不可达
    public void Reachability_Threshold_Is_48px_On_Both_Axes(double windowX, double windowY, bool expected)
    {
        Assert.Equal(expected, PanelPlacement.IsCardReachable(windowX, windowY, W, H, 0, 0, BoundsRight, BoundsBottom));
    }

    [Theory]
    [InlineData(1700, 682)]     // 仅 X 出屏（卡片 x 1714..2034 全在右缘外）；Y 方向可见高 230px 充足
    [InlineData(1266, 1060)]    // 仅 Y 出屏（卡片 y 1074..1304 全在底边外）；X 方向可见宽 320px 充足
    public void Offscreen_On_Single_Axis_Is_Unreachable(double windowX, double windowY)
    {
        // 双轴均需 ≥48px：单轴充足不构成可达
        Assert.False(PanelPlacement.IsCardReachable(windowX, windowY, W, H, 0, 0, BoundsRight, BoundsBottom));
    }

    [Theory]
    [InlineData(-100, 300, true)]    // 左缘外 100px：可见宽 = 320−(100−14) = 234px → 可达
    [InlineData(300, -100, true)]    // 上缘外 100px：可见高 = 230−(100−14) = 144px → 可达
    [InlineData(-400, 300, false)]   // 卡片整块在左缘外（卡片右缘 −66 < 0）→ 不可达
    [InlineData(300, -300, false)]   // 卡片整块在上缘外（卡片底边 −56 < 0）→ 不可达
    public void Negative_Coordinates_Partially_Visible_Stay_Reachable(double windowX, double windowY, bool expected)
    {
        Assert.Equal(expected, PanelPlacement.IsCardReachable(windowX, windowY, W, H, 0, 0, BoundsRight, BoundsBottom));
    }

    [Theory]
    [InlineData(3200, true)]     // 虚拟桌面含副屏（主屏 0..1600 + 副屏 1600..3200）：合法停放 →
                                 // 有可达交集，不得回退（防"用主屏工作区判定"回归）
    [InlineData(1600, false)]    // 对照组：边界为主屏时卡片（1714..2034）全在屏外 → 不可达
    public void Secondary_Screen_Parked_Position_Uses_Virtual_Desktop_Bounds(double boundsRight, bool expected)
    {
        Assert.Equal(expected, PanelPlacement.IsCardReachable(1700, 682, W, H, 0, 0, boundsRight, BoundsBottom));
    }

    [Fact]
    public void Remembered_Position_Inside_Screen_Is_Reachable()
    {
        // 有记忆且屏内（如鼠拖到屏幕中部）→ 使用记忆位（PositionForShow 的记忆分支语义）
        Assert.True(PanelPlacement.IsCardReachable(600, 400, W, H, 0, 0, BoundsRight, BoundsBottom));
    }
}
