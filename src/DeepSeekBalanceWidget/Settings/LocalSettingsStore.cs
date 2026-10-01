using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeepSeekBalanceWidget.Infrastructure;

namespace DeepSeekBalanceWidget.Settings;

/// <summary>
/// settings.json 读写（D-20：非敏感配置存 %APPDATA%，System.Text.Json）。
/// 文件损坏/缺失时回退默认值并记日志；保存为整文件覆盖（先建目录）。
/// 目录可注入（v1.1：集成测试用临时目录做读写往返，不触碰真实 %APPDATA%）；缺省即真实目录。
/// </summary>
public sealed class LocalSettingsStore
{
    private readonly string _directory;

    public LocalSettingsStore(string? directory = null)
        => _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepSeekBalanceWidget");

    public static string SettingsDirectory
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeepSeekBalanceWidget");

    public string SettingsFilePath => Path.Combine(_directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFilePath), JsonOptions);
                if (s != null)
                {
                    s.RefreshIntervalMinutes = (int)SchedulerClamp(s.RefreshIntervalMinutes);
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"settings.json 读取失败（回退默认值）：{ex.Message}");
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Error($"settings.json 写入失败：{ex.Message}");
        }
    }

    private static double SchedulerClamp(int minutes) => Math.Clamp((double)minutes, 5, 1440);
}
