using System;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.10 T2:BrowserLauncher 单元测试。
/// BrowserLauncher 是单例无状态(只用 ResolveChromePath 静态 + Process.Start),
/// 不抽 virtual seam — 测试只覆盖"不抛"行为:Chrome 找不到走默认浏览器 / 默认浏览器
/// 失败走 errorReporter / 无 path no-op / ResolveChromePath 不抛。深度的"Chrome 失败
/// 走 ErrorBanner" 行为测试靠 EnvironmentListViewModel.OpenBrowser / ReportComponents
/// 集成测试覆盖(既有 6 个测试用例继续通过)。
/// </summary>
public class BrowserLauncherTests
{
    [Fact]
    public void OpenWithChromeFallback_EmptyPath_ReturnsImmediately()
    {
        var launcher = new BrowserLauncher();
        // 空 / null path 立即返回,errorReporter 不会被调。
        var ex = Record.Exception(() => launcher.OpenWithChromeFallback(""));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenWithChromeFallback_NonEmptyPath_DoesNotThrow()
    {
        var launcher = new BrowserLauncher();
        // 不断言哪个浏览器被启(测试机可能没 Chrome,可能没默认浏览器 association,
        // Process.Start 可能因为 shell 不在 CI / STA context 抛 Win32Exception)—
        // 只断言"自身逻辑不抛未处理异常"(BrowserLauncher 内部 catch 已经兜住
        // 任何 Exception 并 Invoke errorReporter)。
        var ex = Record.Exception(() =>
            launcher.OpenWithChromeFallback("http://127.0.0.1:8188"));
        Assert.Null(ex);
    }

    [Fact]
    public void OpenWithChromeFallback_WithNullErrorReporter_DoesNotThrow()
    {
        var launcher = new BrowserLauncher();
        // 显式传 null errorReporter — 即便浏览器启动失败也不该 NRE。
        var ex = Record.Exception(() =>
            launcher.OpenWithChromeFallback("https://example.com", null));
        Assert.Null(ex);
    }

    [Fact]
    public void ResolveChromePath_DoesNotThrow_AndReturnsValidValueOrNull()
    {
        // ResolveChromePath 跑 3 个候选路径 File.Exists 检查 — 不抛。
        // 返回值:Chrome 装了 → 命中某条;没装 → null。两种结果都合法。
        var chrome = BrowserLauncher.ResolveChromePath();
        if (chrome is not null)
        {
            Assert.True(
                chrome.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase),
                $"chrome path should end with chrome.exe but was {chrome}");
        }
        // 不抛已通过(测试到这里没 Record.Exception)。
    }
}