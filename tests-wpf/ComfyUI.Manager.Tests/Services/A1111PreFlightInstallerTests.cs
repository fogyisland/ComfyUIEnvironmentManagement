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
/// v1.0.0.x:A1111 / Forge pre-flight installer 测试。覆盖:
/// - step 3 过滤 torch 行(跟 ComfyUI RequirementsInstaller 同 regex)
/// - 缺失 requirements_versions.txt → 失败
/// - marker 写入与不存在判定
///
/// 不覆盖:clip / open_clip / git clone 5 repos(走真实 pip + git,留 manual 集成验证)。
/// </summary>
public class A1111PreFlightInstallerTests : IDisposable
{
    private readonly string _envRoot;

    public A1111PreFlightInstallerTests()
    {
        _envRoot = Path.Combine(Path.GetTempPath(),
            $"a1111preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_envRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_envRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string name = "sdweb")
    {
        var venvDir = Path.Combine(_envRoot, name, "venv", "Scripts");
        Directory.CreateDirectory(venvDir);
        // 写一个 fake python.exe(空文件 — 测试不真正调 pip,只验 filtered 内容)
        File.WriteAllBytes(Path.Combine(venvDir, "python.exe"), new byte[] { 0x00 });
        var env = new Environment
        {
            Id = name,
            Name = name,
            RootPath = Path.Combine(_envRoot, name),
            PythonExecutable = Path.Combine(venvDir, "python.exe"),
            TemplateKind = "A1111",
        };
        Directory.CreateDirectory(env.RootPath);
        // requirements_versions.txt 镜像真实 sdweb 内容。**只过滤裸 torch 行**
        // (v1.0.0.x 用户确认 — launch.py 单独装 torchvision / torchaudio /
        // xformers,不在此文件;这里只防 BED profile 锁的 torch 被覆盖)。
        // 同名含 torch 子串但不是 torch 包(torchdiffeq / torchsde /
        // pytorch_lightning / open-clip-torch)都保留。
        File.WriteAllLines(Path.Combine(env.RootPath, "requirements_versions.txt"),
            new[]
            {
                "setuptools==69.5.1  # temp fix",
                "GitPython==3.1.32",
                "Pillow==9.5.0",
                "torch",                       // 裸名(要被过滤)
                "  torch  ",                   // leading/trailing whitespace(要被过滤)
                "torch==2.1.2",                // 带版本(要被过滤 — 显式版本覆盖 BED)
                "torchvision==0.16.2",         // NOT 过滤(launch.py 单独装,不在此文件)
                "torchaudio==0.13.0",          // NOT 过滤(launch.py 按需装)
                "torchdiffeq==0.2.3",          // NOT 过滤(含 torch 子串但不是 torch 包)
                "torchsde==0.2.6",             // NOT 过滤(同)
                "pytorch_lightning==1.9.4",    // NOT 过滤
                "open-clip-torch==2.20.0",     // NOT 过滤
                "gradio==3.41.2",
                "numpy==1.26.2",
            });
        // pre-create repositories/<repoName>/.git/ 让 git clone 步骤 skip
        // (集成测 test 不依赖网络 — CapturingInstaller 也只 mock pip,不 mock git)
        var reposDir = Path.Combine(env.RootPath, "repositories");
        foreach (var spec in A1111PreFlightConstants.Repos)
            Directory.CreateDirectory(Path.Combine(reposDir, spec.DirName, ".git"));
        return env;
    }

    /// <summary>
    /// Fake:不真跑 pip(只验 filtered 文件内容 + pip args)。强制所有
    /// pip 调用都 success,捕获最后一次 pipArgs 跟 filtered 文件路径。
    /// </summary>
    private class CapturingInstaller : A1111PreFlightInstaller
    {
        public List<string> LastPipArgs { get; } = new();
        public string? LastFilteredFile { get; private set; }
        public List<string>? LastFilteredContent { get; private set; }
        public bool FailOnClip { get; set; }
        public bool FailOnOpenClip { get; set; }
        public bool FailOnReq { get; set; }
        public bool FailOnRepoClone { get; set; }

        public CapturingInstaller() : base() { }

        protected override Task<PipResult> RunPipAsync(
            string pythonExe,
            IReadOnlyList<string> pipArgs,
            Action<string> onLine,
            CancellationToken ct)
        {
            LastPipArgs.Clear();
            foreach (var a in pipArgs) LastPipArgs.Add(a);

            // pip install -r <filteredFile>:读 filtered 内容供 assert
            for (int i = 0; i < pipArgs.Count - 1; i++)
            {
                if (pipArgs[i] == "-r" && i + 1 < pipArgs.Count)
                {
                    LastFilteredFile = pipArgs[i + 1];
                    if (File.Exists(LastFilteredFile))
                        LastFilteredContent = File.ReadAllLines(LastFilteredFile).ToList();
                }
            }

            var isClip = pipArgs.Any(a => a.Contains("CLIP/archive"));
            var isOpenClip = pipArgs.Any(a => a.Contains("open_clip/archive"));
            var isReqFile = pipArgs.Any(a => a.Contains(".requirements_filtered.txt"));
            var result = (isClip && FailOnClip) ||
                         (isOpenClip && FailOnOpenClip) ||
                         (isReqFile && FailOnReq)
                ? new PipResult(1, WasCancelled: false)
                : new PipResult(0, WasCancelled: false);
            return Task.FromResult(result);
        }
    }

    [Fact]
    public void IsInstalled_ReturnsFalse_WhenMarkerMissing()
    {
        var env = SeedEnv();
        Assert.False(A1111PreFlightInstaller.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_ReturnsTrue_WhenMarkerExists()
    {
        var env = SeedEnv();
        File.WriteAllText(
            Path.Combine(env.RootPath, A1111PreFlightConstants.MarkerFileName),
            "2026-08-28T00:00:00Z");
        Assert.True(A1111PreFlightInstaller.IsInstalled(env));
    }

    [Fact]
    public async Task InstallAsync_MissingRequirementsFile_FailsWithClearReason()
    {
        var env = SeedEnv();
        File.Delete(Path.Combine(env.RootPath, "requirements_versions.txt"));
        var installer = new CapturingInstaller();

        var result = await installer.InstallAsync(env);

        Assert.False(result.Success);
        Assert.Contains("requirements_versions.txt", result.Reason ?? "");
    }

    [Fact]
    public async Task InstallAsync_FiltersOnlyBareTorchLines_BeforePipInstallR()
    {
        // 关键:step 3 跑 pip install -r 前,filtered 文件**只**过滤裸 torch 行
        // (用户确认 — 不动 torchvision / torchaudio 等其它 torch 系列,因为
        // 它们不在 requirements_versions.txt 里,launch.py 自己装)。
        var env = SeedEnv();
        var installer = new CapturingInstaller();

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"pre-flight fail: {result.Reason}");
        Assert.NotNull(installer.LastFilteredContent);
        Assert.NotEmpty(installer.LastFilteredContent!);
        // 裸 torch 形式被过滤(行首 "torch" 后跟空白 / 行尾 / 比较运算符)
        Assert.DoesNotContain(installer.LastFilteredContent!, line =>
            A1111PreFlightInstaller.IsBareTorchLine(line));
        // 保留的 torch 系列(launch.py 单独装,不在此过滤范围)
        Assert.Contains(installer.LastFilteredContent!, l => l.StartsWith("torchvision"));
        Assert.Contains(installer.LastFilteredContent!, l => l.StartsWith("torchaudio"));
        // 含 torch 子串但不是 torch 包的也都保留
        Assert.Contains(installer.LastFilteredContent!, l => l.StartsWith("torchdiffeq"));
        Assert.Contains(installer.LastFilteredContent!, l => l.StartsWith("torchsde"));
        Assert.Contains(installer.LastFilteredContent!, l => l.StartsWith("pytorch_lightning"));
        Assert.Contains(installer.LastFilteredContent!, l => l.StartsWith("open-clip-torch"));
        // 普通行也保留
        Assert.Contains(installer.LastFilteredContent!, l => l.StartsWith("numpy"));
        Assert.Contains(installer.LastFilteredContent!, l => l.StartsWith("gradio"));
        // filtered 文件在 pip 调用后被清理(成功失败都清)
        Assert.False(File.Exists(installer.LastFilteredFile),
            $"filtered 文件未被清理:{installer.LastFilteredFile}");
    }

    [Theory]
    [InlineData("torch", true)]                      // 裸名
    [InlineData("torch ", true)]                     // 裸名 + 空格
    [InlineData("  torch", true)]                    // leading whitespace
    [InlineData("torch==2.1.2", true)]               // 带版本
    [InlineData("torch!=1.0", true)]                 // 不等
    [InlineData("torch>=2.0,<3.0", true)]            // 范围
    [InlineData("torchvision==0.16.2", false)]      // NOT 过滤
    [InlineData("torchaudio", false)]                // NOT 过滤
    [InlineData("pytorch_lightning==1.9.4", false)]  // 含 torch 子串但不是 torch
    [InlineData("torchdiffeq==0.2.3", false)]        // 同
    [InlineData("torchsde==0.2.6", false)]           // 同
    [InlineData("open-clip-torch==2.20.0", false)]   // 同
    [InlineData("", false)]                          // 空
    public void IsBareTorchLine_MatchesOnlyBareTorch(string line, bool expected)
    {
        Assert.Equal(expected, A1111PreFlightInstaller.IsBareTorchLine(line));
    }

    [Fact]
    public async Task InstallAsync_DoesNotPassBuildIsolation_ForRequirementsTxt()
    {
        // requirements_versions.txt 都是预编译 wheel,不需要 --no-build-isolation
        // (--no-build-isolation 只用于 CLIP / open_clip 老 setup.py)
        var env = SeedEnv();
        var installer = new CapturingInstaller();

        await installer.InstallAsync(env);

        // LastPipArgs 应该是最后一次 pip 调用(req install)。先验 clip/open_clip
        // 用了 --no-build-isolation(在更早的调用),这里 LastPipArgs 是 req,
        // 所以要 capture all calls 才验得到 — 简化:验 req 不含 isolation flag。
        Assert.Contains("install", installer.LastPipArgs);
        Assert.Contains(".requirements_filtered.txt", string.Join(" ", installer.LastPipArgs));
        Assert.DoesNotContain("--no-build-isolation", installer.LastPipArgs);
    }

    [Fact]
    public async Task InstallAsync_ReqStepFail_DoesNotWriteMarker()
    {
        var env = SeedEnv();
        var installer = new CapturingInstaller { FailOnReq = true };

        var result = await installer.InstallAsync(env);

        Assert.False(result.Success);
        Assert.Contains("requirements_versions.txt", result.Reason ?? "");
        Assert.False(File.Exists(
            Path.Combine(env.RootPath, A1111PreFlightConstants.MarkerFileName)));
    }

    [Fact]
    public async Task InstallAsync_AllStepsSucceed_WritesMarker()
    {
        var env = SeedEnv();
        var installer = new CapturingInstaller();

        var result = await installer.InstallAsync(env);

        Assert.True(result.Success, $"pre-flight fail: {result.Reason}");
        Assert.True(File.Exists(
            Path.Combine(env.RootPath, A1111PreFlightConstants.MarkerFileName)),
            "marker 文件未写入");
    }

    [Fact]
    public void RequirementsVersionsContent_ContainsTorchLine_ThatShouldBeFiltered()
    {
        // Sanity:验证 fixture 真的含 torch 行 — 如果 launch_utils 改 requirements_versions.txt
        // 让它不含 torch 了,这个 fixture 会失效,提示 fixture 需要更新。
        var env = SeedEnv();
        var content = File.ReadAllLines(
            Path.Combine(env.RootPath, "requirements_versions.txt"));
        Assert.Contains(content, l => l.TrimStart().StartsWith("torch"));
    }
}