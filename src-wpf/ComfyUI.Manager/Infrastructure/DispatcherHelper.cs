using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ComfyUI.Manager.Infrastructure;

public static class DispatcherHelper
{
    /// <summary>把 action 派发到 UI 线程(异步)。</summary>
    public static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Dispatcher.CurrentDispatcher
            ?? Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }
}
