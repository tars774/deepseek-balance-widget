using System.IO;
using System.Text;

namespace DeepSeekBalanceWidget.Infrastructure;

/// <summary>
/// 应用日志（D-16，含 D-1 必改项）：落 %APPDATA%\DeepSeekBalanceWidget\logs\，按日滚动
/// （app-yyyyMMdd.log），会话 tag 区分多次运行；写入层统一过 SecretMasker——0 Key / 0 Authorization
/// 兜底（B-1）。探针 D §3 教训：单文件形态不得落 exe 旁。
/// 行格式：yyyy-MM-dd HH:mm:ss.fff [LEVEL] [sess:xxxxxxxx] 消息
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static string? _currentFile;
    private static DateOnly _currentDate;
    private static string _tag = "";

    public static string LogDirectory => _dir ?? "";
    public static string? CurrentFile => _currentFile;

    public static void Init()
    {
        lock (Gate)
        {
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeepSeekBalanceWidget", "logs");
            Directory.CreateDirectory(_dir);
            _tag = Random.Shared.Next(0x10000000, int.MaxValue).ToString("x8");
            Write("INFO", $"=== 会话开始 tag=sess:{_tag} pid={Environment.ProcessId} ===");
        }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    private static void Write(string level, string msg)
    {
        lock (Gate)
        {
            if (_dir is null) return;
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_currentFile is null || today != _currentDate)
                {
                    _currentDate = today;
                    _currentFile = Path.Combine(_dir, $"app-{today:yyyyMMdd}.log");   // D-1：按日滚动
                }
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [sess:{_tag}] {SecretMasker.Mask(msg)}";
                File.AppendAllText(_currentFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch
            {
                // 日志失败不影响应用主流程
            }
        }
    }
}
