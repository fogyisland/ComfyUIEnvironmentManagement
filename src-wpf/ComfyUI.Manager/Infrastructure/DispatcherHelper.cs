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
        // v0.6.18.2 G11+:Dispatcher.CurrentDispatcher 在后台线程会 *新建* 一个 dispatcher,
        // 然后 InvokeAsync 把 action 派回 *同一个后台线程* — 集合修改仍在非 UI 线程,
        // 触发 WPF "CollectionView type does not support changes to its SourceCollection
        // from a thread different from the Dispatcher thread" 异常。
        // 修法:永远拿 Application.Current.Dispatcher(UI dispatcher),不要 CurrentDispatcher。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }
}
