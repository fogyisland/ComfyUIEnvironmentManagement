using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.10 T2:Chrome 优先 fallback 默认浏览器 — 组件报告 / 打开浏览器按钮共享。
/// v0.6.17.1:fallback 链路扩成 Chrome → Edge → 默认浏览器。Edge 是 Windows 10/11
/// 自带必装,基本不会缺 — 保证"组件报告"按钮永远不报"打开失败"。Win10 早期版
/// 可能没 Edge,这时走默认浏览器(UseShellExecute=true)。
/// </summary>
public class BrowserLauncher : IBrowserLauncher
{
    /// <summary>
    /// Test seam — 单元测试通过该 Func 注入确定性 Process.Start 行为。
    /// 返回 true = 启动成功;返回 false = 模拟 Win32Exception(Chrome 缺失 / 损坏)。
    /// null = 走真实 <see cref="Process.Start(ProcessStartInfo)"/>。
    /// </summary>
    internal Func<ProcessStartInfo, bool>? ProcessStartOverride { get; set; }

    public void OpenWithChromeFallback(string path, Action<string, string, ErrorSeverity>? errorReporter = null)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            // 1) Chrome — 用户偏好优先
            var chrome = ResolveChromePath();
            if (chrome is not null)
            {
                if (TryStart(new ProcessStartInfo { FileName = chrome, Arguments = path, UseShellExecute = true }))
                {
                    return;
                }
                // Chrome 装在但启动失败 → 回退 Edge
            }

            // 2) Edge — Windows 自带,避免 Chrome 缺失时整条链路直接挂
            var edge = ResolveEdgePath();
            if (edge is not null)
            {
                if (TryStart(new ProcessStartInfo { FileName = edge, Arguments = path, UseShellExecute = true }))
                {
                    return;
                }
            }

            // 3) 系统默认浏览器 — UseShellExecute=true 走注册表
            if (TryStart(new ProcessStartInfo { FileName = path, UseShellExecute = true }))
            {
                return;
            }

            throw new InvalidOperationException("所有浏览器启动尝试均失败");
        }
        catch (Exception ex)
        {
            errorReporter?.Invoke("BROWSER_OPEN_FAILED", $"打开浏览器失败:{ex.Message}", ErrorSeverity.Warn);
        }
    }

    /// <summary>
    /// 真实启动 / 测试 override 二选一。override 返回 false 或真实 Process.Start 抛异常时返回 false。
    /// </summary>
    private bool TryStart(ProcessStartInfo psi)
    {
        if (ProcessStartOverride is not null) return ProcessStartOverride(psi);
        try
        {
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
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

    /// <summary>
    /// v0.6.17.1:Edge 候选路径 — Windows 10 1809+ / 11 必装。Edge 在 Program Files (x86)
    /// 而非 Program Files,路径固定 <c>Microsoft\Edge\Application\msedge.exe</c>。
    /// Win10 早期版(无 Edge)+ Win Server 没装时本方法返 null,走下一步默认浏览器。
    /// </summary>
    internal static string? ResolveEdgePath()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}