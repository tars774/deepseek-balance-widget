using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Peak;
using H.NotifyIcon;

namespace DeepSeekBalanceWidget.Tray;

/// <summary>
/// 托盘控制器（探针 A 已验证做法正式化）：
/// - 图标：运行时渲染 → ICO 落盘（应用专属目录 %APPDATA%\DeepSeekBalanceWidget\icons，D-02，退出随用随清）；
///   浅/深主题 × 峰/谷共 4 枚按 A1 §3 语义色生成（橙 #E4720B/#FF9C4A、绿 #17994E/#46D27F，环取 --acc-deep）；
/// - 状态切换 = NIM_DELETE + NIM_ADD 整体重建 TaskbarIcon（A-1：tooltip 原地更新有 Shell 层名称拼接问题）；
/// - 左键单击切换面板；右键菜单「打开面板 / 钉住面板(勾选, v1.1) / 设置 / 退出」；tooltip「DeepSeek 余额小组件 · 高峰/空闲时段」；
/// - 订阅去重（A-2 外层）：App 仅订阅一次 StateChanged，SetState 同态短路。
/// 边界说明（A-5 条件项）：托盘图标处于溢出折叠区时，左键单击实际表现为"总是显示面板、
/// 隐藏交给失焦"（弹层打开动作本身使面板失焦）；完整切换体验需用户将图标固定到任务栏可见区。
/// 正式说明见 README（阶段 6）。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly string _iconDir;
    private readonly Func<bool> _isDarkTheme;
    private TaskbarIcon? _tray;
    private PeakState _state = PeakState.Idle;
    private bool _disposed;

    // A1 §3 语义色（--acc 填充 / --acc-deep 环；深色主题用提亮变体）
    private static readonly (Color Fill, Color Ring) PeakLight = (Rgb(0xE4, 0x72, 0x0B), Rgb(0xC2, 0x5E, 0x06));
    private static readonly (Color Fill, Color Ring) PeakDark = (Rgb(0xFF, 0x9C, 0x4A), Rgb(0xE4, 0x72, 0x0B));
    private static readonly (Color Fill, Color Ring) IdleLight = (Rgb(0x17, 0x99, 0x4E), Rgb(0x0F, 0x7A, 0x3C));
    private static readonly (Color Fill, Color Ring) IdleDark = (Rgb(0x46, 0xD2, 0x7F), Rgb(0x17, 0x99, 0x4E));

    public event Action? PanelToggleRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? PanelPinToggleRequested;   // v1.1：托盘菜单勾选/取消「钉住面板」，参数=新勾选态
    public event Action? ExitRequested;

    public PeakState CurrentState => _state;

    // v1.1：面板钉住状态镜像（重建菜单时恢复勾选态）+ 程序同步防回环标志
    private bool _panelPinned;
    private MenuItem? _pinMenuItem;
    private bool _syncingPinMenu;

    public TrayController(string iconDir, Func<bool> isDarkTheme)
    {
        _iconDir = iconDir;
        _isDarkTheme = isDarkTheme;
        Directory.CreateDirectory(_iconDir);
        EnsureIconFiles();
    }

    /// <summary>启动即创建托盘（初始状态由首帧快照预置，避免首 tick 无事件路径下的颜色不一致）。</summary>
    public void Initialize(PeakState initialState)
    {
        _state = initialState;
        _tray = BuildTrayIcon();
        AppLog.Info($"托盘就绪 IsCreated={_tray.IsCreated} tooltip={_tray.ToolTipText} icon={IconPathFor(_state, _isDarkTheme())}");
    }

    /// <summary>翻转沿调用：状态切换 = NIM_DELETE + NIM_ADD 重建（A-1）。同态重复调用短路（A-2 去重）。</summary>
    public void SetState(PeakState state)
    {
        if (_disposed || state == _state) return;
        var old = _state;
        _state = state;
        RebuildTrayIcon($"状态切换 {old}→{state}");
    }

    /// <summary>深浅主题切换后重建（取浅/深主题图标色变体；色板均为 A1 §3 两套值，不引入新色）。</summary>
    public void RefreshTheme() => RebuildTrayIcon("主题切换");

    /// <summary>
    /// v1.1：当前托盘右键菜单（供集成测试断言「钉住面板」勾选态同步；NIM 重建后返回新菜单实例）。
    /// </summary>
    public ContextMenu? TrayContextMenu => _tray?.ContextMenu;

    /// <summary>
    /// v1.1：面板钉住状态 → 托盘菜单勾选实时同步（面板图钉钮切换时由 App 调用）。
    /// 程序同步置勾选态期间以 _syncingPinMenu 抑制 Click 回环（否则会二次通知反向翻转）。
    /// </summary>
    public void SetPanelPinned(bool pinned)
    {
        if (_panelPinned == pinned && (_pinMenuItem == null || _pinMenuItem.IsChecked == pinned)) return;
        _panelPinned = pinned;
        if (_pinMenuItem != null && _pinMenuItem.IsChecked != pinned)
        {
            _syncingPinMenu = true;
            try { _pinMenuItem.IsChecked = pinned; }
            finally { _syncingPinMenu = false; }
        }
        AppLog.Info($"托盘菜单「钉住面板」勾选同步为 {pinned}");
    }

    private TaskbarIcon BuildTrayIcon()
    {
        var tray = new TaskbarIcon
        {
            ToolTipText = ToolTipFor(_state),
            IconSource = DynamicIconRenderer.LoadIco(IconPathFor(_state, _isDarkTheme())),
            NoLeftClickDelay = true,          // 左键单击立即触发，不等双击判定
            Visibility = Visibility.Visible,
        };
        tray.TrayLeftMouseUp += (_, _) => PanelToggleRequested?.Invoke();
        tray.ContextMenu = BuildMenu();
        tray.ForceCreate();                    // NIM_ADD：启动即创建，不等 Loaded
        return tray;
    }

    /// <summary>NIM_DELETE（Dispose 旧实例）→ NIM_ADD（新建并 ForceCreate），tooltip 随之重建（A-1）。</summary>
    private void RebuildTrayIcon(string reason)
    {
        if (_disposed) return;
        try
        {
            _tray?.Dispose();                  // NIM_DELETE
        }
        catch (Exception ex)
        {
            AppLog.Warn($"托盘释放异常（重建路径）：{ex.Message}");
        }
        _tray = BuildTrayIcon();               // NIM_ADD
        AppLog.Info($"托盘图标已重建（{reason}）→ {(_state == PeakState.Peak ? "橙(高峰)" : "绿(空闲)")} " +
                    $"tooltip={_tray.ToolTipText} 主题={(_isDarkTheme() ? "深" : "浅")}");
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "打开面板" };
        open.Click += (_, _) => PanelToggleRequested?.Invoke();

        // v1.1：勾选项「钉住面板」——与面板顶行图钉钮双向实时同步。
        // IsCheckable + Click：用户勾/取消勾都会先翻转 IsChecked 再触发 Click，
        // 以翻转后的 IsChecked 为准通知 App；程序同步（SetPanelPinned）期间用 _syncingPinMenu 抑制。
        var pin = new MenuItem { Header = "钉住面板", IsCheckable = true, IsChecked = _panelPinned };
        pin.Click += (_, _) =>
        {
            if (_syncingPinMenu) return;
            AppLog.Info($"托盘菜单「钉住面板」用户勾选 → {pin.IsChecked}");
            PanelPinToggleRequested?.Invoke(pin.IsChecked);
        };
        _pinMenuItem = pin;

        var settings = new MenuItem { Header = "设置" };
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        var exit = new MenuItem { Header = "退出" };
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(open);
        menu.Items.Add(pin);
        menu.Items.Add(settings);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    private static string ToolTipFor(PeakState state)
        => $"DeepSeek 余额小组件 · {(state == PeakState.Peak ? "高峰时段" : "空闲时段")}";

    private string IconPathFor(PeakState state, bool dark)
        => Path.Combine(_iconDir, (state, dark) switch
        {
            (PeakState.Peak, false) => "peak-light.ico",
            (PeakState.Peak, true) => "peak-dark.ico",
            (PeakState.Idle, false) => "idle-light.ico",
            (PeakState.Idle, true) => "idle-dark.ico",
            _ => "idle-light.ico",   // 非法枚举值防御性回退
        });

    /// <summary>启动时一次性生成 4 枚图标（浅/深 × 峰/谷）。</summary>
    private void EnsureIconFiles()
    {
        WriteIco("peak-light.ico", PeakLight);
        WriteIco("peak-dark.ico", PeakDark);
        WriteIco("idle-light.ico", IdleLight);
        WriteIco("idle-dark.ico", IdleDark);
    }

    private void WriteIco(string fileName, (Color Fill, Color Ring) colors)
    {
        var path = Path.Combine(_iconDir, fileName);
        DynamicIconRenderer.WriteCircleIco(path, colors.Fill, colors.Ring);
        var px = DynamicIconRenderer.DecodeIcoCenterPixel(path);
        AppLog.Info($"图标已生成 {fileName} 中心像素=#{px.R:X2}{px.G:X2}{px.B:X2}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _tray?.Dispose();                  // NIM_DELETE：托盘图标即时消失
            AppLog.Info("托盘图标已释放 (TaskbarIcon.Dispose)");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"托盘释放异常：{ex.Message}");
        }
        _tray = null;
        // D-02 随用随清：图标临时文件放应用专属目录，退出即删
        try
        {
            if (Directory.Exists(_iconDir)) Directory.Delete(_iconDir, recursive: true);
            AppLog.Info("图标临时目录已清理");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"图标目录清理失败：{ex.Message}");
        }
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
