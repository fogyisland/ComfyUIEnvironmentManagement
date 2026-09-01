using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v1.0.0.x (2026-09-01) T24:测试 <see cref="EnvironmentListViewModel.DownloadFooocusAllModelsCommand"/>
/// — 合并 T22 「下载默认模型」+ T23b 「下载 launcher 默认」按钮后的单 command。
///
/// 测试策略:
/// <list type="bullet">
///   <item>CanExecute:跟 TemplateKind 锁定(只 Fooocus)+ busy + 全装齐 3 条件</item>
///   <item>CanExecute 锁 10 个非 Fooocus kind(回归保护)</item>
///   <item>Environment.FooocusAllDefaultModelsDownloaded 字段 + computed bool</item>
///   <item>FooocusModelsDownloadButtonVisible 仍是 Fooocus-only(回归 T22)</item>
/// </list>
///
/// 老 T22/T23b 「DownloadFooocusModelsCommand / DownloadFooocusLauncherDefaultsCommand」
/// tests 已删除(2 个 command 合并为 1 个,旧 command 名不复存在)。
/// </summary>
public class EnvironmentListViewModelFooocusModelsTests : IDisposable
{
    private readonly string _root;
    private readonly SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;

    public EnvironmentListViewModelFooocusModelsTests()
    {
        _root = Path.Combine(Path.GetTempPath(),
            $"envlistview-fooocus-models-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new SqliteConnectionFactory(Path.Combine(_root, "state.db"));
        _repo = new EnvironmentRepository(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Environment SeedEnv(string kind)
    {
        var envDir = Path.Combine(_root, kind + "-env");
        Directory.CreateDirectory(envDir);
        return new Environment
        {
            Id = kind + "-env",
            Name = kind + "-env",
            RootPath = envDir,
            TemplateKind = kind,
        };
    }

    private ComfyUI.Manager.Models.Settings MakeSettings()
    {
        return new ComfyUI.Manager.Models.Settings
        {
            EnvsDir = Path.Combine(_root, "envs"),
            SystemTemplateLibraryDir = _root,
        };
    }

    /// <summary>
    /// 镜像 EnvironmentListViewModelBaseEnvTests pattern —— 镜像 NewSut,只
    /// 设必须的依赖,其它 null! 兜底。
    /// </summary>
    private EnvironmentListViewModel NewSut(
        FooocusDefaultModelsInstaller? installer = null)
    {
        return new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!,
            new BaseEnvProfileLoader(_root),
            null!, null!,
            _root,
            new RequirementsInstaller(),
            new BaseEnvUninstaller(),
            new RequirementsUninstaller(),
            null!, null!, null!,
            null!, null!, null!, null!, null!,
            forgeBaseEnvInstaller: null,
            fooocusBaseEnvInstaller: null,
            fooocusDefaultModelsInstaller: installer);
    }

    // ----- T24 merged DownloadFooocusAllModelsCommand -----

    [Fact]
    public void DownloadFooocusAllModelsCommand_FooocusEnv_CanExecuteTrue()
    {
        // T24:merged command — Fooocus env + !busy + 全未装齐 → CanExecute true
        var env = SeedEnv("Fooocus");
        env.FooocusAllDefaultModelsDownloaded = false;  // 默认:全未装齐 → enabled
        var vm = NewSut();

        Assert.True(vm.DownloadFooocusAllModelsCommand.CanExecute(env));
    }

    [Fact]
    public void DownloadFooocusAllModelsCommand_AllDownloaded_CanExecuteFalse()
    {
        // T24:全装齐 → CanExecute false(按钮 disabled,跟 XAML IsEnabled 绑同源)
        var env = SeedEnv("Fooocus");
        env.FooocusAllDefaultModelsDownloaded = true;  // 全装齐 → disabled
        var vm = NewSut();

        Assert.False(vm.DownloadFooocusAllModelsCommand.CanExecute(env));
    }

    [Theory]
    [InlineData("ComfyUI")]
    [InlineData("Forge")]
    [InlineData("OpenVoice")]
    [InlineData("Whisper")]
    [InlineData("CoquiTTS")]
    [InlineData("Bark")]
    [InlineData("HunyuanVideo")]
    [InlineData("LTXVideo")]
    [InlineData("CogVideoX")]
    [InlineData("HivisionIDPhotos")]
    public void DownloadFooocusAllModelsCommand_NonFooocusKind_CanExecuteFalse(string kind)
    {
        // 回归保护(原 T22 + T23b 测试):只 Fooocus kind 启用,其它 10 个 kind 都禁用。
        // 按钮 Visibility 隐藏 + CanExecute 双重锁,防 busy / race 时误触。
        var env = SeedEnv(kind);
        env.FooocusAllDefaultModelsDownloaded = false;  // 即使设了"全装齐"也不变(Visibility 拦)
        var vm = NewSut();

        Assert.False(vm.DownloadFooocusAllModelsCommand.CanExecute(env));
    }

    [Fact]
    public void DownloadFooocusAllModelsCommand_NullEnv_CanExecuteFalse()
    {
        // Defensive:null env → false,不抛 NPE
        var vm = NewSut();

        Assert.False(vm.DownloadFooocusAllModelsCommand.CanExecute(null));
    }

    // ----- Environment.FooocusAllDefaultModelsDownloaded 字段 -----

    [Fact]
    public void FooocusAllDefaultModelsDownloaded_DefaultsToFalse()
    {
        // T24:JsonIgnore 字段 default false,Load() 末尾 async probe 设回 true
        // (全装齐)。新 env 默认 false → 按钮 enabled。
        var env = new Environment { TemplateKind = "Fooocus" };
        Assert.False(env.FooocusAllDefaultModelsDownloaded);
    }

    // ----- FooocusModelsDownloadButtonVisible 回归(T22 行为)-----

    [Fact]
    public void FooocusModelsDownloadButtonVisible_TrueForFooocusKind_FalseOtherwise()
    {
        // Environment computed bool — XAML 按钮 Visibility 绑它(merged 按钮继续用)
        var fooocus = new Environment { TemplateKind = "Fooocus" };
        Assert.True(fooocus.FooocusModelsDownloadButtonVisible);

        foreach (var kind in new[] { "ComfyUI", "Forge", "OpenVoice", "Whisper",
                                      "CoquiTTS", "Bark", "HunyuanVideo", "LTXVideo",
                                      "CogVideoX", "HivisionIDPhotos" })
        {
            var env = new Environment { TemplateKind = kind };
            Assert.False(env.FooocusModelsDownloadButtonVisible);
        }

        // 空 kind → false(防御)
        Assert.False(new Environment { TemplateKind = "" }.FooocusModelsDownloadButtonVisible);
    }
}