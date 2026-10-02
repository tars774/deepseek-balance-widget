using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepSeekBalanceWidget.Credentials;
using DeepSeekBalanceWidget.Infrastructure;

namespace DeepSeekBalanceWidget.Settings;

/// <summary>
/// 设置窗口：API Key（PasswordBox → Credential Manager，清除 = 删除凭据）、后台（隐藏时）刷新周期
/// （5–1440 校验 + 即时生效）、开机自启（HKCU Run）、主题三模式（即时应用并持久化）、
/// 流光效果（需求变更 2026-10-02：开关 + 节奏/强度，即时生效并持久化）、
/// D-19 可选手动余额、v1.1 钉住时置顶（即时生效并持久化）。
/// API Key 不回显（仅显示配置状态），不落 settings.json。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LocalSettingsStore _store;
    private readonly ICredentialStore _credentials;
    private readonly Action _themeChanged;      // 即时应用主题
    private readonly Action<int> _intervalChanged;   // 即时生效间隔
    private readonly Action _keyChanged;        // Key 变更后立即刷新余额
    private readonly Action<bool> _pinTopmostChanged; // v1.1：钉住时置顶即时生效
    private readonly Action<bool, FlowSpeed, FlowIntensity> _flowChanged; // 需求变更 2026-10-02：流光档位即时生效

    private bool _suppressThemeEvent;
    private bool _suppressPinTopmostEvent;
    private bool _suppressFlowEvent;

    public SettingsWindow(AppSettings settings, LocalSettingsStore store, ICredentialStore credentials,
        Action themeChanged, Action<int> intervalChanged, Action keyChanged,
        Action<bool>? pinTopmostChanged = null,
        Action<bool, FlowSpeed, FlowIntensity>? flowChanged = null)
    {
        InitializeComponent();
        try
        {
            Icon = new BitmapImage(new Uri(
                "pack://application:,,,/DeepSeekBalanceWidget;component/Assets/app.ico"));
        }
        catch { /* 图标缺失不影响功能 */ }

        _settings = settings;
        _store = store;
        _credentials = credentials;
        _themeChanged = themeChanged;
        _intervalChanged = intervalChanged;
        _keyChanged = keyChanged;
        _pinTopmostChanged = pinTopmostChanged ?? (_ => { });
        _flowChanged = flowChanged ?? ((_, _, _) => { });

        IntervalBox.Text = settings.RefreshIntervalMinutes.ToString();
        AutostartBox.IsChecked = new AutostartRegistrar().IsEnabled();
        ManualBox.IsChecked = settings.ManualBalanceEnabled;
        ManualBalanceBox.Text = settings.ManualBalanceText;
        _suppressPinTopmostEvent = true;
        PinTopmostBox.IsChecked = settings.PinTopmost;   // v1.1：默认开（AppSettings 初值）
        _suppressPinTopmostEvent = false;
        _suppressThemeEvent = true;
        ThemeBox.SelectedIndex = settings.Theme switch
        {
            ThemeMode.Light => 1,
            ThemeMode.Dark => 2,
            _ => 0,
        };
        _suppressThemeEvent = false;
        // 需求变更 2026-10-02：流光档位初始化（抑制事件，仅回显当前配置）
        _suppressFlowEvent = true;
        FlowEnabledBox.IsChecked = settings.FlowEnabled;
        FlowSpeedRestrainedRadio.IsChecked = settings.FlowSpeed == FlowSpeed.Restrained;
        FlowSpeedVisibleRadio.IsChecked = settings.FlowSpeed == FlowSpeed.Visible;
        FlowIntensityFaintRadio.IsChecked = settings.FlowIntensity == FlowIntensity.Faint;
        FlowIntensityVisibleRadio.IsChecked = settings.FlowIntensity == FlowIntensity.Visible;
        _suppressFlowEvent = false;

        RefreshKeyState();
        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible)
                ?? Application.Current?.MainWindow;
    }

    private void RefreshKeyState()
    {
        bool has = _credentials.TryRead(out _, out var win32) &&
                   (win32 == 0 || win32 == Win32CredentialStore.ErrorNotFound);
        KeyStateText.Text = has
            ? "已配置（输入新值可覆盖；Key 存储于 Windows 凭据管理器，仅本机可读）"
            : "未配置（Key 存储于 Windows 凭据管理器，仅本机可读）";
        KeyStateText.Foreground = has
            ? (Brush)FindResource("Brush.Acc")
            : (Brush)FindResource("Brush.Muted");
    }

    // ---------- API Key ----------

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            KeyStateText.Text = "请先在输入框粘贴 API Key（sk- 开头）";
            KeyStateText.Foreground = (Brush)FindResource("Brush.Sub");
            return;
        }
        if (_credentials.Write(key, out var win32))
        {
            SecretMasker.RegisterSecret(key);   // B-1：完整 Key 注册进日志脱敏兜底
            KeyBox.Clear();
            RefreshKeyState();
            StatusText.Text = "Key 已保存";
            AppLog.Info($"设置：API Key 已保存（长度 {key.Length}，凭据 Win32={win32}）");
            _keyChanged();
        }
        else
        {
            KeyStateText.Text = $"保存失败（Win32 {win32}）";
            KeyStateText.Foreground = (Brush)FindResource("Brush.Sub");
            AppLog.Error($"设置：API Key 保存失败（Win32 {win32}）");
        }
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        if (_credentials.Delete(out var win32))
        {
            RefreshKeyState();
            StatusText.Text = "Key 已清除";
            AppLog.Info("设置：API Key 已清除");
            _keyChanged();
        }
        else
        {
            KeyStateText.Text = $"清除失败（Win32 {win32}）";
            KeyStateText.Foreground = (Brush)FindResource("Brush.Sub");
        }
    }

    // ---------- 主题（即时应用并持久化） ----------

    private void Theme_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressThemeEvent) return;
        _settings.Theme = ThemeBox.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        _store.Save(_settings);
        AppLog.Info($"设置：主题已切换为 {_settings.Theme}（即时生效）");
        _themeChanged();
    }

    // ---------- v1.1：钉住时置顶（即时应用并持久化） ----------

    private void PinTopmost_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPinTopmostEvent) return;
        _settings.PinTopmost = PinTopmostBox.IsChecked == true;
        _store.Save(_settings);
        AppLog.Info($"设置：钉住时置顶={_settings.PinTopmost}（即时生效）");
        _pinTopmostChanged(_settings.PinTopmost);
    }

    // ---------- 需求变更 2026-10-02：流光效果（即时应用并持久化） ----------

    private void FlowEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFlowEvent) return;
        _settings.FlowEnabled = FlowEnabledBox.IsChecked == true;
        _store.Save(_settings);
        AppLog.Info($"设置：流光效果={(_settings.FlowEnabled ? "开" : "关")}（即时生效）");
        _flowChanged(_settings.FlowEnabled, _settings.FlowSpeed, _settings.FlowIntensity);
    }

    private void FlowSpeed_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFlowEvent) return;
        _settings.FlowSpeed = FlowSpeedVisibleRadio.IsChecked == true ? FlowSpeed.Visible : FlowSpeed.Restrained;
        _store.Save(_settings);
        AppLog.Info($"设置：流光节奏={_settings.FlowSpeed}（即时生效）");
        _flowChanged(_settings.FlowEnabled, _settings.FlowSpeed, _settings.FlowIntensity);
    }

    private void FlowIntensity_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFlowEvent) return;
        _settings.FlowIntensity = FlowIntensityVisibleRadio.IsChecked == true ? FlowIntensity.Visible : FlowIntensity.Faint;
        _store.Save(_settings);
        AppLog.Info($"设置：流光强度={_settings.FlowIntensity}（即时生效）");
        _flowChanged(_settings.FlowEnabled, _settings.FlowSpeed, _settings.FlowIntensity);
    }

    // ---------- 保存（间隔 / 自启 / 手动余额） ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // 间隔校验：5–1440（SchedulerOptions.ClampBalanceInterval 同口径）
        if (!int.TryParse(IntervalBox.Text.Trim(), out var minutes) || minutes is < 5 or > 1440)
        {
            StatusText.Text = "刷新周期无效：请输入 5–1440 的整数分钟";
            StatusText.Foreground = (Brush)FindResource("Brush.Sub");
            return;
        }

        bool intervalChanged = minutes != _settings.RefreshIntervalMinutes;
        _settings.RefreshIntervalMinutes = minutes;
        _settings.ManualBalanceEnabled = ManualBox.IsChecked == true;
        _settings.ManualBalanceText = ManualBalanceBox.Text.Trim();
        _store.Save(_settings);

        var registrar = new AutostartRegistrar();
        bool wantAutostart = AutostartBox.IsChecked == true;
        if (registrar.SetEnabled(wantAutostart) && registrar.IsEnabled() == wantAutostart)
        {
            AppLog.Info($"设置：开机自启已{(wantAutostart ? "开启" : "关闭")}");
        }
        else
        {
            StatusText.Text = "已保存（开机自启写入失败，详见日志）";
            AppLog.Error("设置：开机自启写入未生效");
            return;
        }

        if (intervalChanged)
        {
            _intervalChanged(minutes);   // Scheduler 即时重排到期点
        }
        StatusText.Text = "已保存";
        StatusText.Foreground = (Brush)FindResource("Brush.Acc");
        AppLog.Info($"设置：已保存 间隔={minutes}min 自启={wantAutostart} 手动余额={_settings.ManualBalanceEnabled}");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
