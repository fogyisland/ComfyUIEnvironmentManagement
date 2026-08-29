using System;
using System.Diagnostics;
using System.IO;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v1.0.0.x (2026-08-29):Forge env 自动生成 <c>extra_model_paths.yaml</c>。
///
/// 源 = <see cref="Settings.DefaultModelsDirectory"/>(用户在 Settings → 路径 配置的全局
/// 默认 Models 目录,跟 LocalModels sidebar 同 source)叠加
/// <see cref="Settings.ForgePaths"/> 6 个 per-type 覆盖字段。任一覆盖字段非空 → 该
/// sub-key 走绝对路径;空 → fallback 到 <c>&lt;DefaultModelsDirectory&gt;/&lt;子目录名&gt;</c>。
///
/// 派生 = 6 类子目录:<c>checkpoints / loras / vae / embeddings / hypernetworks /
/// controlnet</c>(都是 ComfyUI 风格子目录名 — 用户共享盘实际是 ComfyUI 布局,
/// webui.py 只看目录里的 <c>.safetensors</c> 不关心目录名)。
///
/// YAML schema 跟 A1111/Forge 官方 <c>extra_model_paths.yaml</c> 一致:
///   a1111_webui_dir / sd_models_dir / a1111_embedding_dir 等字段 Forge 都认,
/// 但最稳的 schema 是顶层 key = section name(<c>comfyui_manager_forge</c>),其下
/// <c>checkpoints / loras / vae / controlnet</c> 等字段 Forge 自动 merge 到对应
/// 内部 list。包成自定义 section 避免污染 Forge 内置 <c>models/Stable-diffusion</c>
/// 等默认值。
///
/// 行为:
/// - <see cref="BuildYamlContent"/> 纯函数 — Settings → YAML 字符串,可单元测试
///   单独跑(不需要 IO)。DefaultModelsDirectory 空 + ForgePaths 全空 → 返 <c>""</c>。
/// - <see cref="EnsureWritten"/> 副作用函数 — 写 <c>&lt;envRoot&gt;/extra_model_paths.yaml</c>。
///   原子写(tmp + File.Move overwrite),UTF-8 无 BOM。
///
/// 错误策略:<see cref="EnsureWritten"/> DefaultModelsDirectory 空 + ForgePaths 全空 →
/// 抛 <see cref="InvalidOperationException"/>(caller 应自己预先 validate;这里 defense
/// in depth);磁盘满 / 权限不够等 IO 异常上抛,让 caller 决定 fail-fast 还是 warn-only。
/// </summary>
public static class ForgeExtraModelPathsYamlGenerator
{
    /// <summary>
    /// YAML 顶层 section key —— Forge fork 把它当作 namespace,不跟 Forge 内置
    /// <c>a1111_webui_dir</c> / <c>sd_models_dir</c> 等已知 key 冲突。
    /// </summary>
    public const string SectionKey = "comfyui_manager_forge";

    /// <summary>
    /// YAML 字段 → ComfyUI 风格子目录名(用户共享盘实际布局)。顺序无所谓,但保持
    /// 稳定以便测试断言。
    /// </summary>
    private static readonly (string Field, string Subdir)[] Subdirs =
    {
        ("checkpoints",    "checkpoints"),
        ("loras",          "loras"),
        ("vae",            "vae"),
        ("embeddings",     "embeddings"),
        ("hypernetworks",  "hypernetworks"),
        ("controlnet",     "controlnet"),
    };

    /// <summary>
    /// 纯函数:把当前 <paramref name="settings"/> 渲染成 Forge 可读的
    /// <c>extra_model_paths.yaml</c> 内容。
    ///
    /// <para>
    /// 派生规则(每个 sub-key 独立判断):
    /// - <see cref="ForgePaths"/> 对应字段非空 → 用该绝对路径(覆盖)
    /// - 否则 <see cref="Settings.DefaultModelsDirectory"/> 非空 → 用 <c>&lt;base&gt;/&lt;sub&gt;</c>(派生)
    /// - 两者皆空 → 该 sub-key 在 yaml 里跳过(同时整个 yaml 也无意义 → 整体返 "")
    /// </para>
    ///
    /// 空 / 空白 <see cref="Settings.DefaultModelsDirectory"/> + <see cref="ForgePaths"/>
    /// 6 字段全空 → 返 <c>""</c>(caller 决定要不要跳过写入;这里不抛 — 空源 = 不写 yaml 是合法状态)。
    ///
    /// 路径分隔符统一:Windows <c>\</c> → <c>/</c>(A1111/Forge yaml 惯例,跨平台
    /// yaml 解析器在 Windows 路径上经常不稳;Forge yaml loader 走 PyYAML,PyYAML
    /// 对 <c>\</c> 在 quoted context 里也认,但 forward slash 是 A1111/Forge
    /// 官方文档推荐的写法)。
    /// </summary>
    /// <param name="settings">当前生效的 Settings(<c>DefaultModelsDirectory</c> + <c>ForgePaths</c> 联合 source of truth)。</param>
    /// <returns>YAML 字符串;空 → 跳过写文件。</returns>
    public static string BuildYamlContent(Settings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        // GetFullPath 解析相对路径 + 去尾斜杠 — 用户在 Settings 配 "D:/models/" 或
        // "D:\models" 都能统一为 "D:\models"(不带尾斜杠)。后续 sub-path 派生
        // 一致性更好(避免 "D:\models" vs "D:\models\" 产生两种 base_path)。
        var baseRaw = !string.IsNullOrWhiteSpace(settings.DefaultModelsDirectory)
            ? Path.GetFullPath(settings.DefaultModelsDirectory)
            : "";

        // v1.0.0.x:ForgePaths 6 个 per-type 覆盖字段 — 跟 baseRaw 联合派生每个 sub-key。
        // Subdirs 顺序固定(checkpoints → controlnet),保证 yaml 输出稳定以便测试断言。
        var fp = settings.ForgePaths ?? new ForgePaths();
        var resolved = new (string Field, string Value)[Subdirs.Length];
        var anyResolved = false;
        for (var i = 0; i < Subdirs.Length; i++)
        {
            var (field, subdir) = Subdirs[i];
            var over = field switch
            {
                "checkpoints" => fp.CheckpointsDir,
                "loras" => fp.LorasDir,
                "vae" => fp.VaeDir,
                "embeddings" => fp.EmbeddingsDir,
                "hypernetworks" => fp.HypernetworksDir,
                "controlnet" => fp.ControlnetDir,
                _ => null,
            };
            var value = ResolveDir(over, baseRaw, subdir);
            resolved[i] = (field, value);
            if (!string.IsNullOrEmpty(value)) anyResolved = true;
        }

        // DefaultModelsDirectory 空 + ForgePaths 全空 → 整体返 "",跟之前语义一致。
        if (!anyResolved)
        {
            return "";
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(SectionKey).Append(':').Append('\n');
        // base_path 字段 Forge 也认(A1111 风格),让用户在 webui UI 里看到这个
        // section 来自哪个根目录;不影响 file path 派生(每 subdir 字段独立指)。
        // DefaultModelsDirectory 空时跳过(没全局根目录)。
        if (!string.IsNullOrEmpty(baseRaw))
        {
            sb.Append("  base_path: ").Append(ToForwardSlash(baseRaw)).Append('\n');
        }
        foreach (var (field, value) in resolved)
        {
            if (string.IsNullOrEmpty(value)) continue;
            sb.Append("  ").Append(field).Append(": ")
              .Append(ToForwardSlash(value)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析单个 sub-key 的最终路径:<paramref name="overridePath"/> 非空 → 直接用;
    /// 否则 <paramref name="defaultModelsDir"/> 非空 → <c>defaultModelsDir/subdir</c>;
    /// 两者皆空 → ""(<see cref="BuildYamlContent"/> 整体返 "" 信号)。
    ///
    /// 不做 <see cref="Path.GetFullPath"/> normalization — override 由用户输入直接
    /// 用,fallback 由 <see cref="BuildYamlContent"/> 的 baseRaw(normalized)拼。
    /// </summary>
    private static string ResolveDir(string? overridePath, string defaultModelsDir, string subdir)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath!;
        if (string.IsNullOrEmpty(defaultModelsDir)) return "";
        return Path.Combine(defaultModelsDir, subdir);
    }

    /// <summary>
    /// 副作用:把 <paramref name="settings"/> 渲染的 yaml 写到
    /// <c>&lt;envRootPath&gt;/extra_model_paths.yaml</c>。
    ///
    /// 失败行为:
    /// - <see cref="Settings.DefaultModelsDirectory"/> 空 + <see cref="Settings.ForgePaths"/>
    ///   6 字段全空 → <see cref="BuildYamlContent"/> 返 "" → 抛
    ///   <see cref="InvalidOperationException"/>(defense in depth — caller 应该
    ///   预先 IsNullOrWhiteSpace 检查;这里抛出来让 bug 早期暴露而不是静默生成
    ///   一个空 base_path 的 yaml)。
    /// - <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> /
    ///   <see cref="DirectoryNotFoundException"/> → 上抛(caller 决定 fail-fast
    ///   还是 warn-only)。
    ///
    /// 原子写:tmp 文件 + <see cref="File.Move(string, string, bool)"/>
    /// overwrite,避免 env 启动期间 yaml 写到一半 Forge 来读造成解析失败。
    /// </summary>
    /// <param name="envRootPath">env 根目录(Forge 自己的工作目录,<c>webui.py</c> 启动时 cwd)。</param>
    /// <param name="settings">当前 Settings。</param>
    public static void EnsureWritten(string envRootPath, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(envRootPath))
            throw new ArgumentException("envRootPath 不能为空", nameof(envRootPath));
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        var content = BuildYamlContent(settings);
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException(
                $"Settings.DefaultModelsDirectory 与 Settings.ForgePaths.* 都为空,无法生成 Forge extra_model_paths.yaml。");
        }

        // 存在旧 yaml 文件且包含非 comfyui_manager_forge section → 警告一下(用户
        // 手写的内容会被覆盖;后面需求来了再加 preserve 逻辑,目前 YAGNI)。
        var targetPath = Path.Combine(envRootPath, "extra_model_paths.yaml");
        if (File.Exists(targetPath))
        {
            var existing = File.ReadAllText(targetPath);
            if (ContainsNonGeneratedSection(existing))
            {
                Debug.WriteLine(
                    $"[ForgeExtraModelPathsYamlGenerator] 警告: {targetPath} 含非 " +
                    $"{SectionKey} section,将被覆盖。用户手写内容需手动备份。");
            }
        }

        Directory.CreateDirectory(envRootPath);
        // 原子写:先写 tmp → File.Move overwrite。Forge 启动期间读这个 yaml 时
        // 不会看到半截文件(mid-write 状态 → PyYAML 解析失败 → Forge 整个 webui
        // crash)。tmp 跟 target 同目录 → File.Move 走 rename atomic(同 NTFS 卷)。
        var tmpPath = targetPath + ".tmp";
        // File.WriteAllText 默认 UTF-8 无 BOM(Python PyYAML 喜欢无 BOM 的 utf-8,
        // BOM 在某些 yaml loader 会被 parse 成 key 名的一部分 → 整个 yaml 解析 fail)。
        File.WriteAllText(tmpPath, content);
        try
        {
            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            // tmp 残留不致命 — 下次启动会覆盖。catch-all + best-effort 清理。
            try { File.Delete(tmpPath); } catch { }
            throw;
        }
    }

    private static string ToForwardSlash(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        // Windows backslash → forward slash(A1111/Forge yaml 惯例)。
        // Path.Combine 在 Windows 上返回 "\",这里手动 replace 即可。
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// 扫已有 yaml 文本,看是否含 <paramref name="SectionKey"/> 之外的顶层 section。
    /// 实现:用行首 anchor 找形如 <c>^([A-Za-z_]\w*):$</c> 的 key line,收集 set,
    /// 去掉我们的 <see cref="SectionKey"/>,set 非空 → true。
    ///
    /// 简化:不严格解析 yaml AST(手写 YAML 字符串生成器,不需要 full parser)。
    /// 注释行 / 空行 / 缩进 continuation 都跳过 — 这对 in-house 生成的 yaml 够用;
    /// 用户手写带奇怪 anchor 的 yaml 可能误判(后续真出现 user-added section
    /// 需求时再上 YamlDotNet 解析)。
    /// </summary>
    private static bool ContainsNonGeneratedSection(string existingContent)
    {
        if (string.IsNullOrWhiteSpace(existingContent)) return false;
        foreach (var line in existingContent.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0) continue;
            // 顶层 section key 行 = "key:" 形式(无前导空格,key 是 identifier)。
            // 字段行("  base_path: ..."等)有前导缩进,被跳过。
            if (char.IsWhiteSpace(trimmed[0])) continue;
            if (trimmed.StartsWith("#")) continue;  // 注释行跳过
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx <= 0) continue;
            var key = trimmed[..colonIdx].Trim();
            if (key.Length == 0) continue;
            // 全 identifier 字符才算顶层 key(field name 也可能含冒号分隔,纯 identifier 是
            // 顶层 section 的形态;其他形态留待后续真解析时处理)
            bool isIdentifier = true;
            foreach (var c in key)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                {
                    isIdentifier = false;
                    break;
                }
            }
            if (!isIdentifier) continue;
            if (key != SectionKey) return true;
        }
        return false;
    }
}