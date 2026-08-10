using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

public sealed class RequirementsFileInstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public RequirementsFileInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"reqfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void FilterTorchLines_StripsTorchFamilyLines()
    {
        var raw = new[] { "torch", "torch==2.1.0", "  torchvision", "torchaudio", "SQLAlchemy", "einops" };
        var filtered = RequirementsFileInstaller.FilterTorchLines(raw);
        Assert.Contains("SQLAlchemy", filtered);
        Assert.Contains("einops", filtered);
        Assert.DoesNotContain(filtered, l => l.Trim().StartsWith("torch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FilterTorchLines_PreservesCommentsAndBlankLines()
    {
        var raw = new[] { "# top comment", "", "  ", "transformers" };
        var filtered = RequirementsFileInstaller.FilterTorchLines(raw);
        Assert.Equal(4, filtered.Count);
    }

    [Fact]
    public async Task InstallAsync_MissingRequirementsFile_ReturnsFailure()
    {
        var installer = new RequirementsFileInstaller();
        var missingPath = Path.Combine(_tempRoot, "nope-requirements.txt");
        var filteredPath = Path.Combine(_tempRoot, RequirementsFileInstaller.FilteredRequirementsFileName);

        var result = await installer.InstallAsync(
            missingPath, filteredPath, "ignored-python", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("不存在", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_PipSucceeds_WritesFilteredFileThenCleansUp()
    {
        var reqPath = Path.Combine(_tempRoot, "requirements.txt");
        File.WriteAllLines(reqPath, new[] { "torch", "SQLAlchemy" });
        var filteredPath = Path.Combine(_tempRoot, RequirementsFileInstaller.FilteredRequirementsFileName);

        // 装 venv python 占位文件 + fake git-style 跑 pip — 这里直接调 InstallAsync,
        // 它内部跑真 python(测试机器上有 python 即可),所以 skip 缺失。
        var pyExe = FindPython();
        if (pyExe is null) return;  // skip if python missing

        var installer = new RequirementsFileInstaller();
        var result = await installer.InstallAsync(
            reqPath, filteredPath, pyExe, line => { }, CancellationToken.None);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.Equal(1, result.InstalledCount);  // torch stripped
        Assert.False(File.Exists(filteredPath), "filtered file 应被清理");
    }

    // ===== v0.6.11++ pip mirror passthrough (G3: lazy via Func<string?>) =====

    [Fact]
    public void BuildPipArgs_ResolveFuncNull_ReturnsEmpty()
    {
        var args = RequirementsFileInstaller.BuildPipArgs(resolveIndexUrl: null);
        Assert.Empty(args);
    }

    [Fact]
    public void BuildPipArgs_ResolveFuncReturnsUrl_AppendsIndexUrlPair()
    {
        var args = RequirementsFileInstaller.BuildPipArgs(
            () => "https://pypi.tuna.tsinghua.edu.cn/simple");
        Assert.Equal(2, args.Count);
        Assert.Equal("--index-url", args[0]);
        Assert.Equal("https://pypi.tuna.tsinghua.edu.cn/simple", args[1]);
    }

    [Fact]
    public void BuildPipArgs_ResolveFuncReturnsNull_ReturnsEmpty()
    {
        // Func 存在但返 null(走官方 / 选 custom 但 URL 空)→ 不拼 --index-url
        var args = RequirementsFileInstaller.BuildPipArgs(() => null);
        Assert.Empty(args);
    }

    [Fact]
    public void BuildPipArgs_ResolveFuncInvokedEachCall_NotCached()
    {
        // G3 强约束:每次 BuildPipArgs 调用都重求值 Func(不缓存),
        // 所以 Settings 在调用之间改值能立即生效。
        int callCount = 0;
        var args1 = RequirementsFileInstaller.BuildPipArgs(() => { callCount++; return "https://first"; });
        var args2 = RequirementsFileInstaller.BuildPipArgs(() => { callCount++; return "https://second"; });
        Assert.Equal("https://first", args1[1]);
        Assert.Equal("https://second", args2[1]);
        Assert.Equal(2, callCount);
    }

    private static string? FindPython()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        foreach (var c in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c, Arguments = "--version",
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p is null) continue;
                p.WaitForExit(2000);
                if (p.ExitCode == 0) return c;
            }
            catch { }
        }
        return null;
    }
}
