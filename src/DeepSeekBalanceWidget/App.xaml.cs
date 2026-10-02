using System.IO;
using System.Windows;
using System.Windows.Threading;
using DeepSeekBalanceWidget.Balance;
using DeepSeekBalanceWidget.Credentials;
using DeepSeekBalanceWidget.Infrastructure;
using DeepSeekBalanceWidget.Panel;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Scheduling;
using DeepSeekBalanceWidget.Settings;
using DeepSeekBalanceWidget.Tray;

namespace DeepSeekBalanceWidget;

/// <summary>
/// 应用入口装配（final-technical-plan §3.1 / §5 数据流）：
/// 单实例 Mutex（D-15/A-3）→ 日志（D-16/D-1）→ 设置加载（D-20）→ 主题（D-17）→
/// 托盘（D-01~D-05）→ A1 面板 → Balance/Peak 服务（D-06~D-14）→ Scheduler（阶段 3 设计）→ 启动。
/// 退出统一走 RequestExit 清理链：Stop 心跳 → 释放托盘（含图标文件清理 D-02）→ 释放 Mutex →
/// Shutdown（A-4：不用 Environment.Exit）。
/// v1.1（2026-09-30）：面板右缘停靠/拖动吸附/钉住/位置记忆/钉住时置顶——持久化经
/// persistPanelState 回调写 settings.json（PanelX/PanelY/Pinned/PinTopmost），两处钉住开关经
/// PinnedChanged ↔ TrayController.SetPanelPinned 双向实时同步。
/// 需求变更 2026-10-02：进度条「剩余口径」（绿条=剩余÷总长，与倒计时同源同步）+ 绿条流光扫过动画
/// （档位经设置窗口即时生效并持久化）；余额显示期快轮询——panelShown/panelHidden → Scheduler.
/// NotifyPanelVisibility，显示期每 5s 快刷、隐藏回设定周期、重显立即先刷。
/// </summary>
public partial class App : Application
{
    private SingleInstanceMutex? _mutex;
    private TrayController? _tray;
    private PanelWindow? _panel;
    private PanelViewModel _panelVm = null!;
    private SettingsWindow? _settingsWindow;
    private Scheduler? _scheduler;
    private HolidayService? _holidays;
    private SystemThemeWatcher? _themeWatcher;
    private ThemeManager _themeManager = null!;
    private AppSettings _settings = null!;
    private LocalSettingsStore _settingsStore = null!;
    private ICredentialStore _credentials = null!;
    private PeakState _lastKnownState = PeakState.Idle;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLog.Init();
        UiThread.Capture();
        AppLog.Info($"应用启动 args=[{string.Join(' ', e.Args)}] pid={Environment.ProcessId}");

        // ---- 单实例（A-3/D-15）：失败即自动退出，任何启动参数不得绕过 ----
        _mutex = new SingleInstanceMutex();
        if (!_mutex.IsFirstInstance)
        {
            AppLog.Warn("单实例检测：已有实例在运行（Mutex 抢占失败），二次启动自动退出");
            Shutdown();
            return;
        }
        AppLog.Info($"单实例 Mutex 抢占成功 name={SingleInstanceMutex.MutexName}");

        InstallGlobalExceptionHandlers();

        // ---- 设置（D-20）----
        _settingsStore = new LocalSettingsStore();
        _settings = _settingsStore.Load();

        // ---- 凭据（D-07/§6-7）----
        _credentials = new Win32CredentialStore();
        if (_credentials.TryRead(out var existingKey, out _)) SecretMasker.RegisterSecret(existingKey);

        // ---- 主题（D-17）----
        _themeManager = new ThemeManager();
        ApplyTheme();

        // ---- Peak 服务（真实双源；探针 C 移植）----
        var time = new SystemTimeProvider();
        var holidayHttp = HolidayHttpFactory.Create();
        _holidays = new HolidayService(new PrimaryHolidayClient(holidayHttp), new BackupHolidayClient(holidayHttp), time);
        var peak = new PeakEngine(_holidays, time);

        // 启动首帧快照（预置托盘/胶囊初始状态，避免首 tick 无事件路径下的初始色不一致）
        var snap0 = peak.ComputeSnapshot();
        _themeManager.ApplyAccent(snap0.State);
        _lastKnownState = snap0.State;

        // ---- 面板 + 托盘 ----
        _panelVm = new PanelViewModel();
        _panelVm.ApplyState(snap0.State);
        _panelVm.UpdateSnapshot(snap0);
        _panel = new PanelWindow(_panelVm,
            openSettings: OpenSettings,
            manualRefreshRequested: () => _scheduler?.RequestBalanceRefresh(BalanceRefreshSource.Manual),
            // v1.1：拖动/吸附结束、钉住切换时持久化窗口位置 + 钉住状态（settings.json 新字段）
            persistPanelState: (x, y, pinned) =>
            {
                _settings.PanelX = x;
                _settings.PanelY = y;
                _settings.Pinned = pinned;
                _settingsStore.Save(_settings);
            },
            panelShown: () =>
            {
                // 面板重开补推：隐藏期间 OnTickSnapshot 直刷被降 CPU 门跳过 → 显示后立即用 LastSnapshot 推一次 UI（UI 线程封送），消除重开后 ≤1s 陈旧闪变；门本身不变
                if (_scheduler?.LastSnapshot is { } snap)
                {
                    if (UiThread.IsCurrent) _panelVm.UpdateSnapshot(snap);
                    else _ = Dispatcher.BeginInvoke(() => _panelVm.UpdateSnapshot(snap));
                }
                // 需求变更 2026-10-02：显示期信号（内部立即先刷一次 + 30s 去抖 + 快轮询 5s 节奏接管）
                _scheduler?.NotifyPanelVisibility(true);
            },
            // 需求变更 2026-10-02：面板隐藏信号 → 快轮询停止、回到后台设定周期
            panelHidden: () => _scheduler?.NotifyPanelVisibility(false));

        // v1.1：位置记忆注入（无记忆字段 → 每次显示走主屏右缘停靠默认位）
        _panel.SetRememberedPosition(_settings.PanelX, _settings.PanelY);
        // 需求变更 2026-10-02：流光效果装配（settings.json 档位；设置窗口切换经 ApplyFlowSettings 即时生效）
        _panel.ApplyFlowSettings(_settings.FlowEnabled, _settings.FlowSpeed, _settings.FlowIntensity);
        _panel.PinnedChanged += pinned =>
        {
            // 两处钉住开关实时同步：面板图钉钮 → 托盘菜单勾选（反向走 _tray.PanelPinToggleRequested）
            _tray?.SetPanelPinned(pinned);
        };

        _tray = new TrayController(
            Path.Combine(LocalSettingsStore.SettingsDirectory, "icons"),
            isDarkTheme: () => _themeManager.IsDark);
        _tray.Initialize(snap0.State);
        _tray.PanelToggleRequested += () => _panel.TogglePanelViaTray();
        _tray.PanelPinToggleRequested += pinned => _panel.SetPinned(pinned, "tray-menu");   // v1.1：托盘菜单勾选 → 面板
        _tray.SettingsRequested += OpenSettings;
        _tray.ExitRequested += () => RequestExit("托盘菜单·退出");

        // ---- v1.1：钉住 / 钉住时置顶记忆恢复（托盘已就绪，PinnedChanged 能同步到菜单勾选） ----
        _panel.SetPinTopmost(_settings.PinTopmost, "startup-settings");
        if (_settings.Pinned)
        {
            _panel.SetPinned(true, "startup-restore");
        }

        // ---- 系统主题跟随（WM_SETTINGCHANGE + 轮询兜底）----
        _themeWatcher = new SystemThemeWatcher(Dispatcher);
        _themeWatcher.AppsUseLightThemeChanged += _ =>
        {
            if (_settings.Theme == ThemeMode.System) ApplyThemeAndRefresh();
        };
        _themeWatcher.Attach(_panel);

        // ---- Balance / Scheduler ----
        var balance = new BalanceClient(_credentials);
        var options = new SchedulerOptions
        {
            BalanceInterval = SchedulerOptions.ClampBalanceInterval(TimeSpan.FromMinutes(_settings.RefreshIntervalMinutes)),
        };
        _scheduler = new Scheduler(time, peak, balance, _holidays, options, Dispatcher,
            isKeyConfigured: HasApiKey);
        // FB-1 根治（2026-10-01）：托盘/胶囊强调色已按 snap0.State 初始化（本方法前段），
        // 该状态即"对外已发布的初始状态"。节假日数据晚于首帧到达时（法定节假日的工作日，
        // snap0 按工作日逻辑=Peak，数据到达重算=Idle），首次重算必须以它为翻转沿基准补发
        // StateChanged，否则托盘/强调色停留 Peak 色直到下一次真实翻转沿（节假日全天无翻转）。
        _scheduler.InitialPublishedState = snap0.State;
        WireSchedulerEvents();
        _scheduler.Sink = _panel;            // 倒计时/进度直刷出口（面板不可见时自行跳过）
        _scheduler.Start();

        // ---- 启动参数：--show-panel-on-start（冒烟/用户快捷方式可选）----
        if (e.Args.Any(a => a.Equals("--show-panel-on-start", StringComparison.OrdinalIgnoreCase)))
        {
            AppLog.Info("启动参数 --show-panel-on-start：启动即显示面板");
            _ = Dispatcher.BeginInvoke(() => _panel?.ShowPanel(), DispatcherPriority.Background);
        }
    }

    private void WireSchedulerEvents()
    {
        // A-2：状态订阅去重——StateChanged 仅在此订阅一次；托盘侧再以同态短路兜底
        _scheduler!.StateChanged += (_, args) =>
        {
            _lastKnownState = args.New;
            _themeManager.ApplyAccent(args.New);       // 胶囊/进度条强调色随状态翻转沿切换
            _panelVm.ApplyState(args.New);
            _tray?.SetState(args.New);                 // 图标 + tooltip NIM 重建（A-1）
        };
        _scheduler.DayChanged += (_, _) =>
        {
            // 跨天：倒计时前缀/状态已由翻转沿与每 tick 直刷覆盖；此处用最新快照兜底刷新一次
            if (_scheduler.LastSnapshot is { } snap) _panelVm.UpdateSnapshot(snap);
        };
        _scheduler.BalanceRefreshRequested += (_, _) => _panelVm.SetRefreshing(true);
        _scheduler.BalanceRefreshed += (_, args) =>
        {
            _panelVm.SetRefreshing(false);
            _panelVm.UpdateBalance(args.Result,
                manualBalanceEnabled: _settings.ManualBalanceEnabled,
                manualBalanceText: _settings.ManualBalanceText,
                atUtc: args.AtUtc);
        };

        // C-3：节假日数据恢复（退避成功）→ 立即刷新快照
        _holidays!.FetchCompleted += () =>
        {
            if (!UiThread.IsCurrent)
                _ = Dispatcher.BeginInvoke(() => _scheduler?.RecomputeNow("节假日数据恢复(C-3)"));
            else
                _scheduler?.RecomputeNow("节假日数据恢复(C-3)");
        };
    }

    private void ApplyTheme()
    {
        var dark = _settings.Theme switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => !SystemThemeWatcher.GetAppsUseLightTheme(),
        };
        _themeManager.Apply(dark);
    }

    private void ApplyThemeAndRefresh()
    {
        ApplyTheme();
        _themeManager.ApplyAccent(_lastKnownState);
        _panelVm.ApplyState(_lastKnownState);     // 触发前缀/胶囊重算（DynamicResource 自动重绘色）
        _tray?.RefreshTheme();
    }

    private bool HasApiKey()
    {
        if (!_credentials.TryRead(out var secret, out var win32)) return false;
        if (string.IsNullOrEmpty(secret)) return false;
        if (win32 != 0) return false;
        SecretMasker.RegisterSecret(secret);
        return true;
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        AppLog.Info("打开设置窗口");
        _settingsWindow = new SettingsWindow(_settings, _settingsStore, _credentials,
            themeChanged: ApplyThemeAndRefresh,
            intervalChanged: minutes => _scheduler?.UpdateBalanceInterval(TimeSpan.FromMinutes(minutes)),
            keyChanged: () =>
            {
                AppLog.Info("Key 已变更 → 立即刷新余额");
                _scheduler?.RequestBalanceRefresh(BalanceRefreshSource.Manual);
            },
            // v1.1：钉住时置顶开关即时生效（Topmost 立即重算）
            pinTopmostChanged: v => _panel?.SetPinTopmost(v, "settings-window"),
            // 需求变更 2026-10-02：流光档位开关即时生效（停旧动画→按档位重建，绿条纯色静止或扫过）
            flowChanged: (enabled, speed, intensity) =>
                _panel?.ApplyFlowSettings(enabled, speed, intensity));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// 退出清理链（D-15/A-3/A-4）：Stop 心跳 → 托盘释放（NIM_DELETE + 图标文件清理）→
    /// 主题监听停 → Mutex 释放 → Shutdown。绝不用 Environment.Exit 兜底。
    /// </summary>
    private void RequestExit(string reason)
    {
        AppLog.Info($"退出开始 reason={reason}");
        try
        {
            _scheduler?.Stop();
            _tray?.Dispose();
            _themeWatcher?.Dispose();
            _mutex?.Dispose();
            AppLog.Info("退出清理完成（Scheduler 已停、托盘已释放、图标已清理、主题监听已停、Mutex 已释放）");
        }
        catch (Exception ex)
        {
            AppLog.Error($"退出清理异常：{ex.Message}");
        }
        AppLog.Info("=== 进程退出 ===");
        Shutdown();
    }

    private void InstallGlobalExceptionHandlers()
    {
        // 全局异常兜底：常驻应用不因单点异常静默死亡；全部记日志（0 Key / 0 Authorization 经脱敏层）
        DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Error("Dispatcher 未处理异常：" + e.Exception.GetType().Name + ": " + e.Exception.Message +
                         "\n" + e.Exception.StackTrace);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("AppDomain 未处理异常：" + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            AppLog.Error("未观察 Task 异常：" + e.Exception?.GetBaseException().Message);
        };
    }
}
