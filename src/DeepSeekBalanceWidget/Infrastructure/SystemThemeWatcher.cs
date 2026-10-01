using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeepSeekBalanceWidget.Infrastructure;

/// <summary>
/// 系统主题监听（D-17「跟随系统」）：读注册表 AppsUseLightTheme + WM_SETTINGCHANGE 广播，
/// 另以 30s 低频轮询兜底（设计允许的辅助手段）。
/// </summary>
public sealed class SystemThemeWatcher : IDisposable
{
    private const int WM_SETTINGCHANGE = 0x001A;
    private readonly DispatcherTimer _pollTimer;
    private HwndSource? _source;
    private bool _lastAppsUseLightTheme;

    /// <summary>系统应用主题变化（参数 = 当前是否浅色）。UI 线程触发。</summary>
    public event Action<bool>? AppsUseLightThemeChanged;

    public SystemThemeWatcher(Dispatcher dispatcher)
    {
        _lastAppsUseLightTheme = GetAppsUseLightTheme();
        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            (_, _) => Poll(), dispatcher);
    }

    /// <summary>读注册表 AppsUseLightTheme（1=浅色；键缺失/异常视为浅色）。</summary>
    public static bool GetAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 AppsUseLightTheme 失败（视为浅色）：{ex.Message}");
        }
        return true;
    }

    /// <summary>挂接窗口句柄接收 WM_SETTINGCHANGE（任一常驻窗口创建后调用；隐藏窗口同样收到广播）。</summary>
    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero) helper.EnsureHandle();   // 面板未显示时也先建 HWND，保证收到广播
        var source = HwndSource.FromHwnd(helper.Handle);
        if (source is null)
        {
            AppLog.Warn("系统主题监听挂接失败（HwndSource 为空）→ 仅轮询兜底生效");
            return;
        }
        _source = source;
        source.AddHook(WndProc);
        _pollTimer.Start();
        AppLog.Info($"系统主题监听就绪：AppsUseLightTheme={_lastAppsUseLightTheme}（WM_SETTINGCHANGE + 30s 轮询兜底）");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE)
        {
            var section = Marshal.PtrToStringAuto(lParam);
            if (section == "ImmersiveColorSet") Poll();
        }
        return IntPtr.Zero;
    }

    private void Poll()
    {
        var light = GetAppsUseLightTheme();
        if (light == _lastAppsUseLightTheme) return;
        _lastAppsUseLightTheme = light;
        AppLog.Info($"系统主题变更 → AppsUseLightTheme={light}");
        AppsUseLightThemeChanged?.Invoke(light);
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
