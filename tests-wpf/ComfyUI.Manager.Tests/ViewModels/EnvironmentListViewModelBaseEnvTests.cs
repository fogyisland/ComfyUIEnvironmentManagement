using System;
using System.Collections.Generic;
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
/// v1.0.0.x (2026-09-01): 测试 ToggleBaseEnvCommand 在 Fooocus env 上走
/// <see cref="FooocusBaseEnvInstaller"/> 路径(跳过 picker dialog),跟 Forge
/// 镜像 — 锁 torch 2.1.0+cu121,不让用户选 picker。
///
/// 镜像 <see cref="EnvironmentListViewModelToggleButtonsTests.NewSut"/>
/// pattern(test seam injection via ctor params),但这里测 FooocusBaseEnvInstaller
/// dispatch 而非 picker 调用。
/// </summary>
public class EnvironmentListViewModelBaseEnvTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _dbPath;
    private readonly ComfyUI.Manager.Data.SqliteConnectionFactory _factory;
    private readonly EnvironmentRepository _repo;

    public EnvironmentListViewModelBaseEnvTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"envlistview-baseenv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _dbPath = Path.Combine(_tempRoot, "state.db");
        _factory = new ComfyUI.Manager.Data.SqliteConnectionFactory(_dbPath);
        _repo = new EnvironmentRepository(_factory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string kind)
    {
        var envDir = Path.Combine(_tempRoot, kind + "-env");
        Directory.CreateDirectory(envDir);
        var venvDir = Path.Combine(envDir, "venv", "Scripts");
        Directory.CreateDirectory(venvDir);
        File.WriteAllBytes(Path.Combine(venvDir, "python.exe"), new byte[] { 0x00 });
        return new Environment
        {
            Id = kind + "-env",
            Name = kind + "-env",
            RootPath = envDir,
            PythonExecutable = Path.Combine(venvDir, "python.exe"),
            TemplateKind = kind,
        };
    }

    /// <summary>
    /// 镜像 <see cref="EnvironmentListViewModelToggleButtonsTests.NewSut"/>
    /// pattern —— 9 个 null! 必填服务 + PickerDialogOverride / ShowProgressDialogOverride
    /// 走 null 让 ToggleBaseEnv 路径直接 fallthrough(只在 Fooocus / Forge kind 走
    /// inline panel 路径,不弹 picker)。
    /// </summary>
    private EnvironmentListViewModel NewSut(FooocusBaseEnvInstaller fooocusInstaller)
    {
        var profileLoader = new BaseEnvProfileLoader(_tempRoot);
        return new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!,
            profileLoader,
            null!, null!,
            _tempRoot,
            new RequirementsInstaller(),
            new BaseEnvUninstaller(),
            new RequirementsUninstaller(),
            null!, null!, null!,
            null!, null!, null!, null!, null!,
            forgeBaseEnvInstaller: null,
            fooocusBaseEnvInstaller: fooocusInstaller);
    }

    /// <summary>
    /// 替身 FooocusBaseEnvInstaller:覆盖 RunPipAsync 拦截真实 pip 调用,直接写 marker
    /// 文件模拟 InstallAsync 成功路径,记录调用次数。
    /// </summary>
    private class CapturingFooocusInstaller : FooocusBaseEnvInstaller
    {
        public int InstallCallCount { get; private set; }
        public List<string> LastPipArgs { get; } = new();

        public override async Task<FooocusBedInstallResult> InstallAsync(
            Environment env,
            IProgress<string>? logProgress = null,
            CancellationToken ct = default)
        {
            InstallCallCount++;
            // 模拟 step 0 (pip upgrade) + step 1 (torch) 成功
            LastPipArgs.Add("install");
            LastPipArgs.Add("--upgrade");
            LastPipArgs.Add("pip");
            LastPipArgs.Add("wheel");
            LastPipArgs.Add("install");
            LastPipArgs.Add($"torch=={FooocusBaseEnvConstants.TorchVersion}");
            LastPipArgs.Add($"torchvision=={FooocusBaseEnvConstants.TorchVisionVersion}");
            LastPipArgs.Add("--extra-index-url");
            LastPipArgs.Add(FooocusBaseEnvConstants.TorchIndexUrl);
            // 写 marker 镜像真实 BaseEnvInstaller 行为
            try
            {
                var markerPath = Path.Combine(env.RootPath, FooocusBaseEnvConstants.MarkerFileName);
                File.WriteAllText(markerPath, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            }
            catch { }
            env.BedStatus = "done";
            return new FooocusBedInstallResult(
                Success: true, Cancelled: false, Reason: null, InstalledCount: 0);
        }
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_FooocusEnv_CallsFooocusBaseEnvInstaller_SkipsPicker()
    {
        // 关键验证:Fooocus env 点「安装基础环境」按钮 → 直接走 FooocusBaseEnvInstaller
        // (锁 torch 2.1.0+cu121),不弹 BaseEnvProfilePickerDialog 让用户选
        var env = SeedEnv("Fooocus");
        env.BedStatus = null;
        var captured = new CapturingFooocusInstaller();
        var vm = NewSut(captured);
        vm.Selected = env;

        vm.ToggleBaseEnvCommand.Execute(env);
        // async void command — 等 SynchronizationContext 跑完
        await Task.Yield();
        await Task.Delay(100);

        Assert.Equal(1, captured.InstallCallCount);
        Assert.Contains("torch==2.1.0", captured.LastPipArgs);
        Assert.Contains("torchvision==0.16.0", captured.LastPipArgs);
        Assert.Contains("https://download.pytorch.org/whl/cu121", captured.LastPipArgs);
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_FooocusEnv_AfterInstall_WritesMarker()
    {
        // 进一步验证:InstallAsync 成功 → 写 .fooocus_base_env_installed marker
        // → env.IsBaseEnvInstalled = true(后续 StartCommand.CanExecute 走 done 路径)
        var env = SeedEnv("Fooocus");
        env.BedStatus = null;
        var captured = new CapturingFooocusInstaller();
        var vm = NewSut(captured);
        vm.Selected = env;

        vm.ToggleBaseEnvCommand.Execute(env);
        await Task.Yield();
        await Task.Delay(100);

        var markerPath = Path.Combine(env.RootPath,
            FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName);
        Assert.True(File.Exists(markerPath), "marker 文件未写入");
        Assert.True(env.IsBaseEnvInstalled);
        Assert.Equal("卸载基础环境", env.BaseEnvButtonText);
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_FooocusEnv_AlreadyInstalled_ShortCircuits()
    {
        // 二次点击:BED 已装(marker + BedStatus=done 模拟老 env)→ OpenBaseEnvProgressForSingleEnvAsync
        // 顶部 BaseEnvUninstaller.IsInstalled(env) 检查返 true → ShowAlreadyInstalled
        // 短路,不再调 InstallAsync(避免老 Fooocus env 反复点按钮重复装 torch)。
        //
        // 注:ToggleBaseEnvAsync 先看 env.IsBaseEnvInstalled(用于 uninstall / install 分支),
        // 这里设 false 走 install 路径,被 OpenBaseEnvProgressForSingleEnvAsync 内部
        // BaseEnvUninstaller.IsInstalled 短路拦截。
        var env = SeedEnv("Fooocus");
        // 写 marker + 设 BedStatus="done" → BaseEnvUninstaller.IsInstalled 返 true
        File.WriteAllText(
            Path.Combine(env.RootPath,
                FooocusBaseEnvInstaller.FooocusBaseEnvConstants.MarkerFileName),
            "2026-09-01T00:00:00Z");
        env.BedStatus = "done";
        env.IsBaseEnvInstalled = false;  // 走 install 路径,但 inner short-circuit 拦截
        var captured = new CapturingFooocusInstaller();
        var vm = NewSut(captured);
        vm.Selected = env;

        vm.ToggleBaseEnvCommand.Execute(env);
        await Task.Yield();
        await Task.Delay(100);

        // 短路不调 InstallAsync(marker 路径)
        Assert.Equal(0, captured.InstallCallCount);
    }
}
