using System;
using System.IO;
using ComfyUI.Manager;
using Xunit;

namespace ComfyUI.Manager.Tests.Data;

/// <summary>
/// v1.0.0.x bug fix unit tests:dev 模式 <see cref="App.ResolveDevProjectRoot"/> 必须把
/// <c>bin/Debug/net8.0-windows/ComfyUI.Manager.exe</c> 解析回真项目根(否则污染
/// <c>config/</c> + <c>.manager/</c>),release publish 模式(旁边没 src-wpf/)保持原行为
/// fallback 回 exe 目录。
/// </summary>
public sealed class ResolveDevProjectRootTests : IDisposable
{
    private readonly string _root;

    public ResolveDevProjectRootTests()
    {
        // 临时造一个 fake 项目根(里面放 src-wpf/ 子目录)
        _root = Path.Combine(Path.GetTempPath(), "resolve-dev-root-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "src-wpf"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DevBinDebug_WalksUpToProjectRoot()
    {
        // dev 模式真实路径:<root>/src-wpf/.../bin/Debug/net8.0-windows/ComfyUI.Manager.exe
        var bin = Path.Combine(_root, "src-wpf", "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "ComfyUI.Manager.exe");
        File.WriteAllText(exe, "");

        var result = App.ResolveDevProjectRoot(exe);

        Assert.Equal(_root, result);
    }

    [Fact]
    public void DevBinDebugDeepSubdir_StillWalksUp()
    {
        // 即使 exe 在更深子目录(例如 self-contained runtime subdir),walk-up 仍生效
        var deep = Path.Combine(_root, "src-wpf", "bin", "Debug", "net8.0-windows", "runtimes", "win-x64", "native");
        Directory.CreateDirectory(deep);
        var exe = Path.Combine(deep, "ComfyUI.Manager.exe");
        File.WriteAllText(exe, "");

        var result = App.ResolveDevProjectRoot(exe);

        Assert.Equal(_root, result);
    }

    [Fact]
    public void ReleasePublish_NoSrcWpfAbove_FallsBackToExeDir()
    {
        // release publish 必须用**完全隔离**的目录(本测试 fixture 的 _root 含 src-wpf/,
        // walk-up 会找到 _root 当 projectRoot 干扰断言)— 用 Path.GetTempPath() 下一层
        // 独立 random dir,上方**无任何 src-wpf/ 子目录**。
        var publishRoot = Path.Combine(Path.GetTempPath(), "release-publish-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(publishRoot);
        try
        {
            var exe = Path.Combine(publishRoot, "ComfyUI.Manager.exe");
            File.WriteAllText(exe, "");

            var result = App.ResolveDevProjectRoot(exe);

            // exe 上面无 src-wpf/(8 层 walk 内)→ fallback 到 exe 目录(publish 预期行为)
            Assert.Equal(publishRoot, result);
        }
        finally { try { Directory.Delete(publishRoot, recursive: true); } catch { } }
    }

    [Fact]
    public void NoSrcWpfFoundAnywhere_FallsBackToExeDir()
    {
        // exe 在 C:\SomeRandomApp\,且此路径上方没有任何 src-wpf/(比如用户 release
        // 没标 publish 子目录的边缘场景)— 8 层 walk 没找到 → 原行为不变
        var standAlone = Path.Combine(Path.GetTempPath(), "standalone-app-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(standAlone);
        try
        {
            var exe = Path.Combine(standAlone, "ComfyUI.Manager.exe");
            File.WriteAllText(exe, "");

            var result = App.ResolveDevProjectRoot(exe);

            // %TEMP%/standalone-app-XXX 没有 src-wpf/ 上方 → fallback 到 standalone 目录
            Assert.Equal(standAlone, result);
        }
        finally { try { Directory.Delete(standAlone, recursive: true); } catch { } }
    }

    [Fact]
    public void NullProcessPath_FallsBackToAppContextBaseDirectory()
    {
        // 极端情况:Environment.ProcessPath 拿不到(null/empty)→ 用 AppContext.BaseDirectory
        var result = App.ResolveDevProjectRoot(null);

        Assert.False(string.IsNullOrEmpty(result));
        // 不抛异常 + 返非空值就够 — AppContext.BaseDirectory 在测试环境指向 bin/...
    }

    [Fact]
    public void EmptyProcessPath_FallsBackToAppContextBaseDirectory()
    {
        var result = App.ResolveDevProjectRoot("");

        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void ProjectRootAtDeeperLevel_StillFindsIt()
    {
        // 项目根再深一层:<root>/subA/subB/src-wpf/ + bin/Debug/...
        var deeper = Path.Combine(_root, "subA", "subB");
        Directory.CreateDirectory(Path.Combine(deeper, "src-wpf"));
        var bin = Path.Combine(deeper, "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "ComfyUI.Manager.exe");
        File.WriteAllText(exe, "");

        var result = App.ResolveDevProjectRoot(exe);

        Assert.Equal(deeper, result);
    }
}