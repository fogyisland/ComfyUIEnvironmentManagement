using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

/// <summary>
/// v0.6.11+ T1:Environment + EnvironmentListViewModel 加 Requirements / BED
/// toggle 命令 + 按钮文字属性。镜像 v0.6.11+ T3 ComfyUI Manager toggle 模式。
/// </summary>
public class EnvironmentListViewModelToggleButtonsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EnvironmentRepository _repo;
    private readonly string _tempRoot;

    public EnvironmentListViewModelToggleButtonsTests()
    {
        _repo = new EnvironmentRepository(_db.Factory);
        _tempRoot = Path.Combine(Path.GetTempPath(),
            $"envlistvm-toggle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string status = "stopped",
        string? bedStatus = null, bool writeMarker = false,
        bool writeManagerDir = false)
    {
        var root = Path.Combine(_tempRoot, id);
        Directory.CreateDirectory(root);
        var env = new Environment
        {
            Id = id, Name = id, RootPath = root,
            VenvPath = Path.Combine(root, "venv"),
            PythonExecutable = Path.Combine(root, "venv", "python.exe"),
            ComfyuiLayout = "isolated",
            ComfyuiSource = Path.Combine(root, "ComfyUI"),
            CustomNodesPath = Path.Combine(root, "nodes"),
            Port = 8188,
            Status = status,
            BedStatus = bedStatus,
        };
        File.WriteAllText(Path.Combine(root, "requirements.txt"), "SQLAlchemy");
        if (writeMarker)
            File.WriteAllText(
                Path.Combine(root, RequirementsInstaller.MarkerFileName),
                "2026-08-11T12:00:00Z");
        if (writeManagerDir)
        {
            var dir = Path.Combine(root, "ComfyUI", "custom_nodes", "ComfyUI-Manager");
            Directory.CreateDirectory(dir);
        }
        _repo.Upsert(env);
        return env;
    }

    [Fact]
    public void Model_RequirementsButtonText_DefaultsToInstallLabel()
    {
        var env = new Environment { Id = "x", Name = "x", RootPath = @"C:\e" };
        Assert.Equal("装依赖", env.RequirementsButtonText);
        Assert.False(env.IsRequirementsInstalled);
    }

    [Fact]
    public void Model_BaseEnvButtonText_DefaultsToInstallLabel()
    {
        var env = new Environment { Id = "x", Name = "x", RootPath = @"C:\e" };
        Assert.Equal("安装基础环境", env.BaseEnvButtonText);
        Assert.False(env.IsBaseEnvInstalled);
    }

    // v1.0.0.x:ComfyUI-Manager 是 ComfyUI 专属 custom_nodes extension,SD Web(A1111 / Forge / SwarmUI)
    // 用 extensions 体系,没 ComfyUI-Manager 概念。「安装 ComfyUI Manager」按钮在非 ComfyUI
    // kind 上不应出现(否则用户点了会 git clone ComfyUI-Manager 到 SD Web 的 custom_nodes,
    // 但 ComfyUI-Manager 只能装 ComfyUI 依赖,SD Web 用不上)。装依赖 按钮保留 —
    // SD Web 也有非 torch 依赖(xformers / clip / gradio / Pillow 等)要装。
    [Theory]
    [InlineData("ComfyUI", true)]
    [InlineData("A1111", false)]
    [InlineData("Forge", false)]
    [InlineData("SwarmUI", false)]
    public void Model_ComfyUiManagerButtonVisible_TrueOnlyForComfyUIKind(string kind, bool expected)
    {
        var env = new Environment
        {
            Id = "x", Name = "x", RootPath = @"C:\e",
            TemplateKind = kind,
        };
        Assert.Equal(expected, env.ComfyUiManagerButtonVisible);
    }

    [Fact]
    public void Model_ComfyUiManagerButtonVisible_DefaultsTrue_WhenTemplateKindNotSet()
    {
        // 老 env 行 SQLite template_kind 列可能 null(backfill 之前),默认 TemplateKind
        // = "ComfyUI" 让 ComfyUiManagerButtonVisible 返 true(安全 fallback — 老 env 走老行为)。
        var env = new Environment { Id = "x", Name = "x", RootPath = @"C:\e" };
        Assert.Equal("ComfyUI", env.TemplateKind);  // 默认值锁
        Assert.True(env.ComfyUiManagerButtonVisible);
    }

    // v1.0.0.x:「安装本地常用」是 ComfyUI custom_nodes 专属 — 用户预下载的节点包
    // 逐个 copy 到 env/custom_nodes/。A1111 用 extensions/ 体系,SwarmUI 用自己的
    // Modules 目录,跟 ComfyUI custom_nodes 不兼容。A1111/Forge/SwarmUI 不显示此按钮。
    [Theory]
    [InlineData("ComfyUI", true)]
    [InlineData("A1111", false)]
    [InlineData("Forge", false)]
    [InlineData("SwarmUI", false)]
    public void Model_LocalNodesButtonVisible_TrueOnlyForComfyUIKind(string kind, bool expected)
    {
        var env = new Environment
        {
            Id = "x", Name = "x", RootPath = @"C:\e",
            TemplateKind = kind,
        };
        Assert.Equal(expected, env.LocalNodesButtonVisible);
    }

    [Fact]
    public void Model_LocalNodesButtonVisible_DefaultsTrue_WhenTemplateKindNotSet()
    {
        var env = new Environment { Id = "x", Name = "x", RootPath = @"C:\e" };
        Assert.Equal("ComfyUI", env.TemplateKind);
        Assert.True(env.LocalNodesButtonVisible);
    }

    [Fact]
    public void Model_PropertiesAreJsonIgnored_NotSerialized()
    {
        // 关键:这些属性不进 SQLite,跟 IsComfyUiManagerInstalled 一致
        // 用反射 + JsonIgnore attribute 验证(避免 System.Text.Json 引入额外测试代码)
        var t = typeof(Environment);
        var reqText = t.GetProperty(nameof(Environment.RequirementsButtonText))!;
        var baseText = t.GetProperty(nameof(Environment.BaseEnvButtonText))!;
        var reqInstalled = t.GetProperty(nameof(Environment.IsRequirementsInstalled))!;
        var baseInstalled = t.GetProperty(nameof(Environment.IsBaseEnvInstalled))!;
        Assert.NotNull(reqText.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
        Assert.NotNull(baseText.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
        Assert.NotNull(reqInstalled.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
        Assert.NotNull(baseInstalled.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false));
    }

    // ---------------------------------------------------------------
    // Toggle routing tests
    // ---------------------------------------------------------------

    private EnvironmentListViewModel NewSut(
        BaseEnvUninstaller? baseUninstaller = null,
        RequirementsInstaller? reqInstaller = null,
        RequirementsUninstaller? reqUninstaller = null)
    {
        // 用真实 BaseEnvProfileLoader(硬编码 9 个 profile)— PickerDialogOverride / ShowProgressDialogOverride
        // 测试 seam 让测试拦截 picker 和 progress dialog,不需要真 WPF dialog。
        var profileLoader = new BaseEnvProfileLoader(_tempRoot);
        return new EnvironmentListViewModel(
            _repo, null!, null!, null!, null!,
            profileLoader,
            null!, null!,
            _tempRoot,
            reqInstaller ?? new RequirementsInstaller(),
            baseUninstaller ?? new BaseEnvUninstaller(),
            reqUninstaller ?? new RequirementsUninstaller(),
            null!, null!, null!);
    }

    /// <summary>
    /// 假 RequirementsInstaller:不真跑 pip,返 canned result + 记录调用次数。
    /// 跟 EnvironmentListViewModelUninstallTests 的 FakeRequirementsUninstaller 模式对称。
    /// 真 ctor 签名: (AppLogger?, RequirementsFileInstaller?, ComfyUIManagerInstaller?, CommonNodeInstaller?)
    /// 全部默认参数;override InstallAsync 后不需要内部 state,直接 base() 即可。
    /// </summary>
    private class FakeRequirementsInstaller : RequirementsInstaller
    {
        public int InstallCallCount { get; private set; }
        public RequirementsInstallResult NextResult { get; set; } =
            new RequirementsInstallResult(true, false, null, 1);

        public FakeRequirementsInstaller() : base() { }

        public override Task<RequirementsInstallResult> InstallAsync(
            Environment env, IProgress<string>? logProgress = null,
            CancellationToken ct = default)
        {
            InstallCallCount++;
            logProgress?.Report("fake-install-line");
            if (NextResult.Success)
            {
                var markerPath = Path.Combine(
                    env.RootPath, RequirementsInstaller.MarkerFileName);
                try { File.WriteAllText(markerPath, "fake-ts"); } catch { }
            }
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public async Task ToggleRequirementsCommand_Uninstalled_InvokesInstall()
    {
        using var db = new TestDb();
        SeedEnv("e1"); // no marker
        var fakeInstaller = new FakeRequirementsInstaller();
        var sut = NewSut(reqInstaller: fakeInstaller);
        var envInList = sut.Environments[0];

        await sut.ToggleRequirementsAsync(envInList);

        Assert.Equal(1, fakeInstaller.InstallCallCount);
        Assert.True(envInList.IsRequirementsInstalled);
        Assert.Equal("卸依赖", envInList.RequirementsButtonText);
    }

    [Fact]
    public async Task ToggleRequirementsCommand_Installed_InvokesUninstall()
    {
        using var db = new TestDb();
        SeedEnv("e1", writeMarker: true);
        // 用 fake RequirementsUninstaller — 真 uninstaller 跑 pip + ResolveVenvPython 需要
        // 真实 venv python.exe,测试环境没装,会 Fail。fake 返 Success + 删 marker 文件。
        var fakeReqUninstaller = new FakeRequirementsUninstaller();
        var sut = NewSut(reqUninstaller: fakeReqUninstaller);
        var envInList = sut.Environments[0];
        // 拦截 confirm dialog(否则测试环境无 UI dispatcher 会挂死)
        sut.ShowConfirmDialogOverride = (_, _) => true;

        await sut.ToggleRequirementsAsync(envInList);

        Assert.Equal(1, fakeReqUninstaller.CallCount);
        Assert.False(envInList.IsRequirementsInstalled);
        Assert.Equal("装依赖", envInList.RequirementsButtonText);
    }

    [Fact]
    public async Task ToggleRequirementsCommand_Busy_DisabledAndNoOp()
    {
        using var db = new TestDb();
        SeedEnv("e1");
        var fakeInstaller = new FakeRequirementsInstaller();
        var sut = NewSut(reqInstaller: fakeInstaller);
        var envInList = sut.Environments[0];

        // 手动 mark busy(模拟其他 long-running 操作占用 env)
        sut.SetEnvBusyForTest(envInList);
        Assert.False(sut.ToggleRequirementsCommand.CanExecute(envInList));

        await sut.ToggleRequirementsAsync(envInList);

        Assert.Equal(0, fakeInstaller.InstallCallCount);
    }

    [Fact]
    public async Task ToggleRequirementsCommand_InstallFails_LabelStaysAtInstall()
    {
        // G10:失败 → label 回原状态(不是"重试"),按钮 enabled,点击 retry 走完整 install 流程
        // 注:Load() 重新从 DB 读 env,需要用 sut.Environments[0] 验最新 label(非 SeedEnv 返回的旧实例)。
        using var db = new TestDb();
        SeedEnv("e1");
        var fakeInstaller = new FakeRequirementsInstaller
        {
            NextResult = new RequirementsInstallResult(false, false, "fake fail", 0),
        };
        var sut = NewSut(reqInstaller: fakeInstaller);

        await sut.ToggleRequirementsAsync(sut.Environments[0]);

        Assert.Equal(1, fakeInstaller.InstallCallCount);
        Assert.False(sut.Environments[0].IsRequirementsInstalled);
        Assert.Equal("装依赖", sut.Environments[0].RequirementsButtonText); // 失败回 install label
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_Uninstalled_InvokesOpenPicker()
    {
        using var db = new TestDb();
        SeedEnv("e1", bedStatus: null);
        var fakeUninstaller = new FakeBaseEnvUninstaller();
        var sut = NewSut(baseUninstaller: fakeUninstaller);
        // 用 sut.Environments[0] 而不是 SeedEnv 返回的 env — Load() 后 DB 实例化新 Environment。
        var envInList = sut.Environments[0];
        // PickerDialogOverride 返单 profile → 等价于用户选了安装
        sut.PickerDialogOverride = (_, _, _) =>
            new List<BaseEnvProfile>
            {
                new BaseEnvProfile { Id = "test-profile", Name = "Test" },
            };
        // ShowProgressDialogOverride 拦截 BaseEnvProgressDialog 显示 + 模拟装完
        var progressCalled = false;
        sut.ShowProgressDialogOverride = (envIds, _, _) =>
        {
            progressCalled = true;
            // 模拟 installer 末尾写 BedStatus="done" + BedProfileId(跟真 BaseEnvInstaller 行为一致)
            foreach (var id in envIds)
            {
                var e = sut.Environments.FirstOrDefault(x => x.Id == id);
                if (e is not null) { e.BedStatus = "done"; e.BedProfileId = "test-profile"; }
            }
        };

        await sut.ToggleBaseEnvAsync(envInList);

        Assert.True(progressCalled);
        Assert.True(envInList.IsBaseEnvInstalled);
        Assert.Equal("卸载基础环境", envInList.BaseEnvButtonText);
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_Installed_InvokesUninstall()
    {
        using var db = new TestDb();
        SeedEnv("e1", bedStatus: "done");
        var fakeUninstaller = new FakeBaseEnvUninstaller();
        var sut = NewSut(baseUninstaller: fakeUninstaller);
        // 拦截 confirm dialog(否则测试环境无 UI dispatcher 会挂死)
        sut.ShowConfirmDialogOverride = (_, _) => true;
        // 用 sut.Environments[0] 而不是 SeedEnv 返回的 env — Load() 后 DB 实例化新 Environment,IsBaseEnvInstalled 由 Load() 设。
        var envInList = sut.Environments[0];

        await sut.ToggleBaseEnvAsync(envInList);

        Assert.Equal(1, fakeUninstaller.CallCount);
        Assert.False(envInList.IsBaseEnvInstalled);
        Assert.Equal("安装基础环境", envInList.BaseEnvButtonText);
    }

    [Fact]
    public async Task ToggleBaseEnvCommand_Busy_DisabledAndNoOp()
    {
        using var db = new TestDb();
        SeedEnv("e1");
        var fakeUninstaller = new FakeBaseEnvUninstaller();
        var sut = NewSut(baseUninstaller: fakeUninstaller);
        var envInList = sut.Environments[0];
        sut.SetEnvBusyForTest(envInList);

        Assert.False(sut.ToggleBaseEnvCommand.CanExecute(envInList));
        await sut.ToggleBaseEnvAsync(envInList);
        Assert.Equal(0, fakeUninstaller.CallCount);
    }

    [Fact]
    public void Load_PopulatesRequirementsButtonTextFromMarkerFile()
    {
        using var db = new TestDb();
        var env1 = SeedEnv("e1", writeMarker: true);
        var env2 = SeedEnv("e2"); // no marker
        var sut = NewSut();

        Assert.Equal("卸依赖", sut.Environments[0].RequirementsButtonText);
        Assert.True(sut.Environments[0].IsRequirementsInstalled);
        Assert.Equal("装依赖", sut.Environments[1].RequirementsButtonText);
        Assert.False(sut.Environments[1].IsRequirementsInstalled);
    }

    [Fact]
    public void Load_PopulatesBaseEnvButtonTextFromBedStatus()
    {
        using var db = new TestDb();
        var env1 = SeedEnv("e1", bedStatus: "done");
        var env2 = SeedEnv("e2", bedStatus: null);
        var sut = NewSut();

        Assert.Equal("卸载基础环境", sut.Environments[0].BaseEnvButtonText);
        Assert.True(sut.Environments[0].IsBaseEnvInstalled);
        Assert.Equal("安装基础环境", sut.Environments[1].BaseEnvButtonText);
        Assert.False(sut.Environments[1].IsBaseEnvInstalled);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("installing")]
    public void Load_FailedOrInstallingBedStatus_ShowsInstallBaseEnvLabel(string bedStatus)
    {
        // v1.0.0.x:BED install 失败或正在装时,BaseEnvButtonText 显示"安装基础环境"
        // (之前 IsInstalled 把 failed/installing 也算 installed → 显示"卸载基础环境",
        // 用户想重试要先去卸载再装,绕路)。
        using var db = new TestDb();
        SeedEnv("e1", bedStatus: bedStatus);
        var sut = NewSut();

        var env = sut.Environments[0];
        Assert.False(env.IsBaseEnvInstalled);
        Assert.Equal("安装基础环境", env.BaseEnvButtonText);
    }

    /// <summary>
    /// 假 BED uninstaller:沿 EnvironmentListViewModelUninstallTests.cs FakeBaseEnvUninstaller 模式。
    /// 跟踪 Install / Uninstall 路径。
    /// </summary>
    private class FakeBaseEnvUninstaller : BaseEnvUninstaller
    {
        public int CallCount { get; private set; }
        public Environment? LastEnv { get; private set; }
        public BaseEnvUninstallResult NextResult { get; set; } = new(
            Success: true, AlreadyUninstalled: false, EnvWasRunning: false, Reason: null);

        public override BaseEnvUninstallResult Uninstall(Environment env)
        {
            CallCount++;
            LastEnv = env;
            if (NextResult.Success && !NextResult.AlreadyUninstalled)
            {
                env.BedStatus = null;
                env.BedProfileId = null;
                env.BedFailedReason = null;
            }
            return NextResult;
        }
    }

    /// <summary>
    /// 假 Requirements uninstaller:不真跑 pip,返 canned result + 删 marker 文件(模拟
    /// 真 uninstaller 成功路径)— 让 VM 走完成功分支设 IsRequirementsInstalled = false。
    /// </summary>
    private class FakeRequirementsUninstaller : RequirementsUninstaller
    {
        public int CallCount { get; private set; }
        public Environment? LastEnv { get; private set; }

        public override Task<RequirementsUninstallResult> UninstallAsync(
            Environment env,
            IProgress<string>? logProgress = null,
            CancellationToken ct = default)
        {
            CallCount++;
            LastEnv = env;
            // 模拟真 uninstaller 成功路径:删 marker 文件。
            var markerPath = Path.Combine(
                env.RootPath, RequirementsInstaller.MarkerFileName);
            try { if (File.Exists(markerPath)) File.Delete(markerPath); } catch { }
            return Task.FromResult(new RequirementsUninstallResult(
                Success: true, AlreadyUninstalled: false, Cancelled: false, Reason: null, UninstalledCount: 0));
        }
    }
}