using System;
using System.Collections.Generic;
using System.IO;
using ComfyUI.Manager.Services;
using ComfyUI.Manager.ViewModels;

namespace ComfyUI.Manager.Infrastructure;

/// <summary>
/// v1.0.0 sidebar.inf 静态配置加载器。
///
/// <para><b>seed 来源</b>:本文件 <b>不是</b>运行时自动生成,而是仓库根
/// <c>config/sidebar.inf</c>(发布 seed 的一部分)。App.xaml.cs 启动时调一次
/// <see cref="Initialize(string)"/> 把文件读进 <see cref="_dict"/>。</para>
///
/// <para><b>缺失语义</b>:文件不存在 → 静默,IsEnabled 全部默认 true(全启用)。
/// 用户装 release 后没文件 → 所有按钮可用,跟"默认状态"语义一致。</para>
///
/// <para><see cref="IsEnabled(MainSection)"/> 读取缓存:命中 key → 返回 bool;
/// 未命中 → 返回 <c>true</c>(用户期望"没写的=启用")。</para>
///
/// <para>本类是 internal static,因为只有 App.xaml.cs 这一处调用方。
/// 测试通过 InternalsVisibleTo 可见。</para>
/// </summary>
internal static class ManagerSidebarConfig
{
    private static IReadOnlyDictionary<MainSection, bool> _dict = new Dictionary<MainSection, bool>(0);
    private static bool _initialized;

    public readonly record struct InitializeResult(bool FileExists);

    /// <summary>
    /// 加载 sidebar.inf。
    /// - 文件存在 → 解析并缓存
    /// - 文件不存在 → 不写、不报错;IsEnabled 走 missing→默认 true
    /// 多次调用幂等(以第一次为准),直到 <see cref="Reset"/>。
    /// </summary>
    public static InitializeResult Initialize(string filePath)
    {
        if (_initialized)
            return new InitializeResult(File.Exists(filePath));

        if (File.Exists(filePath))
        {
            try
            {
                var text = File.ReadAllText(filePath);
                _dict = SidebarInfParser.Parse(text);
            }
            catch (Exception ex)
            {
                // 文件被占用 / 权限拒绝 → 走空 dict,IsEnabled 全返 true
                System.Diagnostics.Debug.WriteLine($"[sidebar.inf] 读取失败 path={filePath} err={ex.Message}");
                _dict = new Dictionary<MainSection, bool>(0);
            }
        }
        else
        {
            // 缺失 = 静默全启用(seed 应该随包发布,缺失说明打包步骤漏了或用户删了)
            _dict = new Dictionary<MainSection, bool>(0);
        }

        _initialized = true;
        return new InitializeResult(File.Exists(filePath));
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
