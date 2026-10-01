using Microsoft.Win32;

namespace DeepSeekBalanceWidget.Settings;

/// <summary>
/// 开机自启（HKCU\Software\Microsoft\Windows\CurrentVersion\Run 写/删，指向当前 exe 路径）。
/// 应用对注册表的唯一写入即此键（不产生其他注册表残留，探针 D §5 口径）。
/// </summary>
public sealed class AutostartRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeepSeekBalanceWidget";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            DeepSeekBalanceWidget.Infrastructure.AppLog.Error($"开机自启写入失败：{ex.Message}");
            return false;
        }
    }
}
