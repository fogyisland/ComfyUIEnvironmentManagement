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
using ComfyUI.Manager.Views;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class BaseEnvViewModelReselectTests : IDisposable
{
    private static BaseEnvProfile Profile(string torch, string cuda) =>
        new() { Id = $"torch=={torch}+{cuda}", TorchVersion = torch, CudaVersion = cuda, CudaVariant = cuda };

    private static (BaseEnvViewModel vm, TestBaseEnvProfileLoader loader, TestDb db) MakeVm()
    {
        var db = new TestDb();
        var appDataDir = Path.Combine(Path.GetTempPath(), $"picker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appDataDir);
        var loader = new TestBaseEnvProfileLoader(appDataDir);
        var directory = new TestPyTorchVersionDirectory(appDataDir);
        var envRepo = new EnvironmentRepository(db.Factory);
        var installer = new FakeBaseEnvInstaller(envRepo);
        var vm = new BaseEnvViewModel(loader, envRepo, installer, directory, appDataDir);
        vm.PickerDialogOverride = (_, _, _) => null;
        return (vm, loader, db);
    }

    [Fact]
    public void ReselectCommand_PickerReturnsSelection_UpdatesSelectedProfiles()
    {
        var (vm, loader, db) = MakeVm();
        var p1 = Profile("2.4.1", "cu118");
        var p2 = Profile("2.4.1", "cu121");
        loader.Hardcoded = new[] { p1, p2 };
        vm.LoadAsync().GetAwaiter().GetResult();
        vm.PickerDialogOverride = (_, _, _) => new[] { p2 };
        vm.ReselectCommand.Execute(null);
        Assert.Single(vm.SelectedProfiles);
        Assert.Equal(p2, vm.SelectedProfiles[0]);
        db.Dispose();
    }

    [Fact]
    public void ReselectCommand_PickerCancel_DoesNotChangeSelection()
    {
        var (vm, loader, db) = MakeVm();
        var p1 = Profile("2.4.1", "cu118");
        loader.Hardcoded = new[] { p1 };
        vm.LoadAsync().GetAwaiter().GetResult();
        // LoadAsync 不会自动选 profile,先手动选上才能验证 picker 取消后选择不变
        vm.SetSelectedProfiles(new[] { p1 });
        Assert.Single(vm.SelectedProfiles);
        var before = vm.SelectedProfiles.ToList();
        vm.PickerDialogOverride = (_, _, _) => null;
        vm.ReselectCommand.Execute(null);
        Assert.Equal(before, vm.SelectedProfiles);
        db.Dispose();
    }

    [Fact]
    public void ReselectCommand_Preselected_PassesToPicker()
    {
        var (vm, loader, db) = MakeVm();
        var p1 = Profile("2.4.1", "cu118");
        var p2 = Profile("2.4.1", "cu121");
        loader.Hardcoded = new[] { p1, p2 };
        vm.LoadAsync().GetAwaiter().GetResult();
        // LoadAsync 不会自动选 profile,先手动选 p1 才能验证 picker 收到的 preselected = p1
        vm.SetSelectedProfiles(new[] { p1 });
        BaseEnvProfile? capturedPreselected = null;
        vm.PickerDialogOverride = (_, pre, _) => { capturedPreselected = pre; return null; };
        vm.ReselectCommand.Execute(null);
        Assert.NotNull(capturedPreselected);
        Assert.Equal(p1, capturedPreselected);
        db.Dispose();
    }

    public void Dispose()
    {
        // Cleanup is per-test via db.Dispose() in each test method.
    }

    // ---- test fakes (TestBaseEnvProfileLoader + TestPyTorchVersionDirectory + NoopCache + FakeBaseEnvInstaller) ----
    private sealed class TestBaseEnvProfileLoader : BaseEnvProfileLoader
    {
        public IReadOnlyList<BaseEnvProfile> Hardcoded { get; set; } = Array.Empty<BaseEnvProfile>();
        public TestBaseEnvProfileLoader(string appDataDir) : base(appDataDir) { }
        public override Task<IReadOnlyList<BaseEnvProfile>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult(Hardcoded);
        public override IReadOnlyList<BaseEnvProfile> GetHardcodedDefaults() => Hardcoded;
    }

    private sealed class TestPyTorchVersionDirectory : PyTorchVersionDirectory
    {
        public TestPyTorchVersionDirectory(string scratchDir)
            : base(new PyTorchVersionCatalog(http: null!), new NoopCache(scratchDir)) { }
        public override Task<IReadOnlyList<PyTorchVersionEntry>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PyTorchVersionEntry>>(new[]
            {
                new PyTorchVersionEntry { Version = "2.4.1", DisplayName = "PyTorch 2.4.1" },
            });
    }

    private sealed class NoopCache : PyTorchVersionCatalogCache
    {
        public NoopCache(string appDataDir) : base(appDataDir) { }
        public override Task<IReadOnlyList<PyTorchVersion>?> TryReadAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PyTorchVersion>?>(null);
        public override Task WriteAsync(IReadOnlyList<PyTorchVersion> versions, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeBaseEnvInstaller : BaseEnvInstaller
    {
        public FakeBaseEnvInstaller(EnvironmentRepository envRepo) : base(envRepo) { }
    }
}