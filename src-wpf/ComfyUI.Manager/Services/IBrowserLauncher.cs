using System;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.10 T2:统一 env-list 组件报告 + 打开浏览器按钮的 Chrome 优先 fallback 行为。
/// 实现见 <see cref="BrowserLauncher"/>。
/// </summary>
public interface IBrowserLauncher
{
    /// <summary>
    /// 用 Chrome 打开 path(URL 或本地文件路径)。Chrome 失败 → 默认浏览器。两者都失败 → errorReporter(可空)。
    /// </summary>
    void OpenWithChromeFallback(string path, Action<string, string, ErrorSeverity>? errorReporter = null);
}