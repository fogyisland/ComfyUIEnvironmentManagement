using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// EnvComponentReportBuilder 测试:用 FakeBuilder override RunCommandAsync,
/// 模拟 pip show / pip list / git 输出,验证采集逻辑 + 字段映射,避免真跑 subprocess(慢+环境依赖)。
/// </summary>
public sealed class EnvComponentReportBuilderTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _envRepo;
    private readonly string _tempRoot;

    public EnvComponentReportBuilderTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
        _tempRoot = Path.Combine(Path.GetTempPath(), $"env-cr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private BaseEnvProfileLoader CreateProfileLoader(params BaseEnvProfile[] profiles)
    {
        var loader = new FakeProfileLoader(profiles);
        return loader;
    }

    private Environment SeedEnv(
        string id = "env-a",
        string? bedProfileId = "pytorch-2.4.1-cu118-stable",
        string? pythonExe = null,
        string? venvPath = null,
        string? comfyuiSource = null,
        string? customNodes = null,
        string templateKind = "ComfyUI")
    {
        var root = Path.Combine(_tempRoot, id);
        Directory.CreateDirectory(root);
        var env = new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            VenvPath = venvPath,
            PythonExecutable = pythonExe,
            ComfyuiSource = comfyuiSource,
            CustomNodesPath = customNodes,
            Port = 8188,
            Status = "stopped",
            BedProfileId = bedProfileId,
            BedStatus = bedProfileId is null ? null : "done",
            TemplateKind = templateKind,
        };
        _envRepo.Upsert(env);
        return env;
    }

    private static BaseEnvProfile DefaultProfile() => new()
    {
        Id = "pytorch-2.4.1-cu118-stable",
        Name = "PyTorch 2.4.1 + CUDA 11.8 (stable)",
        Description = "test",
        TorchVersion = "2.4.1",
        CudaVersion = "cu118",
        Channel = "stable",
        Packages = new List<string> { "torch==2.4.1", "torchvision", "torchaudio", "xformers" },
    };

    [Fact]
    public async Task BuildAsync_MissingPythonExecutable_AddsWarningAndEmptySections()
    {
        var env = SeedEnv(pythonExe: null, venvPath: null);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");

        var report = await builder.BuildAsync(env);

        Assert.NotEmpty(report.SectionWarnings);
        Assert.Contains(report.SectionWarnings,
            w => w.Contains("Python 解释器未找到", StringComparison.Ordinal));
        Assert.Empty(report.KeyPackages);
        Assert.Empty(report.FullPipList);
    }

    [Fact]
    public async Task BuildAsync_BedProfileIdSet_ResolvesRequiredSpec()
    {
        var env = SeedEnv(bedProfileId: "pytorch-2.4.1-cu118-stable");
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");

        var report = await builder.BuildAsync(env);

        Assert.NotNull(report.Required);
        Assert.Equal("pytorch-2.4.1-cu118-stable", report.Required!.ProfileId);
        Assert.Equal("2.4.1", report.Required.TorchVersion);
        Assert.Equal("cu118", report.Required.CudaVersion);
        Assert.Equal("CUDA 11.8", report.Required.CudaLabel);
        Assert.Equal("stable", report.Required.Channel);
        Assert.Contains("torch==2.4.1", report.Required.Packages);
    }

    [Fact]
    public async Task BuildAsync_NoBedProfileId_RequiredIsNull()
    {
        var env = SeedEnv(bedProfileId: null);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");

        var report = await builder.BuildAsync(env);

        Assert.Null(report.Required);
    }

    [Fact]
    public async Task BuildAsync_PipShowOutput_MatchesVersions()
    {
        var pyExe = Path.Combine(_tempRoot, "fake-python.exe");
        File.WriteAllText(pyExe, "");
        var env = SeedEnv(pythonExe: pyExe);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");
        builder.NextRun = MakeOk(
            "Name: torch\nVersion: 2.4.1\n\n" +
            "Name: torchvision\nVersion: 0.19.1\n\n" +
            "Name: torchaudio\nVersion: 2.4.1\n\n" +
            "Name: xformers\nVersion: 0.0.28\n\n");
        // pip list 顺序在 pip show 之后;返回空 JSON 让 FullPipList 走空分支
        builder.NextRun = MakeOk("[]");

        var report = await builder.BuildAsync(env);

        Assert.Equal(4, report.KeyPackages.Count);
        Assert.All(report.KeyPackages, kp => Assert.Equal(KeyPackageMatchStatus.Match, kp.Status));
        // 验证拿到的实际版本
        Assert.Contains(report.KeyPackages, kp => kp.PackageName == "torch" && kp.ActualVersion == "2.4.1");
        Assert.Contains(report.KeyPackages, kp => kp.PackageName == "xformers" && kp.ActualVersion == "0.0.28");
    }

    [Fact]
    public async Task BuildAsync_PipShowMissingPackage_StatusIsMissing()
    {
        var pyExe = Path.Combine(_tempRoot, "fake-python.exe");
        File.WriteAllText(pyExe, "");
        var env = SeedEnv(pythonExe: pyExe);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");
        // pip show 只返 torch
        builder.NextRun = MakeOk("Name: torch\nVersion: 2.4.1\n\n");
        // pip list 返空
        builder.NextRun = MakeOk("[]");

        var report = await builder.BuildAsync(env);

        var xformers = report.KeyPackages.Single(kp => kp.PackageName == "xformers");
        Assert.Equal(KeyPackageMatchStatus.Missing, xformers.Status);
        Assert.Null(xformers.ActualVersion);
        Assert.Equal("2.4.1", report.KeyPackages.First(kp => kp.PackageName == "torch").ActualVersion);
    }

    [Fact]
    public async Task BuildAsync_PipListJson_ReturnsSortedList()
    {
        var pyExe = Path.Combine(_tempRoot, "fake-python.exe");
        File.WriteAllText(pyExe, "");
        var env = SeedEnv(pythonExe: pyExe);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");
        builder.NextRun = MakeOk("[]"); // pip show
        builder.NextRun = MakeOk(
            "[{\"name\":\"torch\",\"version\":\"2.4.1\"}," +
            "{\"name\":\"aiosqlite\",\"version\":\"0.20.0\"}]");

        var report = await builder.BuildAsync(env);

        Assert.Equal(2, report.FullPipList.Count);
        Assert.Equal("aiosqlite", report.FullPipList[0].Name);   // 排序后 aiosqlite 在前
        Assert.Equal("0.20.0", report.FullPipList[0].Version);
        Assert.Equal("torch", report.FullPipList[1].Name);
    }

    [Fact]
    public async Task BuildAsync_NoComfyuiSource_StatusIsNull()
    {
        var env = SeedEnv(comfyuiSource: null);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");

        var report = await builder.BuildAsync(env);

        Assert.Null(report.ComfyuiStatus);
    }

    [Fact]
    public async Task BuildAsync_NvccNotRequested_StillProducesReport()
    {
        // 本期不实现 nvcc 检查 — sanity test 确认报告仍能生成
        var env = SeedEnv();
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");

        var report = await builder.BuildAsync(env);

        Assert.NotNull(report);
        Assert.Equal("env-a", report.EnvName);
    }

    [Fact]
    public async Task BuildAsync_CancellationRequested_Throws()
    {
        var pyExe = Path.Combine(_tempRoot, "fake-python.exe");
        File.WriteAllText(pyExe, "");
        var env = SeedEnv(pythonExe: pyExe);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");
        builder.OnRunAsync = (exe, args, wd, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(MakeOk(""));
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => builder.BuildAsync(env, cts.Token));
    }

    [Fact]
    public async Task BuildAsync_SetsMetadataRootPathFromEnv()
    {
        var env = SeedEnv();
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");

        var report = await builder.BuildAsync(env);

        Assert.Equal(env.RootPath, report.Metadata.RootPath);
        Assert.Equal("8188", report.Metadata.Port);
        Assert.Equal("stopped", report.Metadata.Status);
    }

    [Fact]
    public async Task BuildAsync_GitRevParseFails_StatusIsNotARepository()
    {
        var dir = Path.Combine(_tempRoot, "comfyui");
        Directory.CreateDirectory(dir);
        var env = SeedEnv(comfyuiSource: dir);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");
        // rev-parse 失败
        builder.NextRun = MakeFail(128, "fatal: not a git repository (or any of the parent directories): .git\n");

        var report = await builder.BuildAsync(env);

        Assert.NotNull(report.ComfyuiStatus);
        Assert.Equal(GitTargetState.NotARepository, report.ComfyuiStatus!.State);
        Assert.Contains("not a git repository", report.ComfyuiStatus.ErrorMessage ?? "");
    }

    // --- v1.0.0.x (2026-08-29):EnvComponentReport 源码 DisplayName 按 TemplateKind 派生 ---

    [Theory]
    [InlineData("ComfyUI", "ComfyUI 源码")]      // 向后兼容:ComfyUI 留原样
    [InlineData("Forge", "Forge 源码")]
    [InlineData("OpenVoice", "OpenVoice 源码")]
    [InlineData("Whisper", "Whisper 源码")]
    [InlineData("HunyuanVideo", "HunyuanVideo 源码")]
    [InlineData("LTXVideo", "LTXVideo 源码")]
    public async Task BuildAsync_SourceDisplayName_DerivedFromTemplateKind(
        string templateKind, string expectedDisplayName)
    {
        // v1.0.0.x:组件报告 hardcode "ComfyUI 源码" 不管 env.TemplateKind 是什么 ——
        // 多模板(Forge/OpenVoice/HunyuanVideo 等)共享 env.ComfyuiSource 字段,但显示必须
        // 按实际 template kind。ComfyUI 留 "ComfyUI 源码"(向后兼容);其他 → "{Kind} 源码"。
        var dir = Path.Combine(_tempRoot, "source");
        Directory.CreateDirectory(dir);
        var env = SeedEnv(comfyuiSource: dir, templateKind: templateKind);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");
        // rev-parse 失败让 status 落 NotARepository 分支(只需要 status 非 null + DisplayName 可读)
        builder.NextRun = MakeFail(128, "fatal: not a git repository\n");

        var report = await builder.BuildAsync(env);

        Assert.NotNull(report.ComfyuiStatus);
        Assert.Equal(expectedDisplayName, report.ComfyuiStatus!.DisplayName);
    }

    [Fact]
    public async Task BuildAsync_EmptyTemplateKind_FallsBackToComfyUIString()
    {
        // 防御:env.TemplateKind 为空(老 env 行没填这字段)→ 回落 "ComfyUI 源码" 旧行为。
        // 不抛、不空 DisplayName。
        var dir = Path.Combine(_tempRoot, "source");
        Directory.CreateDirectory(dir);
        var env = SeedEnv(comfyuiSource: dir);
        env.TemplateKind = "";  // 显式清空
        _envRepo.Upsert(env);
        var builder = new FakeBuilder(
            CreateProfileLoader(DefaultProfile()),
            _envRepo,
            "fake-git",
            "0.6.7.0");
        builder.NextRun = MakeFail(128, "fatal: not a git repository\n");

        var report = await builder.BuildAsync(env);

        Assert.NotNull(report.ComfyuiStatus);
        Assert.Equal("ComfyUI 源码", report.ComfyuiStatus!.DisplayName);
    }

    // ----------- helpers -----------

    private sealed class FakeBuilder : EnvComponentReportBuilder
    {
        public Queue<ProcessRunResult> Runs = new();
        public ProcessRunResult NextRun
        {
            set { Runs.Enqueue(value); }
        }
        public Func<string, IReadOnlyList<string>, string?, CancellationToken, Task<ProcessRunResult>>? OnRunAsync { get; set; }

        public FakeBuilder(BaseEnvProfileLoader profileLoader, IEnvironmentRepository envRepo,
            string gitExe, string appVersion)
            : base(profileLoader, envRepo, gitExe, appVersion)
        { }

        public override Task<ProcessRunResult> RunCommandAsync(
            string exe, IReadOnlyList<string> args, string? workdir, CancellationToken ct)
        {
            if (OnRunAsync is not null)
            {
                return OnRunAsync(exe, args, workdir, ct);
            }
            if (Runs.Count > 0)
            {
                return Task.FromResult(Runs.Dequeue());
            }
            // 兜底:返回 0 + 空 stdout,避免测试因为缺漏 NextRun 崩得莫名其妙
            return Task.FromResult(MakeOk(""));
        }
    }

    private sealed class FakeProfileLoader : BaseEnvProfileLoader
    {
        private readonly IReadOnlyList<BaseEnvProfile> _profiles;
        public FakeProfileLoader(IReadOnlyList<BaseEnvProfile> profiles)
            : base(localDataDir: Path.Combine(Path.GetTempPath(), "fake-appdata-" + Guid.NewGuid().ToString("N")))
        {
            _profiles = profiles;
        }
        public override Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_profiles);
        }
    }

    private static ProcessRunResult MakeOk(string stdout) => new(0, stdout, "");
    private static ProcessRunResult MakeFail(int code, string stderr) => new(code, "", stderr);
}

