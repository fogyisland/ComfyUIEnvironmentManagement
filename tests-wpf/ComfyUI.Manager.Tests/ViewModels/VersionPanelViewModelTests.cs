using System.Linq;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public class VersionPanelViewModelTests
{
    private static void SeedNode(
        TestDb db, string envId, string package, bool locked)
    {
        var repo = new NodeRepository(db.Factory);
        repo.Upsert(new ScannedNode
        {
            Id = $"{envId}:{package}",
            EnvId = envId,
            Package = package,
            PackagePath = $"C:\\envs\\{envId}\\{package}",
            Version = "1.0.0",
            Status = "enabled",
            Locked = locked,
        });
    }

    [Fact]
    public void Ctor_LoadsPackagesAndSelectsFirst()
    {
        using var db = new TestDb();
        SeedNode(db, "env-1", "pkg-a", locked: false);

        var vm = new VersionPanelViewModel(
            new NodeRepository(db.Factory), "env-1");

        Assert.Single(vm.Packages);
        Assert.Equal("pkg-a", vm.SelectedPackage);
        Assert.Single(vm.Versions);
        Assert.Equal("1.0.0", vm.Versions[0].CurrentVersion);
    }

    [Fact]
    public void UpgradeCommand_DisabledWhenLocked()
    {
        using var db = new TestDb();
        SeedNode(db, "env-1", "pkg-a", locked: true);

        var vm = new VersionPanelViewModel(
            new NodeRepository(db.Factory), "env-1");
        vm.SelectedPackage = "pkg-a";

        Assert.False(vm.UpgradeCommand.CanExecute(vm.Versions.FirstOrDefault()));
        Assert.True(vm.Versions[0].Locked);
    }
}
