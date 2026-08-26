using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

public sealed class ComfyUIManagerInstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public ComfyUIManagerInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"cmfi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string root, string venvPath, string? comfyuiSource = null)
    {
        Directory.CreateDirectory(venvPath);
        var fakePy = Path.Combine(venvPath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "fake-python.exe" : "fake-python");
        File.WriteAllText(fakePy, "");
        return new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            ComfyuiSource = comfyuiSource ?? root,
            VenvPath = venvPath,
            PythonExecutable = fakePy,
            Port = 8188,
            Status = "stopped",
        };
    }

    private static string? FindGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "git", Arguments = "--version",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            p.WaitForExit(2000);
            return p.ExitCode == 0 ? "git" : null;
        }
        catch { return null; }
    }

    private static string? FindPython()
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        foreach (var c in candidates)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = c, Arguments = "--version",
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p is null) continue;
                p.WaitForExit(2000);
                if (p.ExitCode == 0)
                {
                    // 解析成绝对路径 — 后续 ResolveVenvPython / File.Exists 都按绝对路径处理。
                    // 拿 sys.executable 拿 full path;失败时退回 c 本身(PATH 上的相对名)。
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = c,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                        };
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add("import sys; print(sys.executable)");
                        using var probe = Process.Start(psi);
                        if (probe is not null)
                        {
                            probe.WaitForExit(2000);
                            var resolved = probe.StandardOutput.ReadToEnd().Trim();
                            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                                return resolved;
                        }
                    }
                    catch { }
                    return c;
                }
            }
            catch { }
        }
        return null;
    }

    [Fact]
    public void IsInstalled_NoDirectory_ReturnsFalse()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        Assert.False(sut.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_DirectoryExists_ReturnsTrue()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        Assert.True(sut.IsInstalled(env));
    }

    [Fact]
    public void IsInstalled_NoComfyuiSource_ReturnsFalse()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        Assert.False(sut.IsInstalled(env));
    }

    [Fact]
    public void ResolveTargetDirectory_ReturnsCustomNodesPath()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var target = sut.ResolveTargetDirectory(env);

        Assert.NotNull(target);
        Assert.Equal(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"), target);
    }

    [Fact]
    public void ResolveTargetDirectory_NoComfyuiSource_ReturnsNull()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        Assert.Null(sut.ResolveTargetDirectory(env));
    }

    [Fact]
    public async Task InstallAsync_NoComfyuiSource_ReturnsFailure()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = await sut.InstallAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ComfyuiSource", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_AlreadyInstalled_ReturnsFailure()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = await sut.InstallAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("已安装", result.Reason);
    }

    [Fact]
    public async Task InstallAsync_RealGit_ClonesRepo()
    {
        var git = FindGit();
        var py = FindPython();
        if (git is null || py is null) return;  // skip if git or python missing
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        env.PythonExecutable = py;
        // v1.0.0.x #571: 真 git clone (无 --depth=1,full history) 在国内/慢网络偶尔超 2 min 默认
        // 超时,触发 OperationCanceledException → 假"用户取消"。20 min 给测试留余量;
        // 生产维持默认(实际 install 走 .git bare cache + checkout,不走全 clone,2 min 充裕)。
        var sut = new ComfyUIManagerInstaller(
            new RequirementsFileInstaller(), gitExe: git, gitCloneTimeout: TimeSpan.FromMinutes(20));

        var result = await sut.InstallAsync(env, new Progress<string>(line => { }), CancellationToken.None);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.True(Directory.Exists(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager")),
            "git clone 应创建 Manager 目录");
    }

    [Fact]
    public async Task InstallAsync_PipFails_RollsBackDirectory()
    {
        var git = FindGit();
        var py = FindPython();
        if (git is null || py is null) return;  // skip if missing
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var venv = Path.Combine(_tempRoot, "venv");
        var env = SeedEnv("env-a", _tempRoot, venv, comfyuiSource: comfyuiSource);
        env.PythonExecutable = py;  // 用真 python 但让它因缺包失败

        // 注入 fake helper,模拟 pip 失败
        var failing = new FailingPipInstaller(git);
        var result = await failing.InstallAsync(env, new Progress<string>(line => { }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager")),
            "pip 失败应 rm -rf 整个 Manager 目录");
    }

    [Fact]
    public void Uninstall_DirectoryExists_RemovesIt()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var managerDir = Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager");
        Directory.CreateDirectory(managerDir);
        File.WriteAllText(Path.Combine(managerDir, "marker.txt"), "");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.True(result.Success);
        Assert.False(Directory.Exists(managerDir));
    }

    [Fact]
    public void Uninstall_DirectoryMissing_ReturnsFailure()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.False(result.Success);
        Assert.Contains("未安装", result.Reason);
    }

    [Fact]
    public void Uninstall_NoComfyuiSource_ReturnsFailure()
    {
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"));
        env.ComfyuiSource = null;
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task InstallAsync_CancelledMidway_RollsBackDirectory()
    {
        var git = FindGit();
        if (git is null) return;  // skip if git missing
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource: comfyuiSource);
        var cancelling = new CancellingPipInstaller(git);
        var result = await cancelling.InstallAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager")),
            "取消应回滚整个 Manager 目录");
    }

    /// <summary>
    /// 模拟 pip 失败的 helper(总是返失败 — 用来验 rollback 行为)。
    /// </summary>
    private sealed class FailingPipInstaller : ComfyUIManagerInstaller
    {
        public FailingPipInstaller(string gitExe) : base(new RequirementsFileInstaller(), gitExe) { }
        protected override Task<RequirementsInstallResult> RunPipForManagerAsync(
            string managerDir, string requirementsPath, string venvPython,
            IProgress<string>? progress, CancellationToken ct)
            => Task.FromResult(new RequirementsInstallResult(false, false, "模拟 pip 失败", 0));
    }

    private sealed class CancellingPipInstaller : ComfyUIManagerInstaller
    {
        public CancellingPipInstaller(string gitExe) : base(new RequirementsFileInstaller(), gitExe) { }
        protected override Task<RequirementsInstallResult> RunPipForManagerAsync(
            string managerDir, string requirementsPath, string venvPython,
            IProgress<string>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RequirementsInstallResult(false, true, "用户取消", 0));
        }
    }
}