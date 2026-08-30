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
/// - 9 个主路径字段:TemplatePythonDir / SystemTemplateLibraryDir / EnvsDir /
///   GlobalNodesDir / LocalNodeDirectory / LocalNodesDirectory /
///   DefaultModelsDirectory / WorkflowsDirectory / LogDirectory
/// - 11 个 built-in TemplateConfig.LocalSourceDir(2 图像 + 4 语音 + 5 视频/图像生成/工具:
///   ComfyUI / Forge / OpenVoice / Whisper / CoquiTTS / Bark / HunyuanVideo /
///   LTXVideo / CogVideoX / Fooocus / HivisionIDPhotos;A1111 + SwarmUI 已下线)—— 通过
///   <see cref="TemplatePathResolver.Resolve"/> 拼出绝对路径再判 exists。
///   **但** LocalSourceDir 仍是默认 seed 值(== kind 名,如 "Whisper")且目录不存在
///   时 → 跳过:用户压根没下载这个模板是正常状态,不是路径错位。
///
/// 不扫:GitExe / PythonInterpreter.Path / ExtraPath.Path / URL / token / enum。
/// GitExe 已 seed 在 bin/git-portable/cmd/git.exe 下;PythonInterpreter.Path
/// Apply 已合成完整路径;ExtraPath 是用户主动加的额外路径,probe 阶段不一定存在。
///
/// v1.0.0.x (2026-08-29): A1111 + SwarmUI 从 8 个内置里移除(模板已下线),
/// 剩 6 个;再 +4 个 GitHub-clone 视频/图像生成模板(HunyuanVideo / LTXVideo /
/// CogVideoX / Fooocus),再 +1 个 AI 证件照生成(HivisionIDPhotos),共 11 个 built-in。
/// </summary>
public static class StartupPathProbe
{
    private static readonly BuiltinKind[] BuiltInTemplateKinds =
    {
        new("ComfyUI", "ComfyUI"),
        new("Forge", "Forge"),
        new("OpenVoice", "OpenVoice"),
        new("Whisper", "Whisper"),
        new("CoquiTTS", "CoquiTTS"),
        new("Bark", "Bark"),
        // v1.0.0.x (2026-08-29): 4 个 GitHub-clone 视频/图像生成模板 — 跟
        // OpenVoice/Whisper/CoquiTTS/Bark 一样的源模式,LocalSourceDir 默认 = kind 名。
        new("HunyuanVideo", "HunyuanVideo"),
        // v1.0.0.x LTX-2 (T1):LocalSourceDir = "LTXVideo" (kind 名,跟 ENVTemplate/LTXVideo/
        // 磁盘目录一致 — 不能用 "LTX-Video" 品牌命名,因为磁盘实际目录就叫 "LTXVideo")。
        new("LTXVideo", "LTXVideo"),
        new("CogVideoX", "CogVideoX"),
        new("Fooocus", "Fooocus"),
        new("HivisionIDPhotos", "HivisionIDPhotos"),
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

        // 11 个 built-in TemplateConfig.LocalSourceDir —— 用 TemplatePathResolver 解析
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

            // v1.0.0.x hotfix (2026-08-27):built-in template 的 LocalSourceDir 在
            // TemplateConfigDefaults 里默认 seed 为 kind 名(如 "Whisper" / "Bark")。
            // resolve 到 ENVTemplate/<Kind> 后,如果目录不存在,**这是用户压根没下载
            // 这个模板的正常状态**,不是路径错位 — 用户主动下载才会创建目录。flag
            // 它们会让 startup 每次都弹 "3 个模板路径可疑" 误导用户。
            //
            // 但 raw != kind 的情况(用户主动改成别的相对 / 绝对路径)→ 仍按原规则
            // 检测:resolve 后不存在 = 用户配错了,提示迁移。
            if (string.Equals(raw, kind.Kind, StringComparison.OrdinalIgnoreCase))
            {
                var defaultResolved = TemplatePathResolver.Resolve(raw, systemLibraryAbs);
                if (!Directory.Exists(defaultResolved)) continue;
            }

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

        // v1.0.0.x hotfix (2026-08-27):raw 仍是 SettingsDefaults 的默认 seed 值
        // (== subdir 常量,如 "Workflow" / "Python" / "Envs")且目录不存在 →
        // skip。这是用户压根没启用该功能(没下 workflow / 没建 env / 没装依赖)
        // 的正常状态,不是路径错位。CurrentValue 和 RecommendedValue 此时完全
        // 一致(都 = projectRoot/subdir),弹窗反而让用户疑惑"为啥要确认?没动啊"。
        //
        // 但 raw != subdir 的情况(用户主动改成别的相对 / 绝对路径)→ 仍按原
        // 规则检测:resolve 后不存在 = 用户配错了,提示迁移。
        if (!Path.IsPathRooted(raw) && string.Equals(raw, subdir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        items.Add(new PathMigrationItem(
            Label: label,
            CurrentValue: resolved,
            RecommendedValue: Path.GetFullPath(Path.Combine(projectRoot, subdir))));
    }

    private readonly record struct BuiltinKind(string Kind, string Subdir);
}