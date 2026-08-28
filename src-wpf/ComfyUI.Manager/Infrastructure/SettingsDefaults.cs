using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// SettingsDefaults:首次启动时把已经在 projectRoot 下的绝对路径迁移为相对路径,
/// 并给 template 类 path 字段填上 package root 下的默认子目录名。
///
/// 区分两类 path:
/// - **template paths** (TemplatePythonDir / SystemTemplateLibraryDir) — 默认填子目录名,
///   因为这些是 "包自带的资源" 类的东西(模板 Python 源 / 系统模板库)应该落在程序根下,
///   不需要跑到额外的地方。ComfyUI 模板源目录由 Settings.Templates["ComfyUI"].LocalSourceDir
///   承载(v1.0.0 multi-template 重构),老 template_comfyui_dir 字段在 T12 移除。
///   v1.0.0.x #569 phase 2:SystemTemplateLibraryDir 空时 seed 相对 "ENVTemplate",
///   让 TemplatePathResolver.Resolve 拼出 <projectRoot>/ENVTemplate/<Kind>,
///   符合"项目目录+envtemplate+模板路径"约定。
/// - **user-configured paths** (EnvsDir) — 默认保持空,
///   因为这些是用户主动管理的数据(env 列表),不预创建不预填,
///   留到用户配置后再用。服务使用点(EnvCreatorService)在 path 为空时主动报错。
///   v1.0.0.x #592 起,EnvsDir / LocalNodeDirectory / LocalNodesDirectory 改用
///   ResolveAsAbsolute 3-branch,空时 seed projectRoot + subdir 的绝对路径;
///   v1.0.0.x 扩展到 GlobalNodesDir(catalog 数据库 nodes.db 所在目录),空时
///   seed projectRoot + "Nodes/" 的绝对路径,跨机器自动跟随 projectRoot。
///
/// 存相对路径而非绝对路径:绿色 zip 跨机器/跨盘符时 settings.json 不需要
/// 重新生成。所有 path 使用方在运行时通过 Path.Combine(projectRoot, settings.X)
/// 解出绝对路径。
///
/// 默认子目录名:
///   - Python    : 模板 Python 根(指向 package 自带的 portable Python/ 目录,
///                 内含 3.10/3.11/.../python.exe)
///   - ENVTemplate: 系统模板库根(v1.0.0.x seed,放所有内置 + 用户模板源码 ——
///                 ComfyUI / Forge / SwarmUI / OpenVoice / Whisper / CoquiTTS / Bark)
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
    /// <summary>
    /// v1.0.0.x #569 phase 2: 系统模板库目录的默认相对子目录名。
    /// 用户原话"新建的时候发现模板源路径一样不对,按道理他应该是
    /// 项目目录 + envtemplate + 模板路径"。空 settings + 模板解析 fallback
    /// 到 AppContext.BaseDirectory(= bin/Debug/net8.0-windows/)污染 dev build,
    /// 所以空时主动 seed 相对 "ENVTemplate",让 TemplatePathResolver.Resolve
    /// 在运行时拼出 &lt;projectRoot&gt;/ENVTemplate/&lt;Kind&gt;。
    /// </summary>
    public const string SystemTemplateLibrarySubdir = "ENVTemplate";
    public const string EnvsSubdir = "Envs";
    public const string GlobalNodesSubdir = "Nodes";
    public const string LocalNodesSubdir = "LocalNodes";
    // v1.0.0.x #577:本地常用节点根目录(env 行「安装本地常用」按钮的源)— 小写,
    // 跟仓库根下用户已经手动创建的 `localnodes/` 一致(LocalNodesSubdir PascalCase 是
    // Catalog 单节点下载目录,语义不同所以分开命名)。
    public const string LocalNodesBulkSubdir = "localnodes";
    public const string WorkflowsSubdir = "Workflow";
    public const string ModelsSubdir = "Models";
    /// <summary>
    /// v1.0.0.x:日志根目录的默认子目录名(Logs/ 子目录的父目录)。用户原话
    /// "目录为 logs"(lowercase),SettingsDefaults.Apply 自动 seed 当前
    /// projectRoot + 此 subdir 的绝对路径。AppLogger 会再拼上 "Logs" 子目录
    /// 形成实际写入路径 &lt;projectRoot&gt;/logs/Logs/。
    /// </summary>
    public const string LogsSubdir = "logs";
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
        => Apply(s, projectRoot, rawJson: null);

    /// <summary>
    /// v1.0.0 (T12):Apply 重载,接收 raw JSON 用于模板字段迁移。
    /// 生产链路 SettingsRepository.Load() 拿到 settings 后,应把磁盘上的 settings.json
    /// 文本传给本重载,以便迁移老 template_comfyui_dir 字段到 Templates["ComfyUI"]。
    /// 测试可手动构造老格式 JSON 字符串 + 直接 new Settings() 来验证迁移。
    /// </summary>
    public static void Apply(Settings s, string projectRoot, string? rawJson)
    {
        if (s is null) return;

        // v1.0.0.x: shipped portable python 存在 + 用户没配过解释器 + 老字段为空
        // → seed 相对路径 "python/python.exe"。**必须放在 line 99 `s.TemplatePythonDir = Resolve(...)`
        // 之前** — Resolve 会把空 TemplatePythonDir 填成 "Python",后续 my gate 就 miss 了。
        // 老 settings.json 有 TemplatePythonDir/DefaultPythonVersion → 不 seed,留给下方
        // legacy migration 决定(可能合成成功,也可能跳过让用户 Browse)。
        if (s.PythonInterpreters.Count == 0
            && string.IsNullOrWhiteSpace(s.TemplatePythonDir)
            && string.IsNullOrWhiteSpace(s.DefaultPythonVersion))
        {
            string? relativeSubdir = null;
            if (File.Exists(Path.Combine(projectRoot, "python", "python.exe")))
                relativeSubdir = Path.Combine("python", "python.exe");
            else if (File.Exists(Path.Combine(projectRoot, "Python", "python.exe")))
                relativeSubdir = Path.Combine("Python", "python.exe");
            if (relativeSubdir != null)
            {
                s.PythonInterpreters.Add(new PythonInterpreter
                {
                    Name = "python",
                    Path = relativeSubdir,
                });
                s.ActivePythonInterpreterName = "python";
            }
        }

        // v1.0.0.x: shipped portable git 存在 + GitExe 空 → seed 绝对路径(当前 projectRoot
        // + "bin/git-portable/cmd/git.exe"),跟 python 解释器同款但用绝对存储。
        // v1.0.0.x: 用户原话"git 程序也是绝对目录方式和之前一样" ——
        // 跟 EnvsDir / LocalNodeDirectory 等本地资源路径一致,seed 当前 projectRoot +
        // "bin/git-portable/cmd/git.exe" 的绝对路径。Path.GetFullPath 规范化分隔符避免 UI
        // 显示 ..\ 之类相对形式;跨机器靠每次 Apply 重算 projectRoot + 重新拼接跟随。
        //
        // 行为跟 ResolveAsAbsolute 3-branch 对齐:
        // - 空 → seed 绝对
        // - 相对 → 转绝对 = projectRoot + current(升级已存在的相对路径到绝对)
        // - 绝对 → 保留(用户故意选的别处)
        if (File.Exists(Path.Combine(projectRoot, "bin", "git-portable", "cmd", "git.exe")))
        {
            var absoluteSeed = Path.GetFullPath(
                Path.Combine(projectRoot, "bin", "git-portable", "cmd", "git.exe"));
            if (string.IsNullOrWhiteSpace(s.GitExe))
            {
                s.GitExe = absoluteSeed;
            }
            else if (!Path.IsPathRooted(s.GitExe))
            {
                // 相对路径 → 转绝对 = projectRoot + 相对子路径
                s.GitExe = Path.GetFullPath(Path.Combine(projectRoot, s.GitExe));
            }
            // 绝对路径 → 保留(用户故意选的别处)
        }

        // v1.0.0 目录结构重构:老 settings.json 里写过的旧子目录名(全小写 / kebab-case)
        // 一次性迁到 PascalCase,避免 service 在 projectRoot/envs/ 和 projectRoot/Envs/ 两边分裂。
        // Resolve 之前的 MigrateOldSubdirName 必须走在前面,否则后续 MigrateOnly 会跳过相对路径。
        s.TemplatePythonDir = MigrateOldSubdirName(s.TemplatePythonDir, "python", TemplatePythonSubdir);
        // 注:v1.0.0 multi-template:ComfyUI 模板源目录由 Settings.Templates["ComfyUI"].LocalSourceDir
        // 承载(T12 移除老的 TemplateComfyuiDir 字段),默认值由 SeedBuiltInTemplatesIfMissing
        // 写到 Templates dict。这里不再"自动填默认子目录名"。
        s.EnvsDir = MigrateOldSubdirName(s.EnvsDir, "envs", EnvsSubdir);
        s.GlobalNodesDir = MigrateOldSubdirName(s.GlobalNodesDir, "global-nodes", GlobalNodesSubdir);
        // v0.6.5.9: Catalog 主页「下载」按钮的目标目录。template-style,空字段自动填子目录名。
        s.LocalNodeDirectory = MigrateOldSubdirName(s.LocalNodeDirectory, "local-nodes", LocalNodesSubdir);
        // v1.0.0.x #577:本地常用节点根目录(env 行「安装本地常用」按钮的源)。
        // template-style,空字段自动填子目录名。空 = 用户在设置页清空,运行时报错提示。
        s.LocalNodesDirectory = MigrateOldSubdirName(s.LocalNodesDirectory, "", LocalNodesBulkSubdir);
        // v0.6.19:WorkflowsDirectory — template-style,空字段自动填 "Workflow" 子目录名
        s.WorkflowsDirectory = MigrateOldSubdirName(s.WorkflowsDirectory, "workflows", WorkflowsSubdir);
        // v0.6.22+:DefaultModelsDirectory — 同时担任 env-create junction 目标 + 模型市场下载目录。
        // 原 v0.6.20 ModelsDirectory 字段已硬删,空字段自动填 "Models" 子目录名。
        s.DefaultModelsDirectory = MigrateOldSubdirName(s.DefaultModelsDirectory, "models", ModelsSubdir);

        s.TemplatePythonDir = Resolve(s.TemplatePythonDir, TemplatePythonSubdir, projectRoot);
        // 注:旧单行 s.TemplateComfyuiDir = Resolve(...) 删除 — 现由 Templates dict 接管。
        // v1.0.0.x #569 phase 2: SystemTemplateLibraryDir 空时 seed 相对 "ENVTemplate"。
        // 老用户 settings.json 里若写了大/小写不一致的 "envtemplate" 也迁过来。绝对路径
        // 在 projectRoot 下 → 转相对(剥前缀),外面 → 保留(用户故意选的别处)。
        // v1.0.0.x 用户反馈"路径设置也和其他一样会自动列出当前的绝对目录" ——
        // 跟 EnvsDir / LocalNodeDirectory 等本地资源路径一致,改 ResolveAsAbsolute 自动
        // seed 当前 projectRoot + "ENVTemplate" 的绝对路径(<projectRoot>/ENVTemplate/)。
        // 用户跨机器时每次 Apply 重新计算 projectRoot,自动跟随;不污染用户手填的别处绝对路径。
        s.SystemTemplateLibraryDir = MigrateOldSubdirName(s.SystemTemplateLibraryDir, "envtemplate", SystemTemplateLibrarySubdir);
        s.SystemTemplateLibraryDir = ResolveAsAbsolute(s.SystemTemplateLibraryDir, SystemTemplateLibrarySubdir, projectRoot);
        // v1.0.0.x: WorkflowsDirectory 跟其他本地资源路径一致 ——
        // 用户原话"工作流市场也变更为绝对目录,也就是和之前提到的一样"。
        // ResolveAsAbsolute:空 → seed 当前 projectRoot + "Workflow" 的绝对路径;
        // 相对 → 转绝对;绝对 → 保留。跨机器靠每次 Apply 重算 projectRoot + 重新拼接跟随。
        s.WorkflowsDirectory = ResolveAsAbsolute(s.WorkflowsDirectory, WorkflowsSubdir, projectRoot);
        s.DefaultModelsDirectory = Resolve(s.DefaultModelsDirectory, ModelsSubdir, projectRoot);
        // v1.0.0.x #592:本地资源路径默认绝对 — 用户原话"本地节点的路径默认获取当前
        // 项目的绝对路径,然后再加上相对路径,这样比较好" + "每次启动执行都会扫描
        // 当前项目目录"。三个字段(EnvsDir / LocalNodeDirectory / LocalNodesDirectory)
        // 空 → seed projectRoot + subdir 的绝对路径;相对 → 转绝对 = projectRoot + current;
        // 绝对 → 保留(用户故意选的别处)。每次启动 Apply 都重算 projectRoot,跨机器
        // 自动跟随。template paths(Python / ENVTemplate / Workflow / Models)不动仍存相对。
        s.EnvsDir = ResolveAsAbsolute(s.EnvsDir, EnvsSubdir, projectRoot);
        s.LocalNodeDirectory = ResolveAsAbsolute(s.LocalNodeDirectory, LocalNodesSubdir, projectRoot);
        s.LocalNodesDirectory = ResolveAsAbsolute(s.LocalNodesDirectory, LocalNodesBulkSubdir, projectRoot);
        // v1.0.0.x 扩展 #592 到 GlobalNodesDir — 用户原话"设置中的 base 节点目录指的是
        // 给 catalog 的数据库的内容,也需要和前面一样使用绝对目录,放的是 nodes.db 数据库"。
        // 行为跟 EnvsDir / LocalNodesDirectory 一致:空 → seed 当前 projectRoot + GlobalNodesSubdir
        // 的绝对路径(<projectRoot>/Nodes/),相对 → 转绝对,绝对 → 保留。nodes.db 由 catalog
        // 服务在第一次 scan 时建到该目录下,跨机器跟随 projectRoot。
        s.GlobalNodesDir = ResolveAsAbsolute(s.GlobalNodesDir, GlobalNodesSubdir, projectRoot);

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

        // v1.0.0 multi-template: migrate old template_comfyui_dir JSON property FIRST
        // (before seed)。Settings.TemplateComfyuiDir 字段在 T12 已移除,迁移通过
        // TryMigrateOldTemplateComfyuiDir(s, rawJson) 走 JsonDocument 读老 JSON。
        // rawJson 可为 null(无 JSON 可用,纯 in-memory settings) → 不迁移。
        TryMigrateOldTemplateComfyuiDir(s, rawJson);
        SeedBuiltInTemplatesIfMissing(s, projectRoot);
        // v1.0.0.x bug #509:之前 default 的 LocalSourceDir = "envTemplates\<Kind>",
        // 跟 Settings.SystemTemplateLibraryDir (用户配的 ENVTemplate/) 一拼 →
        // <system_template_library_dir>/envTemplates/<Kind> 多一层嵌套目录。
        // 新 default 已统一成 "<Kind>",但已 shipped 用户 settings.inf 里 4 个 GitHub
        // AI voice 已经被种了 "envTemplates\<Kind>" → 在 seed 之后 normalize 一次,
        // 替换为 "<Kind>"。Custom template 不动。
        NormalizeBuiltInTemplatePaths(s);

        // v1.0.0 Phase 1:dev build 解锁所有 hidden feature flag — 用户原话
        // "开发阶段没有限制,所以在开发就不要限制了模型市场和工作流库了,
        // 只有在 release 时候才限制"。release build 跳过此分支,保留 release 默认
        // (HF/ModelScope disabled 等)保护没配 token 的新装用户避免看到空结果。
        ApplyDevOverridesIfEnabled(s);

        // v1.0.0.x: LogDirectory 用户原话"日志目录也列出绝对路径,目录为 logs" ——
        // 跟 EnvsDir / LocalNodeDirectory 等本地资源路径一致:空 → seed 当前
        // projectRoot + "logs" 的绝对路径(<projectRoot>/logs);相对 → 转绝对;
        // 绝对 → 保留(用户故意选的别处)。AppLogger 构造时仍会拼上 "Logs" 子目录
        // (历史约定,parent of Logs/),实际日志写到 <projectRoot>/logs/Logs/。
        s.LogDirectory = ResolveAsAbsolute(s.LogDirectory, LogsSubdir, projectRoot);

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
            // v1.0.0.x:ComfyUI-Manager 从 seed 去掉 — 用户原话"针对 comfyui 有独立的
            // 界面进行操作",EnvListVM 的 ToggleComfyUiManagerCommand(env 行末按钮 +
            // 显示 inline 状态面板)已经在每行 env 单独管理 ComfyUI-Manager 装/卸,
            // 再在 common_nodes 里出现冗余且会跟独立界面抢 git clone 时机。新装用户
            // settings.inf CommonNodes 列表里不再有 ComfyUI-Manager。
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
    /// v1.0.0 multi-template:首次启动 seed ComfyUI + Forge + SwarmUI + 4 个语音模板
    /// built-in templates(G4 防覆盖。只在 Templates dict 缺对应 key 时填默认,
    /// 用户定制过的 entry 不会被覆盖。
    /// v1.0.0.x: 新增 <see cref="Settings.DisableBuiltInTemplatesSeed"/> 逃生口 — 设置后整个 seed 跳过,
    /// 允许 templates 块为空列表(用户随后手动添加)。设为 false 或删除字段恢复默认行为。
    ///
    /// v1.0.0.x: A1111 模板已下线 — Stability-AI/stablediffusion 仓库已从 github 移除,
    /// A1111 pre-flight 跟 sdweb 启动都会 fail paths.py:34。用户决定去掉 A1111 模板,
    /// 保留 Forge(用 huggingface_guess 替代 SD core)。老 settings.inf 里的 A1111
    /// entry 留 settings 备份不删 — 用户在 Settings 面板里手动 remove 即可。
    /// </summary>
    private static void SeedBuiltInTemplatesIfMissing(Settings s, string projectRoot)
    {
        // v1.0.0.x: 用户显式禁用 seed → 完全跳过,允许 templates 块为空
        if (s.DisableBuiltInTemplatesSeed) return;

        // G4: only seed if missing — never overwrite user customization
        // v1.0.0.x: 加 6 个 built-in defaults — Forge/SwarmUI(#497 修复)+
        // OpenVoice/Whisper/CoquiTTS/Bark(AI 语音 GitHub clone)。G13 delete 保护
        // 通过 TemplateConfig.CanDelete 里的 hardcoded kind 白名单保护所有 8 个。
        // v1.0.0.x: A1111 不再 seed — 模板已下线。
        if (!s.Templates.ContainsKey("ComfyUI"))
        {
            s.Templates["ComfyUI"] = TemplateConfigDefaults.ComfyUi(projectRoot);
        }
        if (!s.Templates.ContainsKey("Forge"))
        {
            s.Templates["Forge"] = TemplateConfigDefaults.Forge(projectRoot);
        }
        if (!s.Templates.ContainsKey("SwarmUI"))
        {
            s.Templates["SwarmUI"] = TemplateConfigDefaults.SwarmUi(projectRoot);
        }
        if (!s.Templates.ContainsKey("OpenVoice"))
        {
            s.Templates["OpenVoice"] = TemplateConfigDefaults.OpenVoice(projectRoot);
        }
        if (!s.Templates.ContainsKey("Whisper"))
        {
            s.Templates["Whisper"] = TemplateConfigDefaults.Whisper(projectRoot);
        }
        if (!s.Templates.ContainsKey("CoquiTTS"))
        {
            s.Templates["CoquiTTS"] = TemplateConfigDefaults.CoquiTts(projectRoot);
        }
        if (!s.Templates.ContainsKey("Bark"))
        {
            s.Templates["Bark"] = TemplateConfigDefaults.Bark(projectRoot);
        }
    }

    // v1.0.0.x bug #509: 跟 TemplateConfigDefaults 里 8 个 built-in 同步。
    // v1.0.0.x:A1111 从 seed + BuiltInKinds 移除(模板已下线),剩 7 个。
    private static readonly string[] BuiltInKinds =
    {
        "ComfyUI", "Forge", "SwarmUI",
        "OpenVoice", "Whisper", "CoquiTTS", "Bark",
    };

    /// <summary>
    /// v1.0.0.x bug #509:把 "envTemplates\&lt;Kind&gt;" 旧 default 残留改成
    /// "&lt;Kind&gt;",让 <see cref="TemplatePathResolver.Resolve"/> 跟
    /// <see cref="Settings.SystemTemplateLibraryDir"/> 拼出来是
    /// &lt;system_template_library_dir&gt;/&lt;Kind&gt;,不再多一层 envTemplates/。
    ///
    /// v1.0.0.x #572: 还处理「绝对路径 = \&lt;任意\&gt;\&lt;Kind&gt;」→ 改成相对 "&lt;Kind&gt;"。
    /// 老 settings.json 时代(<c>template_comfyui_dir</c> 字段)写死了项目根下的绝对路径(如
    /// <c>D:\ToolDevelop\ComfyUI\ComfyUI</c>),#569 后所有内置模板统一应落在
    /// <c>ENVTemplate/&lt;Kind&gt;</c> 下。把绝对路径末段 == Kind 的旧值归一为相对 Kind,
    /// 让 <see cref="TemplatePathResolver.Resolve"/> 重新锚定到
    /// <see cref="Settings.SystemTemplateLibraryDir"/> 拼出正确路径。
    ///
    /// 只动 8 个 built-in,custom templates(用户自定义 LocalSourceDir)一律不动。
    /// </summary>
    private static void NormalizeBuiltInTemplatePaths(Settings s)
    {
        foreach (var kind in BuiltInKinds)
        {
            if (!s.Templates.TryGetValue(kind, out var cfg)) continue;
            if (string.IsNullOrWhiteSpace(cfg.LocalSourceDir)) continue;

            // 规则 1: envTemplates\<Kind> → <Kind>(bug #509)
            var prefixed = System.IO.Path.Combine("envTemplates", kind);
            if (string.Equals(cfg.LocalSourceDir, prefixed, System.StringComparison.OrdinalIgnoreCase))
            {
                cfg.LocalSourceDir = kind;
                continue;
            }

            // 规则 2: 绝对路径末段 == <Kind> → 改成相对 <Kind>(bug #572)
            // 末段匹配说明路径其实就是 <任意>/<Kind>,不再硬编码到老的项目根 ComfyUI/。
            if (System.IO.Path.IsPathRooted(cfg.LocalSourceDir))
            {
                var leaf = System.IO.Path.GetFileName(cfg.LocalSourceDir.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.Equals(leaf, kind, System.StringComparison.OrdinalIgnoreCase))
                {
                    cfg.LocalSourceDir = kind;
                }
            }
        }
    }

    /// <summary>
    /// v1.0.0 multi-template (T12):把老 settings.json 里 <c>template_comfyui_dir</c> 字段值
    /// 迁移到 Templates["ComfyUI"].LocalSourceDir(G6)。
    ///
    /// T12 起 Settings.TemplateComfyuiDir 字段已移除 — 改走 JsonDocument.Parse(rawJson)
    /// 直接读老 JSON property。当 <paramref name="rawJson"/> 为 null 时(in-memory settings,
    /// 没有 JSON 来源)跳过迁移,纯 in-memory 状态保持不变。
    ///
    /// 只在用户没设过 ComfyUI template 时迁移 — 否则视为用户已经表达过意图,
    /// 不让旧字段覆盖当前 entry。
    ///
    /// v1.0.0.1 (settings-to-inf):SettingsRepository 从 .inf 读时 rawJson = null;
    /// 从老 .json 读时 rawJson 是 JSON 文本。**只对看着像 JSON 的 rawJson 跑迁移** —
    /// .inf 文本以 '#' / 'theme = ' 等开头,JsonDocument.Parse 会抛。
    /// </summary>
    private static void TryMigrateOldTemplateComfyuiDir(Settings s, string? rawJson)
    {
        if (s.Templates.ContainsKey("ComfyUI")) return;
        if (string.IsNullOrWhiteSpace(rawJson)) return;
        // 粗筛:看着像 JSON 才解析。INF 文件首字符可能是 '#'(注释)或字母,JSON 首字符
        // 必定是 '{' 或 '['。这样 .inf 文本不会触发 JsonDocument.Parse 抛错。
        var trimmed = rawJson.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return;

        string? oldDir = null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("template_comfyui_dir", out var prop)) return;
            if (prop.ValueKind != JsonValueKind.String) return;
            oldDir = prop.GetString();
        }
        catch
        {
            // JSON 解析失败 → 静默(不要因为迁移失败让整个 Apply 挂掉)
            return;
        }

        if (string.IsNullOrWhiteSpace(oldDir)) return;

        s.Templates["ComfyUI"] = new TemplateConfig
        {
            Name = "ComfyUI",
            Kind = "ComfyUI",
            LocalSourceDir = oldDir,
            EntryScript = "main.py",
            EntryArgs = "--port {port} --listen 0.0.0.0",
            ModelsSubdir = "models",
        };
        // 老字段在 Settings 类已删除,无需再清。SettingsRepository 持久化时也不会
        // 再带这个 key,自然从用户的 settings.json / settings.inf 里消失(G6)。
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
    /// v1.0.0.x #592:本地资源路径 — 空 → seed 当前 projectRoot + subdir 的绝对路径;
    /// 相对 → 转绝对 = projectRoot + current;绝对 → 保留(用户故意选的别处)。
    ///
    /// <para>
    /// 跟 <see cref="Resolve"/> 的区别:<see cref="Resolve"/> 适合 template paths
    /// (随包走,跨机器友好),seed 相对子目录名;<see cref="ResolveAsAbsolute"/> 适合
    /// 本地资源路径(env / 本地节点),seed 绝对路径让 UI 一眼看到完整路径,跨机器靠
    /// 每次启动 Apply 重算 projectRoot + 重新拼接来跟随。
    /// </para>
    /// </summary>
    private static string ResolveAsAbsolute(string current, string defaultSubdir, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(current))
        {
            // 空 → seed 当前项目根下的绝对路径。Path.Combine 规范化分隔符,Path.GetFullPath
            // 也保证绝对,避免 UI 显示用户机器的 ..\ 之类相对形式。
            return Path.GetFullPath(Path.Combine(projectRoot, defaultSubdir));
        }
        if (!Path.IsPathRooted(current))
        {
            // 相对 → 转绝对 = projectRoot + 当前相对子目录(用户之前手填的相对路径升级到绝对)
            return Path.GetFullPath(Path.Combine(projectRoot, current));
        }
        // 绝对 → 保留(用户故意选的别处,不污染)
        return current;
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
