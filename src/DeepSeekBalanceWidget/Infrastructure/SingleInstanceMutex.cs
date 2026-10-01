namespace DeepSeekBalanceWidget.Infrastructure;

/// <summary>
/// 单实例 Mutex（A-3 / D-15）：命名 Local\DeepSeekBalanceWidget.SingleInstance。
/// 启动抢占失败即判定已有实例运行（--show-panel-on-start 等参数不得绕过）；
/// 退出清理链中 Dispose（释放 + 释放命名互斥体，应用退出后系统自动 abandoned 唤醒后备实例判定）。
/// </summary>
public sealed class SingleInstanceMutex : IDisposable
{
    public const string MutexName = @"Local\DeepSeekBalanceWidget.SingleInstance";

    private readonly Mutex _mutex;

    public bool IsFirstInstance { get; }

    public SingleInstanceMutex()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsFirstInstance = createdNew;
    }

    public void Dispose()
    {
        try
        {
            if (IsFirstInstance) _mutex.ReleaseMutex();   // A-3：退出必须释放
        }
        catch (ApplicationException)
        {
            // 当前线程不持有（异常路径防御）：忽略
        }
        _mutex.Dispose();
    }
}
