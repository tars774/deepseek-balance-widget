using System.IO;
using DeepSeekBalanceWidget.Panel;
using DeepSeekBalanceWidget.Settings;
using Xunit;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// v1.1 位置与钉住记忆：settings.json 读写往返（PanelX/PanelY/Pinned/PinTopmost）。
/// 使用注入的临时目录（LocalSettingsStore v1.1 可注入），不触碰真实 %APPDATA%。
/// </summary>
public sealed class PanelStatePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsw-test-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RoundTrip_Panel_Position_And_Pin_Fields()
    {
        var store = new LocalSettingsStore(_dir);
        var s = new AppSettings
        {
            PanelX = 1586,
            PanelY = 770,
            Pinned = true,
            PinTopmost = false,
            RefreshIntervalMinutes = 30,
        };
        store.Save(s);

        var loaded = store.Load();
        Assert.True(loaded.PanelX.HasValue);
        Assert.True(loaded.PanelY.HasValue);
        Assert.Equal(1586, loaded.PanelX!.Value, precision: 6);
        Assert.Equal(770, loaded.PanelY!.Value, precision: 6);
        Assert.True(loaded.Pinned);
        Assert.False(loaded.PinTopmost);
    }

    [Fact]
    public void Missing_File_Defaults_No_Position_Unpinned_PinTopmost_On()
    {
        var store = new LocalSettingsStore(_dir);   // 目录不存在 → 回退默认值
        var s = store.Load();
        Assert.Null(s.PanelX);
        Assert.Null(s.PanelY);
        Assert.False(s.Pinned);
        Assert.True(s.PinTopmost);                  // 钉住时置顶默认开
    }

    [Fact]
    public void Legacy_File_Without_New_Fields_Loads_With_V11_Defaults()
    {
        Directory.CreateDirectory(_dir);
        // v1.0 形态的 settings.json（无 PanelX/PanelY/Pinned/PinTopmost 字段）
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"Theme":"System","RefreshIntervalMinutes":30}""");
        var s = new LocalSettingsStore(_dir).Load();
        Assert.Null(s.PanelX);
        Assert.Null(s.PanelY);
        Assert.False(s.Pinned);
        Assert.True(s.PinTopmost);
    }

    [Fact]
    public void Partial_Fields_File_Loads_Missing_As_Defaults()
    {
        Directory.CreateDirectory(_dir);
        // 仅位置记忆、无钉住字段（模拟拖动后未钉过的文件）
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"RefreshIntervalMinutes":15,"PanelX":100.5,"PanelY":200.25}""");
        var s = new LocalSettingsStore(_dir).Load();
        Assert.Equal(100.5, s.PanelX!.Value, precision: 6);
        Assert.Equal(200.25, s.PanelY!.Value, precision: 6);
        Assert.False(s.Pinned);
        Assert.True(s.PinTopmost);
    }

    // ---------------- 流光效果持久化（需求变更 2026-10-02） ----------------

    [Fact]
    public void RoundTrip_Flow_Fields()
    {
        var store = new LocalSettingsStore(_dir);
        var s = new AppSettings
        {
            FlowEnabled = false,
            FlowSpeed = FlowSpeed.Visible,
            FlowIntensity = FlowIntensity.Visible,
        };
        store.Save(s);

        var loaded = store.Load();
        Assert.False(loaded.FlowEnabled);
        Assert.Equal(FlowSpeed.Visible, loaded.FlowSpeed);
        Assert.Equal(FlowIntensity.Visible, loaded.FlowIntensity);
    }

    [Fact]
    public void Legacy_File_Without_Flow_Fields_Loads_With_Flow_Defaults()
    {
        Directory.CreateDirectory(_dir);
        // 旧形态 settings.json（无流光字段）→ 默认：开 / 克制 / 极淡（向后兼容）
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            """{"Theme":"System","RefreshIntervalMinutes":30}""");
        var s = new LocalSettingsStore(_dir).Load();
        Assert.True(s.FlowEnabled);
        Assert.Equal(FlowSpeed.Restrained, s.FlowSpeed);
        Assert.Equal(FlowIntensity.Faint, s.FlowIntensity);
    }

    [Fact]
    public void PanelViewModel_SetPinned_Raises_IsPinned()
    {
        var vm = new PanelViewModel();
        Assert.False(vm.IsPinned);
        bool? raised = null;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PanelViewModel.IsPinned)) raised = true; };
        vm.SetPinned(true);
        Assert.True(vm.IsPinned);
        Assert.True(raised);
    }
}
