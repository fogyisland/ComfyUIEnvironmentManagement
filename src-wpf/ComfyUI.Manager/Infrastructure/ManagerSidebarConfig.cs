using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v1.0.0 sidebar.inf 静态配置加载器。
///
/// <para>App 启动时调用一次 <see cref="Initialize(string)"/>:
/// 文件不存在 → 写入默认模板(返回 <see cref="InitializeResult.CreatedDefault"/> = true);
/// 已存在 → 解析并缓存进 <see cref="_dict"/>。</para>
///
/// <para><see cref="IsEnabled(MainSection)"/> 读取缓存:
/// 命中 key → 返回 bool;未命中 → 返回 <c>true</c>(用户期望"没写的=启用")。</para>
///
/// <para>本类是 internal static,因为只有 App.xaml.cs 这一处调用方。
/// 测试通过 InternalsVisibleTo 可见。</para>
/// </summary>
internal static class ManagerSidebarConfig
{
    private static IReadOnlyDictionary<MainSection, bool> _dict = new Dictionary<MainSection, bool>(0);
    private static bool _initialized;

    public readonly record struct InitializeResult(bool CreatedDefault);

    /// <summary>默认模板内容。首次启动写入,反映用户意图:节点市场/工作流库/模型市场暂灰。</summary>
    public const string DefaultTemplate =
        "# sidebar.inf — 左侧菜单启用定义. 0=灰(未启用),1=启用. 首次启动自动生成.\n" +
        "# Dashboard=应用总览, Environments=环境管理, Catalog=节点市场,\n" +
        "# LocalNodes=本地节点, Workflows=工作流库, Templates=模板管理,\n" +
        "# Models=模型市场, Settings=应用设置, BulkUpdate=批量更新,\n" +
        "# SystemStatus=系统状态\n" +
        "Dashboard=1\n" +
        "Environments=1\n" +
        "Catalog=0\n" +
        "LocalNodes=1\n" +
        "Workflows=0\n" +
        "Templates=1\n" +
        "Models=0\n" +
        "Settings=1\n" +
        "BulkUpdate=1\n" +
        "SystemStatus=1\n";

    /// <summary>
    /// 加载 sidebar.inf。多次调用幂等:第一次成功后不再重读,直到 <see cref="Reset"/>。
    /// </summary>
    public static InitializeResult Initialize(string filePath)
    {
        if (_initialized)
            return new InitializeResult(false);

        bool createdDefault = false;
        if (!File.Exists(filePath))
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, DefaultTemplate);
            createdDefault = true;
        }

        try
        {
            var text = File.ReadAllText(filePath);
            _dict = SidebarInfParser.Parse(text);
        }
        catch (Exception)
        {
            // 文件被占用 / 权限拒绝 → 走空 dict,IsEnabled 全返 true(全启用,符合兜底语义)
            _dict = new Dictionary<MainSection, bool>(0);
        }

        _initialized = true;
        return new InitializeResult(createdDefault);
    }

    /// <summary>缺省值:未初始化 / 缺 key → true(全启用)。</summary>
    public static bool IsEnabled(MainSection section)
    {
        if (!_initialized) return true;
        return _dict.TryGetValue(section, out var v) ? v : true;
    }

    /// <summary>测试 hook — 重置缓存,允许下一次 Initialize 重新读盘。</summary>
    internal static void Reset()
    {
        _initialized = false;
        _dict = new Dictionary<MainSection, bool>(0);
    }
}
