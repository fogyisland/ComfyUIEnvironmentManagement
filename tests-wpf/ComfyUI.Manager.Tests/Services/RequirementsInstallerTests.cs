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

    private Environment SeedEnv(string id, string root, string venvPath, string? comfyuiSource = null)
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
            // 默认 ComfyuiSource = root(模拟 shared 布局,ComfyUI 源路径 =
            // env.RootPath — 测试 fixture 在该目录下建 requirements.txt)。
            // 独立布局测试可以显式覆盖 comfyuiSource。
            ComfyuiSource = comfyuiSource ?? root,
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

    // ----- v0.6.5.17 hotfix:requirements.txt 候选路径(ComfyuiSource 优先) -----

    [Fact]
    public async Task InstallAsync_IndependentLayout_PrefersComfyuiSourceRequirementsTxt()
    {
        // 独立布局:ComfyuiSource = <env-root>/ComfyUI,文件写在 <env-root>/ComfyUI/requirements.txt
        // 跟根目录同名文件不混淆 — 候选路径应当只解析到 ComfyuiSource 那份。
        var rootComfyui = Path.Combine(_tempRoot, "ComfyUI");
        Directory.CreateDirectory(rootComfyui);
        File.WriteAllLines(Path.Combine(rootComfyui, "requirements.txt"),
            new[] { "FROM_COMFYUI_SOURCE" });
        // 根目录放一个无关文件,确保候选不会错选它。
        File.WriteAllLines(Path.Combine(_tempRoot, "requirements.txt"),
            new[] { "FROM_ROOT_FALLBACK" });
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"),
            comfyuiSource: rootComfyui);
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);

        await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.NotEmpty(fake.CapturedPipArgs);
        // pip args 包含 -r <path>,path 是 filtered 文件。
        var rIndex = fake.CapturedPipArgs.IndexOf("-r");
        Assert.True(rIndex >= 0 && rIndex + 1 < fake.CapturedPipArgs.Count);
        var rPath = fake.CapturedPipArgs[rIndex + 1];
        var filteredPath = Path.Combine(_tempRoot, RequirementsInstaller.FilteredRequirementsFileName);
        Assert.Equal(filteredPath, rPath);
        // filtered 内容来自 ComfyuiSource 那份(FROM_COMFYUI_SOURCE),
        // 不是根目录那份(FROM_ROOT_FALLBACK)。
        Assert.Contains("FROM_COMFYUI_SOURCE", fake.CapturedFilteredContent);
        Assert.DoesNotContain("FROM_ROOT_FALLBACK", fake.CapturedFilteredContent);
        // 过滤文件已清理
        Assert.False(File.Exists(filteredPath));
    }

    [Fact]
    public async Task InstallAsync_NoComfyuiSource_FallsBackToRootPath()
    {
        // 老 env(ComfyuiSource 为空)— fallback 到 <env-root>/requirements.txt
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;  // 清掉默认
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(RequirementsInstaller.IsInstalled(env));
    }

    [Fact]
    public async Task InstallAsync_NoComfyuiSource_FallsBackToRootComfyUIDir()
    {
        // 老 env(ComfyuiSource 为空)但 <env-root>/ComfyUI/requirements.txt 存在
        // → 应当 fallback 到那里,不是根目录。
        var rootComfyui = Path.Combine(_tempRoot, "ComfyUI");
        Directory.CreateDirectory(rootComfyui);
        File.WriteAllLines(Path.Combine(rootComfyui, "requirements.txt"),
            new[] { "FROM_ROOT_COMFYUI_DIR" });
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);

        await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.NotEmpty(fake.CapturedPipArgs);
        var rIndex = fake.CapturedPipArgs.IndexOf("-r");
        var rPath = fake.CapturedPipArgs[rIndex + 1];
        var filteredPath = Path.Combine(_tempRoot, RequirementsInstaller.FilteredRequirementsFileName);
        Assert.Equal(filteredPath, rPath);
        Assert.Contains("FROM_ROOT_COMFYUI_DIR", fake.CapturedFilteredContent);
    }

    [Fact]
    public async Task InstallAsync_NoRequirementsAnywhere_ListsAllTriedPathsInError()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
        // 错误信息列出所有尝试路径,方便用户诊断(env.ComfyuiSource +
        // <env-root>/ComfyUI/requirements.txt + <env-root>/requirements.txt)
        Assert.Contains("requirements.txt", result.Reason);
        Assert.Contains("ComfyUI", result.Reason);
    }

    [Fact]
    public void ResolveRequirementsCandidates_ComfyuiSourceFirst()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"),
            comfyuiSource: Path.Combine(_tempRoot, "ComfyUI"));

        var candidates = RequirementsInstaller.ResolveRequirementsCandidates(env);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(Path.Combine(_tempRoot, "ComfyUI", "requirements.txt"), candidates[0]);
        Assert.Equal(Path.Combine(_tempRoot, "ComfyUI", "requirements.txt"), candidates[1]);
        Assert.Equal(Path.Combine(_tempRoot, "requirements.txt"), candidates[2]);
    }

    [Fact]
    public void ResolveRequirementsCandidates_NoComfyuiSource_OnlyRootPaths()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;

        var candidates = RequirementsInstaller.ResolveRequirementsCandidates(env);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(Path.Combine(_tempRoot, "ComfyUI", "requirements.txt"), candidates[0]);
        Assert.Equal(Path.Combine(_tempRoot, "requirements.txt"), candidates[1]);
    }

    private sealed class FakeRequirementsInstaller : RequirementsInstaller
    {
        public PipResult NextResult { get; set; } = new(0, false);
        public int RunCount { get; private set; }
        public List<string> CapturedPipArgs { get; } = new();

        /// <summary>
        /// 跑 pip 时(InstallAsync 已写完 filtered 文件)读取并捕获,
        /// 让测试能验证 InstallAsync 选了哪个源的 requirements.txt。
        /// FakeInstaller 不真跑 pip,InstallAsync 后续会清掉 filtered 文件,
        /// 所以这里读一次就存住。
        /// </summary>
        public string? CapturedFilteredContent { get; private set; }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe,
            IReadOnlyList<string> pipArgs,
            Action<string> onLine,
            CancellationToken ct)
        {
            RunCount++;
            foreach (var a in pipArgs) CapturedPipArgs.Add(a);
            // pipArgs 是 ["install", "-r", filteredPath, ...],IReadOnlyList 没 IndexOf,转成 List。
            var asList = pipArgs as IList<string> ?? pipArgs.ToList();
            var rIdx = asList.IndexOf("-r");
            if (rIdx >= 0 && rIdx + 1 < asList.Count)
            {
                var p = asList[rIdx + 1];
                if (File.Exists(p))
                {
                    CapturedFilteredContent = File.ReadAllText(p);
                }
            }
            return Task.FromResult(NextResult);
        }
    }
}
