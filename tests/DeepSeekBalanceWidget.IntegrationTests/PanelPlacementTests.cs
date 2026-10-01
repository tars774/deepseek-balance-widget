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
}
