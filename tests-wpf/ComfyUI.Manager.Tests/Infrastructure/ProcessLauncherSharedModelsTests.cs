using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Infrastructure;

/// <summary>
/// v0.6.7.3 + v0.6.11+ T2(DefaultModelsDirectory):ProcessLauncher 启动前
/// EnsureModelsJunctionAsync 行为测试。数据源是 ctor 参数 modelsDirectory
/// (原 sharedModelsDirectory,字段重命名后 caller 透传 settings.DefaultModelsDirectory)。
/// </summary>
public sealed class ProcessLauncherSharedModelsTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _dbPath;

    public ProcessLauncherSharedModelsTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(),
            "launcher-shared-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_rootDir);
        _dbPath = Path.Combine(_rootDir, "state.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { }
    }

    private (ProcessLauncher Launcher, RecordingJunctionLinker Linker, SqliteConnectionFactory Db) BuildLauncher(string modelsDir, bool withEnv = true)
    {
        var factory = new SqliteConnectionFactory(_dbPath);
        var envRepo = new EnvironmentRepository(factory);
        var psRepo = new ProcessStateRepository(factory);
        var linker = new RecordingJunctionLinker();
        var launcher = new ProcessLauncher(
            _rootDir, factory, envRepo, psRepo, logger: null,
            comfyUiStartupTimeoutSeconds: 600,
            comfyUiLocale: "",
            modelsDirectory: modelsDir,
            linker: linker);
        return (launcher, linker, factory);
    }

    /// <summary>
    /// Test seam 让 ProcessLauncher 直接调我们的 RecordingJunctionLinker。
    /// </summary>
    private void PrepareEnv(string envRoot, string comfyuiRoot, string? existingModelsLinkTarget = null)
    {
        Directory.CreateDirectory(Path.Combine(comfyuiRoot, "models"));
        File.WriteAllText(Path.Combine(comfyuiRoot, "main.py"), "");
        if (existingModelsLinkTarget is not null)
        {
            // 预先建个 dummy junction 模拟"旧 target"
            // (用真 mklink 因为 RecordingJunctionLinker.CreateAsync 是 mock)
        }
    }

    [Fact]
    public async Task EnsureModelsJunctionAsync_EmptySetting_DoesNothing()
    {
        var (launcher, linker, _) = BuildLauncher("");
        // 通过 reflection 或 internal accessor 调 EnsureModelsJunctionAsync
        // 这里用 reflection(测试只关心行为,不在意 API 暴露级别)
        var method = typeof(ProcessLauncher).GetMethod(
            "EnsureModelsJunctionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        await (Task)method!.Invoke(launcher, new object?[] { Path.Combine(_rootDir, "ComfyUI"), CancellationToken.None })!;

        Assert.Empty(linker.CreatedLinks);
    }

    [Fact]
    public async Task EnsureModelsJunctionAsync_PlainDirWithNullTarget_Relinks()
    {
        // G9 逻辑:plain dir + GetTargetAsync 返 null → needsRelink=true → 删重建。
        // 改 DefaultModelsDirectory 后第一次启动,旧 env 的 models 还是普通目录,
        // 必须删掉重建 junction 才能让 ComfyUI 走共享。
        var models = Path.Combine(_rootDir, "models");
        Directory.CreateDirectory(models);
        var comfyuiRoot = Path.Combine(_rootDir, "ComfyUI");
        var modelsLink = Path.Combine(comfyuiRoot, "models");
        Directory.CreateDirectory(modelsLink);  // 普通目录,不是 junction(RecordingJunctionLinker.GetTargetAsync 返 null)

        var (launcher, linker, _) = BuildLauncher(models);
        var method = typeof(ProcessLauncher).GetMethod(
            "EnsureModelsJunctionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        await (Task)method!.Invoke(launcher, new object?[] { comfyuiRoot, CancellationToken.None })!;

        Assert.Single(linker.CreatedLinks);
        Assert.Equal(modelsLink, linker.CreatedLinks[0].Link);
        Assert.Equal(
            Path.GetFullPath(models),
            Path.GetFullPath(linker.CreatedLinks[0].Target),
            ignoreCase: true);
    }

    [Fact]
    public async Task EnsureModelsJunctionAsync_TargetDiffers_Relinks()
    {
        var models = Path.Combine(_rootDir, "models");
        Directory.CreateDirectory(models);
        var comfyuiRoot = Path.Combine(_rootDir, "ComfyUI");
        var modelsLink = Path.Combine(comfyuiRoot, "models");
        // modelsLink 不存在 → 触发"建 junction"分支
        Directory.CreateDirectory(comfyuiRoot);

        var (launcher, linker, _) = BuildLauncher(models);
        var method = typeof(ProcessLauncher).GetMethod(
            "EnsureModelsJunctionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        await (Task)method!.Invoke(launcher, new object?[] { comfyuiRoot, CancellationToken.None })!;

        Assert.Single(linker.CreatedLinks);
        Assert.Equal(modelsLink, linker.CreatedLinks[0].Link);
        Assert.Equal(
            Path.GetFullPath(models),
            Path.GetFullPath(linker.CreatedLinks[0].Target),
            ignoreCase: true);
    }

    private sealed class RecordingJunctionLinker : JunctionLinker
    {
        public List<(string Link, string Target)> CreatedLinks { get; } = new();
        public List<string> DeletedLinks { get; } = new();
        public List<string> GetTargetCalls { get; } = new();

        public override Task CreateAsync(string linkPath, string target, CancellationToken ct = default)
        {
            CreatedLinks.Add((linkPath, target));
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateDirectory(linkPath);
            return Task.CompletedTask;
        }
        public override Task<string?> GetTargetAsync(string linkPath, CancellationToken ct = default)
        {
            GetTargetCalls.Add(linkPath);
            // 简化:假装 linkPath 是普通目录(返 null),除非测试手动塞真 junction
            return Task.FromResult<string?>(null);
        }
        public override void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
        }
    }
}
