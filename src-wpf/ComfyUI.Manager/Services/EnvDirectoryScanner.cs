using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Models;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x:扫 Settings.EnvsDir 下面的直接子目录,根据每个子目录里的
/// <see cref="EnvMarker"/>(.cmgr-env.json 隐藏文件)把 env upsert 进 SQLite。
///
/// 触发场景:
///   - App 启动时 — 用户上次改 EnvsDir 后,新目录的 env 必须出现
///   - SettingsViewModel 保存 EnvsDir 后 — 用户改了路径,立即触发一次
///
/// 行为:
///   - marker.envId 在 SQLite 里 → 更新 RootPath(目录可能被移动)+ Name/Kind/
///     TemplateSnapshot(防止 marker 是新版的、覆盖老 SQLite 行)
///   - marker.envId 不在 SQLite 里 → insert 新 env(用户从另一台机器拷贝过来,
///     走 auto-import);port 留 null 等 EnvironmentListViewModel 启动时再分配
///   - 子目录无 marker → 跳过(不误把别的目录当 env)
///
/// 失败策略:scanner 不抛 — 单个 marker 读失败或 upsert 失败累计到 report.Errors,
/// 不阻塞整轮扫描。
/// </summary>
public sealed class EnvDirectoryScanner
{
    private readonly EnvironmentRepository _repo;

    public EnvDirectoryScanner(EnvironmentRepository repo)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    }

    /// <summary>
    /// 一次扫描结果,便于 UI / 日志提示"新发现 N 个 env" / "更新 M 个"。
    /// </summary>
    public sealed class ScanReport
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public List<string> Errors { get; } = new();
    }

    /// <summary>
    /// 扫 <paramref name="envsDir"/> 下的子目录,把 marker upsert 进 SQLite。
    /// <paramref name="envsDir"/> 为空 / 不存在 → 返回全 0 的 report,不报错。
    /// </summary>
    public Task<ScanReport> ScanAsync(string envsDir, CancellationToken ct = default)
    {
        var report = new ScanReport();
        if (string.IsNullOrWhiteSpace(envsDir))
        {
            return Task.FromResult(report);
        }
        if (!Directory.Exists(envsDir))
        {
            return Task.FromResult(report);
        }

        var existing = _repo.ListAll().ToDictionary(e => e.Id, e => e, StringComparer.Ordinal);

        foreach (var subdir in Directory.EnumerateDirectories(envsDir))
        {
            ct.ThrowIfCancellationRequested();
            EnvMarker? marker = null;
            try
            {
                marker = EnvMarkerService.Read(subdir);
            }
            catch (Exception ex)
            {
                report.Errors.Add($"read {subdir}: {ex.Message}");
                continue;
            }
            if (marker is null)
            {
                report.Skipped++;
                continue;
            }

            try
            {
                if (existing.TryGetValue(marker.EnvId, out var existingEnv))
                {
                    // env 已被 SQLite 跟踪 — 同步磁盘当前位置 + marker 携带的最新字段
                    existingEnv.RootPath = subdir;
                    if (!string.IsNullOrWhiteSpace(marker.Name))
                        existingEnv.Name = marker.Name;
                    if (!string.IsNullOrWhiteSpace(marker.Kind))
                        existingEnv.TemplateKind = marker.Kind;
                    if (marker.TemplateSnapshot is not null)
                        existingEnv.TemplateConfigSnapshot = marker.TemplateSnapshot;
                    _repo.Upsert(existingEnv);
                    report.Updated++;
                }
                else
                {
                    // 新发现的 env — 用户从其他位置(其他机器 / 备份还原)搬过来的。
                    // port / venv / custom_nodes 等路径走子目录结构约定,status 留 stopped,
                    // 其他诊断字段留空 — 用户启动 env 时再补。
                    var env = new Environment
                    {
                        Id = marker.EnvId,
                        Name = marker.Name,
                        RootPath = subdir,
                        ComfyuiLayout = "isolated",
                        ComfyuiSource = subdir,
                        BasePythonPath = "",
                        VenvPath = Path.Combine(subdir, "venv"),
                        PythonExecutable = Path.Combine(subdir, "venv", "Scripts", "python.exe"),
                        PythonVersion = "",
                        CustomNodesPath = Path.Combine(subdir, "custom_nodes"),
                        ExtraModelPathsYaml = Path.Combine(subdir, "extra_model_paths.yaml"),
                        Port = null,
                        Status = "stopped",
                        EnabledNodeIdsJson = "[]",
                        Notes = null,
                        TemplateKind = marker.Kind,
                        TemplateConfigSnapshot = marker.TemplateSnapshot,
                    };
                    _repo.Upsert(env);
                    report.Inserted++;
                }
            }
            catch (Exception ex)
            {
                report.Errors.Add($"upsert {marker.EnvId} ({subdir}): {ex.Message}");
            }
        }

        return Task.FromResult(report);
    }
}