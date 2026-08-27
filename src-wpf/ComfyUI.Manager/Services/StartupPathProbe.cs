using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Infrastructure;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x #594:启动期路径错位检测 — 扫描 Settings 的所有路径字段,标记指向不存在
/// / 错位的可疑项。
///
/// **必须在 <see cref="SettingsDefaults.Apply"/> 之后调用**(line 235 App.xaml.cs):
/// - Apply 把空字段 seed 默认子目录名、相对路径转绝对、剥前缀到相对 ——
///   跑 Apply 之前 probe 会看到过渡态误报。
/// - Apply 之后 probe 看到的是即将 Save 回盘的稳态值。
///
/// 错位规则:
/// - 字段空 → 不报(probe 假设用户启用 = 字段非空)
/// - 字段是相对路径 → 拼到 projectRoot 下判 <c>Exists</c>;不存在 → 可疑
/// - 字段是绝对路径 → 直接判 <c>Exists</c>;不存在 → 可疑(经典"程序搬走"场景)
///
/// 推荐值 = <c>projectRoot + defaultSubdir</c>,与 <see cref="SettingsDefaults"/>
/// 的 9 个 subdir 常量对齐。
///
/// 自定义范围:
/// - 8 个主路径字段:TemplatePythonDir / SystemTemplateLibraryDir / EnvsDir /
///   GlobalNodesDir / LocalNodeDirectory / LocalNodesDirectory /
///   DefaultModelsDirectory / WorkflowsDirectory
/// - 1 个 LogDirectory(走 subdir "Logs")
/// - 8 个 built-in TemplateConfig.LocalSourceDir(ComfyUI / A1111 / Forge /
///   SwarmUI / OpenVoice / Whisper / CoquiTTS / Bark)—— 通过
///   <see cref="TemplatePathResolver.Resolve"/> 拼出绝对路径再判 exists
///
/// 不扫:GitExe / PythonInterpreter.Path / ExtraPath.Path / URL / token / enum。
/// GitExe 已 seed 在 bin/git-portable/cmd/git.exe 下;PythonInterpreter.Path
/// Apply 已合成完整路径;ExtraPath 是用户主动加的额外路径,probe 阶段不一定存在。
/// </summary>
public static class StartupPathProbe
{
    private static readonly BuiltinKind[] BuiltInTemplateKinds =
    {
        new("ComfyUI", "ComfyUI"),
        new("A1111", "A1111"),
        new("Forge", "Forge"),
        new("SwarmUI", "SwarmUI"),
        new("OpenVoice", "OpenVoice"),
        new("Whisper", "Whisper"),
        new("CoquiTTS", "CoquiTTS"),
        new("Bark", "Bark"),
    };

    public static IReadOnlyList<PathMigrationItem> Detect(Settings s, string projectRoot)
    {
        if (s is null) return Array.Empty<PathMigrationItem>();
        if (string.IsNullOrWhiteSpace(projectRoot))
            throw new ArgumentException("projectRoot 不能为空", nameof(projectRoot));

        var items = new List<PathMigrationItem>();

        // 9 个主路径字段(顺序无关,但按 Settings.cs 声明顺序排,便于 review)
        TryAddIfMissing(items, "TemplatePythonDir",
            s.TemplatePythonDir, SettingsDefaults.TemplatePythonSubdir, projectRoot, isDirectory: true);
        TryAddIfMissing(items, "SystemTemplateLibraryDir",
            s.SystemTemplateLibraryDir, SettingsDefaults.SystemTemplateLibrarySubdir, projectRoot, isDirectory: true);
        TryAddIfMissing(items, "EnvsDir",
            s.EnvsDir, SettingsDefaults.EnvsSubdir, projectRoot, isDirectory: true);
        TryAddIfMissing(items, "GlobalNodesDir",
            s.GlobalNodesDir, SettingsDefaults.GlobalNodesSubdir, projectRoot, isDirectory: true);
        TryAddIfMissing(items, "LocalNodeDirectory",
            s.LocalNodeDirectory, SettingsDefaults.LocalNodesSubdir, projectRoot, isDirectory: true);
        TryAddIfMissing(items, "LocalNodesDirectory",
            s.LocalNodesDirectory, SettingsDefaults.LocalNodesBulkSubdir, projectRoot, isDirectory: true);
        TryAddIfMissing(items, "DefaultModelsDirectory",
            s.DefaultModelsDirectory, SettingsDefaults.ModelsSubdir, projectRoot, isDirectory: true);
        TryAddIfMissing(items, "WorkflowsDirectory",
            s.WorkflowsDirectory, SettingsDefaults.WorkflowsSubdir, projectRoot, isDirectory: true);
        TryAddIfMissing(items, "LogDirectory",
            s.LogDirectory, "Logs", projectRoot, isDirectory: true);

        // 8 个 built-in TemplateConfig.LocalSourceDir —— 用 TemplatePathResolver 解析
        // raw 是相对时锚到 SystemTemplateLibraryDir(SystemTemplateLibraryDir 自己可能是相对/绝对,
        // 先绝对化再喂给 Resolve,保证最终路径以 projectRoot 为根)。
        var systemLibraryAbs = string.IsNullOrWhiteSpace(s.SystemTemplateLibraryDir)
            ? null
            : (Path.IsPathRooted(s.SystemTemplateLibraryDir)
                ? Path.GetFullPath(s.SystemTemplateLibraryDir)
                : Path.GetFullPath(Path.Combine(projectRoot, s.SystemTemplateLibraryDir)));

        foreach (var kind in BuiltInTemplateKinds)
        {
            if (!s.Templates.TryGetValue(kind.Kind, out var cfg)) continue;
            if (cfg is null) continue;
            var raw = cfg.LocalSourceDir;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var resolved = TemplatePathResolver.Resolve(raw, systemLibraryAbs);

            if (Directory.Exists(resolved)) continue;

            items.Add(new PathMigrationItem(
                Label: $"Template:{kind.Kind}.LocalSourceDir",
                CurrentValue: resolved,
                RecommendedValue: Path.GetFullPath(Path.Combine(projectRoot, kind.Subdir))));
        }

        return items;
    }

    private static void TryAddIfMissing(
        List<PathMigrationItem> items,
        string label,
        string? raw,
        string subdir,
        string projectRoot,
        bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        string resolved;
        if (Path.IsPathRooted(raw))
        {
            // 绝对路径(包括 projectRoot 下 / 不在 projectRoot 下):直接判 exists
            resolved = Path.GetFullPath(raw);
        }
        else
        {
            // 相对路径:拼到 projectRoot 下
            resolved = Path.GetFullPath(Path.Combine(projectRoot, raw));
        }

        var exists = isDirectory ? Directory.Exists(resolved) : File.Exists(resolved);
        if (exists) return;

        items.Add(new PathMigrationItem(
            Label: label,
            CurrentValue: resolved,
            RecommendedValue: Path.GetFullPath(Path.Combine(projectRoot, subdir))));
    }

    private readonly record struct BuiltinKind(string Kind, string Subdir);
}