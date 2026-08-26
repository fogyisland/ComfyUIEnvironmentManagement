using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// Repro test for the user-reported "卸载 ComfyUI Manager 没有删除目录" bug.
/// Mocks a real git-clone-like layout (.git + __pycache__ + read-only + hidden + deep nest)
/// to surface which Delete scenario silently fails.
/// </summary>
public sealed class ComfyUIManagerInstallerUninstallReproTests : IDisposable
{
    private readonly string _tempRoot;

    public ComfyUIManagerInstallerUninstallReproTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"cmfi-uninst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private Environment SeedEnv(string id, string root, string venvPath, string? comfyuiSource)
    {
        Directory.CreateDirectory(venvPath);
        return new Environment
        {
            Id = id,
            Name = id,
            RootPath = root,
            ComfyuiSource = comfyuiSource ?? root,
            VenvPath = venvPath,
            Port = 8188,
            Status = "stopped",
        };
    }

    private static string CreateGitLikeLayout(string root)
    {
        var managerDir = Path.Combine(root, "ComfyUI", "custom_nodes", "ComfyUI-Manager");
        Directory.CreateDirectory(managerDir);

        // 1. .git/objects/pack with read-only idx file
        Directory.CreateDirectory(Path.Combine(managerDir, ".git", "objects", "pack"));
        File.WriteAllText(Path.Combine(managerDir, ".git", "HEAD"), "ref: refs/heads/main");
        var packIdx = Path.Combine(managerDir, ".git", "objects", "pack", "pack-abc.idx");
        File.WriteAllText(packIdx, "fake");
        File.SetAttributes(packIdx, FileAttributes.ReadOnly);

        // 2. __pycache__ with hidden+system pyc file
        Directory.CreateDirectory(Path.Combine(managerDir, "__pycache__"));
        var pyc = Path.Combine(managerDir, "__pycache__", "foo.cpython-311.pyc");
        File.WriteAllText(pyc, "");
        File.SetAttributes(pyc, FileAttributes.Hidden | FileAttributes.System);

        // 3. Deep nesting mimicking ComfyUI Manager source tree
        var deep = managerDir;
        for (var i = 0; i < 6; i++)
        {
            deep = Path.Combine(deep, $"subdir-{i}");
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, $"file-{i}.py"), "");
        }

        return managerDir;
    }

    [Fact]
    public void Repro_GitLikeLayout_UninstallDeletesDirectory()
    {
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var managerDir = CreateGitLikeLayout(_tempRoot);
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.False(Directory.Exists(managerDir), "Manager 目录应被删除");
    }

    [Fact]
    public void Repro_LongPath_UninstallDeletesDirectory()
    {
        // MAX_PATH (260 chars) 边界 — git clone 深嵌套或长文件名可能超过。
        // .NET Directory.Delete 不加 \\?\ 前缀时会抛 PathTooLongException,
        // 老 TryDelete 静默 swallow 后返 Fail 让用户"手动删"。
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var managerDir = Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager");
        Directory.CreateDirectory(managerDir);

        // 拼一个接近 260 char 边界的深嵌套路径
        var deep = managerDir;
        var segment = new string('x', 30);
        for (var i = 0; i < 4; i++)
        {
            deep = Path.Combine(deep, segment);
            Directory.CreateDirectory(deep);
        }
        File.WriteAllText(Path.Combine(deep, "marker.py"), "");

        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.False(Directory.Exists(managerDir), "长路径 Manager 目录应被删除");
    }

    [Fact]
    public void Repro_ReadOnlySubdirectory_UninstallDeletesDirectory()
    {
        // Windows:子目录有 ReadOnly attribute 时 Directory.Delete recursive
        // 会"Access denied"。老 TryDelete 只清 file attr,不清 subdir attr。
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var managerDir = Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager");
        var subDir = Path.Combine(managerDir, "readonly-subdir");
        Directory.CreateDirectory(subDir);
        File.SetAttributes(subDir, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(subDir, "inside.txt"), "");

        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: "git");

        var result = sut.Uninstall(env);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.False(Directory.Exists(managerDir), "ReadOnly subdir 不应阻塞删除");
    }

    [Fact]
    public async Task Repro_RealGitClone_InstallThenUninstall_DeletesDirectory()
    {
        // 用真 git clone ComfyUI-Manager,模拟用户真实场景。
        var git = FindGit();
        if (git is null) return;  // 没 git 跳过

        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        var env = SeedEnv("env-a", _tempRoot, Path.Combine(_tempRoot, "venv"), comfyuiSource);
        var sut = new ComfyUIManagerInstaller(new RequirementsFileInstaller(), gitExe: git);

        // 只验证 Install 创建目录 + Uninstall 删除目录;pip install 那段会失败(virtualenv 不全)
        // 但 InstallAsync 在 pip 失败时会 TryDelete 回滚,所以这俩都会被清理。
        // 所以我们需要一个不调 pip 的路径——直接手工 git clone + 调 Uninstall。
        var managerDir = Path.Combine(comfyuiSource, "custom_nodes", ComfyUIManagerInstaller.DirName);
        var parentDir = Path.Combine(comfyuiSource, "custom_nodes");
        Directory.CreateDirectory(parentDir);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = git,
            WorkingDirectory = parentDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add("--depth=1");
        psi.ArgumentList.Add(ComfyUIManagerInstaller.DefaultRepoUrl);
        psi.ArgumentList.Add(ComfyUIManagerInstaller.DirName);
        using (var clone = System.Diagnostics.Process.Start(psi))
        {
            Assert.NotNull(clone);
            clone!.WaitForExit(120_000);
            Assert.True(clone.ExitCode == 0, $"git clone 失败 exit={clone.ExitCode}");
        }
        Assert.True(Directory.Exists(managerDir), "git clone 后 Manager 目录应存在");

        // 现在调 Uninstall —— 这是用户报告的核心 bug
        var result = sut.Uninstall(env);

        Assert.True(result.Success, $"reason={result.Reason}");
        Assert.False(Directory.Exists(managerDir), "Uninstall 后 Manager 目录应被删除");
    }

    private static string? FindGit()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
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
}