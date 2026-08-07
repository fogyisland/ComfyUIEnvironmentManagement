using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.7.1: 纯逻辑测试 — IsReadyLine + ctor 防呆。
/// 不起真实进程,毫秒级跑完。
/// </summary>
public sealed class ProcessLauncherReadyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _projectRoot;

    public ProcessLauncherReadyTests()
    {
        _projectRoot = Path.Combine(Path.GetTempPath(), $"launcher-ready-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_projectRoot, recursive: true); } catch { }
    }

    private ProcessLauncher NewLauncher(int startupTimeoutSeconds = 600)
    {
        var envRepo = new EnvironmentRepository(_db.Factory);
        var procStateRepo = new ProcessStateRepository(_db.Factory);
        return new ProcessLauncher(
            _projectRoot, _db.Factory, envRepo, procStateRepo,
            logger: null, startupTimeoutSeconds);
    }

    // ===== IsReadyLine: 4 个标志串 + 大小写不敏感 =====

    [Fact]
    public void IsReadyLine_ToSeeTheGui_ReturnsTrue()
    {
        Assert.True(ProcessLauncher.IsReadyLine(
            "To see the GUI go to: http://127.0.0.1:8188"));
    }

    [Fact]
    public void IsReadyLine_StartingServer_ReturnsTrue()
    {
        Assert.True(ProcessLauncher.IsReadyLine("Starting server"));
    }

    [Fact]
    public void IsReadyLine_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(ProcessLauncher.IsReadyLine("to see the gui go to"));
    }

    [Fact]
    public void IsReadyLine_OrdinaryLogLine_ReturnsFalse()
    {
        Assert.False(ProcessLauncher.IsReadyLine("Total VRAM 24576 MB"));
    }

    [Fact]
    public void IsReadyLine_NullOrWhitespace_ReturnsFalse()
    {
        Assert.False(ProcessLauncher.IsReadyLine(null));
        Assert.False(ProcessLauncher.IsReadyLine(""));
        Assert.False(ProcessLauncher.IsReadyLine("   "));
    }

    // ===== ctor 防呆: <=0 回落到 600 =====

    [Fact]
    public void Ctor_ZeroTimeout_FallsBackToDefault()
    {
        var launcher = NewLauncher(startupTimeoutSeconds: 0);
        Assert.Equal(600, launcher.StartupTimeoutSeconds);
    }

    [Fact]
    public void Ctor_NegativeTimeout_FallsBackToDefault()
    {
        var launcher = NewLauncher(startupTimeoutSeconds: -5);
        Assert.Equal(600, launcher.StartupTimeoutSeconds);
    }

    [Fact]
    public void Ctor_DefaultTimeout_Is600()
    {
        var launcher = NewLauncher();
        Assert.Equal(600, launcher.StartupTimeoutSeconds);
    }
}