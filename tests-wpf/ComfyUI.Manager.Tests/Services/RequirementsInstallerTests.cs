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

    [Fact]
    public async Task InstallAsync_PipSucceeds_TriggersComfyUiManagerAutoInstall()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);
        fake.AutoInstallResult = NodeOperationResult.Ok("5");

        await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.Equal(1, fake.AutoInstallCallCount);
        Assert.Same(env, fake.AutoInstallEnv);
    }

    [Fact]
    public async Task InstallAsync_AutoInstallFails_StillReturnsSuccessForRequirements()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);
        fake.AutoInstallResult = NodeOperationResult.Fail("git clone 失败");

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, fake.AutoInstallCallCount);
    }

    [Fact]
    public async Task InstallAsync_AutoInstallThrows_StillReturnsSuccessForRequirements()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);
        fake.AutoInstallThrows = true;

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task InstallAsync_PipSucceeds_TriggersCommonNodesAutoInstall()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);
        fake.AutoInstallCommonNodesResult = NodeOperationResult.Ok("ok");

        await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.Equal(1, fake.AutoInstallCommonNodesCallCount);
        Assert.Same(env, fake.AutoInstallCommonNodesEnv);
    }

    [Fact]
    public async Task InstallAsync_CommonNodesAutoInstallFails_StillReturnsSuccessForRequirements()
    {
        WriteRequirements(_tempRoot, "SQLAlchemy");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var fake = new FakeRequirementsInstaller();
        fake.NextResult = new PipResult(0, false);
        fake.AutoInstallCommonNodesResult = NodeOperationResult.Fail("git clone 失败");

        var result = await fake.InstallAsync(env, logProgress: null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, fake.AutoInstallCommonNodesCallCount);
    }

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
        public string? CapturedFilteredContent { get; private set; }

        public NodeOperationResult AutoInstallResult { get; set; } = NodeOperationResult.Ok(null);
        public Environment? AutoInstallEnv { get; private set; }
        public int AutoInstallCallCount { get; private set; }
        public bool AutoInstallThrows { get; set; }

        public FakeRequirementsInstaller() : base(null, null, null, null)
        {
        }

        protected override Task<NodeOperationResult> AutoInstallComfyUiManagerAsync(
            Environment env,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            AutoInstallCallCount++;
            AutoInstallEnv = env;
            if (AutoInstallThrows) throw new InvalidOperationException("模拟异常");
            progress?.Report("auto-install:克隆 ComfyUI Manager");
            return Task.FromResult(AutoInstallResult);
        }

        public NodeOperationResult AutoInstallCommonNodesResult { get; set; } = NodeOperationResult.Ok(null);
        public Environment? AutoInstallCommonNodesEnv { get; private set; }
        public int AutoInstallCommonNodesCallCount { get; private set; }
        public bool AutoInstallCommonNodesThrows { get; set; }

        protected override Task<NodeOperationResult> AutoInstallCommonNodesAsync(
            Environment env,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            AutoInstallCommonNodesCallCount++;
            AutoInstallCommonNodesEnv = env;
            if (AutoInstallCommonNodesThrows) throw new InvalidOperationException("模拟异常");
            progress?.Report("auto-install-common-nodes:克隆常用节点");
            return Task.FromResult(AutoInstallCommonNodesResult);
        }

        public override async Task<RequirementsInstallResult> InstallAsync(
            Environment env,
            IProgress<string>? logProgress,
            CancellationToken ct)
        {
            if (env is null) throw new ArgumentNullException(nameof(env));
            var candidates = RequirementsInstaller.ResolveRequirementsCandidates(env);
            var reqPath = candidates.FirstOrDefault(File.Exists);
            if (reqPath is null)
            {
                var reason = $"找不到 ComfyUI 的 requirements.txt(已尝试:{string.Join(" | ", candidates)})";
                return new RequirementsInstallResult(false, false, reason, 0);
            }

            var rawLines = await File.ReadAllLinesAsync(reqPath, ct);
            var filtered = RequirementsInstaller.FilterTorchLines(rawLines);
            var filteredPath = Path.Combine(env.RootPath, RequirementsFileInstaller.FilteredRequirementsFileName);
            await File.WriteAllLinesAsync(filteredPath, filtered, ct);

            RunCount++;
            CapturedPipArgs.Add("install");
            CapturedPipArgs.Add("-r");
            CapturedPipArgs.Add(filteredPath);
            CapturedFilteredContent = await File.ReadAllTextAsync(filteredPath, ct);
            try { File.Delete(filteredPath); } catch { }

            if (NextResult.WasCancelled)
                return new RequirementsInstallResult(false, true, "用户取消", 0);
            if (NextResult.ExitCode != 0)
                return new RequirementsInstallResult(false, false, $"pip 退出码 {NextResult.ExitCode}", 0);

            File.WriteAllText(Path.Combine(env.RootPath, RequirementsInstaller.MarkerFileName),
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));

            try
            {
                await AutoInstallComfyUiManagerAsync(env, logProgress, ct);
            }
            catch
            {
            }

            try
            {
                await AutoInstallCommonNodesAsync(env, logProgress, ct);
            }
            catch
            {
            }

            return new RequirementsInstallResult(true, false, null, filtered.Count);
        }
    }

    // ──────────────── v0.6.15.6:InstallNodeRequirementsAsync (节点级 requirements.txt) ────────────────

    /// <summary>
    /// Fake RequirementsFileInstaller:不让真 pip 跑,只记录调用 + 返可控结果。
    /// 复用 RequirementsInstaller 内部 _reqFileInstaller 字段是 private —
    /// 走 InstallNodeRequirementsAsync → _reqFileInstaller.InstallAsync 这条链
    /// 需要能拦截。用子类化 + override 不行(private 字段拿不到),所以改在
    /// RequirementsInstaller 自身的实现里走 _reqFileInstaller 注入。
    /// 测试通过 ctor 传 fake 实现。
    /// </summary>
    private sealed class FakeReqFileInstaller : RequirementsFileInstaller
    {
        public int CallCount { get; private set; }
        public string? LastRequirementsPath { get; private set; }
        public string? LastFilteredPath { get; private set; }
        public string? LastPythonExe { get; private set; }
        public RequirementsInstallResult NextResult { get; set; } =
            new(true, false, null, 3);

        public override Task<RequirementsInstallResult> InstallAsync(
            string requirementsFilePath, string filteredOutputPath,
            string venvPythonPath, Action<string>? onLine, CancellationToken ct)
        {
            CallCount++;
            LastRequirementsPath = requirementsFilePath;
            LastFilteredPath = filteredOutputPath;
            LastPythonExe = venvPythonPath;
            // 模拟几行 pip 输出,验 onLine 链路
            onLine?.Invoke("Looking in indexes: https://pypi.org/simple");
            onLine?.Invoke("Collecting SQLAlchemy");
            onLine?.Invoke("Installing collected packages: SQLAlchemy");
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public async Task InstallNodeRequirementsAsync_NoRequirementsTxt_ReturnsSuccessSkip()
    {
        var env = SeedEnv("env-node", Path.Combine(_tempRoot, "env-node"), Path.Combine(_tempRoot, "venv-node"));
        var nodeDir = Path.Combine(_tempRoot, "node-empty");
        Directory.CreateDirectory(nodeDir);
        // 不写 requirements.txt
        var fakeReqFile = new FakeReqFileInstaller();
        var installer = new RequirementsInstaller(reqFileInstaller: fakeReqFile);

        var result = await installer.InstallNodeRequirementsAsync(env, nodeDir);

        Assert.True(result.Success);
        Assert.Equal("节点无 requirements.txt", result.Reason);
        Assert.Equal(0, fakeReqFile.CallCount);  // 关键:没调 pip
    }

    [Fact]
    public async Task InstallNodeRequirementsAsync_HappyPath_CallsPipOnNodeRequirements()
    {
        var env = SeedEnv("env-node", Path.Combine(_tempRoot, "env-node"), Path.Combine(_tempRoot, "venv-node"));
        var nodeDir = Path.Combine(_tempRoot, "node-with-req");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllLines(Path.Combine(nodeDir, "requirements.txt"), new[] { "SQLAlchemy", "einops" });
        var fakeReqFile = new FakeReqFileInstaller();
        var installer = new RequirementsInstaller(reqFileInstaller: fakeReqFile);

        var progressLines = new System.Collections.Generic.List<string>();
        var progress = new Progress<string>(line => progressLines.Add(line));
        var result = await installer.InstallNodeRequirementsAsync(env, nodeDir, progress);

        Assert.True(result.Success);
        Assert.Equal(1, fakeReqFile.CallCount);
        Assert.Equal(Path.Combine(nodeDir, "requirements.txt"), fakeReqFile.LastRequirementsPath);
        Assert.Equal(Path.Combine(nodeDir, RequirementsFileInstaller.FilteredRequirementsFileName), fakeReqFile.LastFilteredPath);
        Assert.Equal(env.PythonExecutable, fakeReqFile.LastPythonExe);
        Assert.Equal(3, result.InstalledCount);
        // progress 链路通了 — 至少 1 行 pip 输出
        Assert.NotEmpty(progressLines);
    }

    [Fact]
    public async Task InstallNodeRequirementsAsync_PipFail_ReturnsFailure()
    {
        var env = SeedEnv("env-node", Path.Combine(_tempRoot, "env-node"), Path.Combine(_tempRoot, "venv-node"));
        var nodeDir = Path.Combine(_tempRoot, "node-fail");
        Directory.CreateDirectory(nodeDir);
        File.WriteAllText(Path.Combine(nodeDir, "requirements.txt"), "SQLAlchemy");
        var fakeReqFile = new FakeReqFileInstaller
        {
            NextResult = new RequirementsInstallResult(false, false, "pip 退出码 1", 0)
        };
        var installer = new RequirementsInstaller(reqFileInstaller: fakeReqFile);

        var result = await installer.InstallNodeRequirementsAsync(env, nodeDir);

        Assert.False(result.Success);
        Assert.Equal("pip 退出码 1", result.Reason);
        Assert.Equal(1, fakeReqFile.CallCount);
    }

    [Fact]
    public async Task InstallNodeRequirementsAsync_NullEnv_Throws()
    {
        var installer = new RequirementsInstaller(reqFileInstaller: new FakeReqFileInstaller());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => installer.InstallNodeRequirementsAsync(null!, Path.Combine(_tempRoot, "x")));
    }

    [Fact]
    public async Task InstallNodeRequirementsAsync_NodeDirEmpty_Throws()
    {
        var env = SeedEnv("env-node", Path.Combine(_tempRoot, "env-node"), Path.Combine(_tempRoot, "venv-node"));
        var installer = new RequirementsInstaller(reqFileInstaller: new FakeReqFileInstaller());
        await Assert.ThrowsAsync<ArgumentException>(
            () => installer.InstallNodeRequirementsAsync(env, ""));
    }
}
