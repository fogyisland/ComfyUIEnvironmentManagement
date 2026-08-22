using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ComfyUI.Manager.Models;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// SettingsDefaults:首次启动时把已经在 projectRoot 下的绝对路径迁移为相对路径,
/// 并给 template 类 path 字段填上 package root 下的默认子目录名。
///
/// 区分两类 path:
/// - **template paths** (TemplatePythonDir / TemplateComfyuiDir) — 默认填子目录名,
///   因为这些是 "包自带的资源" 类的东西(模板 Python / ComfyUI 源)应该落在程序根下,
///   不需要跑到额外的地方。
/// - **user-configured paths** (EnvsDir / GlobalNodesDir) — 默认保持空,
///   因为这些是用户主动管理的数据(env 列表 / 全局 catalog),不预创建不预填,
///   留到用户配置后再用。服务使用点(EnvCreatorService)在 path 为空时主动报错。
///
/// 存相对路径而非绝对路径:绿色 zip 跨机器/跨盘符时 settings.json 不需要
/// 重新生成。所有 path 使用方在运行时通过 Path.Combine(projectRoot, settings.X)
/// 解出绝对路径。
///
/// 默认子目录名:
///   - Python    : 模板 Python 根(指向 package 自带的 portable Python/ 目录,
///                 内含 3.10/3.11/.../python.exe)
///   - ComfyUITemplate : shared 布局的 ComfyUI 源(package root/ComfyUITemplate/,v1.0.0+ 从 `ComfyUI/` 重命名)
///   - Envs      : EnvCreatorService 创建 env 时放这里(空 → 不预创建)
///   - Nodes     : 全局 catalog 节点根(空 → 不预创建)
///   - LocalNodes: catalog 主页「下载」按钮的目标目录(template-style,空字段自动填子目录名)
///   - Workflow  : 工作流市场下载目录(template-style,空字段自动填子目录名)
///   - Models    : env-create junction 目标 + 模型市场下载目录(template-style,空字段自动填子目录名)
///
/// v1.0.0 目录结构重构:子目录统一 PascalCase 与 package root 一致。旧版本(全小写
/// 或 kebab-case)的子目录名(settings.json 里写过的)在 Apply 里通过 MigrateOldSubdirName
/// 一次性迁到 PascalCase,无需用户手工改 settings.json。
/// </summary>
public static class SettingsDefaults
{
    public const string TemplatePythonSubdir = "Python";
    public const string TemplateComfyuiSubdir = "ComfyUITemplate";
    public const string EnvsSubdir = "Envs";
    public const string GlobalNodesSubdir = "Nodes";
    public const string LocalNodesSubdir = "LocalNodes";
    public const string WorkflowsSubdir = "Workflow";
    public const string ModelsSubdir = "Models";
    public const string DefaultQuerySourceName = "comfyui manager";
    public const string DefaultQuerySourceUrl =
        "https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager/main/custom-node-list.json";
    public const string DefaultDownloadSourceName = "comfyui manager";
    public const string DefaultDownloadSourceUrl = "https://github.com/comfyanonymous/{node}";
    public const CatalogViewMode DefaultCatalogViewMode = CatalogViewMode.List;
    public const int DefaultCatalogPageSize = 20;

    /// <summary>
    /// template paths 空时填默认值(子目录名);user-configured paths 空时保持空。
    /// 同时把已经在 projectRoot 下的绝对路径迁移为相对路径。
    ///
    /// 1. template paths 空 → 填默认子目录名(python / ComfyUI)
    /// 2. user-configured paths 空 → 保持空(交给服务层在使用时报错)
    /// 3. 相对路径 → 不动
    /// 4. 绝对路径且在 projectRoot 之下 → 转相对(剥掉 projectRoot 前缀)
    /// 5. 不在 projectRoot 下的绝对路径(用户故意选的别处)→ 保留
    /// </summary>
    public static void Apply(Settings s, string projectRoot)
    {
        if (s is null) return;

        // v1.0.0 目录结构重构:老 settings.json 里写过的旧子目录名(全小写 / kebab-case)
        // 一次性迁到 PascalCase,避免 service 在 projectRoot/envs/ 和 projectRoot/Envs/ 两边分裂。
        // Resolve 之前的 MigrateOldSubdirName 必须走在前面,否则后续 MigrateOnly 会跳过相对路径。
        s.TemplatePythonDir = MigrateOldSubdirName(s.TemplatePythonDir, "python", TemplatePythonSubdir);
        s.TemplateComfyuiDir = MigrateOldSubdirName(s.TemplateComfyuiDir, "ComfyUI", TemplateComfyuiSubdir);
        // 注:上面 "ComfyUI" 是旧名,"ComfyUITemplate" 是 v1.0.0+ 新名。
        // 老 settings.json 里写过 "ComfyUI" 的会自动迁移;用户也可填其它子目录名。
        s.EnvsDir = MigrateOldSubdirName(s.EnvsDir, "envs", EnvsSubdir);
        s.GlobalNodesDir = MigrateOldSubdirName(s.GlobalNodesDir, "global-nodes", GlobalNodesSubdir);
        // v0.6.5.9: Catalog 主页「下载」按钮的目标目录。template-style,空字段自动填子目录名。
        s.LocalNodeDirectory = MigrateOldSubdirName(s.LocalNodeDirectory, "local-nodes", LocalNodesSubdir);
        // v0.6.19:WorkflowsDirectory — template-style,空字段自动填 "Workflow" 子目录名
        s.WorkflowsDirectory = MigrateOldSubdirName(s.WorkflowsDirectory, "workflows", WorkflowsSubdir);
        // v0.6.22+:DefaultModelsDirectory — 同时担任 env-create junction 目标 + 模型市场下载目录。
        // 原 v0.6.20 ModelsDirectory 字段已硬删,空字段自动填 "Models" 子目录名。
        s.DefaultModelsDirectory = MigrateOldSubdirName(s.DefaultModelsDirectory, "models", ModelsSubdir);

        s.TemplatePythonDir = Resolve(s.TemplatePythonDir, TemplatePythonSubdir, projectRoot);
        s.TemplateComfyuiDir = Resolve(s.TemplateComfyuiDir, TemplateComfyuiSubdir, projectRoot);
        s.EnvsDir = MigrateOnly(s.EnvsDir, projectRoot);
        s.GlobalNodesDir = MigrateOnly(s.GlobalNodesDir, projectRoot);
        s.LocalNodeDirectory = Resolve(s.LocalNodeDirectory, LocalNodesSubdir, projectRoot);
        s.WorkflowsDirectory = Resolve(s.WorkflowsDirectory, WorkflowsSubdir, projectRoot);
        s.DefaultModelsDirectory = Resolve(s.DefaultModelsDirectory, ModelsSubdir, projectRoot);

        // 节点源:空列表 → 装默认 "comfyui manager";空 active → 回落到列表第一条
        if (s.QuerySources is null || s.QuerySources.Count == 0)
        {
            s.QuerySources = new List<NodeSource>
            {
                new() { Name = DefaultQuerySourceName, Url = DefaultQuerySourceUrl },
            };
        }
        if (s.DownloadSources is null || s.DownloadSources.Count == 0)
        {
            s.DownloadSources = new List<NodeSource>
            {
                new() { Name = DefaultDownloadSourceName, Url = DefaultDownloadSourceUrl },
            };
        }
        if (string.IsNullOrWhiteSpace(s.ActiveQuerySourceName))
        {
            s.ActiveQuerySourceName = s.QuerySources[0].Name;
        }
        if (string.IsNullOrWhiteSpace(s.ActiveDownloadSourceName))
        {
            s.ActiveDownloadSourceName = s.DownloadSources[0].Name;
        }

        // Catalog 视图:默认值兜底(空枚举/0 表示未设 → 默认 List / 20)
        if (s.CatalogPageSize <= 0) s.CatalogPageSize = DefaultCatalogPageSize;
        // CatalogViewMode 枚举:JSON 反序列化时无效值会落到 0 (List),不需要额外 fallback

        // —— v0.6.5.6 hotfix:清理 v0.6.5.6 留下的坏 migration 条目 ——
        // 精确匹配 <TemplatePythonDir>/<DefaultPythonVersion>/python.exe 且文件不存在
        // → 当成坏 migration 产物清掉,让下方 migration 重新合成正确的。
        // 不动用户手动 Browse 加的(路径不会精确等于这个合成路径)。
        if (!string.IsNullOrWhiteSpace(s.TemplatePythonDir)
            && !string.IsNullOrWhiteSpace(s.DefaultPythonVersion)
            && s.PythonInterpreters.Count > 0)
        {
            var resolvedDir = Path.IsPathRooted(s.TemplatePythonDir)
                ? s.TemplatePythonDir
                : Path.Combine(projectRoot, s.TemplatePythonDir);
            var multiVerPath = Path.Combine(resolvedDir, s.DefaultPythonVersion, "python.exe");
            if (!File.Exists(multiVerPath))
            {
                var fullMultiVer = Path.GetFullPath(multiVerPath);
                var removedCount = s.PythonInterpreters.RemoveAll(p =>
                    string.Equals(
                        Path.GetFullPath(p.Path),
                        fullMultiVer,
                        StringComparison.OrdinalIgnoreCase));
                // 只在确实清掉了坏条目时回退 active,避免误改无关 state
                if (removedCount > 0
                    && !string.IsNullOrWhiteSpace(s.ActivePythonInterpreterName)
                    && !s.PythonInterpreters.Any(p => p.Name == s.ActivePythonInterpreterName))
                {
                    s.ActivePythonInterpreterName = s.PythonInterpreters.Count > 0
                        ? s.PythonInterpreters[0].Name
                        : "";
                }
            }
        }

        // —— v0.6.5.6:首次加载老 settings.json 时,从老 TemplatePythonDir/DefaultPythonVersion 合成默认条目 ——
        if (s.PythonInterpreters.Count == 0
            && !string.IsNullOrWhiteSpace(s.TemplatePythonDir)
            && !string.IsNullOrWhiteSpace(s.DefaultPythonVersion))
        {
            // 探测 python.exe 实际位置,支持两种 layout:
            //   1. multi-version (spec 原意):<TemplatePythonDir>/<DefaultPythonVersion>/python.exe
            //   2. flat venv root (portable python 实际布局):<TemplatePythonDir>/python.exe
            // TemplatePythonDir 可能是相对或绝对,先 resolve 到绝对路径再做 File.Exists。
            var resolvedDir = Path.IsPathRooted(s.TemplatePythonDir)
                ? s.TemplatePythonDir
                : Path.Combine(projectRoot, s.TemplatePythonDir);
            var multiVerPath = Path.Combine(resolvedDir, s.DefaultPythonVersion, "python.exe");
            var flatPath = Path.Combine(resolvedDir, "python.exe");
            string? candidate = null;
            if (File.Exists(multiVerPath)) candidate = multiVerPath;
            else if (File.Exists(flatPath)) candidate = flatPath;

            if (candidate != null)
            {
                s.PythonInterpreters.Add(new PythonInterpreter
                {
                    Name = s.DefaultPythonVersion,
                    Path = candidate,
                });
                s.ActivePythonInterpreterName = s.DefaultPythonVersion;
            }
            // 都不存在 → 跳过合成,留空让用户去 Settings → Browse 添加。
            // (避免合成出 <dir>/<version>/python.exe 这种死路径污染 active。)
            // 老字段 TemplatePythonDir / DefaultPythonVersion 保留不动
        }

        // v0.6.11++:首次启动种 curated 常用节点(只在空时 seed,G13)。
        s.CommonNodes = SeedCommonNodesIfEmpty(s.CommonNodes);

        // v1.0.0 Phase 1:dev build 解锁所有 hidden feature flag — 用户原话
        // "开发阶段没有限制,所以在开发就不要限制了模型市场和工作流库了,
        // 只有在 release 时候才限制"。release build 跳过此分支,保留 release 默认
        // (HF/ModelScope disabled 等)保护没配 token 的新装用户避免看到空结果。
        ApplyDevOverridesIfEnabled(s);

        // v0.6.12:LogDirectory 非空则 Directory.CreateDirectory,失败静默
        if (!string.IsNullOrWhiteSpace(s.LogDirectory))
        {
            try
            {
                var dir = s.LogDirectory;
                if (!Path.IsPathRooted(dir))
                    dir = Path.Combine(projectRoot, dir);
                Directory.CreateDirectory(dir);
            }
            catch { /* 权限/盘满/路径非法 → 静默,运行时再 CreateDirectory 兜底 */ }
        }
    }

    /// <summary>
    /// v1.0.0 Phase 1:dev build 强制启用所有 hidden feature flag。Release build
    /// 此方法体是空(no-op,const fold 优化掉),保持 release 字段默认值不变。
    ///
    /// 启用项:
    /// - ModelSourceHuggingFaceEnabled = true(dev 默认开;release 默认 false 防止空结果)
    /// - ModelSourceModelScopeEnabled = true(dev 默认开;release 默认 false 同理)
    /// - WorkflowSourceCommunityJson/CivitAi/OpenArt 三 source — release 已默认 true,
    ///   dev 此处显式置 true 防止用户手动关掉后 dev 跳不出页面
    ///
    /// 注:不修改 CivitAI 默认(已 true)、不修改 ModelSourceProxyMode 默认
    /// (已 InheritGlobal,合理默认)、不修改 CivitAiUseMirror(默认 false,镜像站可选)。
    /// </summary>
    private static void ApplyDevOverridesIfEnabled(Settings s)
    {
#if DEBUG
        s.ModelSourceHuggingFaceEnabled = true;
        s.ModelSourceModelScopeEnabled = true;
        s.WorkflowSourceCommunityJsonEnabled = true;
        s.WorkflowSourceCivitAiEnabled = true;
        s.WorkflowSourceOpenArtEnabled = true;
#endif
    }

    /// <summary>
    /// v0.6.11++:首次启动种 10 个 curated 常用节点到 <see cref="Settings.CommonNodes"/>。
    /// 只在 <c>CommonNodes.Count == 0</c> 时 seed(G13 防覆盖),保护用户清空操作。
    /// 用户可取消勾选(<c>Enabled=false</c>)— 仍保留条目,只是不装。
    /// </summary>
    private static List<CommonNodeEntry> SeedCommonNodesIfEmpty(List<CommonNodeEntry>? current)
    {
        if (current is { Count: > 0 }) return current;
        return new List<CommonNodeEntry>
        {
            new() { Id = "ltdrdata/ComfyUI-Manager",         DisplayName = "ComfyUI Manager",                IsBuiltIn = true, Enabled = true },
            new() { Id = "ltdrdata/ComfyUI-Impact-Pack",     DisplayName = "ComfyUI Impact Pack",            IsBuiltIn = true, Enabled = true },
            new() { Id = "ltdrdata/ComfyUI-Inspire-Pack",    DisplayName = "ComfyUI Inspire Pack",           IsBuiltIn = true, Enabled = true },
            new() { Id = "pythongosssss/ComfyUI-Custom-Scripts", DisplayName = "ComfyUI Custom Scripts",     IsBuiltIn = true, Enabled = true },
            new() { Id = "rgthree/rgthree-comfy",            DisplayName = "rgthree Comfy",                 IsBuiltIn = true, Enabled = true },
            new() { Id = "jags111/efficiency-nodes-comfyui", DisplayName = "Efficiency Nodes",              IsBuiltIn = true, Enabled = true },
            new() { Id = "Kosinkadink/ComfyUI-VideoHelperSuite", DisplayName = "ComfyUI Video Helper Suite", IsBuiltIn = true, Enabled = true },
            new() { Id = "kijai/ComfyUI-KJNodes",            DisplayName = "ComfyUI KJNodes",               IsBuiltIn = true, Enabled = true },
            new() { Id = "kijai/ComfyUI-Florence2",          DisplayName = "ComfyUI Florence2",             IsBuiltIn = true, Enabled = true },
            new() { Id = "Kosinkadink/ComfyUI-Advanced-ControlNet", DisplayName = "ComfyUI Advanced ControlNet", IsBuiltIn = true, Enabled = true },
        };
    }

    /// <summary>
    /// template path:空字段填默认子目录名,其余走迁移逻辑。
    /// </summary>
    private static string Resolve(string current, string defaultSubdir, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            return defaultSubdir;
        }
        return MigrateOnly(current, projectRoot);
    }

    /// <summary>
    /// v1.0.0:老 settings.json 里写过的旧子目录名(全小写 / kebab-case)迁到新 PascalCase。
    /// 只在字段等于 oldName(精确匹配,忽略大小写)时改写;其他值(用户手填的绝对路径、
    /// 已经迁过的 PascalCase、空)都原样保留。空字段不在这里处理,留给 Resolve / MigrateOnly。
    /// </summary>
    private static string MigrateOldSubdirName(string current, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(current)) return current;
        return string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase) ? newName : current;
    }

    /// <summary>
    /// 空字符串 / 相对路径直接返回;绝对路径若在 projectRoot 下则转相对,
    /// 否则保留。
    /// </summary>
    private static string MigrateOnly(string current, string projectRoot)
    {
        // 空 → 保持空(交给服务层校验)
        if (string.IsNullOrWhiteSpace(current))
        {
            return current;
        }
        // 已经是相对的 → 不动
        if (!Path.IsPathRooted(current))
        {
            return current;
        }
        // 绝对路径:尝试剥 projectRoot 前缀,成功就转相对
        try
        {
            var fullProject = Path.GetFullPath(projectRoot);
            var fullCurrent = Path.GetFullPath(current);
            var fullProjectWithSep = fullProject.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (fullCurrent.StartsWith(fullProjectWithSep, System.StringComparison.OrdinalIgnoreCase))
            {
                // 用 Path.GetRelativePath 规范化分隔符,避免漏 ..\ 或混 \ 和 /
                return Path.GetRelativePath(fullProject, fullCurrent);
            }
        }
        catch
        {
            // Path.GetFullPath 失败(罕见)→ 当作不可迁移,保留原值
        }
        // 不在 projectRoot 下的绝对路径(用户故意选的别处)→ 保留
        return current;
    }
}
