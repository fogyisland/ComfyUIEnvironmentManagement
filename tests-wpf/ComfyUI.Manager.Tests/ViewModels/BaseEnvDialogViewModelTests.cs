using System.Collections.Generic;
using System.Linq;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.ViewModels;
using Xunit;

namespace ComfyUI.Manager.Tests.ViewModels;

public sealed class BaseEnvDialogViewModelTests
{
    private static Environment FakeEnv(string id) => new()
    {
        Id = id,
        Name = id,
        RootPath = $"/tmp/{id}",
        VenvPath = $"/tmp/{id}/venv",
        CustomNodesPath = $"/tmp/{id}/nodes",
        Port = 8188,
        Status = "stopped",
    };

    private static BaseEnvConfig DefaultConfig() => new();

    [Fact]
    public void Ctor_BuildsEnvChoicesUnchecked()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a"), FakeEnv("b") }, DefaultConfig());
        Assert.Equal(2, vm.Envs.Count);
        Assert.All(vm.Envs, c => Assert.False(c.IsChecked));
    }

    [Fact]
    public void Config_StartsAsClone_EditingDoesNotMutateSource()
    {
        var src = DefaultConfig();
        src.Packages.Add("orig-only");
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, src);
        vm.Packages.Add("added-by-dialog");
        Assert.DoesNotContain("added-by-dialog", src.Packages);
        Assert.Contains("orig-only", vm.Packages);  // 副本含(orig-only 是 clone 时已存在)
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenNoEnvSelected()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.Envs[0].IsChecked = true;
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void StartCommand_CannotExecute_WhenPackagesEmpty()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        vm.Envs[0].IsChecked = true;
        vm.Packages.Clear();
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Start_EmitsClosedWithSelectedEnvIdsAndEditedConfig()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a"), FakeEnv("b") }, DefaultConfig());
        BaseEnvDialogResult? captured = null;
        vm.Closed += r => captured = r;

        vm.Envs[1].IsChecked = true;   // 只选 b
        vm.Config.CudaVersion = "cu121";
        vm.StartCommand.Execute(null);

        Assert.NotNull(captured);
        Assert.Equal(new[] { "b" }, captured!.SelectedEnvIds);
        Assert.Equal("cu121", captured.Config.CudaVersion);
    }

    [Fact]
    public void Cancel_EmitsClosedWithNull()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        BaseEnvDialogResult? captured = new(new List<string>(), DefaultConfig());
        vm.Closed += r => captured = r;

        vm.CancelCommand.Execute(null);

        Assert.Null(captured);
    }

    [Fact]
    public void AddPackageCommand_AddsToPackages_ClearsInput()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        vm.NewPackageName = "transformers";
        vm.AddPackageCommand.Execute(null);

        Assert.Contains("transformers", vm.Packages);
        Assert.Equal("", vm.NewPackageName);
    }

    [Fact]
    public void RemovePackageCommand_RemovesByParameter()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        Assert.Contains("torch", vm.Packages);
        vm.RemovePackageCommand.Execute("torch");
        Assert.DoesNotContain("torch", vm.Packages);
    }

    [Fact]
    public void PreviewCommandText_ReflectsConfigState()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        // 默认 cu118 / stable → 含 index-url https://download.pytorch.org/whl/cu118
        Assert.Contains("cu118", vm.PreviewCommandText);
        Assert.Contains("torch", vm.PreviewCommandText);

        // 改 CUDA → preview 应变
        vm.Config.CudaVersion = "cu121";
        Assert.Contains("cu121", vm.PreviewCommandText);
        Assert.DoesNotContain("cu118", vm.PreviewCommandText);

        // 改 CustomPipArgs → 完全覆盖
        vm.Config.CustomPipArgs = "install foo bar";
        Assert.Equal("pip install foo bar", vm.PreviewCommandText);
    }

    [Fact]
    public void CudaVersions_ContainsCpu()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        Assert.Contains("cpu", vm.CudaVersions);
        Assert.Contains("cu118", vm.CudaVersions);
        Assert.Contains("cu124", vm.CudaVersions);
    }

    [Fact]
    public void TorchChannels_ContainsStableAndNightly()
    {
        var vm = new BaseEnvDialogViewModel(
            new[] { FakeEnv("a") }, DefaultConfig());
        Assert.Contains("stable", vm.TorchChannels);
        Assert.Contains("nightly", vm.TorchChannels);
    }
}