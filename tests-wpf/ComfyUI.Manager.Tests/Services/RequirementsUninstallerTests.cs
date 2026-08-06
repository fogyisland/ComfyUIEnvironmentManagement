using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v0.6.5.22 T2:RequirementsUninstaller — 跑 `pip uninstall -y -r &lt;filtered&gt;`
/// 然后删 marker。测试用 FakePipRunner 覆盖 RunPipAsync(跟
/// RequirementsInstallerTests.FakeRequirementsInstaller 同 pattern),不真跑 python。
/// </summary>
public sealed class RequirementsUninstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public RequirementsUninstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"requninstall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string? comfyuiSource = null)
    {
        var venvPath = Path.Combine(_tempRoot, "venv");
        Directory.CreateDirectory(venvPath);
        var fakePy = Path.Combine(venvPath, "fake-python.exe");
        File.WriteAllText(fakePy, "");
        return new Environment
        {
            Id = id,
            Name = id,
            RootPath = _tempRoot,
            ComfyuiSource = comfyuiSource ?? _tempRoot,
            VenvPath = venvPath,
            PythonExecutable = fakePy,
            CustomNodesPath = Path.Combine(_tempRoot, "nodes"),
            Port = 8188,
            Status = "stopped",
        };
    }

    private void WriteRequirements(params string[] lines)
        => File.WriteAllLines(Path.Combine(_tempRoot, "requirements.txt"), lines);

    private string MarkerPath => Path.Combine(_tempRoot, RequirementsUninstaller.MarkerFileName);

    private void WriteMarker()
        => File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

    [Fact]
    public async Task UninstallAsync_NullEnv_ReturnsFailureReason()
    {
        var fake = new FakePipRunner();

        var result = await fake.UninstallAsync(null!, logProgress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.AlreadyUninstalled);
        Assert.False(result.Cancelled);
        Assert.Equal("env 为空", result.Reason);
        Assert.Equal(0, fake.RunCount);
    }

    [Fact]
    public async Task UninstallAsync_NotInstalled_ReturnsAlreadyUninstalledTrue()
    {
        WriteRequirements("SQLAlchemy");
        var env = SeedEnv("env-a");
        // 没写 marker → 视为没装过
        Assert.False(RequirementsUninstaller.IsInstalled(env));
        var fake = new FakePipRunner();

        var result = await fake.UninstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.AlreadyUninstalled);
        Assert.False(result.Cancelled);
        Assert.Null(result.Reason);
        Assert.Equal(0, result.UninstalledCount);
        Assert.Equal(0, fake.RunCount);
    }

    [Fact]
    public async Task UninstallAsync_Installed_RunsPipUninstallWithFiltered()
    {
        WriteRequirements(
            "torch",           // 应被过滤(BED 管的)
            "torchvision",     // 应被过滤
            "SQLAlchemy",
            "transformers",
            "einops");
        var env = SeedEnv("env-a");
        WriteMarker();
        var fake = new FakePipRunner { NextResult = new PipResult(0, false) };

        var result = await fake.UninstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, fake.RunCount);

        var expectedFiltered = Path.Combine(
            _tempRoot, RequirementsUninstaller.FilteredRequirementsFileName);
        Assert.Equal(
            new[] { "uninstall", "-y", "-r", expectedFiltered, "--disable-pip-version-check" },
            fake.CapturedPipArgs);

        // filtered 内容排除 torch 系列,只剩 3 行
        Assert.NotNull(fake.CapturedFilteredContent);
        Assert.Contains("SQLAlchemy", fake.CapturedFilteredContent);
        Assert.DoesNotContain("torchvision", fake.CapturedFilteredContent);
        Assert.Equal(3, result.UninstalledCount);
    }

    [Fact]
    public async Task UninstallAsync_PipFails_KeepsMarker()
    {
        WriteRequirements("SQLAlchemy", "transformers");
        var env = SeedEnv("env-a");
        WriteMarker();
        var fake = new FakePipRunner { NextResult = new PipResult(1, false) };

        var result = await fake.UninstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Equal("pip 退出码 1", result.Reason);
        Assert.True(File.Exists(MarkerPath), "pip 失败不应删 marker");
        Assert.True(RequirementsUninstaller.IsInstalled(env));
    }

    [Fact]
    public async Task UninstallAsync_Success_DeletesMarker()
    {
        WriteRequirements("SQLAlchemy");
        var env = SeedEnv("env-a");
        WriteMarker();
        var fake = new FakePipRunner { NextResult = new PipResult(0, false) };

        var result = await fake.UninstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.AlreadyUninstalled);
        Assert.False(result.Cancelled);
        Assert.Null(result.Reason);
        Assert.False(File.Exists(MarkerPath), "成功应删 marker");
        Assert.False(RequirementsUninstaller.IsInstalled(env));

        var filteredPath = Path.Combine(
            _tempRoot, RequirementsUninstaller.FilteredRequirementsFileName);
        Assert.False(File.Exists(filteredPath), "成功应清理临时 filtered 文件");
    }

    /// <summary>
    /// Test seam:覆盖 RunPipAsync 捕获参数 + filtered 内容,不真起 python 进程。
    /// </summary>
    private sealed class FakePipRunner : RequirementsUninstaller
    {
        public PipResult NextResult { get; set; } = new(0, false);
        public int RunCount { get; private set; }
        public List<string> CapturedPipArgs { get; } = new();

        /// <summary>
        /// UninstallAsync 跑完 pip 后会清掉 filtered 临时文件,所以在这里读一次存住。
        /// </summary>
        public string? CapturedFilteredContent { get; private set; }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe,
            IReadOnlyList<string> pipArgs,
            Action<string> onLine,
            CancellationToken ct)
        {
            RunCount++;
            CapturedPipArgs.AddRange(pipArgs);

            var asList = pipArgs as IList<string> ?? pipArgs.ToList();
            var rIdx = asList.IndexOf("-r");
            if (rIdx >= 0 && rIdx + 1 < asList.Count)
            {
                var p = asList[rIdx + 1];
                if (File.Exists(p)) CapturedFilteredContent = File.ReadAllText(p);
            }
            return Task.FromResult(NextResult);
        }
    }
}
