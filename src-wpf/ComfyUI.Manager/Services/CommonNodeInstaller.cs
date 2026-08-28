using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.11++:env-create / 装依赖末尾自动装用户在 Settings 勾选的一组「常用节点」。
/// 行为:
/// - 遍历 <see cref="Settings.CommonNodes"/> 里 <c>Enabled=true</c> 的条目
/// - 目标目录 <c>&lt;targetDir&gt;/&lt;repo-name&gt;</c> 已存在 → 跳过(G6)
/// - 否则跑 <c>git clone --depth=1 https://github.com/&lt;id&gt;.git .git</c>
/// - 单节点失败 → 写 WARN + 状态面板 warn: 行,继续下一个(G5)
/// - 整体结果是 Fail if any node failed;否则 Ok
///
/// v1.0.0.x:SettingsView 加「下载到本地节点目录」按钮,复用同核心方法但指定 targetDir
/// 为 <see cref="Settings.LocalNodesDirectory"/>(用户原话"将设置勾选的哪些节点 全部都
/// 下载到本地节点目录中")。<see cref="InstallEnabledAsync(Environment, IProgress{string}?,
/// CancellationToken)"/> 内部 delegate 给 <see cref="InstallEnabledToAsync(string,
/// IProgress{string}?, CancellationToken)"/> 把 <c>env.ComfyuiSource/custom_nodes</c>
/// 当 targetDir。
///
/// git clone 走注入的 <c>Func&lt;string, IReadOnlyList&lt;string&gt;, Task&lt;NodeOperationResult&gt;&gt;</c>
/// (App.xaml.cs 那里 lambda 包 GitRunner.RunAsync)— 不直接依赖 GitRunner 实例,便于测试用
/// fake func 验证调用。
/// </summary>
public sealed class CommonNodeInstaller
{
    private readonly Settings _settings;
    private readonly Func<string, IReadOnlyList<string>, Task<NodeOperationResult>> _gitClone;
    private readonly AppLogger? _logger;

    /// <param name="gitClone">参数 1 = repo id (e.g. "ltdrdata/ComfyUI-Manager"),
    /// 参数 2 = git args 列表,return NodeOperationResult(由 App.xaml.cs 那里包
    /// GitRunner.RunAsync(".", args))。</param>
    public CommonNodeInstaller(
        Settings settings,
        Func<string, IReadOnlyList<string>, Task<NodeOperationResult>> gitClone,
        AppLogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _gitClone = gitClone ?? throw new ArgumentNullException(nameof(gitClone));
        _logger = logger;
    }

    /// <summary>
    /// 把 enabled=true 的常用节点 git clone 到 <c>&lt;env.ComfyuiSource&gt;/custom_nodes/&lt;repo-name&gt;</c>。
    /// env-create / 装依赖末尾自动调用。
    /// </summary>
    public Task<NodeOperationResult> InstallEnabledAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        if (string.IsNullOrWhiteSpace(env.ComfyuiSource))
        {
            return Task.FromResult(NodeOperationResult.Fail(
                "env 无 ComfyuiSource,跳过常用节点(env-create 后 ComfyUI 路径未设置)"));
        }

        var customNodesDir = Path.Combine(env.ComfyuiSource, "custom_nodes");
        return InstallEnabledToAsync(customNodesDir, progress, ct);
    }

    /// <summary>
    /// v1.0.0.x:把 enabled=true 的常用节点 git clone 到任意 <paramref name="targetDir"/>
    /// (不依赖 Environment)。SettingsView「下载到本地节点目录」按钮用这个方法把节点
    /// 下到 <see cref="Settings.LocalNodesDirectory"/>,而不是 per-env custom_nodes/。
    ///
    /// <paramref name="targetDir"/> 为空 / null → 返 Fail;targetDir 不存在 → 自动 CreateDirectory。
    /// </summary>
    public async Task<NodeOperationResult> InstallEnabledToAsync(
        string targetDir,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            return NodeOperationResult.Fail("targetDir 为空,跳过常用节点");
        }

        Directory.CreateDirectory(targetDir);

        var enabled = _settings.CommonNodes
            .Where(n => n.Enabled && !string.IsNullOrWhiteSpace(n.Id))
            .ToList();

        if (enabled.Count == 0)
        {
            return NodeOperationResult.Ok("无已勾选节点");
        }

        var failures = new List<string>();
        var skipped = new List<string>();
        var installed = new List<string>();

        foreach (var node in enabled)
        {
            ct.ThrowIfCancellationRequested();

            var repoName = node.Id.Contains('/')
                ? node.Id.Substring(node.Id.IndexOf('/') + 1)
                : node.Id;
            var nodeTargetDir = Path.Combine(targetDir, repoName);

            // G6:已装跳过(不 git pull)
            if (Directory.Exists(nodeTargetDir))
            {
                progress?.Report($"info:已装,跳过 {repoName}");
                skipped.Add(repoName);
                continue;
            }

            progress?.Report($"info:克隆 {node.Id} → {nodeTargetDir}");
            var args = new List<string>
            {
                "clone", "--depth=1", $"https://github.com/{node.Id}.git", nodeTargetDir,
            };
            NodeOperationResult result;
            try
            {
                result = await _gitClone(node.Id, args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = NodeOperationResult.Fail(ex.Message);
            }
            if (!result.Success)
            {
                _logger?.Warn("common-nodes", $"{node.Id} clone 失败:{result.Reason}");
                progress?.Report($"warn:{node.Id} clone 失败:{result.Reason}");
                failures.Add($"{node.Id}({result.Reason})");
                continue;
            }
            installed.Add(repoName);
        }

        var summary = $"installed={installed.Count} skipped={skipped.Count} failed={failures.Count}";
        if (failures.Count > 0)
        {
            return NodeOperationResult.Fail($"{summary};失败:{string.Join("; ", failures)}");
        }
        progress?.Report($"info:常用节点 {summary}");
        return NodeOperationResult.Ok(summary);
    }
}