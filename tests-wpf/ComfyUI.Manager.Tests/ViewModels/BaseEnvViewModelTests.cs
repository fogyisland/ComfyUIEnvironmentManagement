using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using EnvModel = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class BaseEnvViewModelTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _appDataDir;
    private readonly EnvironmentRepository _envRepo;
    private readonly FakeBaseEnvInstaller _installer;

    public BaseEnvViewModelTests()
    {
        _envRepo = new EnvironmentRepository(_db.Factory);
        _installer = new FakeBaseEnvInstaller(_envRepo);
        _appDataDir = Path.Combine(
            Path.GetTempPath(), $"base-env-vm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_appDataDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_appDataDir)) Directory.Delete(_appDataDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private BaseEnvViewModel MakeVm()
        => new(new BaseEnvProfileLoader(_appDataDir), _envRepo, _installer,
              new FakeDirectory(entries: null, scratchDir: _appDataDir), _appDataDir);

    private BaseEnvViewModel MakeVmWithDirectory(PyTorchVersionDirectory directory)
        => new(new BaseEnvProfileLoader(_appDataDir), _envRepo, _installer,
              directory, _appDataDir);

    private static EnvModel FakeEnv(string id) => new()
    {
        Id = id,
        Name = id,
        RootPath = $"/tmp/{id}",
        VenvPath = $"/tmp/{id}/venv",
        CustomNodesPath = $"/tmp/{id}/nodes",
        Port = 8188,
        Status = "stopped",
    };

    [Fact]
    public void Ctor_DoesNotLoadEnvsOrProfiles()
    {
        var vm = MakeVm();
        Assert.Empty(vm.Profiles);
        Assert.Empty(vm.Envs);
        Assert.Empty(vm.SelectedProfiles);
        Assert.Empty(vm.SelectedEnvIds);
        Assert.Empty(vm.Versions);
        Assert.Null(vm.SelectedVersion);
        Assert.False(vm.IsUserOverrideActive);
    }

    [Fact]
    public async Task LoadAsync_PopulatesProfilesAndEnvs()
    {
        _envRepo.Upsert(FakeEnv("env-a"));
        _envRepo.Upsert(FakeEnv("env-b"));

        // MakeVm()'s default FakeDirectory is empty — use a populated one for
        // the legacy "loads profiles" semantics.
        var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStable("2.13.0"));
        await vm.LoadAsync();

        Assert.NotEmpty(vm.Profiles);
        Assert.Equal(2, vm.Envs.Count);
        Assert.Contains(vm.Envs, e => e.Id == "env-a");
        Assert.Contains(vm.Envs, e => e.Id == "env-b");
    }

    [Fact]
    public async Task LoadAsync_ReloadsEnvsOnSecondCall()
    {
        var vm = MakeVm();
        await vm.LoadAsync();
        Assert.Empty(vm.Envs);

        _envRepo.Upsert(FakeEnv("env-new"));
        _envRepo.Upsert(FakeEnv("env-other"));

        await vm.LoadAsync();

        Assert.Equal(2, vm.Envs.Count);
        Assert.Contains(vm.Envs, e => e.Id == "env-new");
    }

    [Fact]
    public async Task LoadAsync_ClearsPreviousSelection()
    {
        _envRepo.Upsert(FakeEnv("env-a"));
        var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStable("2.13.0"));
        await vm.LoadAsync();

        vm.SetSelectedProfiles(vm.Profiles.Take(1));
        vm.SetSelectedEnvIds(vm.Envs);
        Assert.Single(vm.SelectedProfiles);
        Assert.Single(vm.SelectedEnvIds);

        await vm.LoadAsync(); // 重新加载应清空选择

        Assert.Empty(vm.SelectedProfiles);
        Assert.Empty(vm.SelectedEnvIds);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void SetSelectedProfiles_StoresAndRaisesStartCanExecuteChanged()
    {
        var vm = MakeVm();
        // Need at least one env selection for CanStart to be true.
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
        Assert.False(vm.StartCommand.CanExecute(null));

        var profiles = new List<BaseEnvProfile>
        {
            new() { Id = "p1", Name = "P1" },
            new() { Id = "p2", Name = "P2" },
        };
        vm.SetSelectedProfiles(profiles);

        Assert.Equal(2, vm.SelectedProfiles.Count);
        Assert.Equal("p1", vm.SelectedProfiles[0].Id);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void SetSelectedEnvIds_StoresAndRaisesStartCanExecuteChanged()
    {
        var vm = MakeVm();
        vm.SetSelectedProfiles(new[] { new BaseEnvProfile { Id = "p1" } });
        Assert.False(vm.StartCommand.CanExecute(null));

        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a"), FakeEnv("env-b") });

        Assert.Equal(2, vm.SelectedEnvIds.Count);
        Assert.Contains("env-a", vm.SelectedEnvIds);
        Assert.Contains("env-b", vm.SelectedEnvIds);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenNoSelection()
    {
        var vm = MakeVm();
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenEnvsSelectedButNoProfile()
    {
        var vm = MakeVm();
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenProfileSelectedButNoEnv()
    {
        var vm = MakeVm();
        vm.SetSelectedProfiles(new[] { new BaseEnvProfile { Id = "p1" } });
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CanExecute_WhenBothHaveOne()
    {
        var vm = MakeVm();
        vm.SetSelectedProfiles(new[] { new BaseEnvProfile { Id = "p1" } });
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Start_LaunchesBaseEnvProgressDialog_WithFirstProfile()
    {
        var vm = MakeVm();
        env_a_setup(vm);
        var firstProfile = new BaseEnvProfile { Id = "first", Name = "First" };
        var secondProfile = new BaseEnvProfile { Id = "second", Name = "Second" };
        vm.SetSelectedProfiles(new[] { firstProfile, secondProfile });

        (IReadOnlyList<string>? Ids, BaseEnvProfile? Profile, BaseEnvInstaller? Inst) captured =
            (null, null, null);
        vm.ShowDialogOverride = (ids, p, inst) => captured = (ids, p, inst);

        vm.StartCommand.Execute(null);

        Assert.NotNull(captured.Ids);
        Assert.NotNull(captured.Profile);
        Assert.NotNull(captured.Inst);
        Assert.Equal(new[] { "env-a" }, captured.Ids);
        Assert.Same(firstProfile, captured.Profile); // 多 profile → 取第一个
        Assert.Same(_installer, captured.Inst);
    }

    [Fact]
    public void Start_NoOp_WhenNoSelection()
    {
        var vm = MakeVm();
        bool called = false;
        vm.ShowDialogOverride = (_, _, _) => called = true;

        vm.StartCommand.Execute(null);

        Assert.False(called);
    }

    [Fact]
    public void Start_NoOp_WhenNoProfile()
    {
        var vm = MakeVm();
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
        bool called = false;
        vm.ShowDialogOverride = (_, _, _) => called = true;

        vm.StartCommand.Execute(null);

        Assert.False(called);
    }

    [Fact]
    public void Start_NoOp_WhenNoEnv()
    {
        var vm = MakeVm();
        vm.SetSelectedProfiles(new[] { new BaseEnvProfile { Id = "p1" } });
        bool called = false;
        vm.ShowDialogOverride = (_, _, _) => called = true;

        vm.StartCommand.Execute(null);

        Assert.False(called);
    }

    [Fact]
    public void Ctor_NullArguments_Throw()
    {
        var dir = new FakeDirectory(entries: null, scratchDir: _appDataDir);
        Assert.Throws<ArgumentNullException>(() =>
            new BaseEnvViewModel(null!, _envRepo, _installer, dir, _appDataDir));
        Assert.Throws<ArgumentNullException>(() =>
            new BaseEnvViewModel(new BaseEnvProfileLoader(_appDataDir), null!, _installer, dir, _appDataDir));
        Assert.Throws<ArgumentNullException>(() =>
            new BaseEnvViewModel(new BaseEnvProfileLoader(_appDataDir), _envRepo, null!, dir, _appDataDir));
    }

    [Fact]
    public void Ctor_NullDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BaseEnvViewModel(new BaseEnvProfileLoader(_appDataDir), _envRepo, _installer,
                directory: null!, _appDataDir));
    }

    [Fact]
    public void Ctor_NullAppDataDir_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new BaseEnvViewModel(new BaseEnvProfileLoader(_appDataDir), _envRepo, _installer,
                new FakeDirectory(entries: null, scratchDir: _appDataDir), appDataDir: ""));
    }

    // ----- Task 5: multi-version BED VM -----

    [Fact]
    public async Task LoadAsync_UserOverrideFile_LoadsFileAndSetsIsUserOverrideActiveTrue()
    {
        var path = Path.Combine(_appDataDir, "base_env_profiles.json");
        var custom = new List<BaseEnvProfile>
        {
            new() { Id = "custom-1", Name = "Custom 1", TorchVersion = "2.5.0", CudaVersion = "cu118", Channel = "stable" },
            new() { Id = "custom-2", Name = "Custom 2", TorchVersion = "2.6.0", CudaVersion = "cu121", Channel = "stable" },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(custom));

        var vm = MakeVmWithDirectory(FakeDirectoryWithStable("2.13.0"));
        await vm.LoadAsync();

        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal("custom-1", vm.Profiles[0].Id);
        Assert.Equal("custom-2", vm.Profiles[1].Id);
        Assert.Empty(vm.Versions);
        Assert.True(vm.IsUserOverrideActive);
        Assert.Null(vm.SelectedVersion);
    }

    [Fact]
    public async Task LoadAsync_UserOverrideCorruptJson_FallsThroughToMultiVersion()
    {
        var path = Path.Combine(_appDataDir, "base_env_profiles.json");
        File.WriteAllText(path, "{not valid json][");

        var vm = MakeVmWithDirectory(FakeDirectoryWithStable("2.13.0"));
        await vm.LoadAsync();

        Assert.False(vm.IsUserOverrideActive);
        // Directory was consulted → Versions populated, SelectedVersion is stable.
        Assert.NotEmpty(vm.Versions);
        Assert.NotNull(vm.SelectedVersion);
        Assert.False(vm.SelectedVersion!.IsNightly);
    }

    [Fact]
    public async Task LoadAsync_NoUserOverride_DefaultsToLatestStableAndLoadsProfiles()
    {
        var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStable("2.13.0", "2.12.0"));
        await vm.LoadAsync();

        Assert.True(vm.Versions[0].IsNightly);
        Assert.Equal("2.13.0", vm.SelectedVersion!.Version);
        Assert.False(vm.SelectedVersion.IsNightly);
        Assert.NotEmpty(vm.Profiles);
        Assert.All(vm.Profiles, p => Assert.Equal("2.13.0", p.TorchVersion));
    }

    [Fact]
    public async Task LoadAsync_NoUserOverride_NightlyIsFirstInVersions()
    {
        var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStable("2.13.0", "2.12.0"));
        await vm.LoadAsync();

        Assert.True(vm.Versions[0].IsNightly);
        Assert.Equal("nightly", vm.Versions[0].Version);
        Assert.Equal("PyTorch Nightly", vm.Versions[0].DisplayName);
    }

    [Fact]
    public async Task SelectedVersion_Stable_LoadsProfilesForThatVersion()
    {
        var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStable("2.13.0", "2.12.0"));
        await vm.LoadAsync();

        // Default was 2.13.0; switch to 2.12.0 entry.
        var entry212 = vm.Versions.First(e => e.Version == "2.12.0");
        vm.SelectedVersion = entry212;
        // Wait for the fire-and-forget async reload to settle.
        await vm.LastReloadTask;

        Assert.Equal("2.12.0", vm.SelectedVersion!.Version);
        Assert.NotEmpty(vm.Profiles);
        Assert.All(vm.Profiles, p => Assert.Equal("2.12.0", p.TorchVersion));
        // Profile selection cleared on selection change.
        Assert.Empty(vm.SelectedProfiles);
    }

    [Fact]
    public async Task SelectedVersion_Nightly_LoadsNightlyCu126Profile()
    {
        var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStable("2.13.0", "2.12.0"));
        await vm.LoadAsync();

        var nightly = vm.Versions[0];
        Assert.True(nightly.IsNightly);
        vm.SelectedVersion = nightly;
        await vm.LastReloadTask;

        Assert.Single(vm.Profiles);
        Assert.Equal("nightly", vm.Profiles[0].TorchVersion);
        Assert.Equal("cu126", vm.Profiles[0].CudaVersion);
    }

    [Fact]
    public async Task SelectedVersion_Change_PreservesEnvSelection()
    {
        _envRepo.Upsert(FakeEnv("env-a"));
        _envRepo.Upsert(FakeEnv("env-b"));
        var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStable("2.13.0", "2.12.0"));
        await vm.LoadAsync();

        // Force at least one profile (the stable 2.13.0 set has CUDA profiles,
        // so an env selection plus a profile selection yields a valid CanStart).
        vm.SetSelectedProfiles(new[] { vm.Profiles[0] });
        vm.SetSelectedEnvIds(vm.Envs);
        Assert.Equal(2, vm.SelectedEnvIds.Count);
        Assert.True(vm.StartCommand.CanExecute(null));

        var entry212 = vm.Versions.First(e => e.Version == "2.12.0");
        vm.SelectedVersion = entry212;
        await vm.LastReloadTask;

        // Env selection preserved across version change.
        Assert.Equal(2, vm.SelectedEnvIds.Count);
        Assert.Contains("env-a", vm.SelectedEnvIds);
        Assert.Contains("env-b", vm.SelectedEnvIds);
    }

    [Fact]
    public async Task SelectedVersion_Change_ClearsProfileSelection()
    {
        var vm = MakeVmWithDirectory(FakeDirectoryWithNightlyAndStable("2.13.0", "2.12.0"));
        await vm.LoadAsync();

        // Make a profile selection.
        vm.SetSelectedProfiles(new[] { vm.Profiles[0] });
        Assert.NotEmpty(vm.SelectedProfiles);

        var entry212 = vm.Versions.First(e => e.Version == "2.12.0");
        vm.SelectedVersion = entry212;
        await vm.LastReloadTask;

        Assert.Empty(vm.SelectedProfiles);
    }

    // ----- Fakes & helpers -----

    /// <summary>
    /// In-memory <see cref="PyTorchVersionDirectory"/> that skips the cache
    /// / fetch chain and returns a preset entry list. We pass a
    /// <see cref="PyTorchVersionCatalog"/> constructed with <c>http: null</c>
    /// (its <c>FetchAsync</c> won't be called because we override
    /// <see cref="GetAllAsync"/>) and an in-memory cache stub.
    /// </summary>
    private sealed class FakeDirectory : PyTorchVersionDirectory
    {
        private readonly IReadOnlyList<PyTorchVersionEntry>? _entries;

        public FakeDirectory(IReadOnlyList<PyTorchVersionEntry>? entries, string scratchDir)
            : base(new PyTorchVersionCatalog(http: null!), new NoopCache(scratchDir))
        {
            _entries = entries;
        }

        public override Task<IReadOnlyList<PyTorchVersionEntry>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PyTorchVersionEntry>>(
                _entries ?? Array.Empty<PyTorchVersionEntry>());
    }

    /// <summary>
    /// Minimal cache that pretends to be empty and accepts writes (which
    /// never happen because the fake directory bypasses <see cref="GetAllAsync"/>).
    /// </summary>
    private sealed class NoopCache : PyTorchVersionCatalogCache
    {
        public NoopCache(string appDataDir) : base(appDataDir) { }

        public override Task<IReadOnlyList<PyTorchVersion>?> TryReadAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PyTorchVersion>?>(null);

        public override Task WriteAsync(IReadOnlyList<PyTorchVersion> versions, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private FakeDirectory FakeDirectoryWithStable(params string[] versions)
    {
        var entries = new List<PyTorchVersionEntry>();
        foreach (var v in versions)
        {
            entries.Add(new PyTorchVersionEntry
            {
                Version = v,
                IsNightly = false,
                DisplayName = $"PyTorch {v}",
                StableMetadata = new PyTorchVersion
                {
                    Version = v,
                    ReleaseDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    CudaVariants = new[] { "cu118", "cu121", "cu124", "cu126" },
                    HasCpu = true,
                },
            });
        }
        return new FakeDirectory(entries, _appDataDir);
    }

    private FakeDirectory FakeDirectoryWithNightlyAndStable(params string[] stableVersions)
    {
        var nightly = new PyTorchVersionEntry
        {
            Version = "nightly",
            IsNightly = true,
            DisplayName = "PyTorch Nightly",
            StableMetadata = null,
        };
        var entries = new List<PyTorchVersionEntry> { nightly };
        foreach (var v in stableVersions)
        {
            entries.Add(new PyTorchVersionEntry
            {
                Version = v,
                IsNightly = false,
                DisplayName = $"PyTorch {v}",
                StableMetadata = new PyTorchVersion
                {
                    Version = v,
                    ReleaseDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    CudaVariants = new[] { "cu118", "cu121", "cu124", "cu126" },
                    HasCpu = true,
                },
            });
        }
        return new FakeDirectory(entries, _appDataDir);
    }

    private void env_a_setup(BaseEnvViewModel vm)
    {
        _envRepo.Upsert(FakeEnv("env-a"));
        vm.SetSelectedEnvIds(new[] { FakeEnv("env-a") });
    }

    /// <summary>
    /// Minimal local fake:BaseEnvInstallerTests 的 FakeBaseEnvInstaller 是 private nested,
    /// 这里只需要 ctor + InstallAsync 不被实际调用。Start 测试只走 ShowDialogOverride。
    /// </summary>
    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public FakeBaseEnvInstaller(EnvironmentRepository envRepo) : base(envRepo) { }
    }
}
