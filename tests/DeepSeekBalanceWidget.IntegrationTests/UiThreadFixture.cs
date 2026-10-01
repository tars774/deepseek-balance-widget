using System.Windows.Threading;
using DeepSeekBalanceWidget.Infrastructure;

namespace DeepSeekBalanceWidget.IntegrationTests;

/// <summary>
/// STA + Dispatcher 测试线程（WPF 组件——TrayController/TaskbarIcon/DynamicIconRenderer——
/// 必须在 STA 线程构造；Scheduler 的 DispatcherTimer 依赖该 Dispatcher 泵送）。
/// 每个测试自建自毁，线程为后台线程不阻塞进程退出。
/// </summary>
public sealed class UiThreadFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Dispatcher Dispatcher { get; }

    public UiThreadFixture()
    {
        _thread = new Thread(() =>
        {
            var d = Dispatcher.CurrentDispatcher;
            _ready.SetResult(d);
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "tests-ui" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = _ready.Task.GetAwaiter().GetResult();
        // scheduler-design §3.2 封送契约依赖 UiThread 标识：测试 UI 线程即该线程
        Dispatcher.Invoke(() => UiThread.Capture());
    }

    public void Invoke(Action action) => Dispatcher.Invoke(action, DispatcherPriority.Send);
    public T Invoke<T>(Func<T> func) => Dispatcher.Invoke(func, DispatcherPriority.Send);

    public void Dispose()
    {
        Dispatcher.InvokeShutdown();
        _thread.Join(3000);
    }
}
