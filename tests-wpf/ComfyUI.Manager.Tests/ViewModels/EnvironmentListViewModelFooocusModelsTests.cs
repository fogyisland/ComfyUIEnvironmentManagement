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
/// v1.0.0.x (2026-09-01) T22: 测试 ToggleBaseEnvCommand.FooocusEnv_DownloadFooocusModels
/// dispatch 路径 —— Fooocus env 走 FooocusDefaultModelsInstaller,其它 kind
/// 走 null fallback 不调用。
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
            fooocusDefaultModelsInstaller: installer);
    }

    [Fact]
    public void DownloadFooocusModelsCommand_FooocusEnv_CanExecuteTrue()
    {
        // 镜像 BaseEnvTests 模式:CanExecute 在 Fooocus env + !busy 时返 true
        var env = SeedEnv("Fooocus");
        var vm = NewSut();

        Assert.True(vm.DownloadFooocusModelsCommand.CanExecute(env));
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
    public void DownloadFooocusModelsCommand_NonFooocusKind_CanExecuteFalse(string kind)
    {
        // 回归保护:只 Fooocus kind 启用,其它 10 个 kind 都禁用(按钮 Visibility 隐藏 +
        // CanExecute 双重锁,防 busy / race 时误触)
        var env = SeedEnv(kind);
        var vm = NewSut();

        Assert.False(vm.DownloadFooocusModelsCommand.CanExecute(env));
    }

    [Fact]
    public void FooocusModelsDownloadButtonVisible_TrueForFooocusKind_FalseOtherwise()
    {
        // Environment computed bool — XAML 按钮 Visibility 绑它
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

    // ----- T23b DownloadFooocusLauncherDefaultsCommand -----

    [Fact]
    public void DownloadFooocusLauncherDefaultsCommand_FooocusEnv_CanExecuteTrue()
    {
        // 镜像 T22 模式:CanExecute 在 Fooocus env + !busy 时返 true
        var env = SeedEnv("Fooocus");
        var vm = NewSut();

        Assert.True(vm.DownloadFooocusLauncherDefaultsCommand.CanExecute(env));
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
    public void DownloadFooocusLauncherDefaultsCommand_NonFooocusKind_CanExecuteFalse(string kind)
    {
        // 回归保护:同 T22 模式,只 Fooocus kind 启用
        var env = SeedEnv(kind);
        var vm = NewSut();

        Assert.False(vm.DownloadFooocusLauncherDefaultsCommand.CanExecute(env));
    }
}
