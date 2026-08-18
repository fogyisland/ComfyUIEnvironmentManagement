using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.10 T2:BrowserLauncher 单元测试。
/// 通过 <see cref="BrowserLauncher.ProcessStartOverride"/> 注入确定性 Process.Start 行为,
/// 覆盖 Chrome 优先 / Chrome 失败回退 Edge / Edge 失败回退默认浏览器 / 都失败 ErrorBanner Warn / 空 path no-op 5 条核心契约。
/// v0.6.17.1:fallback 链路扩成 Chrome → Edge → 默认浏览器(Edge 是 Win10/11 必装,
/// 保证"组件报告"按钮在没装 Chrome 的机器上也不会报"打开失败")。
/// </summary>
public class BrowserLauncherTests
{
    /// <summary>
    /// 测试 1:Chrome 已装且能启动 → 第一次启动必须是 chrome.exe(Chrome 优先),只调一次。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_ResolvesChromeAndAttemptsChromeFirst()
    {
        var chromePath = BrowserLauncher.ResolveChromePath();
        ProcessStartInfo? firstAttempt = null;
        var callCount = 0;
        var launcher = new BrowserLauncher
        {
            ProcessStartOverride = psi =>
            {
                callCount++;
                if (firstAttempt is null) firstAttempt = psi;
                return true;
            },
        };

        launcher.OpenWithChromeFallback("http://127.0.0.1:8188");

        Assert.NotNull(firstAttempt);
        if (chromePath is not null)
        {
            // Chrome 装了 → 第一次启动必须是 Chrome(无论 Edge 装没装)。
            Assert.True(
                firstAttempt!.FileName!.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase),
                $"first attempt should target chrome.exe but was {firstAttempt!.FileName}");
            Assert.Equal(1, callCount);
        }
        // Chrome 没装 → 第一次尝试是 Edge(若装) 或默认浏览器。Edge 测试在 Test 2/3 覆盖。
    }

    /// <summary>
    /// 测试 2:Chrome 装了但启动失败 → 第二次尝试 Edge(若装);Edge 没装 → 默认浏览器。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_ChromeLaunchFails_FallsBackToEdgeOrDefault()
    {
        var chromePath = BrowserLauncher.ResolveChromePath();
        var edgePath = BrowserLauncher.ResolveEdgePath();
        var attempts = new List<ProcessStartInfo>();
        var launcher = new BrowserLauncher
        {
            ProcessStartOverride = psi =>
            {
                attempts.Add(psi);
                // 让 Edge(若存在)+默认浏览器成功,Chrome 失败 — 模拟 Chrome 损坏场景
                var isChrome = chromePath is not null && psi.FileName == chromePath;
                return !isChrome;
            },
        };

        launcher.OpenWithChromeFallback("http://127.0.0.1:8188");

        if (chromePath is null)
        {
            // Chrome 没装,本测试不适用(由 Test 1/3 覆盖)。
            return;
        }

        // Chrome 装了 → 第一次必须是 Chrome
        Assert.Equal(chromePath, attempts[0].FileName);

        if (edgePath is not null)
        {
            // Edge 装了 → 第二次是 Edge,总共 2 次尝试
            Assert.Equal(2, attempts.Count);
            Assert.Equal(edgePath, attempts[1].FileName);
        }
        else
        {
            // Edge 没装 → 第二次是默认浏览器,path 作为 FileName
            Assert.Equal(2, attempts.Count);
            Assert.Equal("http://127.0.0.1:8188", attempts[1].FileName);
        }
    }

    /// <summary>
    /// 测试 3:Chrome 没装 + Edge 装了 → 第一次启动尝试是 Edge(msedge.exe)。
    /// 这是 v0.6.17.1 新链路的关键场景 — 没 Chrome 时直接走 Edge 不报失败。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_ChromeMissing_EdgePresent_UsesEdge()
    {
        var chromePath = BrowserLauncher.ResolveChromePath();
        var edgePath = BrowserLauncher.ResolveEdgePath();
        if (chromePath is not null || edgePath is null)
        {
            // 需要"Chrome 没装 + Edge 装了"才适用,否则跳过。
            return;
        }

        ProcessStartInfo? firstAttempt = null;
        var launcher = new BrowserLauncher
        {
            ProcessStartOverride = psi =>
            {
                if (firstAttempt is null) firstAttempt = psi;
                return true;
            },
        };

        launcher.OpenWithChromeFallback("http://127.0.0.1:8188");

        Assert.NotNull(firstAttempt);
        Assert.Equal(edgePath, firstAttempt!.FileName);
    }

    /// <summary>
    /// 测试 4:Chrome + Edge 都没装 → 第一次(也是唯一)启动是默认浏览器(path 作为 FileName)。
    /// UseShellExecute=true 让 Windows 走注册表关联。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_ChromeAndEdgeMissing_FallsBackToDefaultBrowser()
    {
        var chromePath = BrowserLauncher.ResolveChromePath();
        var edgePath = BrowserLauncher.ResolveEdgePath();
        if (chromePath is not null || edgePath is not null)
        {
            // 需要"都没装"才适用。
            return;
        }

        ProcessStartInfo? firstAttempt = null;
        var launcher = new BrowserLauncher
        {
            ProcessStartOverride = psi =>
            {
                if (firstAttempt is null) firstAttempt = psi;
                return true;
            },
        };

        launcher.OpenWithChromeFallback("http://127.0.0.1:8188");

        Assert.NotNull(firstAttempt);
        // 默认浏览器 fallback:path 作为 FileName,Arguments 不设(UseShellExecute=true 走注册表)。
        Assert.Equal("http://127.0.0.1:8188", firstAttempt!.FileName);
    }

    /// <summary>
    /// 测试 5:Chrome + Edge + 默认浏览器全部失败 → errorReporter 调一次,code=BROWSER_OPEN_FAILED,
    /// severity=ErrorSeverity.Warn。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_AllLaunchesFail_InvokesErrorReporter()
    {
        string? capturedCode = null;
        string? capturedMessage = null;
        ErrorSeverity? capturedSeverity = null;
        var reporterCallCount = 0;
        Action<string, string, ErrorSeverity> errorReporter = (code, message, severity) =>
        {
            reporterCallCount++;
            capturedCode = code;
            capturedMessage = message;
            capturedSeverity = severity;
        };

        var launcher = new BrowserLauncher
        {
            // 强制所有启动都失败。
            ProcessStartOverride = _ => false,
        };

        launcher.OpenWithChromeFallback("http://127.0.0.1:8188", errorReporter);

        Assert.Equal(1, reporterCallCount);
        Assert.Equal("BROWSER_OPEN_FAILED", capturedCode);
        Assert.Equal(ErrorSeverity.Warn, capturedSeverity);
        Assert.False(string.IsNullOrEmpty(capturedMessage));
    }

    /// <summary>
    /// 测试 6:path 为 "" 或 null → 立即返回,Process.Start 不被调,errorReporter 不被调。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_EmptyPath_DoesNotInvokeErrorReporter_AndNoLaunch()
    {
        var callCount = 0;
        var reporterCallCount = 0;
        Action<string, string, ErrorSeverity> errorReporter = (_, _, _) => reporterCallCount++;

        var launcher = new BrowserLauncher
        {
            ProcessStartOverride = _ =>
            {
                callCount++;
                return true;
            },
        };

        launcher.OpenWithChromeFallback("", errorReporter);
        launcher.OpenWithChromeFallback(null!, errorReporter);

        Assert.Equal(0, callCount);
        Assert.Equal(0, reporterCallCount);
    }

    /// <summary>
    /// 测试 7:ResolveEdgePath 返回的是真实存在的 msedge.exe 文件路径(若 Edge 装了)。
    /// </summary>
    [Fact]
    public void ResolveEdgePath_WhenEdgeInstalled_ReturnsExistingFile()
    {
        var edgePath = BrowserLauncher.ResolveEdgePath();
        if (edgePath is null)
        {
            // Edge 没装,跳过(避免在没 Edge 的 CI 上失败)。
            return;
        }
        Assert.True(File.Exists(edgePath), $"resolved edge path should exist: {edgePath}");
        Assert.EndsWith("msedge.exe", edgePath, StringComparison.OrdinalIgnoreCase);
    }
}