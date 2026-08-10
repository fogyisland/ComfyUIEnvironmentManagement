using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.10 T2:Chrome 优先 fallback 默认浏览器 — 组件报告 / 打开浏览器按钮共享。
/// 复用 EnvironmentListViewModel 原有 3 个 Chrome 候选路径。
/// </summary>
public class BrowserLauncher : IBrowserLauncher
{
    public void OpenWithChromeFallback(string path, Action<string, string, ErrorSeverity>? errorReporter = null)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var chrome = ResolveChromePath();
            if (chrome is not null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = chrome, Arguments = path, UseShellExecute = true });
                    return;
                }
                catch
                {
                    // Chrome 装在但启动失败 → 回退默认浏览器
                }
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            errorReporter?.Invoke("BROWSER_OPEN_FAILED", $"打开浏览器失败:{ex.Message}", ErrorSeverity.Warn);
        }
    }

    /// <summary>
    /// 复用 EnvironmentListViewModel 既有 3 个 Chrome 候选路径。
    /// internal static 走 InternalsVisibleTo 给测试用。
    /// </summary>
    internal static string? ResolveChromePath()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}