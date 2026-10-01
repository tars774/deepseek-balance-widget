using System.IO;
using System.Windows;
using System.Windows.Media;
using DeepSeekBalanceWidget.Peak;
using DeepSeekBalanceWidget.Settings;

namespace DeepSeekBalanceWidget.Infrastructure;

/// <summary>
/// 主题管理（D-17 / A1 §3）：浅/深两套资源字典（Panel/Themes/Light|Dark.xaml，含 A1 §3 完整四态色板）
/// 统一切换入口；状态语义色（高峰=橙、空闲=绿）按「状态 × 主题」从 A1 精确变量表取值注入 Brush.*。
/// 色板严格取 A1 §3 变量表，不引入新色（浅/深橙 #E4720B/#FF9C4A、浅/深绿 #17994E/#46D27F 等）。
/// </summary>
public sealed class ThemeManager
{
    private static readonly Uri LightUri =
        new("pack://application:,,,/DeepSeekBalanceWidget;component/Panel/Themes/Light.xaml");
    private static readonly Uri DarkUri =
        new("pack://application:,,,/DeepSeekBalanceWidget;component/Panel/Themes/Dark.xaml");

    private ResourceDictionary? _themeDict;

    /// <summary>当前生效主题是否深色（含「跟随系统」解析结果）。</summary>
    public bool IsDark { get; private set; }

    /// <summary>
    /// 应用主题（三模式统一入口）：System 由调用方传入 systemIsLight 解析后的深浅布尔。
    /// 幂等：同深浅重复调用不重载字典。
    /// 【实测教训】App.xaml 预载的 Light 字典与运行时插入的字典会并存，且 WPF 合并字典查找
    /// 「后加入者优先」——切换时若不移除预载字典，新字典会被其遮蔽（表现为日志已切换但刷子仍是旧色）。
    /// 因此每次 Apply 先清空全部主题字典，再装入唯一一份。
    /// </summary>
    public void Apply(bool dark)
    {
        if (_themeDict != null && IsDark == dark) return;
        IsDark = dark;

        var app = Application.Current.Resources;
        for (int i = app.MergedDictionaries.Count - 1; i >= 0; i--)
            app.MergedDictionaries.RemoveAt(i);
        _themeDict = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        app.MergedDictionaries.Add(_themeDict);

        // A1 §3 中性色 6 项：从主题字典取精确值
        SetBrush("Brush.Bg", "Color.Bg");
        SetBrush("Brush.Fg", "Color.Fg");
        SetBrush("Brush.Sub", "Color.Sub");
        SetBrush("Brush.Muted", "Color.Muted");
        SetBrush("Brush.Line", "Color.Line");
        SetBrush("Brush.Card", "Color.Card");

        AppLog.Info($"主题已应用 → {(dark ? "深色" : "浅色")}（资源字典 {Path.GetFileName(dark ? DarkUri.LocalPath : LightUri.LocalPath)}）");
    }

    /// <summary>
    /// 应用状态语义色（A1 §3 状态表四态精确值）：
    /// 高峰·浅 (E4720B/FDF0E3/C25E06)、高峰·深 (FF9C4A/rgba(255,156,74,.14)/E4720B)、
    /// 空闲·浅 (17994E/E6F6EC/0F7A3C)、空闲·深 (46D27F/rgba(70,210,127,.14)/17994E)。
    /// 状态翻转沿与主题切换后均须调用。
    /// </summary>
    public void ApplyAccent(PeakState state)
    {
        var app = Application.Current.Resources;
        (Color acc, Color soft, Color deep) = (state, IsDark) switch
        {
            (PeakState.Peak, false) => (Col(0xE4, 0x72, 0x0B), Col(0xFD, 0xF0, 0xE3), Col(0xC2, 0x5E, 0x06)),
            (PeakState.Peak, true) => (Col(0xFF, 0x9C, 0x4A), Color.FromArgb(36, 0xFF, 0x9C, 0x4A), Col(0xE4, 0x72, 0x0B)),
            (PeakState.Idle, false) => (Col(0x17, 0x99, 0x4E), Col(0xE6, 0xF6, 0xEC), Col(0x0F, 0x7A, 0x3C)),
            (PeakState.Idle, true) => (Col(0x46, 0xD2, 0x7F), Color.FromArgb(36, 0x46, 0xD2, 0x7F), Col(0x17, 0x99, 0x4E)),
            // 非法枚举值防御性回退（空闲·浅色）
            _ => (Col(0x17, 0x99, 0x4E), Col(0xE6, 0xF6, 0xEC), Col(0x0F, 0x7A, 0x3C)),
        };
        app["Brush.Acc"] = Frozen(acc);
        app["Brush.AccSoft"] = Frozen(soft);
        app["Brush.AccDeep"] = Frozen(deep);
    }

    private void SetBrush(string brushKey, string colorKey)
    {
        var color = (Color)Application.Current.Resources[colorKey];
        Application.Current.Resources[brushKey] = Frozen(color);
    }

    private static Color Col(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
