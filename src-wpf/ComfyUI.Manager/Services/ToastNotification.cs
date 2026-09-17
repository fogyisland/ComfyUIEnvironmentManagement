using System;
using System.Windows;
using System.Windows.Threading;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-09-17) T47:Toast 服务最小版(项目此前无 toast 控件,本 task 落地)。
/// 4 种级别 (Success / Warning / Error / Info) + UI 线程调度 ——
/// 真 toast UI (弹窗 + 3-7s 自动消失) 由后续 task 落地,本版仅 System.Diagnostics.Debug.WriteLine
/// 作为占位实现 + 线程安全 dispatching。T4 「VM 注入 ToastNotification」时可直接引用本类。
/// 抛异常:本服务永不抛 — 内部 try/catch 保护 Debug.WriteLine 失败。
/// </summary>
public sealed class ToastNotification
{
    /// <summary>v1.0.0.x T47:成功提示(3s 显示时长)。</summary>
    public void Success(string message) => Show("✓ " + message);

    /// <summary>v1.0.0.x T47:警告提示(5s 显示时长)。</summary>
    public void Warning(string message) => Show("⚠ " + message);

    /// <summary>v1.0.0.x T47:错误提示(7s 显示时长)。</summary>
    public void Error(string message) => Show("✕ " + message);

    /// <summary>v1.0.0.x T47:信息提示(3s 显示时长)。</summary>
    public void Info(string message) => Show("ℹ " + message);

    private void Show(string text)
    {
        try
        {
            var app = Application.Current;
            if (app?.Dispatcher == null || app.Dispatcher.CheckAccess())
            {
                // 占位实现:无 toast 控件时 Debug 输出 + 日志可见。
                System.Diagnostics.Debug.WriteLine($"[Toast] {text}");
            }
            else
            {
                app.Dispatcher.Invoke(() =>
                {
                    try { System.Diagnostics.Debug.WriteLine($"[Toast] {text}"); }
                    catch { /* dispatcher 内吞,toast 永不抛 */ }
                });
            }
        }
        catch
        {
            // 任何 fallback 失败 → 静默吞,toast 服务不允许 throw 污染业务路径
        }
    }
}
