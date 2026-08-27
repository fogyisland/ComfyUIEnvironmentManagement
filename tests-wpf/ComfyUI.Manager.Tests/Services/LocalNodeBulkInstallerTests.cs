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
/// v1.0.0.x #577:LocalNodeBulkInstaller 单元测试 — 覆盖 IsInstalled 三态、EnumerateLocalPackageNames
/// 过滤排序、ResolveSourceDirectory 相对/绝对路径解析、InstallAsync happy / pip 失败 skip /
/// 全失败 / 空目录 等分支。
///
/// <para>
/// pip 路径用 <see cref="FakeRequirementsInstaller"/> 注入 — 真实跑 pip 会拉 PyPI,测试不依赖网络。
/// </para>
/// </summary>
public class LocalNodeBulkInstallerTests : IDisposable
{
    private readonly string _srcDir;
    private readonly string _envRoot;
    private readonly Settings _settings;

    public LocalNodeBulkInstallerTests()
    {
        _srcDir = Path.Combine(Path.GetTempPath(), "lnbi-src-" + Guid.NewGuid().ToString("N"));
        _envRoot = Path.Combine(Path.GetTempPath(), "lnbi-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_srcDir);
        _settings = new Settings
        {
            LocalNodesDirectory = _srcDir  // 测试基线:用绝对临时目录
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_srcDir)) TryDelete(_srcDir);
        if (Directory.Exists(_envRoot)) TryDelete(_envRoot);
    }

    private static void TryDelete(string p)
    {
        try { Directory.Delete(p, recursive: true); } catch { /* 容忍 lock */ }
    }

    private Environment SeedEnv(string id = "env-1", string? customNodesPath = null)
    {
        customNodesPath ??= Path.Combine(_envRoot, id, "custom_nodes");
        return new Environment
        {
            Id = id,
            Name = id,
            CustomNodesPath = customNodesPath,
            ComfyuiSource = Path.Combine(_envRoot, id, "ComfyUI")
        };
    }

    // ───── EnumerateLocalPackageNames ─────

    [Fact]
    public void Enumerate_MissingDir_ReturnsEmpty()
    {
        var r = LocalNodeBulkInstaller.EnumerateLocalPackageNames(Path.Combine(_srcDir, "nope"));
        Assert.Empty(r);
    }

    [Fact]
    public void Enumerate_EmptyDir_ReturnsEmpty()
    {
        var r = LocalNodeBulkInstaller.EnumerateLocalPackageNames(_srcDir);
        Assert.Empty(r);
    }

    [Fact]
    public void Enumerate_DirsOnly_FilesAndHiddenDirsFiltered()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-b"));
        Directory.CreateDirectory(Path.Combine(_srcDir, ".hidden"));
        File.WriteAllText(Path.Combine(_srcDir, "README.md"), "x");

        var r = LocalNodeBulkInstaller.EnumerateLocalPackageNames(_srcDir);

        Assert.Equal(new[] { "pkg-a", "pkg-b" }, r);
    }

    [Fact]
    public void Enumerate_SortedCaseInsensitive()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "Zeta"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "alpha"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "Beta"));

        var r = LocalNodeBulkInstaller.EnumerateLocalPackageNames(_srcDir);

        // OrdinalIgnoreCase:alpha, Beta, Zeta
        Assert.Equal(new[] { "alpha", "Beta", "Zeta" }, r);
    }

    // ───── IsInstalled ─────

    [Fact]
    public void IsInstalled_SourceEmpty_ReturnsFalse()
    {
        _settings.LocalNodesDirectory = "";  // 故意留空
        var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
        Assert.False(installer.IsInstalled(SeedEnv()));
    }

    [Fact]
    public void IsInstalled_SourceDirMissing_ReturnsFalse()
    {
        _settings.LocalNodesDirectory = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"));
        var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
        Assert.False(installer.IsInstalled(SeedEnv()));
    }

    [Fact]
    public void IsInstalled_EnvCustomNodesMissing_ReturnsFalse()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
        var env = SeedEnv(customNodesPath: Path.Combine(_envRoot, "missing", "custom_nodes"));
        Assert.False(installer.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_AllSourceDirsCopied_ReturnsTrue()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-b"));
        var env = SeedEnv();
        var cnp = env.CustomNodesPath!;
        Directory.CreateDirectory(cnp);
        Directory.CreateDirectory(Path.Combine(cnp, "pkg-a"));
        Directory.CreateDirectory(Path.Combine(cnp, "pkg-b"));

        var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
        Assert.True(installer.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_OneMissing_ReturnsFalse()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-b"));
        var env = SeedEnv();
        var cnp = env.CustomNodesPath!;
        Directory.CreateDirectory(cnp);
        Directory.CreateDirectory(Path.Combine(cnp, "pkg-a"));  // pkg-b 缺失

        var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
        Assert.False(installer.IsInstalled(env));
    }

    // ───── ResolveSourceDirectory ─────

    [Fact]
    public void ResolveSourceDirectory_Empty_ReturnsNull()
    {
        _settings.LocalNodesDirectory = "";
        var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
        Assert.Null(installer.ResolveSourceDirectory());
    }

    [Fact]
    public void ResolveSourceDirectory_AbsoluteExisting_ReturnsAsIs()
    {
        // _srcDir 已存在
        var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
        Assert.Equal(_srcDir, installer.ResolveSourceDirectory());
    }

    [Fact]
    public void ResolveSourceDirectory_AbsoluteMissing_ReturnsNull()
    {
        _settings.LocalNodesDirectory = Path.Combine(Path.GetTempPath(), "lnbi-missing-" + Guid.NewGuid().ToString("N"));
        var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
        Assert.Null(installer.ResolveSourceDirectory());
    }

    [Fact]
    public void ResolveSourceDirectory_RelativePath_ResolvesToBaseDir()
    {
        // 用一个真实存在的相对名(子目录名 = localnodes),让它相对 BaseDirectory 解析
        var relName = "lnbi-rel-" + Guid.NewGuid().ToString("N");
        var relAbs = Path.Combine(AppContext.BaseDirectory, relName);
        Directory.CreateDirectory(relAbs);
        try
        {
            _settings.LocalNodesDirectory = relName;
            var installer = new LocalNodeBulkInstaller(_settings, new RequirementsFileInstaller());
            Assert.Equal(relAbs, installer.ResolveSourceDirectory());
        }
        finally
        {
            TryDelete(relAbs);
        }
    }

    // ───── InstallAsync ─────

    [Fact]
    public async Task InstallAsync_SourceEmpty_Fails()
    {
        _settings.LocalNodesDirectory = "";
        var installer = new LocalNodeBulkInstaller(_settings, new FakeRequirementsInstaller());
        var r = await installer.InstallAsync(SeedEnv(), progress: null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("LocalNodesDirectory", r.Reason);
    }

    [Fact]
    public async Task InstallAsync_SourceDirEmpty_Fails()
    {
        // _srcDir 存在但没子目录
        var installer = new LocalNodeBulkInstaller(_settings, new FakeRequirementsInstaller());
        var r = await installer.InstallAsync(SeedEnv(), progress: null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("本地节点目录为空", r.Reason);
    }

    [Fact]
    public async Task InstallAsync_EnvMissingCustomNodesPath_Fails()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        var installer = new LocalNodeBulkInstaller(_settings, new FakeRequirementsInstaller());
        var env = SeedEnv();
        env.CustomNodesPath = "";

        var r = await installer.InstallAsync(env, progress: null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("custom_nodes", r.Reason);
    }

    [Fact]
    public async Task InstallAsync_EnvMissingComfyuiSource_Fails()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        var installer = new LocalNodeBulkInstaller(_settings, new FakeRequirementsInstaller());
        var env = SeedEnv();
        env.ComfyuiSource = "";

        var r = await installer.InstallAsync(env, progress: null, CancellationToken.None);

        Assert.False(r.Success);
        Assert.Contains("comfyui_source", r.Reason);
    }

    [Fact]
    public async Task InstallAsync_HappyPath_CopiesAllWithoutPip()
    {
        // 两个包,都没 requirements.txt → 只 copy,跑 fake pip 不触发
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-a", "code.py"), "a");
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-b"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-b", "code.py"), "b");

        var env = SeedEnv();
        var fakePip = new FakeRequirementsInstaller();
        var installer = new LocalNodeBulkInstaller(_settings, fakePip);

        var r = await installer.InstallAsync(env, progress: null, CancellationToken.None);

        Assert.True(r.Success, $"Reason={r.Reason}");
        Assert.Contains("2/2", r.Version);
        Assert.Equal(0, fakePip.Calls);
        Assert.True(File.Exists(Path.Combine(env.CustomNodesPath!, "pkg-a", "code.py")));
        Assert.True(File.Exists(Path.Combine(env.CustomNodesPath!, "pkg-b", "code.py")));
    }

    [Fact]
    public async Task InstallAsync_PipFailure_SkipsAndContinues()
    {
        // pkg-a 有 requirements + fake pip fail → skip;pkg-b 无 requirements → success
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-a", "requirements.txt"), "requests==1.0");
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-b"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-b", "code.py"), "b");

        var env = SeedEnv();
        // env.VenvPath 不指真实存在 → TryResolveVenvPython 返 null → pip 跳过,只 copy
        // 所以这里 fakePip.Calls == 0;但因为 venvPath 缺失,pkg-a 还是算 success(copy 完成)
        var fakePip = new FakeRequirementsInstaller { FailNext = true };
        var installer = new LocalNodeBulkInstaller(_settings, fakePip);

        var r = await installer.InstallAsync(env, progress: null, CancellationToken.None);

        Assert.True(r.Success, r.Reason);
        Assert.Equal(0, fakePip.Calls);  // venvPython null → 整个 pip 块被跳过
        Assert.Contains("2/2", r.Version);
    }

    [Fact]
    public async Task InstallAsync_PipFailureWithVenv_RecordsFailReason()
    {
        // pkg-a 有 requirements.txt + 真实 venv python(指向 file 但内容无所谓,fake installer 不真跑) →
        // fakePip.FailNext=true → InstallAsync 标记 pkg-a 失败,但仍 copy 完成
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-a", "requirements.txt"), "requests==1.0");

        var env = SeedEnv();
        // VenvPath 指一个 fake venv,Scripts/python.exe 写一个空文件(fake 不会真跑)
        var fakeVenv = Path.Combine(_envRoot, "fakevenv");
        Directory.CreateDirectory(Path.Combine(fakeVenv, "Scripts"));
        File.WriteAllText(Path.Combine(fakeVenv, "Scripts", "python.exe"), "fake");  // 内容无所谓
        env.VenvPath = fakeVenv;

        var fakePip = new FakeRequirementsInstaller { FailNext = true };
        var installer = new LocalNodeBulkInstaller(_settings, fakePip);

        var r = await installer.InstallAsync(env, progress: null, CancellationToken.None);

        // pkg-a copy ok + pip fail → 不算 success(successNames.Count==0 因为 pip 失败时 continue 跳过 successNames.Add)
        // 所以 overall Fail
        Assert.False(r.Success);
        Assert.Contains("pkg-a", r.Reason);
        Assert.Contains("全部失败", r.Reason);
        Assert.True(fakePip.Calls >= 1);  // pip 真调了
    }

    [Fact]
    public async Task InstallAsync_ProgressReportsStageAndInfoLines()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));

        var env = SeedEnv();
        var installer = new LocalNodeBulkInstaller(_settings, new FakeRequirementsInstaller());

        var lines = new List<string>();
        var progress = new Progress<string>(l => lines.Add(l));
        var r = await installer.InstallAsync(env, progress, CancellationToken.None);

        Assert.True(r.Success, r.Reason);
        Assert.Contains(lines, l => l.StartsWith("stage:"));
        Assert.Contains(lines, l => l.Contains("copy pkg-a"));
        Assert.Contains(lines, l => l.Contains("1/1"));
    }

    [Fact]
    public async Task InstallAsync_ExistingTarget_RemovedAndReplaced()
    {
        // env 的 custom_nodes/pkg-a 已有旧文件 → 应该被删再重建(不是 merge)
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));
        File.WriteAllText(Path.Combine(_srcDir, "pkg-a", "code.py"), "new");

        var env = SeedEnv();
        var cnp = env.CustomNodesPath!;
        Directory.CreateDirectory(Path.Combine(cnp, "pkg-a"));
        File.WriteAllText(Path.Combine(cnp, "pkg-a", "old.txt"), "old");
        File.WriteAllText(Path.Combine(cnp, "pkg-a", "code.py"), "old");

        var installer = new LocalNodeBulkInstaller(_settings, new FakeRequirementsInstaller());
        var r = await installer.InstallAsync(env, progress: null, CancellationToken.None);

        Assert.True(r.Success, r.Reason);
        Assert.False(File.Exists(Path.Combine(cnp, "pkg-a", "old.txt")));  // 旧文件被 RobustDirectoryDelete 干掉
        Assert.Equal("new", File.ReadAllText(Path.Combine(cnp, "pkg-a", "code.py")));
    }

    [Fact]
    public async Task InstallAsync_CancellationRequested_Throws()
    {
        Directory.CreateDirectory(Path.Combine(_srcDir, "pkg-a"));

        var env = SeedEnv();
        var installer = new LocalNodeBulkInstaller(_settings, new FakeRequirementsInstaller());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.InstallAsync(env, progress: null, cts.Token));
    }

    // ───── Fake ─────

    /// <summary>
    /// 注入 pip 行为 — 默认 Success=true,设 FailNext=true 让下次 InstallAsync 返 Fail。
    /// 跟踪调用次数便于断言「pip 是否被触发」。
    /// </summary>
    private sealed class FakeRequirementsInstaller : RequirementsFileInstaller
    {
        public int Calls { get; private set; }
        public bool FailNext { get; set; }

        public override Task<RequirementsInstallResult> InstallAsync(
            string requirementsFilePath,
            string filteredOutputPath,
            string venvPythonPath,
            Action<string>? onLine,
            CancellationToken ct)
        {
            Calls++;
            onLine?.Invoke("fake pip line");
            if (FailNext)
            {
                FailNext = false;
                return Task.FromResult(new RequirementsInstallResult(
                    Success: false, Cancelled: false, Reason: "fake pip fail", InstalledCount: 0));
            }
            return Task.FromResult(new RequirementsInstallResult(
                Success: true, Cancelled: false, Reason: null, InstalledCount: 1));
        }
    }
}