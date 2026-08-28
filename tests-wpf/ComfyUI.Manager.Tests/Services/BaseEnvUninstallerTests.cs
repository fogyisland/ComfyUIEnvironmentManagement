using System;
using System.IO;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class BaseEnvUninstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public BaseEnvUninstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"beduninstall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedInstalledEnv()
    {
        return new Environment
        {
            Id = "env-a",
            Name = "env-a",
            RootPath = _tempRoot,
            VenvPath = Path.Combine(_tempRoot, "venv"),
            Status = "stopped",
            BedStatus = "done",
            BedProfileId = "py3.10-cu118",
            BedFailedReason = null,
        };
    }

    [Fact]
    public void Uninstall_NullEnv_ReturnsFailureReason()
    {
        var u = new BaseEnvUninstaller();

        var r = u.Uninstall(null!);

        Assert.False(r.Success);
        Assert.False(r.AlreadyUninstalled);
        Assert.False(r.EnvWasRunning);
        Assert.Equal("env 为空", r.Reason);
    }

    [Fact]
    public void Uninstall_EnvNotInstalled_ReturnsAlreadyUninstalledTrue()
    {
        var env = SeedInstalledEnv();
        env.BedStatus = null;
        env.BedProfileId = null;
        env.BedFailedReason = null;
        var u = new BaseEnvUninstaller();

        var r = u.Uninstall(env);

        Assert.True(r.Success);
        Assert.True(r.AlreadyUninstalled);
        Assert.False(r.EnvWasRunning);
    }

    [Fact]
    public void Uninstall_EnvRunning_ReturnsEnvWasRunningTrue()
    {
        var env = SeedInstalledEnv();
        env.Status = "running";
        var u = new BaseEnvUninstaller();

        var r = u.Uninstall(env);

        Assert.False(r.Success);
        Assert.True(r.EnvWasRunning);
        Assert.Equal("running", env.Status, StringComparer.Ordinal);
        // 未改 BED 字段
        Assert.Equal("done", env.BedStatus);
        Assert.Equal("py3.10-cu118", env.BedProfileId);
    }

    [Fact]
    public void Uninstall_EnvInstalled_ResetsAllBedFields()
    {
        var env = SeedInstalledEnv();
        env.BedFailedReason = "上次 pip 失败";
        var u = new BaseEnvUninstaller();

        var r = u.Uninstall(env);

        Assert.True(r.Success);
        Assert.False(r.AlreadyUninstalled);
        Assert.False(r.EnvWasRunning);
        Assert.Null(env.BedStatus);
        Assert.Null(env.BedProfileId);
        Assert.Null(env.BedFailedReason);
    }

    [Fact]
    public void Uninstall_EnvInstalled_DoesNotDeleteVenvFiles()
    {
        var env = SeedInstalledEnv();
        // 在 RootPath 下写若干文件 + 子目录,模拟 venv 文件
        var venvPy = Path.Combine(_tempRoot, "venv", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(venvPy)!);
        File.WriteAllText(venvPy, "fake-python");
        var venvLib = Path.Combine(_tempRoot, "venv", "Lib", "site-packages", "torch");
        Directory.CreateDirectory(venvLib);
        File.WriteAllText(Path.Combine(venvLib, "__init__.py"), "");
        var marker = Path.Combine(_tempRoot, ".bed-installed");
        File.WriteAllText(marker, "done");

        var u = new BaseEnvUninstaller();
        var r = u.Uninstall(env);

        Assert.True(r.Success);
        // 文件全部仍在(轻量 reset,不动 venv)
        Assert.True(File.Exists(venvPy));
        Assert.True(File.Exists(Path.Combine(venvLib, "__init__.py")));
        Assert.True(File.Exists(marker));
        // BED 字段已清
        Assert.Null(env.BedStatus);
        Assert.Null(env.BedProfileId);
        Assert.Null(env.BedFailedReason);
    }

    // v1.0.0.x:IsInstalled 只在 BedStatus=="done" 时返回 true。之前的实现把 "failed" 和
    // "installing" 也算 installed,导致 BaseEnvButtonText 在 failed 时显示"卸载基础环境",
    // 用户想重试要先去卸载再装,绕路。新行为 failed → 按钮显示"安装基础环境"直接重试。
    [Theory]
    [InlineData("done", true)]
    [InlineData("failed", false)]
    [InlineData("installing", false)]
    [InlineData(null, false)]
    public void IsInstalled_BedStatus_ReturnsTrueOnlyWhenDone(string? bedStatus, bool expected)
    {
        var env = new Environment
        {
            Id = "e", Name = "e", RootPath = _tempRoot,
            VenvPath = Path.Combine(_tempRoot, "venv"),
            Status = "stopped",
            BedStatus = bedStatus,
        };

        Assert.Equal(expected, BaseEnvUninstaller.IsInstalled(env));
    }
}
