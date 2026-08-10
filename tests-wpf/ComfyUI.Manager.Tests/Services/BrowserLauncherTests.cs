using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.10 T2:BrowserLauncher 单元测试。
/// 通过 <see cref="BrowserLauncher.ProcessStartOverride"/> 注入确定性 Process.Start 行为,
/// 覆盖 Chrome 优先 / Chrome 失败回退 / 都失败 ErrorBanner Warn / 空 path no-op 4 条核心契约。
/// </summary>
public class BrowserLauncherTests
{
    /// <summary>
    /// 测试 1:Chrome 已装时,第一次启动尝试 FileName 应该是 chrome.exe(Chrome 优先)。
    /// Chrome 没装 → 该断言跳过(graceful degrade)。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_ResolvesChromeAndAttemptsChromeFirst()
    {
        var chromePath = BrowserLauncher.ResolveChromePath();
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
        Assert.Equal("http://127.0.0.1:8188", firstAttempt!.Arguments);
        if (chromePath is not null)
        {
            // Chrome 装了 → 第一次启动必须是 Chrome。
            Assert.Equal(chromePath, firstAttempt.FileName);
            Assert.True(
                firstAttempt.FileName!.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase),
                $"first attempt should target chrome.exe but was {firstAttempt.FileName}");
        }
        // Chrome 没装时,firstAttempt.FileName 应该是 path 本身(默认浏览器 fallback)。
    }

    /// <summary>
    /// 测试 2:Chrome 启动失败(返回 false)→ 回退默认浏览器。第二次启动 FileName 应等于 path。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_ChromeLaunchFails_FallsBackToDefaultBrowser()
    {
        var chromePath = BrowserLauncher.ResolveChromePath();
        var attempts = new List<ProcessStartInfo>();
        var callCount = 0;
        var launcher = new BrowserLauncher
        {
            ProcessStartOverride = psi =>
            {
                attempts.Add(psi);
                callCount++;
                // 第一次(Chrome)返回 false → 模拟启动失败;第二次(默认浏览器)返回 true。
                return callCount >= 2;
            },
        };

        launcher.OpenWithChromeFallback("http://127.0.0.1:8188");

        if (chromePath is not null)
        {
            // Chrome 装了 → 必须有 2 次启动尝试:Chrome + 默认浏览器。
            Assert.Equal(2, callCount);
            Assert.Equal(chromePath, attempts[0].FileName);
            Assert.Equal("http://127.0.0.1:8188", attempts[1].FileName);
        }
        else
        {
            // Chrome 没装 → 1 次启动尝试(默认浏览器),不能被第一次失败短路掉。
            Assert.True(callCount >= 1, "should attempt at least one launch");
        }
    }

    /// <summary>
    /// 测试 3:Chrome + 默认浏览器两次都失败 → errorReporter 调一次,code=BROWSER_OPEN_FAILED,
    /// severity=ErrorSeverity.Warn。
    /// </summary>
    [Fact]
    public void OpenWithChromeFallback_BothLaunchesFail_InvokesErrorReporter()
    {
        var chromePath = BrowserLauncher.ResolveChromePath();
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
    /// 测试 4:path 为 "" 或 null → 立即返回,Process.Start 不被调,errorReporter 不被调。
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
}