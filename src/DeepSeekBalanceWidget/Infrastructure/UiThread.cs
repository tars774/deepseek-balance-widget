using System.Threading;

namespace DeepSeekBalanceWidget.Infrastructure;

/// <summary>
/// UI 线程标识（scheduler-design §3.2 封送契约：4 类事件必须都在 UI 线程触发；
/// 异步完成回调的防御性封送判定用）。
/// </summary>
public static class UiThread
{
    public static int Id { get; private set; }

    /// <summary>在 UI 线程启动路径调用一次（App.OnStartup）。</summary>
    public static void Capture() => Id = Environment.CurrentManagedThreadId;

    public static bool IsCurrent => Environment.CurrentManagedThreadId == Id;
}
