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
/// - 目标目录 <c>&lt;env.ComfyuiSource&gt;/custom_nodes/&lt;repo-name&gt;</c> 已存在 → 跳过(G6)
/// - 否则跑 <c>git clone --depth=1 https://github.com/&lt;id&gt;.git &lt;targetDir&gt;</c>
/// - 单节点失败 → 写 WARN + 状态面板 warn: 行,继续下一个(G5)
/// - 整体结果是 Fail if any node failed;否则 Ok
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

    public async Task<NodeOperationResult> InstallEnabledAsync(
        Environment env,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (env is null) throw new ArgumentNullException(nameof(env));

        if (string.IsNullOrWhiteSpace(env.ComfyuiSource))
        {
            return NodeOperationResult.Fail(
                "env 无 ComfyuiSource,跳过常用节点(env-create 后 ComfyUI 路径未设置)");
        }

        var customNodesDir = Path.Combine(env.ComfyuiSource, "custom_nodes");
        Directory.CreateDirectory(customNodesDir);

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
            var targetDir = Path.Combine(customNodesDir, repoName);

            // G6:已装跳过(不 git pull)
            if (Directory.Exists(targetDir))
            {
                progress?.Report($"info:已装,跳过 {repoName}");
                skipped.Add(repoName);
                continue;
            }

            progress?.Report($"info:克隆 {node.Id} → {targetDir}");
            var args = new List<string>
            {
                "clone", "--depth=1", $"https://github.com/{node.Id}.git", targetDir,
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
