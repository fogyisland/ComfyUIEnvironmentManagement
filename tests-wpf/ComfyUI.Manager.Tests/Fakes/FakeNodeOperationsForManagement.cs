using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.Tests.Services;

namespace ComfyUI.Manager.Tests.Fakes;

/// <summary>
/// v0.6.15.8 T2:fake NodeOperations — override RescanAsync 返预设 list,override
/// UninstallAsync 返预设 result,捕获 RescanCalled / UninstallCalled flags 给测试断言。
///
/// 命名说明:另一个 <c>FakeNodeOperations</c> 已存在于
/// <c>ComfyUI.Manager.Tests.ViewModels.InstallDialogViewModelProgressTests</c>
/// (v0.6.15.5 T2 引入,internal class)。本类重命名为
/// <c>FakeNodeOperationsForManagement</c> 以避免命名冲突。
///
/// 适配 codebase(跟 brief 有偏离):
/// - NodeOperations ctor 需要 Settings + NodeInstallDiffService(brief 漏列)
/// - UninstallAsync 第二个参数是 nodeId 不是 packageName(brief 用 packageName)
/// - 传 fake repos 给 base ctor 是为了让任何被 RescanAsync override 跳过的代码
///   路径不会 NRE;这里 override 覆盖了 RescanAsync,所以 base 字段浪费但安全。
/// </summary>
public class FakeNodeOperationsForManagement : NodeOperations
{
    public IReadOnlyList<ScannedNode>? ScanResult { get; set; }
    public NodeRepository? NodeRepo { get; set; }
    public bool RescanCalled { get; set; }

    public NodeOperationResult UninstallResult { get; set; } = NodeOperationResult.Ok("v0");
    public bool UninstallCalled { get; private set; }

    public NodeOperationResult UpgradeResult { get; set; } = NodeOperationResult.Ok("v0");
    public bool UpgradeCalled { get; private set; }

    public FakeNodeOperationsForManagement() : base(
        new FakeGitRunner(),
        new EnvironmentRepository(new TestDb().Factory),
        new NodeRepository(new TestDb().Factory),
        new Settings(),
        new NodeInstallDiffService((_, _, _, _) => Task.FromResult(new ProcessResult(true, 0, "[]", ""))),
        logger: null)
    { }

    public override Task<IReadOnlyList<ScannedNode>> RescanAsync(
        string envId, CancellationToken ct = default)
    {
        RescanCalled = true;
        if (ScanResult is null) return Task.FromResult<IReadOnlyList<ScannedNode>>(new List<ScannedNode>());
        // Upsert into NodeRepo so ListByEnv works
        if (NodeRepo is not null)
        {
            foreach (var n in ScanResult) NodeRepo.Upsert(n);
        }
        return Task.FromResult(ScanResult);
    }

    public override Task<NodeOperationResult> UninstallAsync(
        string envId, string nodeId, CancellationToken ct = default)
    {
        UninstallCalled = true;
        return Task.FromResult(UninstallResult);
    }

    public override Task<NodeOperationResult> UpgradeAsync(
        string envId, string nodeId,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        UpgradeCalled = true;
        return Task.FromResult(UpgradeResult);
    }
}
