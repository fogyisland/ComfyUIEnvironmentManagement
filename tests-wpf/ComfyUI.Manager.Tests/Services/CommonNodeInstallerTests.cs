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

public sealed class CommonNodeInstallerTests : IDisposable
{
    private readonly string _tempRoot;

    public CommonNodeInstallerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"commonnode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static Settings S(params (string id, bool enabled)[] nodes)
    {
        var list = new List<CommonNodeEntry>();
        foreach (var (id, enabled) in nodes)
        {
            list.Add(new CommonNodeEntry { Id = id, DisplayName = id, IsBuiltIn = true, Enabled = enabled });
        }
        return new Settings { CommonNodes = list };
    }

    private static Environment EnvWithComfyui(string root, string comfyuiSource)
    {
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes"));
        return new Environment
        {
            Id = "env-1",
            Name = "env-1",
            RootPath = root,
            ComfyuiSource = comfyuiSource,
            CustomNodesPath = Path.Combine(comfyuiSource, "custom_nodes"),
        };
    }

    [Fact]
    public async Task InstallEnabledAsync_EmptyList_ReturnsOkAndCallsNothing()
    {
        var settings = S();  // 空 list
        var invocations = new List<(string, IReadOnlyList<string>)>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add((id, args));
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var env = EnvWithComfyui(_tempRoot, Path.Combine(_tempRoot, "ComfyUI"));
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task InstallEnabledAsync_NullComfyuiSource_ReturnsFailAndCallsNothing()
    {
        var settings = S(("ltdrdata/ComfyUI-Manager", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var env = new Environment
        {
            Id = "env-1",
            Name = "env-1",
            RootPath = _tempRoot,
            ComfyuiSource = null,  // 没 Comfyui 源
            CustomNodesPath = Path.Combine(_tempRoot, "custom_nodes"),
        };
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ComfyuiSource", result.Reason ?? "");
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task InstallEnabledAsync_DisabledNodesSkipped()
    {
        // enabled=false 视为 "不装",跟"已装"同等待遇 — 直接跳过
        var settings = S(
            ("ltdrdata/ComfyUI-Manager", false),
            ("ltdrdata/ComfyUI-Impact-Pack", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var env = EnvWithComfyui(_tempRoot, Path.Combine(_tempRoot, "ComfyUI"));
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(invocations);
        Assert.Contains("ltdrdata/ComfyUI-Impact-Pack", invocations[0]);
    }

    [Fact]
    public async Task InstallEnabledAsync_AlreadyInstalled_Skipped()
    {
        // dir 已存在 → 跳过(不调 git clone,不 git pull)— G6 idempotent
        var comfyuiSource = Path.Combine(_tempRoot, "ComfyUI");
        Directory.CreateDirectory(Path.Combine(comfyuiSource, "custom_nodes", "ComfyUI-Manager"));

        var settings = S(("ltdrdata/ComfyUI-Manager", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var env = EnvWithComfyui(_tempRoot, comfyuiSource);
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task InstallEnabledAsync_PartialFailure_AggregatesResult()
    {
        // 一个成功 + 一个失败 → 整体 Success=false 但成功的依然装了(G5 best-effort)
        var settings = S(
            ("ltdrdata/ComfyUI-Manager", true),
            ("ltdrdata/ComfyUI-Impact-Pack", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            if (id == "ltdrdata/ComfyUI-Manager")
                return Task.FromResult(NodeOperationResult.Ok("ok"));
            return Task.FromResult(NodeOperationResult.Fail("git clone 失败"));
        });

        var env = EnvWithComfyui(_tempRoot, Path.Combine(_tempRoot, "ComfyUI"));
        var result = await installer.InstallEnabledAsync(env, null, CancellationToken.None);

        Assert.False(result.Success);  // 部分失败 → 整体 fail
        Assert.Equal(2, invocations.Count);  // 两个都尝试
    }

    // v1.0.0.x:InstallEnabledToAsync(targetDir, ...) — 不依赖 Environment,把 enabled
    // common_nodes 直接 git clone 到任意 targetDir。SettingsView「下载到本地节点目录」
    // 按钮 = targetDir = Settings.LocalNodesDirectory;env-create / 装依赖自动跑路径
    // 仍是 InstallEnabledAsync(env, ...) → 内部 delegate 到本方法,targetDir = env.ComfyuiSource/custom_nodes。

    [Fact]
    public async Task InstallEnabledToAsync_EmptyList_ReturnsOkAndCallsNothing()
    {
        var settings = S();  // 空 list
        var invocations = new List<(string, IReadOnlyList<string>)>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add((id, args));
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var targetDir = Path.Combine(_tempRoot, "localnodes");
        var result = await installer.InstallEnabledToAsync(targetDir, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task InstallEnabledToAsync_ClonesEnabledNodesToTargetDir()
    {
        // 验证:targetDir 是 Settings.LocalNodesDirectory 时(不再是 env.ComfyuiSource/custom_nodes)
        // gitClone 被调用,参数 id = repo id,args 第 4 个 = targetDir/<repo-name>
        var settings = S(
            ("ltdrdata/ComfyUI-Manager", true),
            ("pythongosssss/ComfyUI-Custom-Scripts", true));
        var invocations = new List<(string id, IReadOnlyList<string> args)>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add((id, args));
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var localnodesDir = Path.Combine(_tempRoot, "localnodes");
        var result = await installer.InstallEnabledToAsync(localnodesDir, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, invocations.Count);
        // git args 形式:clone --depth=1 https://github.com/<id>.git <targetDir>/<repo-name>
        var managerCall = invocations.Single(i => i.id == "ltdrdata/ComfyUI-Manager");
        Assert.Equal("clone", managerCall.args[0]);
        Assert.Equal("--depth=1", managerCall.args[1]);
        Assert.Equal("https://github.com/ltdrdata/ComfyUI-Manager.git", managerCall.args[2]);
        // 第 4 个 arg 是 target 子目录,末尾必须 = repo-name(从 id 取末段)
        Assert.Equal(Path.Combine(localnodesDir, "ComfyUI-Manager"), managerCall.args[3]);
    }

    [Fact]
    public async Task InstallEnabledToAsync_AlreadyInstalledAtTarget_Skipped()
    {
        // 已存在 <targetDir>/<repo-name> → 跳过(G6 idempotent,跟 InstallEnabledAsync 同款)
        var localnodesDir = Path.Combine(_tempRoot, "localnodes");
        Directory.CreateDirectory(Path.Combine(localnodesDir, "ComfyUI-Manager"));

        var settings = S(("ltdrdata/ComfyUI-Manager", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var result = await installer.InstallEnabledToAsync(localnodesDir, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(invocations);
    }

    [Fact]
    public async Task InstallEnabledToAsync_NullOrEmptyTarget_ReturnsFail()
    {
        // targetDir null / 空 → Fail,不创建目录、不调 gitClone
        var settings = S(("ltdrdata/ComfyUI-Manager", true));
        var invocations = new List<string>();
        var installer = new CommonNodeInstaller(settings, (id, args) =>
        {
            invocations.Add(id);
            return Task.FromResult(NodeOperationResult.Ok("ok"));
        });

        var result1 = await installer.InstallEnabledToAsync("", null, CancellationToken.None);
        var result2 = await installer.InstallEnabledToAsync(null!, null, CancellationToken.None);

        Assert.False(result1.Success);
        Assert.False(result2.Success);
        Assert.Empty(invocations);
    }
}
