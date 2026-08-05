using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class RequirementsInstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public RequirementsInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"reqinstall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string root, string venvPath)
    {
        // 写一个假的 venv python 文件,避免 ResolveVenvPython 抛
        Directory.CreateDirectory(venvPath);
        var fakePy = Path.Combine(venvPath, "fake-python.exe");
        File.WriteAllText(fakePy, "");
        return new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            VenvPath = venvPath,
            PythonExecutable = fakePy,
            CustomNodesPath = Path.Combine(root, "nodes"),
            Port = 8188,
            Status = "stopped",
        };
    }

    private static void WriteRequirements(string root, params string[] lines)
    {
        File.WriteAllLines(Path.Combine(root, "requirements.txt"), lines);
    }

    [Fact]
    public void FilterTorchLines_StripsTorchPinnedAndCommentedLines()
    {
        var raw = new[]
        {
            "# top comment",
            "torch",
            "torch==2.1.0",
            "torch>=2.0",
            "  torchvision",
            "torchaudio ==1.0",
            "torchtext",
            "torchdata>=1.0",
            "",
            "SQLAlchemy",
            "transformers>=4.0",
            "einops",
            "# torch  -- this is also a comment",
        };
        var filtered = RequirementsInstaller.FilterTorchLines(raw);
        // 留下:top comment / 空行 / SQLAlchemy / transformers / einops / "torch ..." 注释行
        // 注意:`# torch  -- this is also a comment` 不在 strip 列表 — 它被识别成 torch 行
        // 因为正则 #?\s* 匹配。注释 + torch 名也是 torch 行,过滤掉。
        // 保留的应是 top comment / 空行 / 非 torch 依赖
        Assert.Contains("# top comment", filtered);
        Assert.Contains("", filtered);
        Assert.Contains("SQLAlchemy", filtered);
        Assert.Contains("transformers>=4.0", filtered);
        Assert.Contains("einops", filtered);

        // 过滤掉的:
        Assert.DoesNotContain(filtered, l => l.Trim().Equals("torch", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered, l => l.Trim().Equals("torch==2.1.0", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered, l => l.Trim().Equals("torchvision", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered, l => l.Trim().Equals("torchaudio ==1.0", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered, l => l.Trim().Equals("torchtext", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered, l => l.Trim().Equals("torchdata>=1.0", StringComparison.Ordinal));
        Assert.DoesNotContain(filtered, l => l.Trim().Equals("# torch  -- this is also a comment", StringComparison.Ordinal));
    }

    [Fact]
    public void FilterTorchLines_PreservesNonTorchPinnedLines()
    {
        var raw = new[]
        {
            "xformers==0.0.20",
            "Pillow",
            "numpy>=1.20",
        };
        var filtered = RequirementsInstaller.FilterTorchLines(raw);
        Assert.Equal(raw.Length, filtered.Count);
    }

    [Fact]
    public void IsInstalled_NoMarker_ReturnsFalse()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        Assert.False(RequirementsInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_MarkerExists_ReturnsTrue()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        File.WriteAllText(Path.Combine(_tempRoot, RequirementsInstaller.MarkerFileName), "");
        Assert.True(RequirementsInstaller.IsInstalled(env));
    }

    [Fact]
    public async Task InstallAsync_MissingRequirementsFile_ReturnsFailure()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.Reason);
        Assert.Contains("requirements.txt", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_PipSucceeds_WritesMarkerFileAndReturnsSuccess()
    {
        WriteRequirements(_tempRoot,
            "torch",  // 应被过滤
            "SQLAlchemy",
            "transformers",
            "einops");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(ExitCode: 0, WasCancelled: false);
        var logLines = new List<string>();
        var progress = new Progress<string>(line => logLines.Add(line));

        var result = await fake.InstallAsync(env, progress, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Cancelled);
        Assert.Equal(3, result.InstalledCount);  // 过滤后剩 3 个非 torch 包
        Assert.True(RequirementsInstaller.IsInstalled(env),
            "成功应写 marker 文件");
        Assert.Equal(1, fake.RunCount);
        // logLines 不必有内容(FakePip 不真输出),但 progress 至少传过 1 次
    }

    [Fact]
    public async Task InstallAsync_PipFails_ReturnsFailureWithReason()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy", "transformers");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(ExitCode: 1, WasCancelled: false);

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Contains("退出码 1", result.Reason);
        Assert.False(RequirementsInstaller.IsInstalled(env),
            "失败不应写 marker");
    }

    [Fact]
    public async Task InstallAsync_Cancelled_ReturnsCancelledTrue()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(ExitCode: 130, WasCancelled: true);

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.False(RequirementsInstaller.IsInstalled(env));
    }

    [Fact]
    public async Task InstallAsync_FilteredFileCleanedUp_OnSuccess()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(ExitCode: 0, WasCancelled: false);

        await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        var filteredPath = Path.Combine(_tempRoot, RequirementsInstaller.FilteredRequirementsFileName);
        Assert.False(File.Exists(filteredPath), "成功路径应清理 filtered 文件");
    }

    [Fact]
    public async Task InstallAsync_ArgNullEnv_Throws()
    {
        var fake = new FakeRequirementsInstaller();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            fake.InstallAsync(null!, logProgress: null, CancellationToken.None));
    }

    private sealed class FakeRequirementsInstaller : RequirementsInstaller
    {
        public PipResult NextResult { get; set; } = new(0, false);
        public int RunCount { get; private set; }
        public List<string> CapturedPipArgs { get; } = new();

        protected override Task<PipResult> RunPipAsync(
            string pythonExe,
            IReadOnlyList<string> pipArgs,
            Action<string> onLine,
            CancellationToken ct)
        {
            RunCount++;
            foreach (var a in pipArgs) CapturedPipArgs.Add(a);
            return Task.FromResult(NextResult);
        }
    }
}
