using System.IO;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Scheduling;
using DeepSeekBalanceWidget.Tray;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>真实 TrayController 落盘的 4 枚 ICO 变体（浅/深 × 峰/谷）中心像素 = A1 §3 精确色值。</summary>
public sealed class TrayIconVariantTests
{
    // TaskbarIcon/DynamicIconRenderer 必须 STA —— 与其余 UI 测试共用串行 UI 线程
    private static readonly UiThreadFixture Ui = new();

    [Fact]
    public void Four_Variants_Match_A1_Palette_And_Flip_Rebuilds()
    {
        var iconDir = Path.Combine(Path.GetTempPath(), "dsw-test-icons-" + Guid.NewGuid().ToString("N"));
        try
        {
            Ui.Invoke(() =>
            {
                var tray = new TrayController(iconDir, isDarkTheme: () => false);
                try
                {
                    tray.Initialize(PeakState.Peak);
                    Assert.Equal(PeakState.Peak, tray.CurrentState);

                    // A1 §3 语义色：高峰浅 #E4720B、高峰深 #FF9C4A、空闲浅 #17994E、空闲深 #46D27F
                    Assert.Equal(0xE4720B, Rgb(Path.Combine(iconDir, "peak-light.ico")));
                    Assert.Equal(0xFF9C4A, Rgb(Path.Combine(iconDir, "peak-dark.ico")));
                    Assert.Equal(0x17994E, Rgb(Path.Combine(iconDir, "idle-light.ico")));
                    Assert.Equal(0x46D27F, Rgb(Path.Combine(iconDir, "idle-dark.ico")));

                    // 翻转沿路径（真实 NIM_DELETE+NIM_ADD）：同态短路，异态翻转
                    tray.SetState(PeakState.Peak);            // 同态 → 短路
                    Assert.Equal(PeakState.Peak, tray.CurrentState);
                    tray.SetState(PeakState.Idle);            // 峰→谷
                    Assert.Equal(PeakState.Idle, tray.CurrentState);
                    tray.SetState(PeakState.Idle);            // 同态 → 短路（A-2 去重兜底）
                    Assert.Equal(PeakState.Idle, tray.CurrentState);
                }
                finally
                {
                    tray.Dispose();
                }
            });
            // D-02：Dispose 后图标目录随用随清
            Assert.False(Directory.Exists(iconDir));
        }
        finally
        {
            try { if (Directory.Exists(iconDir)) Directory.Delete(iconDir, recursive: true); } catch { }
        }
    }

    private static int Rgb(string path)
    {
        var (r, g, b) = DynamicIconRenderer.DecodeIcoCenterPixel(path);
        return (r << 16) | (g << 8) | b;
    }
}
