using System;
using System.Threading;
using System.Threading.Tasks;
using ComfyUI.Manager.Data;
using ComfyUI.Manager.Search;

namespace ComfyUI.Manager.Services;

/// <summary>
/// v0.6.9 T6:跨 4 类数据源构建搜索索引 — environments(实时) + nodes(实时) +
/// 静态 Settings 章节 + 静态 Commands。
/// <para>
/// G7:打开 Spotlight 时构建,键入仅走内存。BuildAsync 跑在 <see cref="Task.Run"/>,
/// 避免 sync SQLite 调用阻塞 UI 线程。索引构建后即视为 immutable,不再修改。
/// </para>
/// <para>
/// T7 用法:
/// <code>
/// var index = await _globalSearch.BuildAsync();
/// var hits = index.Query("设置", maxResults: 20);
/// </code>
/// </para>
/// <para>
/// T7:implements <see cref="IGlobalSearchService"/>(T7 step A1) — 让
/// <c>SpotlightSearchViewModel</c> 拿 contract 而非 concrete class,可注入 stub 测试。
/// </para>
/// </summary>
public sealed class GlobalSearchService : IGlobalSearchService
{
    private readonly IEnvironmentRepository _envRepo;
    private readonly INodeRepository _nodeRepo;

    public GlobalSearchService(IEnvironmentRepository envRepo, INodeRepository nodeRepo)
    {
        _envRepo = envRepo;
        _nodeRepo = nodeRepo;
    }

    /// <summary>
    /// 异步构建索引。Env + Node 用真实 DB;Settings 章节 + Commands 走静态列表
    /// (跟 SettingsView.xaml 章节一一对应 + MainViewModel 16 个 RelayCommand property 同名)。
    /// 索引最大 <see cref="SearchIndex.MaxEntries"/> 项,超出截断。
    /// </summary>
    public Task<SearchIndex> BuildAsync(CancellationToken ct = default)
    {
        return Task.Run(() => BuildCore(ct), ct);
    }

    private SearchIndex BuildCore(CancellationToken ct)
    {
        var index = new SearchIndex();

        // 1. Environments — 实时 DB
        foreach (var env in _envRepo.ListAll())
        {
            ct.ThrowIfCancellationRequested();
            index.Add(new SearchEntry
            {
                Id = $"env-{env.Id}",
                Kind = TargetKind.Environment,
                DisplayName = env.Name,
                Subtitle = new[] { env.Status, env.BedProfileId ?? "未装 BED" },
                NormalizedTokens = SearchIndex.TokenizeRaw(env.Name),
                Target = SearchTarget.ForEnvironment(env.Id, env.Name),
            });
        }

        // 2. Nodes — 每个 env 下扫到的节点(避免空 N+1:先收集 env 列表再循环)
        var envs = _envRepo.ListAll();
        foreach (var env in envs)
        {
            foreach (var node in _nodeRepo.ListByEnv(env.Id))
            {
                ct.ThrowIfCancellationRequested();
                var name = string.IsNullOrEmpty(node.Package) ? node.Id : node.Package;
                index.Add(new SearchEntry
                {
                    Id = $"node-{env.Id}-{node.Id}",
                    Kind = TargetKind.Node,
                    DisplayName = name,
                    Subtitle = new[] { env.Name, node.Version ?? "" },
                    NormalizedTokens = SearchIndex.TokenizeRaw(name),
                    Target = SearchTarget.ForNode(env.Id, node.Id, name),
                });
            }
        }

        // 3. Settings sections — 静态数组,跟 SettingsView.xaml 7 个 section header 一一对应
        foreach (var section in SettingsSections)
        {
            index.Add(new SearchEntry
            {
                Id = $"settings-{section.Key}",
                Kind = TargetKind.SettingsSection,
                DisplayName = section.Title,
                Subtitle = new[] { "Settings" },
                NormalizedTokens = SearchIndex.TokenizeRaw(section.Title),
                Target = SearchTarget.ForSettingsSection(section.Key, section.Title),
            });
        }

        // 4. Commands — 静态数组,16 个名字跟 MainViewModel.*Command property 一一对应
        //    T7 拿到 CommandName 后用 reflection 或直接绑到对应 Command 触发。
        foreach (var cmd in Commands)
        {
            index.Add(new SearchEntry
            {
                Id = $"cmd-{cmd.Name}",
                Kind = TargetKind.Command,
                DisplayName = cmd.Label,
                Subtitle = new[] { "Command" },
                NormalizedTokens = SearchIndex.TokenizeRaw(cmd.Label),
                Target = SearchTarget.ForCommand(cmd.Name, cmd.Label),
            });
        }

        return index;
    }

    /// <summary>
    /// Settings 章节列表(7 项)— 跟 <c>SettingsView.xaml</c> 章节标题字面量对齐。
    /// Key 是 <see cref="SearchTarget.SectionKey"/>,Title 是 <see cref="SearchEntry.DisplayName"/>。
    /// </summary>
    internal static readonly (string Key, string Title)[] SettingsSections = new[]
    {
        ("general", "基础"),
        ("querySources", "查询节点的源"),
        ("downloadSources", "下载节点的源"),
        ("paths", "路径"),
        ("pythonInterpreters", "Python 解释器"),
        ("envTools", "环境 / 工具"),
        ("extraPaths", "高级 — 额外路径"),
    };

    /// <summary>
    /// Command 列表(16 项)— Name 必须跟 <c>MainViewModel.cs:108-124</c> 的
    /// <c>RelayCommand *Command</c> property 同名(首字母大写,大小写敏感)。
    /// Label 是 UI 显示用。
    /// </summary>
    internal static readonly (string Name, string Label)[] Commands = new[]
    {
        ("ShowDashboard",                    "主页 — Dashboard"),
        ("ShowEnvironments",                 "环境"),
        ("ShowCatalog",                      "节点目录"),
        ("ShowSettings",                     "设置"),
        ("OpenBulkUpdate",                   "批量更新"),
        ("ShowSystemStatus",                 "系统状态"),
        ("SaveUiPreferences",                "保存 UI 偏好"),
        ("LoadUiPreferences",                "加载 UI 偏好"),
        ("OpenProjectFolder",                "打开项目文件夹"),
        ("OpenLogFolder",                    "打开日志文件夹"),
        ("OpenComfySettingsJsonCommand",     "打开 ComfyUI 配置 (comfy.settings.json)"),
        ("OpenExtraModelPathsYamlCommand",   "打开 ComfyUI 模型配置 (extra_model_paths.yaml)"),
        ("ShowAbout",                        "关于"),
        ("ShowDonateQr",                     "赞助作者"),
        ("ExitApp",                          "退出"),
    };
}
