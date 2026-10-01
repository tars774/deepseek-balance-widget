using System.IO;
using System.Windows.Controls;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Tray;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// v1.1 托盘「钉住面板」勾选项（IsCheckable）与面板钉住状态双向同步（真实 TrayController/ContextMenu）：
/// 菜单存在且可勾选 → 程序同步（面板图钉钮切换）不改勾选态事件 → 用户勾/取消勾发出带新状态的请求。
/// </summary>
public sealed class TrayPinMenuTests : IDisposable
{
    private static readonly UiThreadFixture Ui = new();

    private readonly string _iconDir = Path.Combine(Path.GetTempPath(), "dsw-test-icons-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_iconDir)) Directory.Delete(_iconDir, recursive: true); } catch { }
    }

    private static MenuItem? FindPinItem(TrayController tray)
    {
        var menu = tray.TrayContextMenu;            // TrayController v1.1 暴露的当前右键菜单
        if (menu == null) return null;
        foreach (var item in menu.Items)
        {
            if (item is MenuItem mi && (mi.Header as string) == "钉住面板") return mi;
        }
        return null;
    }

    [Fact]
    public void Pin_Menu_Item_Exists_IsCheckable_And_Syncs_With_Panel_State()
    {
        Ui.Invoke(() =>
        {
            var tray = new TrayController(_iconDir, isDarkTheme: () => false);
            try
            {
                tray.Initialize(PeakState.Idle);

                // 菜单含「钉住面板」勾选项，初始未勾选（未钉）
                var pin = FindPinItem(tray);
                Assert.NotNull(pin);
                Assert.True(pin!.IsCheckable);
                Assert.False(pin.IsChecked);

                // 面板图钉钮切换 → SetPanelPinned 同步勾选态（不产生反向请求）
                bool? requested = null;
                tray.PanelPinToggleRequested += p => requested = p;
                tray.SetPanelPinned(true);
                Assert.True(pin.IsChecked);
                Assert.Null(requested);                      // 程序同步不回环
                tray.SetPanelPinned(false);
                Assert.False(pin.IsChecked);

                // 状态翻转沿重建菜单（NIM 重建）后勾选态镜像保持
                tray.SetPanelPinned(true);
                tray.SetState(PeakState.Peak);               // → RebuildTrayIcon → BuildMenu
                var pinAfterRebuild = FindPinItem(tray);
                Assert.NotNull(pinAfterRebuild);
                Assert.True(pinAfterRebuild!.IsChecked);
            }
            finally
            {
                tray.Dispose();
            }
        });
    }

    [Fact]
    public void User_Click_On_Pin_Item_Raises_Toggle_Request_With_New_Checked_State()
    {
        Ui.Invoke(() =>
        {
            var tray = new TrayController(_iconDir, isDarkTheme: () => false);
            try
            {
                tray.Initialize(PeakState.Idle);
                var requests = new List<bool>();
                tray.PanelPinToggleRequested += p => requests.Add(p);
                var pin = FindPinItem(tray)!;

                // 用户勾选：IsChecked 先翻转再触发 Click（WPF IsCheckable 语义）
                pin.IsChecked = true;
                pin.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(new[] { true }, requests);

                // 用户取消勾选
                pin.IsChecked = false;
                pin.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Equal(new[] { true, false }, requests);
            }
            finally
            {
                tray.Dispose();
            }
        });
    }
}
