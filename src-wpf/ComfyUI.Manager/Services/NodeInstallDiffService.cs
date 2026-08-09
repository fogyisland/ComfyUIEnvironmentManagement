using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// 跑 env 的 venv `python -m pip list --format=json`,对比 catalog PipRequirements,
/// 分类成 New / Upgrade / Downgrade / Conflict(Warning = Downgrade + Conflict)。
///
/// 失败模式(G2):pip list 失败 / 超时 / parse 失败 → 返 Empty report,不抛。
/// </summary>
public sealed class NodeInstallDiffService
{
    private static readonly TimeSpan PipListTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>> _runProcess;
    private readonly AppLogger? _logger;

    public NodeInstallDiffService(
        Func<string, string[], TimeSpan, CancellationToken, Task<ProcessResult>> runProcess,
        AppLogger? logger = null)
    {
        _runProcess = runProcess ?? throw new ArgumentNullException(nameof(runProcess));
        _logger = logger;
    }

    public async Task<NodeInstallDiffReport> CheckAsync(
        Environment env,
        IReadOnlyList<PipRequirement> catalogReqs,
        CancellationToken ct)
    {
        if (catalogReqs.Count == 0) return NodeInstallDiffReport.Empty;

        var pythonExe = env.PythonExecutable ?? "";
        if (string.IsNullOrEmpty(pythonExe))
        {
            _logger?.Info("node-diff", $"env='{env.Id}' python 路径为空,跳过 diff");
            return NodeInstallDiffReport.Empty;
        }

        ProcessResult result;
        try
        {
            result = await _runProcess(
                pythonExe,
                new[] { "-m", "pip", "list", "--format=json" },
                PipListTimeout, ct);
        }
        catch (Exception ex)
        {
            _logger?.Info("node-diff", $"env='{env.Id}' pip list 抛异常: {ex.Message}");
            return NodeInstallDiffReport.Empty;
        }

        if (!result.Ok)
        {
            _logger?.Info("node-diff", $"env='{env.Id}' pip list 失败 exit={result.ExitCode}");
            return NodeInstallDiffReport.Empty;
        }

        List<PipJsonRow>? installed;
        try
        {
            installed = JsonSerializer.Deserialize<List<PipJsonRow>>(result.Stdout, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger?.Info("node-diff", $"env='{env.Id}' pip list json 解析失败: {ex.Message}");
            return NodeInstallDiffReport.Empty;
        }

        if (installed is null) return NodeInstallDiffReport.Empty;

        var installedMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in installed)
        {
            if (!string.IsNullOrEmpty(p.name)) installedMap[p.name] = p.version ?? "";
        }

        var entries = new List<DiffEntry>();
        foreach (var req in catalogReqs)
        {
            if (!installedMap.TryGetValue(req.Name, out var installedVer))
            {
                entries.Add(new DiffEntry(req.Name, DiffCategory.New, null, req.Specifier));
                continue;
            }
            if (PipRequirementMatcher.IsSatisfiedBy(req, installedVer)) continue; // NoChange
            var (category, toV) = Classify(req, installedVer);
            entries.Add(new DiffEntry(req.Name, category, installedVer, toV));
        }
        return new NodeInstallDiffReport(entries);
    }

    private static (DiffCategory category, string? toV) Classify(PipRequirement req, string installedVer)
    {
        Version? installedV = TryParseVersion(installedVer);
        if (installedV is null)
            return (DiffCategory.Conflict, req.Specifier);

        var (minV, maxV) = ParseBounds(req.Specifier);

        if (minV is not null && installedV < minV)
            return (DiffCategory.Upgrade, req.Specifier);
        if (maxV is not null && installedV > maxV)
        {
            // installed 同时大于 max 的 major 和 minor → Conflict(跨主次版本号)
            if (installedV.Major > maxV.Major && installedV.Minor > maxV.Minor)
                return (DiffCategory.Conflict, req.Specifier);
            return (DiffCategory.Downgrade, req.Specifier);
        }

        // 复合 spec(IsSatisfiedBy 已返 false,但单边没界)→ Conflict
        return (DiffCategory.Conflict, req.Specifier);
    }

    private static (Version? min, Version? max) ParseBounds(string? specifier)
    {
        if (string.IsNullOrWhiteSpace(specifier)) return (null, null);
        Version? min = null, max = null;
        foreach (var single in specifier.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            for (int i = 0; i < single.Length; i++)
            {
                var c = single[i];
                if (c is not ('>' or '<' or '!' or '=' or '~')) continue;
                int opLen = 1;
                if (i + 1 < single.Length && single[i + 1] == '=') opLen = 2;
                if (i + 2 < single.Length && single[i] == '=' && single[i + 1] == '=' && single[i + 2] == '=') opLen = 3;
                var op = single[..(i + opLen)];
                var ver = single[(i + opLen)..];
                if (!Version.TryParse(NormalizeVersion(ver), out var v)) break;
                if (op is ">=" or ">" or "==" or "~=")
                {
                    if (min is null || v > min) min = v;
                }
                else if (op is "<=" or "<")
                {
                    if (max is null || v < max) max = v;
                }
                break;
            }
        }
        return (min, max);
    }

    private static string NormalizeVersion(string v)
    {
        var dash = v.IndexOfAny(new[] { 'a', 'b', 'r', 'p', '-' });
        var clean = dash >= 0 ? v[..dash] : v;
        var parts = clean.Split('.');
        while (parts.Length < 3) parts = parts.Append("0").ToArray();
        return string.Join('.', parts.Take(3));
    }

    private static Version? TryParseVersion(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        return Version.TryParse(NormalizeVersion(v), out var ver) ? ver : null;
    }

    private sealed class PipJsonRow
    {
        public string? name { get; set; }
        public string? version { get; set; }
    }
}