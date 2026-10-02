namespace DeepSeekBalanceWidget.Settings;

/// <summary>主题三模式（D-17）：跟随系统 / 浅色 / 深色。</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>流光扫过节奏（需求变更 2026-10-02）。</summary>
public enum FlowSpeed
{
    /// <summary>克制（默认）：一次扫过约 3.5s + 停顿约 1.5s，循环约 5s。</summary>
    Restrained,

    /// <summary>明显：约 2s 一次、无停顿、无缝循环。</summary>
    Visible,
}

/// <summary>流光高光强度（需求变更 2026-10-02）。</summary>
public enum FlowIntensity
{
    /// <summary>极淡（默认）：白色低透明度渐变，叠加 Brush.Acc 上仅隐约可见。</summary>
    Faint,

    /// <summary>可见：一眼可辨但不刺眼。</summary>
    Visible,
}

/// <summary>
/// 非敏感设置（D-20）：存 %APPDATA%\DeepSeekBalanceWidget\settings.json（System.Text.Json 读写）。
/// 仅 API Key 进 Credential Manager，绝不入此文件。
/// </summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>后台刷新间隔（分钟）。有效范围 5–1440，默认 30（读取后经 ClampBalanceInterval 钳制）。</summary>
    public int RefreshIntervalMinutes { get; set; } = 30;

    /// <summary>D-19 P2 可选降级：不提供 API Key 时手动输入余额（仅本地展示，不涉及网络与凭据存储）。</summary>
    public bool ManualBalanceEnabled { get; set; }

    public string ManualBalanceText { get; set; } = string.Empty;

    // ---------------- v1.1 面板位置与钉住（需求变更 2026-09-30） ----------------

    /// <summary>
    /// 面板窗口位置记忆（窗口左上角 DIP 坐标，含 14px 阴影边距的窗口坐标，非可见卡片坐标）。
    /// null = 无记忆（首次/旧版配置文件）→ 每次显示走主屏右缘停靠默认位（PanelPlacement）。
    /// </summary>
    public double? PanelX { get; set; }

    /// <summary>面板窗口位置记忆 Y 分量（语义同 <see cref="PanelX"/>）。</summary>
    public double? PanelY { get; set; }

    /// <summary>面板钉住状态记忆：钉住时失焦不隐藏；拖动/钉住切换时与位置一并持久化。</summary>
    public bool Pinned { get; set; }

    /// <summary>钉住时置顶（默认开）：开=钉住时 Topmost；关=钉住时普通层级（可被遮挡）。</summary>
    public bool PinTopmost { get; set; } = true;

    // ---------------- 流光效果（需求变更 2026-10-02） ----------------

    /// <summary>绿条流光扫过动画开关（默认开；关闭时绿条纯色静止）。</summary>
    public bool FlowEnabled { get; set; } = true;

    /// <summary>流光节奏（默认克制 Restrained）：克制 = 3.5s 扫过 + 1.5s 停顿循环；明显 = 约 2s 无缝循环。</summary>
    public FlowSpeed FlowSpeed { get; set; } = FlowSpeed.Restrained;

    /// <summary>流光强度（默认极淡 Faint）：极淡 = 白色低透明度仅隐约可见；可见 = 一眼可辨但不刺眼。</summary>
    public FlowIntensity FlowIntensity { get; set; } = FlowIntensity.Faint;
}
