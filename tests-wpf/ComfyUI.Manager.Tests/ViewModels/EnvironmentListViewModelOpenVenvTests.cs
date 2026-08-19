using System;
using System.IO;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Tests.Fakes;
using ComfyUI.Manager.ViewModels;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.ViewModels;

public class EnvironmentListViewModelOpenVenvTests
{
    private static (EnvironmentListViewModel vm, TestDb db) NewVm()
    {
        var db = new TestDb();
        var repo = new EnvironmentRepository(db.Factory);
        var vm = new EnvironmentListViewModel(
            repo,
            null!,
            null!,
            null!,
            new Settings(),
            null!,
            null!,
            null!,
            Path.GetTempPath(),
            null!);
        return (vm, db);
    }

    [Fact]
    public void OpenVenvCommand_ValidEnvWithVenvPath_CanExecute()
    {
        // v0.6.22 T4: CanExecute 应该返回 true 当 env 有 VenvPath + 目录存在。
        var (vm, db) = NewVm();
        using (db)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "ComfyUIMgrVenvTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                var env = new Environment { Id = "test-env", Name = "TestEnv", VenvPath = tmpDir };
                vm.Environments.Add(env);

                Assert.True(vm.OpenVenvCommand.CanExecute(env));
            }
            finally
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void OpenVenvCommand_NullParam_CannotExecute()
    {
        // v0.6.22 T4: CanExecute 应该返回 false 当参数不是 Environment。
        var (vm, db) = NewVm();
        using (db)
        {
            Assert.False(vm.OpenVenvCommand.CanExecute(null));
        }
    }

    [Fact]
    public void OpenVenvCommand_EnvWithMissingVenvDir_CannotExecute()
    {
        // v0.6.22 T4: CanExecute 应该返回 false 当 VenvPath 指向不存在的目录(避免已删
        // env 点图标静默失败)。
        var (vm, db) = NewVm();
        using (db)
        {
            var env = new Environment
            {
                Id = "test-env",
                Name = "TestEnv",
                VenvPath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"))
            };

            Assert.False(vm.OpenVenvCommand.CanExecute(env));
        }
    }
}
